using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Notes;
using Hearsay.Core.Audio;
using Hearsay.Core.History;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.App.Features.Transcription;

/// <summary>A finished SRT for the notes flow (PLAN.md 4.3 step 1) and the language it was written in.</summary>
/// <param name="Srt">The transcript.</param>
/// <param name="Language">The session language.</param>
/// <param name="JobId">The queue job it belongs to, or null (File tab).</param>
internal sealed record NotesRequest(string Srt, TranscriptLanguage Language, string? JobId = null);

/// <summary>
/// What a recording session hands to the queue when it ends normally
/// (PLAN.md 4.9 item 1). Port of <c>RecordingHandover</c> in
/// mac/Hearsay/Features/Transcription/TranscriptionQueue.swift.
/// </summary>
/// <param name="Recording">The closed spool WAV.</param>
/// <param name="StopTime">When the session stopped.</param>
/// <param name="Tracker">The session's language tracker (the queue owns it from now on).</param>
/// <param name="ChineseScript">The script the cues are converted to (zh only).</param>
/// <param name="KeepRecording">"Keep the recording" as it was at Stop.</param>
/// <param name="LiveSegments">The live preview so far.</param>
/// <param name="LiveEnabled">The session transcribed live chunks.</param>
/// <param name="LiveSink">The session's live sink with the tail still in flight, or null.</param>
/// <param name="LanguageNotice">The suggestion or fallback banner so far.</param>
/// <param name="LiveNotice">Why the live preview is off or incomplete.</param>
internal sealed record RecordingHandover(
    string Recording,
    DateTimeOffset StopTime,
    SessionLanguageTracker Tracker,
    ChineseScript? ChineseScript,
    bool KeepRecording,
    IReadOnlyList<TranscriptSegment> LiveSegments,
    bool LiveEnabled,
    LiveSink? LiveSink,
    LanguageNotice? LanguageNotice,
    string? LiveNotice);

/// <summary>A queue event, for the debug replay (<see cref="TranscriptionQueue.EventObserver"/>).</summary>
internal abstract record QueueEvent
{
    private QueueEvent()
    {
    }

    public sealed record Queued(string Id, string Recording) : QueueEvent;

    public sealed record Running(string Id) : QueueEvent;

    public sealed record Suspended(string Id, double Progress) : QueueEvent;

    public sealed record Resumed(string Id) : QueueEvent;

    /// <summary>Manual timing (PLAN.md 4.11): the job is held (queued unreleased, put on hold, or held by a switch to Manual).</summary>
    public sealed record Held(string Id) : QueueEvent;

    /// <summary>Manual timing: the user released the job (Transcribe, Transcribe All, Try Again, or a switch to Manual for a job that had started).</summary>
    public sealed record Released(string Id) : QueueEvent;

    public sealed record Language(string Id, LanguageDecision Decision) : QueueEvent;

    public sealed record Done(string Id, string Srt) : QueueEvent;

    public sealed record Failed(string Id, string Message) : QueueEvent;
}

/// <summary>
/// One recording in the background transcription queue (PLAN.md 4.9): what
/// the session owned after Stop, plus the final pass's progress and result.
/// Port of <c>TranscriptionJob</c> in TranscriptionQueue.swift. Every
/// property raises <see cref="PropertyChanged"/> on the UI thread only;
/// only <see cref="TranscriptionQueue"/> changes them (the setters are
/// internal). <see cref="DisplayName"/> is the one thing a view may set.
/// </summary>
internal sealed class TranscriptionJob : INotifyPropertyChanged
{
    private TranscriptionJobState state;
    private double progress;
    private TranscriptOutput.PendingRecording recording;
    private string? displayName;
    private List<TranscriptSegment> liveSegments;
    private bool liveEnabled;
    private string? liveNotice;
    private LanguageNotice? languageNotice;
    private string? errorMessage;
    private bool needsModel;
    private string? srt;
    private string? wav;
    private bool isUsingLivePreview;
    private bool isRerunning;
    private double rerunProgress;
    private string? rerunError;
    private bool offersNotes;
    private bool isReleased;
    private bool isHeld;

