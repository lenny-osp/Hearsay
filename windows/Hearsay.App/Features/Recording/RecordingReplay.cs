using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Hearsay.App.Features.Debug;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// Debug only. When Hearsay is launched with <c>HEARSAY_REPLAY_FILE=&lt;audio&gt;</c>
/// and <c>HEARSAY_MODEL_DIR=&lt;model file or folder&gt;</c> (see
/// <see cref="DebugModel"/>), feeds the file through the app's
/// <see cref="RecordingController"/> exactly as a recording: a fake
/// microphone yields it in 0.1 s chunks paced in real time from a timer on
/// the thread pool (like a capture callback), through <see cref="AudioMixer"/>,
/// the WAV spool, <see cref="LiveChunker"/>, and the live queue. When the file
/// is used up it presses Stop, waits for the final pass, prints one line per
/// live job (queued, started, finished, audio seconds, cues) and the totals
/// to stderr, and quits with status 0 (1 when the final pass failed).
/// Port of mac/Hearsay/Features/Recording/RecordingReplay.swift.
/// </summary>
/// <remarks>
/// <c>HEARSAY_LANGUAGE</c> (auto, en, zh-TW, zh-CN, de, es; zh is an alias for
/// zh-TW; default en) is optional. Every language detection and the session's
/// final decision are printed to stdout, and so is the final SRT (Windows
/// only: it lives in the throwaway folder, removed at exit).
/// <c>HEARSAY_REPLAY_SYSTEM=silence</c> adds a second, silent source in place
/// of system audio; <c>HEARSAY_REPLAY_UI=1</c> also shows the Record tab for
/// the session. Settings, spool and output are in the throwaway settings
/// folder, so the user's settings, spool and output folder are never
/// touched. The Mac's <c>HEARSAY_REPLAY_SNAPSHOTS</c> is not ported.
/// </remarks>
internal static class RecordingReplay
{
    public const string Variable = "HEARSAY_REPLAY_FILE";

    /// <summary>Returns false (and does nothing) when the variable is not set.</summary>
    public static bool RunIfRequested(AppShell shell)
    {
        var environment = DebugEnvironment.Environment;
        if (!environment.TryGetValue(Variable, out var file) || file.Length == 0) return false;
        if (!environment.TryGetValue(DebugModel.Variable, out var model) || model.Length == 0)
        {
            Say($"{Variable} needs {DebugModel.Variable}");
            shell.FinishDebugRun(1);
            return true;
        }
        _ = RunAndFinishAsync(shell, file, model);
        return true;
    }

    private static async Task RunAndFinishAsync(AppShell shell, string file, string modelValue)
    {
        var status = 1;
        try
        {
            status = await RunAsync(shell, Path.GetFullPath(file), modelValue).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Say($"failed: {error}");
        }
        shell.FinishDebugRun(status);
    }

    private sealed class JobTiming(double start, double seconds, double queued)
    {
        public double Start { get; } = start;

        public double Seconds { get; } = seconds;

        public double Queued { get; } = queued;

        public double? Started { get; set; }

        public double? Finished { get; set; }

        public int Cues { get; set; }
    }

