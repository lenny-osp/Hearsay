using System.Collections.Concurrent;
using Hearsay.App.Features.Transcription;
using Hearsay.Core;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.App.Tests;

/// <summary>
/// A dedicated thread with a <see cref="SynchronizationContext"/> that runs
/// posted callbacks one at a time: what the WinUI UI thread is to the queue
/// and the controller, which return to it after every await
/// (<c>ConfigureAwait(true)</c>) and are used from it only.
/// </summary>
internal sealed class UiThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> work = [];
    private readonly Thread thread;

    public UiThread()
    {
        var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            ready.Set();
            foreach (var (callback, state) in work.GetConsumingEnumerable()) callback(state);
        })
        {
            IsBackground = true,
            Name = "Test UI thread",
        };
        thread.Start();
        ready.Wait();
    }

    /// <summary>Runs <paramref name="body"/> on the thread; awaits inside it come back to the thread.</summary>
    public Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add((async _ =>
        {
            try
            {
                await body();
                done.SetResult();
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }, null));
        return done.Task;
    }

    public void Dispose()
    {
        work.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class Context(UiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                owner.work.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Completed: the test ended.
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => new Context(owner);
    }
}

/// <summary>
/// A stand-in for the Whisper engine (<see cref="IQueueEngine"/>). A pass has
/// <see cref="TotalWindows"/> windows of 30 s; window <c>w</c> becomes a cue
/// "window w". Like the real engine, a step yields only before a window and
/// only after at least one window of the call, returns a checkpoint, and
/// throws <see cref="TranscriptionCheckpointException"/> for another model.
/// </summary>
internal sealed class FakeEngine : IQueueEngine
{
    private readonly ConcurrentQueue<StepCall> steps = new();
    private int detectCalls;
    private int rerunCalls;

    public const int WindowSeconds = 30;

    public int TotalWindows { get; set; } = 3;

    public int SampleCount => TotalWindows * WindowSeconds * 16_000;

    /// <summary>Set: every window waits for a release (the test paces the pass).</summary>
    public SemaphoreSlim? WindowGate { get; set; }

    /// <summary>Set: the detection waits for a release.</summary>
    public SemaphoreSlim? DetectGate { get; set; }

    public volatile int Foreground;

    public Func<Exception?>? FailStep { get; set; }

    public Exception? DetectFailure { get; set; }

    public DetectionResult Detection { get; set; } = new("en", 0.99f, 1, []);

    public IReadOnlyList<StepCall> Steps => [.. steps];

    public int DetectCalls => Volatile.Read(ref detectCalls);

    public int RerunCalls => Volatile.Read(ref rerunCalls);

    public List<TranscriptLanguage> RerunLanguages { get; } = [];

    public int ForegroundWaiting => Foreground;

    public Task<TranscriptionStep> TranscribeStepAsync(
        string modelPath, float[] samples, TranscriptLanguage language, TranscriptionCheckpoint? resumeFrom,
        IProgress<double>? progress, Func<bool> shouldYield, CancellationToken cancellationToken)
    {
        steps.Enqueue(new StepCall(modelPath, language, resumeFrom));
        return Task.Run(() =>
        {
            if (FailStep?.Invoke() is { } failure) throw failure;
            if (resumeFrom is not null && !resumeFrom.IsForModel(modelPath))
            {
                throw new TranscriptionCheckpointException(TranscriptionCheckpointError.ModelMismatch);
            }
            var segments = resumeFrom?.Segments.ToList() ?? [];
            var first = resumeFrom is null ? 0 : (int)(resumeFrom.ResumeSeconds / WindowSeconds);
            for (var window = first; window < TotalWindows; window++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (window > first && shouldYield())
                {
                    return TranscriptionStep.Suspend(new TranscriptionCheckpoint(
                        segments, window * WindowSeconds, language.Code(), Path.GetFullPath(modelPath), samples.Length,
                        TranscriptionCheckpoint.Fingerprint(samples), TranscriptionOptions.App(language)));
                }
                WindowGate?.Wait(cancellationToken);
                segments.Add(new TranscriptSegment(window * WindowSeconds, window * WindowSeconds + 1, $"window {window}"));
                progress?.Report((window + 1.0) / TotalWindows);
            }
            return TranscriptionStep.Done(new WhisperTranscription(segments, language.Code()));
        }, CancellationToken.None);
    }

    public async Task<DetectionResult> DetectLanguageAsync(string modelPath, float[] samples, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref detectCalls);
        if (DetectGate is { } gate) await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (DetectFailure is { } failure) throw failure;
        return Detection;
    }

    public Task<WhisperTranscription> TranscribeAsync(
        string modelPath, float[] samples, TranscriptLanguage language, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref rerunCalls);
        lock (RerunLanguages) RerunLanguages.Add(language);
        progress?.Report(0.5);
        return Task.FromResult(new WhisperTranscription([new TranscriptSegment(0, 1, $"again in {language.Code()}")], language.Code()));
    }

    /// <summary>One <see cref="TranscribeStepAsync"/> call.</summary>
    public sealed record StepCall(string ModelPath, TranscriptLanguage Language, TranscriptionCheckpoint? ResumeFrom);
}