    internal TranscriptionJob(
        string id, TranscriptOutput.PendingRecording recording, DateTimeOffset stopTime, SessionLanguageTracker tracker,
        ChineseScript? chineseScript, bool keepRecording, IEnumerable<TranscriptSegment> liveSegments, bool liveEnabled,
        TranscriptionJobState state = TranscriptionJobState.Waiting)
    {
        Id = id;
        this.recording = recording;
        StopTime = stopTime;
        Tracker = tracker;
        ChineseScript = chineseScript;
        KeepRecording = keepRecording;
        this.liveSegments = [.. liveSegments];
        this.liveEnabled = liveEnabled;
        this.state = state;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    /// <summary>When the session stopped.</summary>
    public DateTimeOffset StopTime { get; }

    /// <summary>The job's language tracker (Auto decides here when the session did not).</summary>
    public SessionLanguageTracker Tracker { get; internal set; }

    /// <summary>The script the cues are converted to (zh only).</summary>
    public ChineseScript? ChineseScript { get; internal set; }

    /// <summary>"Keep the recording" as it was at Stop.</summary>
    public bool KeepRecording { get; }

    /// <summary>
    /// Where the WAV is now: in the spool until the job is done, or in the
    /// output folder after a failure (a retry reads it there).
    /// </summary>
    public TranscriptOutput.PendingRecording Recording
    {
        get => recording;
        internal set => Set(ref recording, value, nameof(Recording), nameof(Title), nameof(Files), nameof(CanUseLivePreview));
    }

    /// <summary>The meeting name, when one was given; the row shows the time otherwise.</summary>
    public string? DisplayName
    {
        get => displayName;
        set => Set(ref displayName, value, nameof(DisplayName), nameof(Title));
    }

    public IReadOnlyList<TranscriptSegment> LiveSegments => liveSegments;

    public bool LiveEnabled
    {
        get => liveEnabled;
        internal set => Set(ref liveEnabled, value, nameof(LiveEnabled), nameof(CanUseLivePreview));
    }

    public string? LiveNotice
    {
        get => liveNotice;
        internal set => Set(ref liveNotice, value, nameof(LiveNotice));
    }

    public TranscriptionJobState State
    {
        get => state;
        internal set => Set(
            ref state, value, nameof(State), nameof(IsPending), nameof(IsDetectingLanguage), nameof(CanUseLivePreview),
            nameof(CanChangeLanguage), nameof(CanRetry), nameof(CanDismiss), nameof(DisplayProgress), nameof(LiveChunksWaiting));
    }

    /// <summary>Final-pass progress, 0...1.</summary>
    public double Progress
    {
        get => progress;
        internal set => Set(ref progress, value, nameof(Progress), nameof(DisplayProgress));
    }

    /// <summary>The suggestion or fallback banner of this recording.</summary>
    public LanguageNotice? LanguageNotice
    {
        get => languageNotice;
        internal set => Set(ref languageNotice, value, nameof(LanguageNotice));
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        internal set => Set(ref errorMessage, value, nameof(ErrorMessage));
    }

    /// <summary>The failure was the missing model; the view links to Models.</summary>
    public bool NeedsModel
    {
        get => needsModel;
        internal set => Set(ref needsModel, value, nameof(NeedsModel));
    }

    /// <summary>The transcript: the final SRT, or the live preview saved after a failure.</summary>
    public string? Srt
    {
        get => srt;
        internal set => Set(ref srt, value, nameof(Srt), nameof(Files), nameof(CanChangeLanguage));
    }

    /// <summary>The kept recording after the job ended (null when not kept).</summary>
    public string? Wav
    {
        get => wav;
        internal set => Set(ref wav, value, nameof(Wav), nameof(Files), nameof(CanChangeLanguage));
    }

    /// <summary>"Use live preview instead" was chosen; the live segments become the SRT.</summary>
    public bool IsUsingLivePreview
    {
        get => isUsingLivePreview;
        internal set => Set(ref isUsingLivePreview, value, nameof(IsUsingLivePreview), nameof(CanUseLivePreview), nameof(CanChangeLanguage));
    }

    /// <summary>A "Transcribe again" pass over the finished recording is running.</summary>
    public bool IsRerunning
    {
        get => isRerunning;
        internal set => Set(ref isRerunning, value, nameof(IsRerunning), nameof(CanChangeLanguage), nameof(CanDismiss), nameof(DisplayProgress));
    }

    public double RerunProgress
    {
        get => rerunProgress;
        internal set => Set(ref rerunProgress, value, nameof(RerunProgress), nameof(DisplayProgress));
    }

    public string? RerunError
    {
        get => rerunError;
        internal set => Set(ref rerunError, value, nameof(RerunError));
    }

    /// <summary>The job finished while notes could not open; its row offers them.</summary>
    public bool OffersNotes
    {
        get => offersNotes;
        internal set => Set(ref offersNotes, value, nameof(OffersNotes));
    }

    /// <summary>
    /// Manual timing (PLAN.md 4.11): the user released this job, so it may run
    /// (when no session is active and no foreground work waits). In memory
    /// only: <c>queue.json</c> does not store it, so every job restored at
    /// launch is unreleased. Read only while the timing is Manual.
    /// </summary>
    public bool IsReleased
    {
        get => isReleased;
        internal set => Set(ref isReleased, value, nameof(IsReleased));
    }

    /// <summary>
    /// Held (PLAN.md 4.11): the timing is Manual and this job is pending and not
    /// released. Kept up to date by the queue (on release, hold, state and timing
    /// changes); the row reads "Not transcribed yet" and offers Transcribe.
    /// </summary>
    public bool IsHeld
    {
        get => isHeld;
        internal set => Set(ref isHeld, value, nameof(IsHeld));
    }

    // Internal state of the queue's driver (the Mac's fileprivate / ObservationIgnored members).

    /// <summary>The session's live sink while its tail is still being transcribed.</summary>
    internal LiveSink? LiveSink { get; set; }

    internal TranscriptionCheckpoint? Checkpoint { get; set; }

    /// <summary>The samples, read from the WAV when the job first runs and kept while it is suspended.</summary>
    internal float[]? Samples { get; set; }

    /// <summary>Samples of a finished recording whose WAV was not kept, held while a language notice offers a re-run.</summary>
    internal float[]? RerunSamples { get; set; }

    internal Task? StepTask { get; set; }

    internal CancellationTokenSource? StepCancellation { get; set; }

    /// <summary>Settles an undecided Auto language right after Stop (see <c>TranscriptionQueue.SettleEarly</c>).</summary>
    internal Task? SettleTask { get; set; }

    internal CancellationTokenSource? SettleCancellation { get; set; }

    internal Task? FinishTask { get; set; }

    internal Task? RerunTask { get; set; }

    internal CancellationTokenSource? RerunCancellation { get; set; }

    internal bool LiveSegmentsDirty { get; set; }

    internal bool RerunCancelledByQuit { get; set; }

    /// <summary>The language of the transcript (null while Auto is undecided).</summary>
    public TranscriptLanguage? Language => Tracker.Language;

    /// <summary>Not transcribed yet: waiting, running, or suspended.</summary>
    public bool IsPending => state.IsPending();

    /// <summary>Auto has not decided the language yet.</summary>
    public bool IsDetectingLanguage => IsPending && Tracker.IsUndecided;

    /// <summary>Live chunks of the ended session still to be transcribed for the live preview (0 once the job ended).</summary>
    public int LiveChunksWaiting => IsPending ? LiveSink?.Waiting ?? 0 : 0;

    /// <summary>Live chunks of the ended session are still in flight, even after the job ended (the debug replay waits for them).</summary>
    public bool HasLiveTail => LiveSink?.HasPendingChunks ?? false;

    /// <summary>"Use live preview instead" applies.</summary>
    public bool CanUseLivePreview => IsPending && liveEnabled && !isUsingLivePreview && recording.InSpool;

    /// <summary>
    /// The language notice's buttons apply: before the pass starts (it will
    /// use the picked language), or after it finished while the audio is
    /// still available.
    /// </summary>
    public bool CanChangeLanguage => state switch
    {
        TranscriptionJobState.Waiting => !isUsingLivePreview,
        TranscriptionJobState.Done => srt is not null && (wav is not null || RerunSamples is not null) && !isRerunning,
        _ => false,
    };

    public bool CanRetry => state == TranscriptionJobState.Failed;

    public bool CanDismiss => state is TranscriptionJobState.Done or TranscriptionJobState.Failed && !isRerunning;

    /// <summary>Progress to show: the final pass while pending, a re-run while it runs; null otherwise.</summary>
    public double? DisplayProgress => isRerunning ? rerunProgress : IsPending ? progress : null;

    /// <summary>The files to reveal in Explorer.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            var files = new[] { srt, wav }.OfType<string>().ToList();
            if (files.Count == 0) files.Add(recording.Path);
            return files;
        }
    }

    /// <summary>The name shown in the queue: the meeting name, else when the recording started (from its timestamp name), else when it stopped.</summary>
    public string Title
    {
        get
        {
            if (!string.IsNullOrEmpty(displayName)) return displayName;
            var stem = Path.GetFileNameWithoutExtension(recording.Path);
            var date = HistoryIndex.TimestampDate(stem) ?? StopTime;
            return date.ToString("g", CultureInfo.CurrentCulture);
        }
    }

    internal void AppendLive(IEnumerable<TranscriptSegment> cues)
    {
        liveSegments.AddRange(cues);
        Notify(nameof(LiveSegments));
    }

    internal void ClearLive()
    {
        liveSegments = [];
        Notify(nameof(LiveSegments));
    }

    /// <summary>The live sink's waiting count changed.</summary>
    internal void NotifyLiveWaiting() => Notify(nameof(LiveChunksWaiting), nameof(HasLiveTail));

    /// <summary>Raises <see cref="PropertyChanged"/> for each name (everything when none is given).</summary>
    internal void Notify(params string[] names)
    {
        if (names.Length == 0)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            return;
        }
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(names);
    }
}

/// <summary>
/// The background transcription queue (PLAN.md 4.9, 18.10): recordings whose
/// session ended wait here for their final pass, first in first out, one at a
/// time, on the one loaded model. Owned by <c>AppShell</c> next to
/// <c>RecordingController</c>. Port of <c>TranscriptionQueue</c> in
/// mac/Hearsay/Features/Transcription/TranscriptionQueue.swift.
/// <para>
/// The driver re-evaluates <see cref="TranscriptionQueuePolicy.Next"/> whenever
/// a job is added, a step returns, the session-active flag or the timing
/// changes, and every <see cref="PollInterval"/> while a job waits or is
/// suspended (the engine's foreground counter is not observable). A step runs
/// through <see cref="IQueueEngine.TranscribeStepAsync"/>, which suspends at
/// the next 30 s window while foreground work waits or the hold flag is set
/// (whenIdle or Manual with a session active, or Manual and the running job
/// not released). The queue checks
/// <see cref="TranscriptionQueuePolicy.MayRun"/> again just before each step,
/// because a step decodes one window before it can yield.
/// </para>
/// <para>
/// Completion and failure follow PLAN.md 4.1 (<see cref="TranscriptOutput.SaveTranscript"/>
/// and <see cref="TranscriptOutput.KeepAfterFailure"/>). The queue is saved to
/// <c>&lt;spool&gt;\queue.json</c> on every change and each job's live segments
/// to <c>&lt;spool&gt;\&lt;id&gt;.live.srt</c> at most once per second;
/// <see cref="Restore"/> queues the saved jobs again at launch.
/// </para>
/// <para>
/// Windows: the Mac's <c>@MainActor @Observable</c> becomes "UI thread only":
/// the queue and its jobs raise <see cref="INotifyPropertyChanged"/> events
/// there, never from a pool thread, and the driver's continuations return to
/// the UI context (<c>ConfigureAwait(true)</c>). The engine work runs on the
/// pool; <c>shouldYield</c> runs on the engine's thread and reads only the
/// lock-free foreground counter and a volatile flag. For WI-4 (the queue
/// list, tray, Settings): bind to <see cref="Jobs"/> (an observable
/// collection), each job's <see cref="TranscriptionJob.PropertyChanged"/>,
/// <see cref="PropertyChanged"/> (PendingCount, ActiveJob, FeaturedJob,
/// IsSessionActive, IsHeldForSession, HeldCount, AllPendingHeld) or the
/// catch-all <see cref="Changed"/>, and call the row commands below.
/// </para>
/// <para>
/// Manual timing (PLAN.md 4.11, 18.12): every job has an in-memory
/// <see cref="TranscriptionJob.IsReleased"/> flag, read only while the timing
/// is Manual. A pending, unreleased job is held (<see cref="IsHeld"/>); the
/// driver never starts or resumes it. <see cref="Release"/>, <see cref="ReleaseAll"/>
/// and <see cref="Hold"/> change the flag; "Try Again" queues the job
/// released; a switch to Manual releases started jobs and holds waiting ones.
/// </para>
/// </summary>
internal sealed class TranscriptionQueue : INotifyPropertyChanged, IDisposable
{
    /// <summary>The default poll interval (the Mac polls every 250 ms).</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppSettings settings;
    private readonly IQueueEngine engine;
    private readonly Func<string, CancellationToken, float[]> loadSamples;
    private readonly bool drives;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan liveWriteDelay;
    private readonly SynchronizationContext? context;
    private readonly ObservableCollection<TranscriptionJob> jobs = [];
    private RecordingSpool spool;
    private TranscriptionQueueStore store;
    private Func<string> modelLocation;
    private volatile bool held;
    private FinalPassTiming lastTiming;
    private bool isSessionActive;
    private bool isHeldForSession;
    private NotesRequest? notesRequest;
    private bool polling;
    private CancellationTokenSource? pollCancellation;
    private bool liveWriteScheduled;
    private bool isQuitting;
    private bool showsSamples;