    private static async Task<int> RunAsync(AppShell shell, string file, string modelValue)
    {
        var environment = DebugEnvironment.Environment;
        var (modelPath, modelError) = DebugModel.Resolve(modelValue, shell.Models.Catalog);
        if (modelPath is null)
        {
            Say(modelError ?? "no model");
            return 1;
        }
        float[] samples;
        try
        {
            samples = await Task.Run(() => AudioFileLoader.LoadMono16k(file)).ConfigureAwait(true);
        }
        catch (AudioFileLoaderException error)
        {
            Say($"cannot read {file}: {error.Message}");
            return 1;
        }
        var language = environment.TryGetValue("HEARSAY_LANGUAGE", out var value) && value.Length > 0
            ? LanguageChoice.FromDebugValue(value) ?? LanguageChoice.Fixed(TranscriptLanguage.English)
            : LanguageChoice.Fixed(TranscriptLanguage.English);
        var silentSystem = environment.TryGetValue("HEARSAY_REPLAY_SYSTEM", out var system) && system == "silence";

        var root = Path.Combine(shell.SettingsFile.Folder, "replay");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        var settings = shell.Settings;
        settings.OutputFolder = OutputLocation.MakeStoredPath(output);
        settings.LanguageChoice = language;
        settings.CaptureSystemAudio = silentSystem;
        settings.KeepRecording = false;

        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new CaptureSources(
            () => new ReplayMicrophone(new ReplayFeed(samples, () => exhausted.TrySetResult())),
            () => new ReplaySystemAudio(new ReplayFeed(null, () => { })),
            _ => modelPath);
        var controller = shell.RecordingController;
        controller.UseForDebug(sources, new RecordingSpool(Path.Combine(root, "spool")));
        controller.SuppressNotesRequests = true;

        var jobs = new SortedDictionary<int, JobTiming>();
        controller.LiveJobObserver = job =>
        {
            var now = Now();
            switch (job)
            {
                case LiveJobEvent.Queued queued:
                    jobs[queued.Index] = new JobTiming(queued.Start, queued.Seconds, now);
                    Say(Format($"{now,7:F2}  job {queued.Index} queued ({queued.Start:F1}-{queued.Start + queued.Seconds:F1} s)"));
                    break;
                case LiveJobEvent.Started started:
                    if (jobs.TryGetValue(started.Index, out var s)) s.Started = now;
                    Say(Format($"{now,7:F2}  job {started.Index} started"));
                    break;
                case LiveJobEvent.Finished finished:
                    if (jobs.TryGetValue(finished.Index, out var f))
                    {
                        f.Finished = now;
                        f.Cues = finished.Cues;
                    }
                    Say(Format($"{now,7:F2}  job {finished.Index} finished, {finished.Cues} cues{(finished.Error is { } e ? $", error: {e}" : "")}"));
                    break;
                case LiveJobEvent.Detection detection:
                    var line = Format($"{now,7:F2}  detection over {detection.Seconds:F1} s: ")
                        + (detection.Result is { } result ? TranscriptionEngine.DebugSummary(result) : "failed")
                        + (detection.Decision is { } decision ? "; settled: " + decision.DebugSummary() : "; not settled");
                    Say(line);
                    Print(line);
                    break;
            }
        };

        if (environment.TryGetValue("HEARSAY_REPLAY_UI", out var ui) && ui == "1")
        {
            shell.Tabs.Tab = MainTab.Record;
            shell.MainWindow.Activate();
            Say("showing the Record tab");
        }
        Say(Format($"replaying {Path.GetFileName(file)} ({samples.Length / 16_000.0:F2} s), language {language.StorageValue}, preferred {settings.PreferredLanguage.Code()}, system audio {(silentSystem ? "silence" : "off")}, model {Path.GetFileName(modelPath)}"));
        clock.Restart();
        controller.Start();
        using var statusTimer = new CancellationTokenSource();
        var statusTask = ReportStatusAsync(controller, Now, statusTimer.Token);

        await exhausted.Task.ConfigureAwait(true);
        var stopAt = Now();
        var waitingAtStop = controller.LiveChunksWaiting;
        Say(Format($"{stopAt,7:F2}  file used up; Stop with {waitingAtStop} live chunks waiting"));
        await controller.StopAsync().ConfigureAwait(true);
        while (controller.IsTranscribing || controller.Phase is ControllerPhase.Stopping)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }
        await statusTimer.CancelAsync().ConfigureAwait(true);
        await statusTask.ConfigureAwait(true);
        var doneAt = Now();

        Say("job  audio (s)        len   queued  started finished  latency  cues");
        static string Time(double? t) => t is { } v ? v.ToString("F2", CultureInfo.InvariantCulture).PadLeft(8) : "       -";
        foreach (var (index, job) in jobs)
        {
            var latency = job.Finished is { } done ? (done - job.Queued).ToString("F2", CultureInfo.InvariantCulture).PadLeft(8) : "       -";
            Say(Format($"{index,3}  {job.Start,6:F1}-{job.Start + job.Seconds,6:F1}  {job.Seconds,5:F1} {Time(job.Queued)} {Time(job.Started)} {Time(job.Finished)} {latency}  {job.Cues,4}"));
        }
        var latencies = jobs.Values.Where(j => j.Finished is not null).Select(j => (j.Finished ?? 0) - j.Queued).ToList();
        var duringRecording = jobs.Values.Count(j => (j.Finished ?? double.PositiveInfinity) <= stopAt);
        Say(Format($"totals: {jobs.Count} jobs, {duringRecording} finished before Stop, {waitingAtStop} waiting at Stop, max latency {(latencies.Count == 0 ? 0 : latencies.Max()):F2} s, live cues {controller.LiveSegments.Count}, final pass done {doneAt - stopAt:F2} s after Stop"));
        if (controller.LiveNotice is { } liveNotice) Say($"live notice: {liveNotice}");

