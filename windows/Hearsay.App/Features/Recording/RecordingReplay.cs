using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Hearsay.App.Features.Debug;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Naming;
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
/// is used up it presses Stop; the session becomes a job of the real
/// <see cref="TranscriptionQueue"/>, which runs the final pass.
/// Port of mac/Hearsay/Features/Recording/RecordingReplay.swift.
/// </summary>
/// <remarks>
/// <para><c>HEARSAY_REPLAY_FILE=&lt;a&gt;,&lt;b&gt;[,...]</c> replays the files as
/// consecutive sessions joined by Stop &amp; Start Next (PLAN.md 4.9): when a
/// file is used up the next one starts at once, and Stop follows the last.
/// Every live job (queued, started, finished, with its session), every queue
/// event (queued, running, suspended, resumed, language, done with the SRT
/// path, failed), and the gap between two sessions are printed with
/// timestamps to stderr. The run ends when the queue has nothing left, prints
/// the tables and totals, deletes everything it wrote, and quits with status
/// 0 (1 when a job failed).</para>
/// <para><c>HEARSAY_LANGUAGE</c> (auto, en, zh-TW, zh-CN, de, es; zh is an
/// alias for zh-TW; default en) is optional and applies to every session.
/// Every language detection and each job's final language decision are
/// printed to stdout, and so is each final SRT (Windows only: it lives in the
/// throwaway folder, removed at exit). <c>HEARSAY_REPLAY_TIMING=immediate|whenIdle|manual</c>
/// picks the final-pass timing (default immediate, as on the Mac; <c>manual</c>
/// is Windows only, PLAN.md 4.11 and 18.12).
/// With <c>manual</c> every job is held (queue events <c>held</c> and
/// <c>released</c> are printed next to the others), nothing is transcribed,
/// and the run ends once the live tails are done, listing the held jobs and
/// exiting with its normal status (a held job is not a failure);
/// <c>HEARSAY_REPLAY_RELEASE=&lt;seconds&gt;</c> instead waits that long after
/// the last session stops and calls <c>ReleaseAll</c> ("Transcribe All"), then
/// runs the final passes as usual. <c>HEARSAY_REPLAY_TRASH=1</c> (manual only)
/// right after the last Stop moves the first held job's WAV to the Recycle Bin
/// ("Move to Recycle Bin…", PLAN.md 4.11, 18.12), checks that it left the
/// replay's spool, prints a <c>trashed</c> event, and removes the recycled copy
/// from the Recycle Bin again (a file this run wrote; only that item, found by
/// its handle and the bin's own record of the original path); before it trashes
/// anything it checks that the spool is inside the temporary folder.
/// <c>HEARSAY_REPLAY_LIVE=off</c> turns the live preview off in the throwaway
/// settings (PLAN.md 4.12): no live job runs, and an Auto session's language
/// is settled by the queue at Stop; <c>on</c> is Always on (no speed probe),
/// <c>automatic</c> (the default) follows the probe.
/// <c>HEARSAY_REPLAY_SYSTEM=silence</c> adds a second, silent source in place
/// of system audio; <c>HEARSAY_REPLAY_UI=1</c> also shows the Record tab for
/// the session. Settings, spool and output are in the throwaway settings
/// folder, so the user's settings, spool and output folder are never
/// touched. The Mac's <c>HEARSAY_REPLAY_SNAPSHOTS</c> is not ported.</para>
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
        var files = file.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath).ToList();
        _ = RunAndFinishAsync(shell, files, model);
        return true;
    }

    private static async Task RunAndFinishAsync(AppShell shell, IReadOnlyList<string> files, string modelValue)
    {
        var status = 1;
        try
        {
            status = await RunAsync(shell, files, modelValue).ConfigureAwait(true);
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

    /// <summary>Numbers sessions and queue jobs 1, 2, 3... in the order they appear.</summary>
    private sealed class ReplayNumbers
    {
        private readonly Dictionary<int, int> sessions = [];
        private readonly Dictionary<string, int> jobs = [];

        public int Session(int id)
        {
            if (sessions.TryGetValue(id, out var known)) return known;
            return sessions[id] = sessions.Count + 1;
        }

        public int Job(string id)
        {
            if (jobs.TryGetValue(id, out var known)) return known;
            return jobs[id] = jobs.Count + 1;
        }
    }

    private static async Task<int> RunAsync(AppShell shell, IReadOnlyList<string> files, string modelValue)
    {
        var environment = DebugEnvironment.Environment;
        var (modelPath, modelError) = DebugModel.Resolve(modelValue, shell.Models.Catalog);
        if (modelPath is null)
        {
            Say(modelError ?? "no model");
            return 1;
        }
        var loaded = new List<float[]>();
        foreach (var file in files)
        {
            try
            {
                loaded.Add(await Task.Run(() => AudioFileLoader.LoadMono16k(file)).ConfigureAwait(true));
            }
            catch (AudioFileLoaderException error)
            {
                Say($"cannot read {file}: {error.Message}");
                return 1;
            }
        }
        var language = environment.TryGetValue("HEARSAY_LANGUAGE", out var value) && value.Length > 0
            ? LanguageChoice.FromDebugValue(value) ?? LanguageChoice.Fixed(TranscriptLanguage.English)
            : LanguageChoice.Fixed(TranscriptLanguage.English);
        var timing = environment.TryGetValue("HEARSAY_REPLAY_TIMING", out var timingValue)
            ? FinalPassTimings.FromStorageValue(timingValue) ?? FinalPassTiming.Immediate
            : FinalPassTiming.Immediate;
        double? releaseAfter = null;
        if (environment.TryGetValue("HEARSAY_REPLAY_RELEASE", out var releaseValue) && releaseValue.Length > 0)
        {
            if (!double.TryParse(releaseValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || double.IsInfinity(seconds))
            {
                Say($"HEARSAY_REPLAY_RELEASE must be a number of seconds, not {releaseValue}");
                return 1;
            }
            releaseAfter = seconds;
        }
        var trashFirst = environment.TryGetValue("HEARSAY_REPLAY_TRASH", out var trashValue) && trashValue == "1";
        if (trashFirst && timing != FinalPassTiming.Manual)
        {
            Say("HEARSAY_REPLAY_TRASH needs HEARSAY_REPLAY_TIMING=manual");
            return 1;
        }
        var silentSystem = environment.TryGetValue("HEARSAY_REPLAY_SYSTEM", out var system) && system == "silence";
        // PLAN.md 4.12 and 18.9: off = no live preview; on = always on (no speed probe).
        var livePreview = LivePreviewMode.Automatic;
        if (environment.TryGetValue("HEARSAY_REPLAY_LIVE", out var liveValue) && liveValue.Length > 0)
        {
            if (LivePreviewModes.FromStorageValue(liveValue) is not { } liveMode)
            {
                Say($"HEARSAY_REPLAY_LIVE must be off, on or automatic, not {liveValue}");
                return 1;
            }
            livePreview = liveMode;
        }

        var root = Path.Combine(shell.SettingsFile.Folder, "replay");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        var settings = shell.Settings;
        settings.OutputFolder = OutputLocation.MakeStoredPath(output);
        settings.LanguageChoice = language;
        settings.CaptureSystemAudio = silentSystem;
        settings.KeepRecording = false;
        settings.FinalPassTiming = timing;
        settings.LivePreviewMode = livePreview;

        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        var exhausted = Channel.CreateUnbounded<int>();
        var nextSession = 0;
        var sources = new CaptureSources(
            () =>
            {
                var index = nextSession++;
                var samples = index < loaded.Count ? loaded[index] : [];
                return new ReplayMicrophone(new ReplayFeed(samples, () => exhausted.Writer.TryWrite(index)));
            },
            () => new ReplaySystemAudio(new ReplayFeed(null, () => { })),
            _ => modelPath);
        var controller = shell.RecordingController;
        var queue = shell.Queue;
        var spool = new RecordingSpool(Path.Combine(root, "spool"));
        controller.UseForDebug(sources, spool);
        queue.UseForDebug(spool, () => modelPath);
        // No notes flow in a replay: no transcript reaches an AI provider.
        queue.NotesOnScreen = () => true;

        var numbers = new ReplayNumbers();
        var jobs = new SortedDictionary<(int Session, int Index), JobTiming>();
        controller.LiveJobObserver = job =>
        {
            var now = Now();
            switch (job)
            {
                case LiveJobEvent.Queued queued:
                    var qs = numbers.Session(queued.Session);
                    jobs[(qs, queued.Index)] = new JobTiming(queued.Start, queued.Seconds, now);
                    Say(Format($"{now,7:F2}  s{qs} live {queued.Index} queued ({queued.Start:F1}-{queued.Start + queued.Seconds:F1} s)"));
                    break;
                case LiveJobEvent.Started started:
                    var ss = numbers.Session(started.Session);
                    if (jobs.TryGetValue((ss, started.Index), out var s)) s.Started = now;
                    Say(Format($"{now,7:F2}  s{ss} live {started.Index} started"));
                    break;
                case LiveJobEvent.Finished finished:
                    var fs = numbers.Session(finished.Session);
                    if (jobs.TryGetValue((fs, finished.Index), out var f))
                    {
                        f.Finished = now;
                        f.Cues = finished.Cues;
                    }
                    Say(Format($"{now,7:F2}  s{fs} live {finished.Index} finished, {finished.Cues} cues{(finished.Error is { } e ? $", error: {e}" : "")}"));
                    break;
                case LiveJobEvent.Detection detection:
                    var line = Format($"{now,7:F2}  s{numbers.Session(detection.Session)} detection over {detection.Seconds:F1} s: ")
                        + (detection.Result is { } result ? TranscriptionEngine.DebugSummary(result) : "failed")
                        + (detection.Decision is { } decision ? "; settled: " + decision.DebugSummary() : "; not settled");
                    Say(line);
                    Print(line);
                    break;
            }
        };

        var failed = false;
        queue.EventObserver = queueEvent =>
        {
            var now = Now();
            string Label(string id) => $"queue job {numbers.Job(id)}";
            switch (queueEvent)
            {
                case QueueEvent.Queued queued:
                    Say(Format($"{now,7:F2}  {Label(queued.Id)} queued ({Path.GetFileName(queued.Recording)})"));
                    break;
                case QueueEvent.Running running:
                    Say(Format($"{now,7:F2}  {Label(running.Id)} running"));
                    break;
                case QueueEvent.Suspended suspended:
                    Say(Format($"{now,7:F2}  {Label(suspended.Id)} suspended at {suspended.Progress * 100:F0}%"));
                    break;
                case QueueEvent.Resumed resumed:
                    Say(Format($"{now,7:F2}  {Label(resumed.Id)} resumed"));
                    break;
                case QueueEvent.Held held:
                    Say(Format($"{now,7:F2}  {Label(held.Id)} held"));
                    break;
                case QueueEvent.Released released:
                    Say(Format($"{now,7:F2}  {Label(released.Id)} released"));
                    break;
                case QueueEvent.Trashed trashed:
                    Say(Format($"{now,7:F2}  {Label(trashed.Id)} trashed: {trashed.Recording} -> {trashed.TrashedAs ?? "(unknown)"}"));
                    break;
                case QueueEvent.Language languageEvent:
                    var line = Format($"{now,7:F2}  {Label(languageEvent.Id)} language: {languageEvent.Decision.DebugSummary()}");
                    Say(line);
                    Print(line);
                    break;
                case QueueEvent.Done done:
                    Say(Format($"{now,7:F2}  {Label(done.Id)} done: {done.Srt}"));
                    break;
                case QueueEvent.Failed fail:
                    failed = true;
                    Say(Format($"{now,7:F2}  {Label(fail.Id)} failed: {fail.Message}"));
                    break;
            }
        };

        double? stopPressedAt = null;
        var gaps = new List<double>();
        controller.SessionStartObserver = wav =>
        {
            var now = Now();
            if (stopPressedAt is { } pressed)
            {
                gaps.Add(now - pressed);
                Say(Format($"{now,7:F2}  next session recording ({Path.GetFileName(wav)}), {now - pressed:F3} s after Stop & Start Next"));
                stopPressedAt = null;
            }
            else
            {
                Say(Format($"{now,7:F2}  session recording ({Path.GetFileName(wav)})"));
            }
        };

        if (environment.TryGetValue("HEARSAY_REPLAY_UI", out var ui) && ui == "1")
        {
            shell.Tabs.Tab = MainTab.Record;
            shell.MainWindow.Activate();
            Say("showing the Record tab");
        }
        var names = string.Join(", ", files.Select((file, i) => Format($"{Path.GetFileName(file)} ({loaded[i].Length / 16_000.0:F2} s)")));
        Say(Format($"replaying {names}, language {language.StorageValue}, preferred {settings.PreferredLanguage.Code()}, timing {timing.StorageValue()}{(releaseAfter is { } r ? Format($", release {r:F1} s after the last Stop") : "")}, system audio {(silentSystem ? "silence" : "off")}, live preview {livePreview.StorageValue()}, model {Path.GetFileName(modelPath)}"));
        clock.Restart();
        controller.Start();
        using var statusTimer = new CancellationTokenSource();
        var statusTask = ReportStatusAsync(controller, queue, Now, statusTimer.Token);

        double stopAt = 0;
        while (await exhausted.Reader.WaitToReadAsync().ConfigureAwait(true))
        {
            if (!exhausted.Reader.TryRead(out var index)) continue;
            if (index + 1 < loaded.Count)
            {
                stopPressedAt = Now();
                Say(Format($"{Now(),7:F2}  file {index + 1} used up; Stop & Start Next with {controller.LiveChunksWaiting} live chunks waiting"));
                controller.StopAndStartNext();
            }
            else
            {
                stopAt = Now();
                Say(Format($"{stopAt,7:F2}  file {index + 1} used up; Stop with {controller.LiveChunksWaiting} live chunks waiting"));
                await controller.StopAsync().ConfigureAwait(true);
                break;
            }
        }
        if (trashFirst && !TrashFirstHeldJob(queue, spool, numbers, Now)) failed = true;
        if (releaseAfter is { } delay)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(true);
            Say(Format($"{Now(),7:F2}  Transcribe All: releasing {queue.HeldCount} held job(s) of {queue.PendingCount} pending"));
            queue.ReleaseAll();
        }
        // Held jobs (manual) wait for the user: they do not keep the run open.
        while (controller.IsSessionActive || queue.PendingCount > queue.HeldCount || queue.HasWorkInFlight || queue.Jobs.Any(job => job.HasLiveTail))
        {
            await Task.Delay(100).ConfigureAwait(true);
        }
        await statusTimer.CancelAsync().ConfigureAwait(true);
        await statusTask.ConfigureAwait(true);
        var doneAt = Now();

        Say("sess live  audio (s)        len   queued  started finished  latency  cues");
        static string Time(double? t) => t is { } v ? v.ToString("F2", CultureInfo.InvariantCulture).PadLeft(8) : "       -";
        foreach (var ((session, index), job) in jobs)
        {
            var latency = job.Finished is { } done ? (done - job.Queued).ToString("F2", CultureInfo.InvariantCulture).PadLeft(8) : "       -";
            Say(Format($"s{session,-3} {index,4}  {job.Start,6:F1}-{job.Start + job.Seconds,6:F1}  {job.Seconds,5:F1} {Time(job.Queued)} {Time(job.Started)} {Time(job.Finished)} {latency}  {job.Cues,4}"));
        }
        var latencies = jobs.Values.Where(j => j.Finished is not null).Select(j => (j.Finished ?? 0) - j.Queued).ToList();
        Say(Format($"totals: {jobs.Count} live jobs, max latency {(latencies.Count == 0 ? 0 : latencies.Max()):F2} s, queue empty {doneAt - stopAt:F2} s after the last Stop{(gaps.Count == 0 ? "" : ", session gaps " + string.Join(", ", gaps.Select(g => Format($"{g:F3} s"))))}"));

        foreach (var job in queue.Jobs)
        {
            // The job's number from the events (a trashed job leaves a gap).
            var number = numbers.Job(job.Id);
            Print($"queue job {number} decision: " + (job.Tracker.Decision?.DebugSummary() ?? "none"));
            if (job.LanguageNotice is { } notice) Print($"queue job {number} notice: " + notice.Message);
            switch (job.State)
            {
                case TranscriptionJobState.Done:
                    var text = job.Srt is { } srt && File.Exists(srt) ? await File.ReadAllTextAsync(srt).ConfigureAwait(true) : "";
                    var first = Srt.Parse(text).Where(cue => cue.Text.Length > 0).Select(cue => cue.Text).FirstOrDefault() ?? "";
                    Say($"queue job {number} final pass: {text.Split(" --> ").Length - 1} cues, first: {first}");
                    Print($"--- srt (queue job {number}) ---");
                    Print(text.TrimEnd('\n'));
                    Print("--- end ---");
                    break;
                case TranscriptionJobState.Failed:
                    Say($"queue job {number} failed: {job.ErrorMessage}");
                    failed = true;
                    break;
                default:
                    if (queue.IsHeld(job))
                    {
                        Say($"queue job {number} held, not transcribed yet ({job.State.StorageValue()}); {job.Recording.Path}");
                        break;
                    }
                    Say($"queue job {number} ended in {job.State.StorageValue()}");
                    failed = true;
                    break;
            }
        }
        if (controller.Phase is ControllerPhase.Failed failure)
        {
            Say($"session failed: {failure.Message}");
            failed = true;
        }
        controller.LiveJobObserver = null;
        controller.SessionStartObserver = null;
        queue.EventObserver = null;
        // Everything was written below the throwaway folder; remove anything that was not.
        foreach (var path in queue.Jobs.SelectMany(job => new[] { job.Srt, job.Wav }).OfType<string>())
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                Say($"removing {path}");
                File.Delete(path);
            }
        }
        return failed ? 1 : 0;
    }

    /// <summary>
    /// HEARSAY_REPLAY_TRASH: "Move to Recycle Bin…" on the first held job. Refuses unless its WAV
    /// is in the replay's spool inside the temporary folder; afterwards the WAV must be gone from
    /// there, and its copy in the Recycle Bin (written by this run) is removed again, only that
    /// item: <see cref="OutputWriter.PurgeFromRecycleBin"/> checks the bin's record of the item
    /// names the WAV's original path. Returns false when anything is wrong.
    /// </summary>
    private static bool TrashFirstHeldJob(TranscriptionQueue queue, RecordingSpool spool, ReplayNumbers numbers, Func<double> now)
    {
        var job = queue.Jobs.FirstOrDefault(queue.IsHeld);
        if (job is null)
        {
            Say("trash: no held job");
            return false;
        }
        static string WithSeparator(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var temporary = WithSeparator(Path.GetTempPath());
        var spoolPath = WithSeparator(spool.Root);
        var wav = Path.GetFullPath(job.Recording.Path);
        if (!spoolPath.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
            || !wav.StartsWith(spoolPath, StringComparison.OrdinalIgnoreCase))
        {
            Say($"trash: refusing, {wav} is not in a spool inside {temporary}");
            return false;
        }
        Say(Format($"{now(),7:F2}  Move to Recycle Bin: queue job {numbers.Job(job.Id)} ({Path.GetFileName(wav)}), spool {spoolPath}"));
        string? trashedAs = null;
        var previous = queue.EventObserver;
        queue.EventObserver = queueEvent =>
        {
            if (queueEvent is QueueEvent.Trashed trashed) trashedAs = trashed.TrashedAs;
            previous?.Invoke(queueEvent);
        };
        queue.Trash(job);
        queue.EventObserver = previous;
        if (job.TrashError is { } error)
        {
            Say($"trash: failed: {error}");
            return false;
        }
        if (queue.Jobs.Contains(job) || File.Exists(wav))
        {
            Say("trash: the job or its WAV is still there");
            return false;
        }
        var manifest = new TranscriptionQueueStore(spool).ManifestPath;
        Say($"trash: {Path.GetFileName(wav)} left the spool; {queue.Jobs.Count} job(s) left, queue.json {(File.Exists(manifest) ? "kept" : "removed")}");
        // The copy in the Recycle Bin is a temporary file this run wrote.
        if (trashedAs is null)
        {
            Say("trash: the Recycle Bin gave no handle for the recycled copy; remove it from the Recycle Bin by hand: " + wav);
            return false;
        }
        try
        {
            Say($"removing the recycled copy {trashedAs}");
            OutputWriter.PurgeFromRecycleBin(trashedAs, wav);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Say($"trash: could not remove the recycled copy ({exception.Message}); remove {Path.GetFileName(wav)} from the Recycle Bin by hand");
            return false;
        }
        return true;
    }

    private static async Task ReportStatusAsync(RecordingController controller, TranscriptionQueue queue, Func<double> now, CancellationToken token)
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
            var states = string.Join(", ", queue.Jobs.Select(job => Format($"{job.State.StorageValue()} {Math.Round(job.Progress * 100):F0}%")));
            Say(Format($"{now(),7:F2}  status: recorded {controller.Elapsed:F1} s, live waiting {controller.LiveChunksWaiting}, live cues {controller.LiveSegments.Count}, queue [{states}]"));
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