    /// <param name="settings">The app's settings (final-pass timing, output folder).</param>
    /// <param name="engine">The Whisper engine (a fake in tests).</param>
    /// <param name="spool">Where <c>queue.json</c> and the live segments live, and the recordings wait.</param>
    /// <param name="modelLocation">The model file of the moment; throws <see cref="WhisperEngineException"/> when none is installed.</param>
    /// <param name="drives">False: the queue only holds jobs (UI snapshots).</param>
    /// <param name="loadSamples">Reads a WAV as 16 kHz mono samples; null uses <see cref="AudioFileLoader.LoadMono16k"/>.</param>
    /// <param name="pollInterval">Null uses <see cref="DefaultPollInterval"/>.</param>
    /// <param name="liveWriteDelay">How long live-segment changes are collected before <c>&lt;id&gt;.live.srt</c> is written; null is one second.</param>
    public TranscriptionQueue(
        AppSettings settings, IQueueEngine engine, RecordingSpool spool, Func<string> modelLocation, bool drives = true,
        Func<string, CancellationToken, float[]>? loadSamples = null, TimeSpan? pollInterval = null, TimeSpan? liveWriteDelay = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(modelLocation);
        this.settings = settings;
        this.engine = engine;
        this.spool = spool;
        store = new TranscriptionQueueStore(spool);
        this.modelLocation = modelLocation;
        this.drives = drives;
        this.loadSamples = loadSamples ?? ((path, token) => AudioFileLoader.LoadMono16k(path, token));
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        this.liveWriteDelay = liveWriteDelay ?? TimeSpan.FromSeconds(1);
        context = SynchronizationContext.Current;
        Jobs = new ReadOnlyObservableCollection<TranscriptionJob>(jobs);
        jobs.CollectionChanged += OnJobsChanged;
        lastTiming = settings.FinalPassTiming;
        UpdateHold();
        settings.PropertyChanged += OnSettingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Anything in the queue changed: its jobs, one job's property, or the session and hold flags. For views that just re-render.</summary>
    public event EventHandler? Changed;

    /// <summary>A finished SRT is waiting for the notes flow; see <see cref="TakeNotesRequest"/>.</summary>
    public event EventHandler? NotesRequested;

    /// <summary>Debug only: sees every queue event (see <c>RecordingReplay</c>).</summary>
    public Action<QueueEvent>? EventObserver { get; set; }

    /// <summary>Whether a notes sheet or another notes flow is on screen (PLAN.md 4.9 item 4).</summary>
    public Func<bool> NotesOnScreen { get; set; } = () => NotesFlowViewModel.IsAnyRunning;

    /// <summary>The jobs in queue order, done and failed ones included until dismissed.</summary>
    public ReadOnlyObservableCollection<TranscriptionJob> Jobs { get; }

    /// <summary>A session is active (Starting, Recording, Paused, Stopping); set by the recording controller.</summary>
    public bool IsSessionActive => isSessionActive;

    /// <summary>whenIdle or Manual, and a session is active: released jobs do not run.</summary>
    public bool IsHeldForSession => isHeldForSession;

    /// <summary>The finished SRT waiting for the notes flow (taken with <see cref="TakeNotesRequest"/>).</summary>
    public NotesRequest? PendingNotesRequest => notesRequest;

    // MARK: - State

    /// <summary>
    /// The jobs as the policy reads them. A job saving its live preview is
    /// left out once its step returned, so the next job can start.
    /// </summary>
    private List<TranscriptionQueuePolicy.Job> PolicyJobs()
    {
        var list = new List<TranscriptionQueuePolicy.Job>();
        foreach (var job in jobs)
        {
            if (job is { IsUsingLivePreview: true, IsPending: true })
            {
                // The user chose the live preview for this job: its save is never held.
                if (job.StepTask is not null) list.Add(new(job.Id, TranscriptionJobState.Running, Released: true));
                continue;
            }
            list.Add(new(job.Id, job.State, job.IsReleased));
        }
        return list;
    }

    private List<TranscriptionQueuePolicy.Job> StateJobs() =>
        [.. jobs.Select(job => new TranscriptionQueuePolicy.Job(job.Id, job.State, job.IsReleased))];

    /// <summary>Recordings not transcribed yet, held ones included; Quit asks when above 0.</summary>
    public int PendingCount => TranscriptionQueuePolicy.QuitNeedsConfirmation(StateJobs());

    /// <summary>
    /// An update install must wait. Held jobs (Manual) do not block: they are in
    /// <c>queue.json</c> and come back held after the relaunch.
    /// </summary>
    public bool BlocksUpdateInstall => TranscriptionQueuePolicy.BlocksUpdateInstall(settings.FinalPassTiming, StateJobs());

    /// <summary>The job is held: the timing is Manual and it is pending and not released (PLAN.md 4.11).</summary>
    public bool IsHeld(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return TranscriptionQueuePolicy.IsHeld(settings.FinalPassTiming, new(job.Id, job.State, job.IsReleased));
    }

    /// <summary>How many jobs are held; "Transcribe All" shows while this is above 0.</summary>
    public int HeldCount => jobs.Count(IsHeld);

    /// <summary>
    /// Every pending job is held, and there is at least one: the quit alert reads
    /// "They stay in the queue until you transcribe them." and the tray line reads
    /// "Not transcribed yet · in queue: N". False unless the timing is Manual.
    /// </summary>
    public bool AllPendingHeld => TranscriptionQueuePolicy.AllPendingHeld(settings.FinalPassTiming, StateJobs());

    /// <summary>At least one job waits for a session to end (a released job under whenIdle or Manual; held jobs do not count).</summary>
    public bool HasJobPausedForSession => jobs.Any(IsPausedForSession);

    /// <summary>
    /// The job being transcribed or suspended, for the tray. A suspended job
    /// that is held (Manual, put on hold) is not active: it waits for the user.
    /// </summary>
    public TranscriptionJob? ActiveJob =>
        jobs.FirstOrDefault(job => job.State == TranscriptionJobState.Running
            || (job.State == TranscriptionJobState.Suspended && !IsHeld(job)));

    /// <summary>
    /// With no session active and exactly one job, the Record tab shows it as
    /// the single meeting it always showed (PLAN.md 4.9 item 5).
    /// </summary>
    public TranscriptionJob? FeaturedJob => !isSessionActive && jobs.Count == 1 ? jobs[0] : null;

    /// <summary>
    /// Output-folder files the queue is working on: History does not rename
    /// them (a retry reads its WAV there, a re-run rewrites its SRT).
    /// </summary>
    public IReadOnlyList<string> BusyFiles
    {
        get
        {
            var files = new List<string>();
            foreach (var job in jobs.Where(job => job.IsPending || job.IsRerunning))
            {
                files.AddRange(new[] { job.Srt, job.Wav }.OfType<string>());
                if (!job.Recording.InSpool) files.Add(job.Recording.Path);
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The stems of <see cref="BusyFiles"/> (the Mac's <c>busyStems</c>).</summary>
    public IReadOnlySet<string> BusyStems =>
        BusyFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A job waits or is suspended because a session runs in whenIdle or Manual;
    /// its row says "Paused while recording". A held job (Manual, not released)
    /// is not paused for the session: it waits for the user.
    /// </summary>
    public bool IsPausedForSession(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return isHeldForSession && job.State is TranscriptionJobState.Suspended or TranscriptionJobState.Waiting
            && !job.IsUsingLivePreview && !IsHeld(job);
    }

    /// <summary>
    /// The finished SRT waiting for the notes flow, once. The request is
    /// checked again when it is taken (the window may take it only later,
    /// during a session or while another notes flow runs); when the notes may
    /// not open now, its job's row offers "Generate Notes…" instead.
    /// </summary>
    public NotesRequest? TakeNotesRequest()
    {
        if (notesRequest is not { } request) return null;
        notesRequest = null;
        if (!TranscriptionQueuePolicy.PresentsNotes(isSessionActive, NotesOnScreen()))
        {
            OfferNotesOnRow(request);
            return null;
        }
        return request;
    }

    private void OfferNotesOnRow(NotesRequest request)
    {
        if (jobs.FirstOrDefault(job => job.Id == request.JobId) is { } job) job.OffersNotes = true;
    }

    // MARK: - Session and timing

    /// <summary>The recording controller's session-active flag changed.</summary>
    public void SetSessionActive(bool active)
    {
        if (active == isSessionActive) return;
        isSessionActive = active;
        // A notes request nobody took before the session started must not open during it.
        if (active && notesRequest is { } request)
        {
            notesRequest = null;
            OfferNotesOnRow(request);
        }
        UpdateHold();
        RaiseQueueChanged(nameof(IsSessionActive), nameof(FeaturedJob));
        Evaluate();
    }

    /// <summary>
    /// Recomputes the volatile flag the decoder's <c>shouldYield</c> reads
    /// (whenIdle or Manual with a session active; Manual with a running job that
    /// is not released, i.e. a Hold) and <see cref="IsHeldForSession"/>.
    /// </summary>
    private void UpdateHold()
    {
        var timing = settings.FinalPassTiming;
        var sessionHold = timing is FinalPassTiming.WhenIdle or FinalPassTiming.Manual && isSessionActive;
        var holdRunning = timing == FinalPassTiming.Manual
            && jobs.Any(job => job.State == TranscriptionJobState.Running && !job.IsReleased && !job.IsUsingLivePreview);
        held = sessionHold || holdRunning;
        if (sessionHold == isHeldForSession) return;
        isHeldForSession = sessionHold;
        RaiseQueueChanged(nameof(IsHeldForSession));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.FinalPassTiming)) return;
        Post(() =>
        {
            ApplyTimingChange();
            UpdateHold();
            Evaluate();
        });
    }

    /// <summary>
    /// PLAN.md 4.11: switching to Manual releases the jobs that already started
    /// (running or suspended) and holds the waiting ones; switching away ignores
    /// the flags, and <see cref="Evaluate"/> applies the other timing's rules at once.
    /// </summary>
    private void ApplyTimingChange()
    {
        var timing = settings.FinalPassTiming;
        var previous = lastTiming;
        lastTiming = timing;
        if (timing == FinalPassTiming.Manual && previous != FinalPassTiming.Manual)
        {
            foreach (var job in jobs.Where(job => job.IsPending))
            {
                var started = job.State is TranscriptionJobState.Running or TranscriptionJobState.Suspended;
                var changes = job.IsReleased != started;
                SetReleased(job, started);
                // A waiting job that was never released becomes held without a flag change: announce it too.
                if (!started && !changes) EventObserver?.Invoke(new QueueEvent.Held(job.Id));
            }
        }
        foreach (var job in jobs) SyncHeld(job);
        RaiseQueueChanged(nameof(HeldCount), nameof(AllPendingHeld));
    }

    /// <summary>Keeps <see cref="TranscriptionJob.IsHeld"/> equal to <see cref="IsHeld"/>.</summary>
    private void SyncHeld(TranscriptionJob job) => job.IsHeld = IsHeld(job);

    /// <summary>
    /// Sets the released flag. Under Manual a change is announced to the observer
    /// (the debug replay) as a Held or Released event; under the other timings
    /// the flag has no visible meaning and nothing is announced.
    /// </summary>
    private void SetReleased(TranscriptionJob job, bool value)
    {
        if (job.IsReleased == value) return;
        job.IsReleased = value;
        if (settings.FinalPassTiming != FinalPassTiming.Manual) return;
        EventObserver?.Invoke(value ? new QueueEvent.Released(job.Id) : new QueueEvent.Held(job.Id));
        AppLog.Write($"queue: {(value ? "released" : "held")} {job.Id}");
    }

    // MARK: - Adding jobs

    /// <summary>A session ended normally: it becomes a waiting job.</summary>
    public void Enqueue(RecordingHandover handover)
    {
        ArgumentNullException.ThrowIfNull(handover);
        var job = new TranscriptionJob(
            Guid.NewGuid().ToString("D"), new TranscriptOutput.PendingRecording(handover.Recording, InSpool: true),
            handover.StopTime, handover.Tracker, handover.ChineseScript, handover.KeepRecording, handover.LiveSegments,
            handover.LiveEnabled)
        {
            LanguageNotice = handover.LanguageNotice,
            LiveNotice = handover.LiveNotice,
        };
        Adopt(handover.LiveSink, job);
        Add(job);
        SettleEarly(job);
    }

    /// <summary>
    /// Auto was still undecided at Stop: detect over the whole recording now,
    /// as foreground work like the detection during the session and whatever
    /// the timing, so the live tail (which waits for the language) finishes
    /// and the row shows the language. The samples are read for this and
    /// dropped again; the pass reads them when it runs.
    /// </summary>
    private void SettleEarly(TranscriptionJob job)
    {
        if (!drives || job.Tracker.Language is not null) return;
        var cancellation = new CancellationTokenSource();
        job.SettleCancellation = cancellation;
        job.SettleTask = SettleEarlyAsync(job, cancellation);
    }

    private async Task SettleEarlyAsync(TranscriptionJob job, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            var token = cancellation.Token;
            if (token.IsCancellationRequested || isQuitting) return;
            string location;
            try
            {
                location = modelLocation();
            }
            catch (WhisperEngineException)
            {
                return;
            }
            float[] samples;
            try
            {
                var path = job.Recording.Path;
                samples = await Task.Run(() => loadSamples(path, token), CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return;
            }
            if (!job.IsPending || token.IsCancellationRequested) return;
            await SettleLanguageAsync(job, samples, location, token).ConfigureAwait(true);
        }
        finally
        {
            job.SettleTask = null;
            job.SettleCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>"Try Again" after a capture failure: the kept WAV (normally in the output folder, next to the saved live preview) becomes a job.</summary>
    public void EnqueueRetry(
        TranscriptOutput.PendingRecording recording, SessionLanguageTracker tracker, ChineseScript? chineseScript,
        bool keepRecording, IReadOnlyList<TranscriptSegment> liveSegments, string? transcript)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(tracker);
        var job = new TranscriptionJob(
            Guid.NewGuid().ToString("D"), recording, DateTimeOffset.Now, tracker, chineseScript, keepRecording, liveSegments,
            liveEnabled: false)
        {
            Srt = transcript,
            // The user asked for this pass (PLAN.md 4.11): not held under Manual.
            IsReleased = true,
        };
        Add(job);
    }

    private void Add(TranscriptionJob job)
    {
        jobs.Add(job);
        job.PropertyChanged += OnJobPropertyChanged;
        SyncHeld(job);
        EventObserver?.Invoke(new QueueEvent.Queued(job.Id, job.Recording.Path));
        if (job.IsHeld) EventObserver?.Invoke(new QueueEvent.Held(job.Id));
        AppLog.Write($"queue: queued {job.Id} {Path.GetFileName(job.Recording.Path)}");
        if (job.LiveSegments.Count > 0) MarkLiveSegmentsChanged(job);
        Persist();
        Evaluate();
    }

    /// <summary>The ended session's live tail now goes to <paramref name="job"/>.</summary>
    private void Adopt(LiveSink? sink, TranscriptionJob job)
    {
        if (sink is null) return;
        job.LiveSink = sink;
        sink.OnResult = (_, outcome) =>
        {
            if (!job.IsPending) return;
            if (outcome.Cues is { } cues)
            {
                job.AppendLive(cues);
                MarkLiveSegmentsChanged(job);
            }
            else if (outcome.Error is { } error)
            {
                job.LiveNotice = Strings.LivePreviewMissedChunk(TranscriptionEngine.Describe(error));
            }
        };
        sink.Changed += (_, _) => job.NotifyLiveWaiting();
        if (job.Tracker.Language is { } language) sink.SetLanguage(language);
    }

    /// <summary>
    /// Queues the jobs saved in <c>queue.json</c> again (PLAN.md 4.9). Jobs
    /// that were done or failed are removed; running and suspended ones start
    /// over.
    /// </summary>
    public void Restore()
    {
        var result = store.Load();
        if (result.Error is { } error) AppLog.Write($"queue: {error}");
        foreach (var dropped in result.Dropped)
        {
            AppLog.Write($"queue: dropped job {dropped.Id ?? "?"} ({dropped.WavFileName ?? "?"}): {dropped.Reason}");
            if (dropped.Id is { } id) store.RemoveLiveSegments(id);
        }
        foreach (var entry in result.Manifest.Jobs)
        {
            if (!entry.State.IsPending())
            {
                store.RemoveLiveSegments(entry.Id);
                continue;
            }
            var tracker = new SessionLanguageTracker(entry.LanguageChoice, settings.PreferredLanguage);
            if (entry.SettledLanguage is { } settled) tracker.Choose(settled);
            var live = store.ReadLiveSegments(entry.Id);
            var job = new TranscriptionJob(
                entry.Id, new TranscriptOutput.PendingRecording(Path.Combine(spool.Root, entry.WavFileName), InSpool: true),
                entry.StopTime, tracker, entry.ChineseScript ?? tracker.Language?.ChineseScript(), entry.KeepRecording, live,
                liveEnabled: live.Count > 0)
            {
                DisplayName = entry.DisplayName,
            };
            // Not released: with Manual the app never starts a pass by itself after a relaunch (PLAN.md 4.11).
            jobs.Add(job);
            job.PropertyChanged += OnJobPropertyChanged;
            SyncHeld(job);
            AppLog.Write($"queue: restored {entry.Id} {entry.WavFileName}");
            EventObserver?.Invoke(new QueueEvent.Queued(job.Id, job.Recording.Path));
            if (job.IsHeld) EventObserver?.Invoke(new QueueEvent.Held(job.Id));
        }
        Persist();
        Evaluate();
    }

    /// <summary>Inserts a job as it is, without running it: the driver stays off until <see cref="ClearSamples"/> (UI snapshots only).</summary>
    public TranscriptionJob InsertSample(
        string recording, TranscriptionJobState state, double progress = 0, string? srt = null,
        TranscriptLanguage? language = null, bool offersNotes = false, SessionLanguageTracker? tracker = null,
        LanguageNotice? notice = null, IReadOnlyList<TranscriptSegment>? live = null)
    {
        var lang = language ?? TranscriptLanguage.English;
        if (tracker is null)
        {
            tracker = new SessionLanguageTracker(LanguageChoice.Fixed(lang), lang);
            tracker.Choose(lang);
        }
        var job = new TranscriptionJob(
            Guid.NewGuid().ToString("D"), new TranscriptOutput.PendingRecording(recording, state.IsPending()),
            DateTimeOffset.Now, tracker, (tracker.Language ?? lang).ChineseScript(), true, live ?? [], state.IsPending(), state)
        {
            Progress = progress,
            LanguageNotice = notice,
            Srt = srt,
            Wav = state == TranscriptionJobState.Done ? recording : null,
            OffersNotes = offersNotes,
        };
        showsSamples = true;
        jobs.Add(job);
        job.PropertyChanged += OnJobPropertyChanged;
        SyncHeld(job);
        return job;
    }

    /// <summary>Removes every job, without touching any file (UI snapshots only).</summary>
    public void ClearSamples()
    {
        foreach (var job in jobs.ToList()) job.PropertyChanged -= OnJobPropertyChanged;
        jobs.Clear();
        showsSamples = false;
    }

    /// <summary>Debug only (the replay): its own spool and model; nothing may be queued yet.</summary>
    public void UseForDebug(RecordingSpool debugSpool, Func<string> debugModelLocation)
    {
        ArgumentNullException.ThrowIfNull(debugSpool);
        ArgumentNullException.ThrowIfNull(debugModelLocation);
        if (jobs.Count > 0) throw new InvalidOperationException("The queue has jobs.");
        spool = debugSpool;
        store = new TranscriptionQueueStore(debugSpool);
        modelLocation = debugModelLocation;
    }

    // MARK: - Driver

    /// <summary>Starts or resumes the next job when the policy says so; keeps polling while a job waits for foreground work or the session to end.</summary>
    public void Evaluate()
    {
        if (!drives || isQuitting || showsSamples) return;
        var decision = TranscriptionQueuePolicy.Next(
            settings.FinalPassTiming, isSessionActive, engine.ForegroundWaiting, PolicyJobs());
        switch (decision.Kind)
        {
            case TranscriptionQueuePolicy.DecisionKind.Start:
            case TranscriptionQueuePolicy.DecisionKind.Resume:
                if (jobs.FirstOrDefault(job => job.Id == decision.JobId) is { } next)
                {
                    Run(next, resuming: decision.Kind == TranscriptionQueuePolicy.DecisionKind.Resume);
                }
                break;
            default:
                // A running step suspends itself at its next window.
                break;
        }
        SchedulePoll();
    }

    private void SchedulePoll()
    {
        var policy = PolicyJobs();
        var running = policy.Any(job => job.State == TranscriptionJobState.Running);
        // Held jobs (Manual) wait for the user, not for foreground work or the session: no polling for them.
        var timing = settings.FinalPassTiming;
        var queued = policy.Any(job =>
            job.State is TranscriptionJobState.Waiting or TranscriptionJobState.Suspended && !TranscriptionQueuePolicy.IsHeld(timing, job));
        if (running || !queued || polling) return;
        polling = true;
        pollCancellation?.Dispose();
        pollCancellation = new CancellationTokenSource();
        _ = PollAsync(pollCancellation.Token);
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(pollInterval, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            polling = false;
            return;
        }
        polling = false;
        Evaluate();
    }

    private void Run(TranscriptionJob job, bool resuming)
    {
        job.State = TranscriptionJobState.Running;
        EventObserver?.Invoke(resuming ? new QueueEvent.Resumed(job.Id) : new QueueEvent.Running(job.Id));
        Persist();
        var cancellation = new CancellationTokenSource();
        job.StepCancellation = cancellation;
        job.StepTask = RunStepAsync(job, cancellation);
    }

    private async Task RunStepAsync(TranscriptionJob job, CancellationTokenSource cancellation)
    {
        // The caller stores this task in job.StepTask first.
        await Task.Yield();
        try
        {
            await PerformStepAsync(job, cancellation.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"queue: step of {job.Id} failed unexpectedly: {error}");
            if (IsStillRunning(job, cancellation.Token)) await FailAsync(job, Strings.TranscriptionFailed(TranscriptionEngine.Describe(error))).ConfigureAwait(true);
        }
        finally
        {
            job.StepTask = null;
            job.StepCancellation = null;
            cancellation.Dispose();
        }
        Evaluate();
    }

    /// <summary>One step of a job: read the samples and settle the language when needed, then decode until the end or the next suspension.</summary>
    private async Task PerformStepAsync(TranscriptionJob job, CancellationToken token)
    {
        if (job.SettleTask is { } settling) await settling.ConfigureAwait(true);
        if (!IsStillRunning(job, token)) return;
        string location;
        try
        {
            location = modelLocation();
        }
        catch (WhisperEngineException error)
        {
            await FailAsync(job, error.Message, missingModel: true).ConfigureAwait(true);
            return;
        }
        if (!await LoadSamplesAsync(job, token).ConfigureAwait(true) || job.Samples is not { } samples
            || !IsStillRunning(job, token))
        {
            return;
        }
        await SettleLanguageAsync(job, samples, location, token).ConfigureAwait(true);
        if (!IsStillRunning(job, token)) return;
        if (job.Tracker.Language is not { } language)
        {
            await FailAsync(job, Strings.LanguageNotDecided).ConfigureAwait(true);
            return;
        }
        // The decoder runs at least one window before it checks for a yield;
        // do not start one when the job may not run any more (a session
        // started, or foreground work arrived, while the samples were read or
        // the language detected), or the user put it on hold (Manual).
        if (TranscriptionQueuePolicy.ShouldYield(settings.FinalPassTiming, isSessionActive, engine.ForegroundWaiting, job.IsReleased))
        {
            job.State = job.Checkpoint is null ? TranscriptionJobState.Waiting : TranscriptionJobState.Suspended;
            EventObserver?.Invoke(new QueueEvent.Suspended(job.Id, job.Progress));
            Persist();
            return;
        }
        if (job.Checkpoint is { } stale && !stale.IsForModel(location))
        {
            // Another model is active now: its checkpoint does not apply.
            job.Checkpoint = null;
            job.Progress = 0;
        }
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (job.State == TranscriptionJobState.Running) job.Progress = Math.Clamp(value, 0, 1);
            });
            var step = await engine.TranscribeStepAsync(
                location, samples, language, job.Checkpoint, progress, ShouldYield, token).ConfigureAwait(true);
            if (step.Finished is { } transcription)
            {
                // A finished pass is written even while quitting (only the
                // file writes; the live tail is not awaited then).
                if (job.State != TranscriptionJobState.Running || job.IsUsingLivePreview) return;
                await CompleteAsync(job, transcription.Cues(0, job.ChineseScript)).ConfigureAwait(true);
            }
            else if (step.Suspended is { } checkpoint)
            {
                if (!IsStillRunning(job, token)) return;
                job.Checkpoint = checkpoint;
                job.Progress = checkpoint.FractionDone;
                job.State = TranscriptionJobState.Suspended;
                EventObserver?.Invoke(new QueueEvent.Suspended(job.Id, checkpoint.FractionDone));
                Persist();
            }
        }
        catch (TranscriptionCheckpointException error)
        {
            // The checkpoint does not fit (another model, other audio or options): start over.
            AppLog.Write($"queue: {job.Id} starts over: {error.Message}");
            job.Checkpoint = null;
            job.Progress = 0;
            job.State = TranscriptionJobState.Waiting;
            Persist();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Cancelled by "Use live preview instead" or by quitting; whoever
            // cancelled writes the files (or the next launch starts over).
            if (TranscriptionEngine.IsCancellation(error)) return;
            if (!IsStillRunning(job, token)) return;
            await FailAsync(job, Strings.TranscriptionFailed(TranscriptionEngine.Describe(error))).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The decoder's yield check, on the engine's thread: only the lock-free
    /// counter and a volatile flag (<see cref="UpdateHold"/>: a session under
    /// whenIdle or Manual, or Manual with a running job that was put on hold).
    /// </summary>
    private bool ShouldYield() => engine.ForegroundWaiting > 0 || held;

    private bool IsStillRunning(TranscriptionJob job, CancellationToken token) =>
        job.State == TranscriptionJobState.Running && !job.IsUsingLivePreview && !isQuitting && !token.IsCancellationRequested;

    /// <summary>Reads the job's WAV unless its samples are already held. Fails the job and returns false when the file cannot be read.</summary>
    private async Task<bool> LoadSamplesAsync(TranscriptionJob job, CancellationToken token)
    {
        if (job.Samples is not null) return true;
        var path = job.Recording.Path;
        try
        {
            job.Samples = await Task.Run(() => loadSamples(path, token), CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!job.IsPending || isQuitting) return false;
            await FailAsync(job, Strings.TranscriptionFailed(TranscriptionEngine.Describe(error))).ConfigureAwait(true);
            return false;
        }
    }

    /// <summary>
    /// Settles the job's language before its pass when the session did not:
    /// one detection over the whole recording, locked with the Auto rules (the
    /// preferred language when detection is unsure or fails).
    /// </summary>
    private async Task SettleLanguageAsync(TranscriptionJob job, float[] samples, string location, CancellationToken token)
    {
        if (job.Tracker.IsSettled) return;
        DetectionResult? result;
        try
        {
            result = await engine.DetectLanguageAsync(location, samples, token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A cancelled detection (quit, "Use live preview instead") settles
            // nothing; only a real failure falls back.
            if (TranscriptionEngine.IsCancellation(error) || token.IsCancellationRequested || isQuitting) return;
            AppLog.Write($"queue: detection failed: {error.Message}");
            result = null;
        }
        if (token.IsCancellationRequested || isQuitting || job.Tracker.IsSettled || !job.IsPending) return;
        var decision = job.Tracker.Finish(result?.Detection);
        job.LanguageNotice = LanguageNotice.From(decision);
        LanguageChanged(job);
        EventObserver?.Invoke(new QueueEvent.Language(job.Id, decision));
    }

    private void LanguageChanged(TranscriptionJob job)
    {
        if (job.Tracker.Language is { } language)
        {
            job.ChineseScript = language.ChineseScript();
            job.LiveSink?.SetLanguage(language);
        }
        job.Notify(nameof(TranscriptionJob.Language), nameof(TranscriptionJob.IsDetectingLanguage));
        Persist();
    }

    // MARK: - Completion

    private async Task CompleteAsync(TranscriptionJob job, IReadOnlyList<TranscriptSegment> cues)
    {
        var result = TranscriptOutput.SaveTranscript(cues, job.Recording, job.KeepRecording, settings);
        if (result.Saved is not { } saved)
        {
            await FailAsync(job, result.Failure ?? Strings.RecordingMissing).ConfigureAwait(true);
            return;
        }
        job.Srt = saved.Srt;
        job.Wav = saved.Wav;
        if (saved.Notice is { } notice) job.LiveNotice = notice;
        if (saved.Wav is { } wav)
        {
            job.Recording = new TranscriptOutput.PendingRecording(
                wav, wav != job.Recording.Path ? false : job.Recording.InSpool);
        }
        // A re-run needs the audio; hold it only when the WAV is gone.
        job.RerunSamples = job.LanguageNotice is not null && saved.Wav is null ? job.Samples : null;
        if (job.RerunSamples is not null) DropOlderRerunSamples(job);
        job.Samples = null;
        job.Checkpoint = null;
        job.NeedsModel = false;
        job.ErrorMessage = null;
        job.Progress = 1;
        job.State = TranscriptionJobState.Done;
        // The final SRT replaces the live preview: drop the rest of it (a
        // chunk already in the engine still finishes).
        if (job.LiveSink is { } sink)
        {
            sink.Close();
            _ = ReleaseSinkAsync(job, sink);
        }
        store.RemoveLiveSegments(job.Id);
        job.LiveSegmentsDirty = false;
        EventObserver?.Invoke(new QueueEvent.Done(job.Id, saved.Srt));
        AppLog.Write($"queue: done {job.Id} {Path.GetFileName(saved.Srt)}");
        Persist();
        OfferNotes(job, saved.Srt);
    }

    private static async Task ReleaseSinkAsync(TranscriptionJob job, LiveSink sink)
    {
        await sink.DrainAsync().ConfigureAwait(true);
        if (ReferenceEquals(job.LiveSink, sink)) job.LiveSink = null;
        job.NotifyLiveWaiting();
    }

    /// <summary>
    /// At most one finished job holds its samples for a re-run (an hour of
    /// audio is about 230 MB): the newest. The older ones lose the re-run, and
    /// their rows say why.
    /// </summary>
    private void DropOlderRerunSamples(TranscriptionJob newest)
    {
        foreach (var job in jobs.Where(job => job != newest && job.RerunSamples is not null))
        {
            job.RerunSamples = null;
            if (job.LanguageNotice is not null && !job.IsRerunning) job.RerunError = Strings.CouldNotTranscribeAgainNotKept;
            job.Notify(nameof(TranscriptionJob.CanChangeLanguage));
        }
    }

    /// <summary>
    /// PLAN.md 4.1 failure rules: the WAV is kept (in the output folder), the
    /// live preview is saved as its SRT (after the rest of the live tail), and
    /// the row offers Retry.
    /// </summary>
    private async Task FailAsync(TranscriptionJob job, string message, bool missingModel = false)
    {
        if (!isQuitting) await FinishLiveTailAsync(job).ConfigureAwait(true);
        if (!job.IsPending) return;
        var kept = TranscriptOutput.KeepAfterFailure(message, job.Recording, job.LiveSegments, job.Srt, settings);
        if (kept.Recording is { } recording) job.Recording = recording;
        job.Wav = kept.Recording?.Path;
        job.Srt = kept.Srt;
        job.ErrorMessage = kept.Message;
        job.NeedsModel = missingModel;
        job.Samples = null;
        job.Checkpoint = null;
        job.State = TranscriptionJobState.Failed;
        store.RemoveLiveSegments(job.Id);
        job.LiveSegmentsDirty = false;
        EventObserver?.Invoke(new QueueEvent.Failed(job.Id, kept.Message));
        AppLog.Write($"queue: failed {job.Id}: {message}");
        Persist();
    }

    /// <summary>Waits for the live chunks still coming for <paramref name="job"/>; drops them when the language is still open (they would wait for it forever).</summary>
    private static async Task FinishLiveTailAsync(TranscriptionJob job)
    {
        if (job.LiveSink is not { } sink) return;
        if (job.Tracker.Language is null) sink.Close();
        await sink.DrainAsync().ConfigureAwait(true);
        job.LiveSink = null;
        job.NotifyLiveWaiting();
    }

    /// <summary>
    /// PLAN.md 4.9 item 4: the notes flow opens only when no session is
    /// active and no notes flow is on screen; otherwise the row offers it.
    /// </summary>
    private void OfferNotes(TranscriptionJob job, string srt)
    {
        if (job.Tracker.Language is not { } language) return;
        if (TranscriptionQueuePolicy.PresentsNotes(isSessionActive, NotesOnScreen()))
        {
            // A request nobody took yet stays available on its row.
            if (notesRequest is { } pending && pending.JobId != job.Id) OfferNotesOnRow(pending);
            job.OffersNotes = false;
            notesRequest = new NotesRequest(srt, language, job.Id);
            NotesRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            job.OffersNotes = true;
        }
    }

    // MARK: - Actions

    /// <summary>
    /// "Use live preview instead": stops the job's pass (at the next 30 s
    /// window) and writes its live segments as the SRT, after the rest of the
    /// live tail.
    /// </summary>
    public void UseLivePreviewInstead(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.CanUseLivePreview) return;
        job.IsUsingLivePreview = true;
        // The user asked for this save: a held job (Manual) is released, so it is not
        // listed as "not transcribed yet" while the live preview is written (PLAN.md 4.11, WI-2).
        SetReleased(job, true);
        job.StepCancellation?.Cancel();
        job.FinishTask = FinishWithLivePreviewAsync(job);
        Evaluate();
    }

    private async Task FinishWithLivePreviewAsync(TranscriptionJob job)
    {
        await Task.Yield();
        try
        {
            if (job.SettleTask is { } settling) await settling.ConfigureAwait(true);
            if (job.Tracker.Language is null && !isQuitting)
            {
                // The live tail waits for the language: settle it over the whole recording first.
                using var cancellation = new CancellationTokenSource();
                try
                {
                    var location = modelLocation();
                    var path = job.Recording.Path;
                    var samples = job.Samples ?? await Task.Run(() => loadSamples(path, cancellation.Token), CancellationToken.None)
                        .ConfigureAwait(true);
                    await SettleLanguageAsync(job, samples, location, cancellation.Token).ConfigureAwait(true);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    AppLog.Write($"queue: cannot settle the language of {job.Id}: {error.Message}");
                }
            }
            await FinishLiveTailAsync(job).ConfigureAwait(true);
            if (job.StepTask is { } step) await step.ConfigureAwait(true);
            if (!job.IsPending || isQuitting) return;
            await CompleteAsync(job, job.LiveSegments).ConfigureAwait(true);
        }
        finally
        {
            job.FinishTask = null;
        }
        Evaluate();
    }

    /// <summary>Retry after a failure: the job waits in line again, reading its kept WAV.</summary>
    public void Retry(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.CanRetry) return;
        // The user asked for this pass: released under Manual (PLAN.md 4.11).
        SetReleased(job, true);
        job.State = TranscriptionJobState.Waiting;
        job.ErrorMessage = null;
        job.NeedsModel = false;
        job.Progress = 0;
        job.Checkpoint = null;
        job.IsUsingLivePreview = false;
        job.OffersNotes = false;
        Persist();
        Evaluate();
    }

    /// <summary>
    /// Manual timing, "Transcribe" on a held row (PLAN.md 4.11): the job may run;
    /// released jobs run first in, first out (queue order), so releasing a later
    /// job first does not let it jump an earlier released one. A pending job only;
    /// harmless under the other timings, which ignore the flag.
    /// </summary>
    public void Release(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.IsPending) return;
        SetReleased(job, true);
        UpdateHold();
        Evaluate();
    }

    /// <summary>Manual timing, "Transcribe All": releases every held job.</summary>
    public void ReleaseAll()
    {
        foreach (var job in jobs.Where(job => job.IsPending).ToList()) SetReleased(job, true);
        UpdateHold();
        Evaluate();
    }

    /// <summary>
    /// Manual timing, "Hold" on a released row (PLAN.md 4.11): clears the flag. A
    /// running job suspends at its next 30 s window and keeps its checkpoint, so a
    /// later <see cref="Release"/> continues where it stopped. A pending job only.
    /// </summary>
    public void Hold(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.IsPending) return;
        SetReleased(job, false);
        UpdateHold();
        Evaluate();
    }