        Print("decision: " + (controller.SessionDecision?.DebugSummary() ?? "none"));
        if (controller.LanguageNotice is { } notice) Print("notice: " + notice.Message);
        var failed = false;
        switch (controller.Phase)
        {
            case ControllerPhase.Finished finished:
                var text = finished.Srt is { } srt && File.Exists(srt) ? await File.ReadAllTextAsync(srt).ConfigureAwait(true) : "";
                Say($"final pass: {text.Split(" --> ").Length - 1} cues, {finished.Srt}");
                Print("--- srt ---");
                Print(text.TrimEnd('\n'));
                Print("--- end ---");
                break;
            case ControllerPhase.Failed failure:
                Say($"final pass failed: {failure.Message}");
                failed = true;
                break;
            default:
                Say($"ended in {controller.Phase}");
                failed = true;
                break;
        }
        controller.LiveJobObserver = null;
        // Everything was written below the throwaway folder; remove anything that was not.
        foreach (var path in new[] { controller.FinishedTranscript, controller.FinishedRecording }.OfType<string>())
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                Say($"removing {path}");
                File.Delete(path);
            }
        }
        return failed ? 1 : 0;
    }

    private static async Task ReportStatusAsync(RecordingController controller, Func<double> now, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Say(Format($"{now(),7:F2}  status: recorded {controller.Elapsed:F1} s, waiting {controller.LiveChunksWaiting}, live cues {controller.LiveSegments.Count}, phase {controller.Phase.GetType().Name}"));
        }
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void Say(string line)
    {
        DebugOutput.Error.WriteLine("hearsay replay: " + line);
        DebugOutput.Error.Flush();
    }

    private static void Print(string line)
    {
        DebugOutput.Out.WriteLine(line);
        DebugOutput.Out.Flush();
    }

    /// <summary>
    /// Yields <c>samples</c> (or endless silence when null) as 0.1 s chunks
    /// from a thread-pool timer, paced in real time, like a capture callback.
    /// Calls <c>onExhausted</c> once when the samples run out; the stream stays
    /// open until <see cref="Finish"/>, as a live device's would.
    /// </summary>
    private sealed class ReplayFeed(float[]? samples, Action onExhausted) : IDisposable
    {
        private const int Chunk = 1600;
        private readonly Channel<TimedChunk> channel = Channel.CreateUnbounded<TimedChunk>();
        private readonly Lock gate = new();
        private Timer? timer;
        private double startHostTime;
        private int chunksSent;
        private long delivered;
        private bool exhausted;
        private bool finished;

        public ChannelReader<TimedChunk> Stream => channel.Reader;

        public long Delivered
        {
            get
            {
                lock (gate) return delivered;
            }
        }

        public void Start()
        {
            lock (gate)
            {
                startHostTime = HostClock.NowSeconds();
                timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            }
        }

        public void Dispose() => Finish();

        public void Finish()
        {
            lock (gate)
            {
                finished = true;
                timer?.Dispose();
                timer = null;
            }
            channel.Writer.TryComplete();
        }

        /// <summary>Sends every chunk that is due by now.</summary>
        private void Tick()
        {
            var justExhausted = false;
            lock (gate)
            {
                if (finished || exhausted) return;
                var due = (int)Math.Floor((HostClock.NowSeconds() - startHostTime) * 10) + 1;
                while (chunksSent < due)
                {
                    var offset = chunksSent * Chunk;
                    float[] piece;
                    if (samples is not null)
                    {
                        if (offset >= samples.Length)
                        {
                            exhausted = true;
                            justExhausted = true;
                            break;
                        }
                        piece = samples.AsSpan(offset, Math.Min(Chunk, samples.Length - offset)).ToArray();
                    }
                    else
                    {
                        piece = new float[Chunk];
                    }
                    channel.Writer.TryWrite(new TimedChunk(piece, startHostTime + chunksSent / 10.0));
                    chunksSent++;
                    delivered += piece.Length;
                }
            }
            if (justExhausted) onExhausted();
        }
    }

    /// <summary>The fake microphone of the replay. Pause and resume are not simulated.</summary>
    private sealed class ReplayMicrophone(ReplayFeed feed) : IMicrophoneCapture
    {
        public ChannelReader<TimedChunk> TimedSamples => feed.Stream;

        public MicrophoneDiagnostics Diagnostics => new() { DeviceName = "replay", SamplesDelivered = feed.Delivered };

        public MicrophoneRecorderException? Failure => null;

        public void Start(AudioInputDevice? device) => feed.Start();

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop() => feed.Finish();
    }

    /// <summary>Silent system audio for the replay; starts on creation like the live recorder.</summary>
    private sealed class ReplaySystemAudio : ISystemAudioCapture
    {
        private readonly ReplayFeed feed;

        public ReplaySystemAudio(ReplayFeed feed)
        {
            this.feed = feed;
            feed.Start();
        }

        public ChannelReader<TimedChunk> TimedSamples => feed.Stream;

        public SystemAudioRecorderException? Failure => null;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop() => feed.Finish();
    }
}