/// <summary>
/// A queue, a fake engine and scratch folders for spool, output and settings.
/// Make it on the UI thread (the queue captures that context).
/// </summary>
internal sealed class QueueRig : IDisposable
{
    private readonly ScratchFolder root = new();
    private readonly InterfaceLanguageScope english = new(InterfaceLanguage.English, "en-US");
    private readonly Dictionary<string, int> numbers = [];
    private int stems;

    public QueueRig(FinalPassTiming timing, bool queueDrives = true)
    {
        SpoolPath = Path.Combine(root.Path, "spool");
        OutputPath = Path.Combine(root.Path, "out");
        Directory.CreateDirectory(SpoolPath);
        Directory.CreateDirectory(OutputPath);
        Settings = new AppSettings(new SettingsFile(Path.Combine(root.Path, "settings")));
        Settings.OutputFolder = OutputLocation.MakeStoredPath(OutputPath);
        Settings.FinalPassTiming = timing;
        Spool = new RecordingSpool(SpoolPath);
        Engine = new FakeEngine();
        Model = Path.Combine(root.Path, "models", "a.bin");
        Queue = MakeQueue(queueDrives);
    }

    public string SpoolPath { get; }

    public string OutputPath { get; }

    public AppSettings Settings { get; }

    public RecordingSpool Spool { get; }

    public FakeEngine Engine { get; }

    public TranscriptionQueue Queue { get; private set; }

    /// <summary>The model the queue asks for; change it to simulate another active model.</summary>
    public string Model { get; set; }

    /// <summary>When set, the queue's model lookup throws it (no model installed).</summary>
    public Exception? ModelFailure { get; set; }

    /// <summary>Queue events as "queued 1", "running 1", ... (jobs numbered in order of first appearance).</summary>
    public List<string> Events { get; } = [];

    public string Combine(string name) => System.IO.Path.Combine(root.Path, name);

    /// <summary>A second queue on the same spool, as after a relaunch.</summary>
    public TranscriptionQueue MakeQueue(bool drives = true)
    {
        var queue = new TranscriptionQueue(
            Settings, Engine, Spool,
            () => ModelFailure is { } failure ? throw failure : Model,
            drives,
            (_, _) => new float[Engine.SampleCount],
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20))
        {
            NotesOnScreen = () => NotesOnScreen,
        };
        queue.EventObserver = Observe;
        Queue = queue;
        return queue;
    }

    /// <summary>What the queue's notes-on-screen check answers.</summary>
    public bool NotesOnScreen { get; set; }

    private void Observe(QueueEvent queueEvent)
    {
        int Number(string id)
        {
            if (!numbers.TryGetValue(id, out var number)) numbers[id] = number = numbers.Count + 1;
            return number;
        }
        Events.Add(queueEvent switch
        {
            QueueEvent.Queued queued => $"queued {Number(queued.Id)}",
            QueueEvent.Running running => $"running {Number(running.Id)}",
            QueueEvent.Suspended suspended => $"suspended {Number(suspended.Id)}",
            QueueEvent.Resumed resumed => $"resumed {Number(resumed.Id)}",
            QueueEvent.Language language => $"language {Number(language.Id)}",
            QueueEvent.Done done => $"done {Number(done.Id)}",
            QueueEvent.Failed failed => $"failed {Number(failed.Id)}",
            _ => "?",
        });
    }

    public string NewWav(string? stem = null)
    {
        stem ??= $"2026-09-30_10-00-{++stems:00}";
        var wav = System.IO.Path.Combine(SpoolPath, stem + ".wav");
        File.WriteAllBytes(wav, [0]);
        return wav;
    }

    /// <summary>Queues a recording as a session's Stop would.</summary>
    public TranscriptionJob Add(
        LanguageChoice? choice = null, bool keep = true, IReadOnlyList<TranscriptSegment>? live = null, LiveSink? sink = null,
        string? stem = null, TranscriptLanguage preferred = TranscriptLanguage.English)
    {
        var wav = NewWav(stem);
        var tracker = new SessionLanguageTracker(choice ?? LanguageChoice.Fixed(TranscriptLanguage.English), preferred);
        Queue.Enqueue(new RecordingHandover(
            wav, DateTimeOffset.UtcNow, tracker, tracker.Language?.ChineseScript(), keep, live ?? [], live is not null, sink, null, null));
        return Queue.Jobs[^1];
    }

    public static async Task WaitUntil(Func<bool> condition, string what, int milliseconds = 10_000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    public static async Task Stays(Func<bool> condition, string what, int milliseconds = 150)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!condition()) throw new Xunit.Sdk.XunitException($"{what} stopped holding");
            await Task.Delay(5);
        }
    }

    /// <summary>The cue texts of an SRT file.</summary>
    public static List<string> Texts(string srt) => [.. Srt.Parse(File.ReadAllText(srt)).Select(cue => cue.Text)];

    public void Dispose()
    {
        Queue.Dispose();
        english.Dispose();
        root.Dispose();
    }

    /// <summary>Makes a rig on a UI thread, runs <paramref name="body"/> there, and cleans up.</summary>
    public static async Task RunAsync(FinalPassTiming timing, Func<QueueRig, Task> body)
    {
        using var ui = new UiThread();
        QueueRig? rig = null;
        try
        {
            await ui.RunAsync(async () =>
            {
                rig = new QueueRig(timing);
                await body(rig);
            });
        }
        finally
        {
            if (rig is not null)
            {
                await ui.RunAsync(() =>
                {
                    rig.Dispose();
                    return Task.CompletedTask;
                });
            }
        }
    }
}