    /// <summary>Removes a done or failed job from the queue.</summary>
    public void Dismiss(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.CanDismiss) return;
        job.PropertyChanged -= OnJobPropertyChanged;
        jobs.Remove(job);
        store.RemoveLiveSegments(job.Id);
        Persist();
    }

    /// <summary>
    /// A plain Start (not Stop &amp; Start Next) clears the finished card as it
    /// always did: done jobs whose notes flow already opened are dismissed.
    /// </summary>
    public void DismissFinishedForNewSession()
    {
        var finished = jobs.Where(job => job is { State: TranscriptionJobState.Done, OffersNotes: false, IsRerunning: false }).ToList();
        foreach (var job in finished) Dismiss(job);
    }

    /// <summary>"Generate Notes…" on a row: the Record tab starts the flow itself.</summary>
    public void NotesStarted(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.OffersNotes = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The notes flow renamed the files of <paramref name="srt"/> (PLAN.md 4.3 step 7); <paramref name="files"/> are the final paths, SRT first.</summary>
    public void FilesRenamed(string srt, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(srt);
        ArgumentNullException.ThrowIfNull(files);
        if (jobs.FirstOrDefault(job => string.Equals(job.Srt, srt, StringComparison.OrdinalIgnoreCase)) is not { } job) return;
        if (files.FirstOrDefault(file => file.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) is { } newSrt) job.Srt = newSrt;
        if (files.FirstOrDefault(file => file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) is { } newWav)
        {
            job.Wav = newWav;
            if (!job.Recording.InSpool) job.Recording = new TranscriptOutput.PendingRecording(newWav, InSpool: false);
        }
    }

    public void DismissLanguageNotice(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.LanguageNotice = null;
        if (!job.IsRerunning) job.RerunSamples = null;
        job.Notify(nameof(TranscriptionJob.CanChangeLanguage));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A language notice button. Before the pass starts, the pass uses
    /// <paramref name="language"/>; after it finished, one full pass runs
    /// again in it (foreground work, like before) and rewrites the same SRT.
    /// Never changes the language choice or the preferred language.
    /// </summary>
    public void TranscribeAgain(TranscriptionJob job, TranscriptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.CanChangeLanguage) return;
        job.Tracker.Choose(language);
        job.LanguageNotice = null;
        job.RerunError = null;
        LanguageChanged(job);
        if (job.State != TranscriptionJobState.Done || job.Srt is not { } srt) return;
        var samples = job.RerunSamples;
        var wav = job.Wav;
        job.IsRerunning = true;
        job.RerunProgress = 0;
        var cancellation = new CancellationTokenSource();
        job.RerunCancellation = cancellation;
        job.RerunTask = RunRerunAsync(job, srt, wav, samples, language, cancellation);
    }

    private async Task RunRerunAsync(
        TranscriptionJob job, string srt, string? wav, float[]? held, TranscriptLanguage language,
        CancellationTokenSource cancellation)
    {
        await Task.Yield();
        var token = cancellation.Token;
        try
        {
            string location;
            try
            {
                location = modelLocation();
            }
            catch (WhisperEngineException error)
            {
                job.RerunError = Strings.CouldNotTranscribeAgain(error.Message);
                return;
            }
            float[] samples;
            if (held is not null)
            {
                samples = held;
            }
            else if (wav is not null)
            {
                samples = await Task.Run(() => loadSamples(wav, token), CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                job.RerunError = Strings.CouldNotTranscribeAgainNotKept;
                return;
            }
            var progress = new Progress<double>(value => job.RerunProgress = Math.Clamp(value, 0, 1));
            var result = await engine.TranscribeAsync(location, samples, language, progress, token).ConfigureAwait(true);
            if (!File.Exists(srt))
            {
                job.RerunError = Strings.CouldNotTranscribeAgainMoved(Path.GetFileName(srt));
                return;
            }
            TranscriptOutput.WriteSrt(result.Cues(0, job.ChineseScript), srt);
            job.RerunSamples = null;
            OfferNotes(job, srt);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!TranscriptionEngine.IsCancellation(error))
            {
                job.RerunError = Strings.CouldNotTranscribeAgain(TranscriptionEngine.Describe(error));
            }
        }
        finally
        {
            job.IsRerunning = false;
            job.RerunTask = null;
            job.RerunCancellation = null;
            cancellation.Dispose();
        }
    }

    // MARK: - Quit

    /// <summary>Something must be stopped before quitting: a step, a live-preview save, a language settling, or a re-run.</summary>
    public bool HasWorkInFlight =>
        jobs.Any(job => job.StepTask is not null || job.SettleTask is not null || job.FinishTask is not null || job.RerunTask is not null);

    /// <summary>
    /// Quit: stops every step and re-run (the decoder stops before its next
    /// window), drops the live chunks still queued, then saves the queue and
    /// the live segments. The spool WAVs stay; the next launch continues with
    /// them (PLAN.md 4.9).
    /// </summary>
    public async Task PrepareForQuitAsync()
    {
        isQuitting = true;
        pollCancellation?.Cancel();
        foreach (var job in jobs)
        {
            job.StepCancellation?.Cancel();
            job.SettleCancellation?.Cancel();
            if (job.RerunCancellation is { } rerun)
            {
                rerun.Cancel();
                job.RerunCancelledByQuit = true;
            }
            // The engine must be free at exit; a chunk already decoding finishes.
            job.LiveSink?.Close();
        }
        foreach (var job in jobs.ToList())
        {
            if (job.StepTask is { } step) await step.ConfigureAwait(true);
            if (job.SettleTask is { } settle) await settle.ConfigureAwait(true);
            if (job.FinishTask is { } finish) await finish.ConfigureAwait(true);
            if (job.RerunTask is { } rerun) await rerun.ConfigureAwait(true);
        }
        FlushLiveSegments();
        Persist();
    }

    /// <summary>The quit did not happen after all: jobs stopped by <see cref="PrepareForQuitAsync"/> wait in line again.</summary>
    public void QuitCancelled()
    {
        if (!isQuitting) return;
        isQuitting = false;
        foreach (var job in jobs.Where(job => job.IsPending && job.IsUsingLivePreview && job.FinishTask is null))
        {
            // Its live-preview save was stopped by the quit; offer it again.
            job.IsUsingLivePreview = false;
        }
        foreach (var job in jobs.Where(job => job.State == TranscriptionJobState.Running && job.StepTask is null))
        {
            job.State = job.Checkpoint is null ? TranscriptionJobState.Waiting : TranscriptionJobState.Suspended;
        }
        // A "Transcribe again" pass stopped by the quit is not restarted; the previous SRT stays, and the row says why.
        foreach (var job in jobs.Where(job => job.RerunCancelledByQuit))
        {
            job.RerunCancelledByQuit = false;
            job.RerunError = Strings.CancelledBecauseQuit;
        }
        Persist();
        Evaluate();
    }

    // MARK: - Persistence

    private void Persist()
    {
        if (!drives) return;
        // Only spool WAVs can be named in queue.json; a retry of a WAV in the
        // output folder is not continued after a relaunch.
        var entries = jobs.Where(job => job.Recording.InSpool).Select(job => new TranscriptionQueueManifest.Job(
            job.Id,
            Path.GetFileName(job.Recording.Path),
            job.StopTime,
            job.Tracker.Choice,
            job.Tracker.IsSettled ? job.Tracker.Language : null,
            job.ChineseScript,
            job.KeepRecording,
            job.State,
            job.DisplayName)).ToList();
        try
        {
            if (entries.Count == 0)
            {
                if (File.Exists(store.ManifestPath)) File.Delete(store.ManifestPath);
            }
            else
            {
                store.Save(new TranscriptionQueueManifest(entries));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidJobIdException)
        {
            AppLog.Write($"queue: could not save queue.json: {error.Message}");
        }
    }

    private void MarkLiveSegmentsChanged(TranscriptionJob job)
    {
        if (!drives || !job.Recording.InSpool) return;
        job.LiveSegmentsDirty = true;
        if (liveWriteScheduled) return;
        liveWriteScheduled = true;
        _ = WriteLiveSegmentsLaterAsync();
    }

    private async Task WriteLiveSegmentsLaterAsync()
    {
        await Task.Delay(liveWriteDelay).ConfigureAwait(true);
        liveWriteScheduled = false;
        FlushLiveSegments();
    }

    /// <summary>Writes the live segments of every job that changed since the last write.</summary>
    public void FlushLiveSegments()
    {
        foreach (var job in jobs.Where(job => job.LiveSegmentsDirty).ToList())
        {
            job.LiveSegmentsDirty = false;
            if (!job.IsPending) continue;
            try
            {
                store.WriteLiveSegments(job.LiveSegments, job.Id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidJobIdException)
            {
                AppLog.Write($"queue: could not save live segments of {job.Id}: {error.Message}");
            }
        }
    }

    /// <summary>Stops polling (at quit, after <see cref="PrepareForQuitAsync"/>).</summary>
    public void Dispose()
    {
        settings.PropertyChanged -= OnSettingsChanged;
        pollCancellation?.Cancel();
        pollCancellation?.Dispose();
        pollCancellation = null;
    }

    // MARK: - Notifications

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RaiseQueueChanged(nameof(PendingCount), nameof(ActiveJob), nameof(FeaturedJob));

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(TranscriptionJob.State))
        {
            if (sender is TranscriptionJob changed) SyncHeld(changed);
            UpdateHold();
            RaiseQueueChanged(nameof(PendingCount), nameof(ActiveJob), nameof(HeldCount), nameof(AllPendingHeld));
        }
        else if (e.PropertyName == nameof(TranscriptionJob.IsReleased))
        {
            if (sender is TranscriptionJob changed) SyncHeld(changed);
            UpdateHold();
            RaiseQueueChanged(nameof(HeldCount), nameof(AllPendingHeld));
        }
        else if (e.PropertyName == nameof(TranscriptionJob.IsHeld))
        {
            RaiseQueueChanged(nameof(HeldCount), nameof(AllPendingHeld));
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseQueueChanged(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (the context the queue was made on); inline when there is none (tests).</summary>
    private void Post(Action action)
    {
        if (context is null || SynchronizationContext.Current == context)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }
}
