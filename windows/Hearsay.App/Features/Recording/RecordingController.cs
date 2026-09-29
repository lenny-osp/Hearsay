using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Naming;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// The phases of <see cref="RecordingController"/> (the Mac's
/// <c>RecordingController.Phase</c>).
/// </summary>
internal abstract record ControllerPhase
{
    private ControllerPhase()
    {
    }

    public static ControllerPhase IdleState { get; } = new Idle();

    public sealed record Idle : ControllerPhase;

    public sealed record Starting : ControllerPhase;

    public sealed record Recording : ControllerPhase;

    public sealed record Paused : ControllerPhase;

    public sealed record Stopping : ControllerPhase;

    /// <summary>The WAV is closed and the final pass (or a retry) is running.</summary>
    public sealed record Transcribing(double Progress) : ControllerPhase;

    /// <summary>The transcript was written to <paramref name="Srt"/>; the recording is at <paramref name="Wav"/>, or null when it was not kept.</summary>
    public sealed record Finished(string? Srt, string? Wav) : ControllerPhase;

    /// <summary>The last recording, its transcription, or the last start had a problem; captured audio is at <see cref="RecordingController.FinishedRecording"/>.</summary>
    public sealed record Failed(string Message) : ControllerPhase;
}

/// <summary>A finished SRT for the notes flow (PLAN.md 4.3 step 1) and the session language it was written in.</summary>
internal sealed record NotesRequest(string Srt, TranscriptLanguage Language);

/// <summary>
/// Owns the one recording session the app can run at a time (PLAN.md 4.1,
/// 4.4). It lives in <see cref="AppShell"/>, not in a view, so closing the
/// main window never stops a recording; the Record tab, the tray menu, the
/// global hotkeys, and the quit prompt all drive this same object.
/// Port of mac/Hearsay/Features/Recording/RecordingController.swift.
/// </summary>
/// <remarks>
/// <para>The microphone and, when enabled, the system audio run through
/// <see cref="AudioMixer"/>; the mixed stream feeds the spool WAV and the main
/// meter, and each source's level feeds its small meter. System audio that
/// cannot start never blocks a recording: it goes on mic-only with a notice.
/// Elapsed time is derived from the number of samples written, so paused
/// time never counts and the display matches the WAV exactly.</para>
/// <para>Transcription (PLAN.md 4.1 steps 4 to 7): while recording,
/// <see cref="LiveChunker"/> cuts the mixed stream into chunks that the
/// engine transcribes in order for the live preview. After Stop the WAV is
/// closed and one full pass over the in-memory samples produces
/// <c>&lt;timestamp&gt;.srt</c> in the output folder; the WAV is then moved
/// beside it (or deleted when "Keep the recording" is off). If the pass fails,
/// the WAV is always kept, the live preview is saved as the SRT when there is
/// one, and the recording can be retried here or sent to the File tab.
/// Without an installed model the recording still works; the live preview is
/// skipped and the WAV is kept.</para>
/// <para>Phase transitions: idle/finished/failed → starting → recording ⇄
/// paused → stopping → transcribing(progress) → finished(srt, wav) or
/// failed(message), exactly as on the Mac.</para>
/// <para>Windows differences (PLAN.md 18.4): the live preview runs only when
/// the model's speed probe (<see cref="TranscriptionEngine.MeasureSpeedAsync"/>)
/// finished a warm 30 s window in under 15 s; otherwise the Record tab says
/// "Live preview off: this computer is too slow for it" and the language is
/// detected at Stop. There is no microphone or screen-capture permission to
/// request; the capture devices are opened on the thread pool. Use from the
/// UI thread.</para>
/// </remarks>
internal sealed class RecordingController : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings settings;
    private readonly ModelStore modelStore;
    private readonly TranscriptionEngine engine;
    private readonly SynchronizationContext? context;
    private RecordingSpool spool;
    private CaptureSources sources;

    private ControllerPhase phase = ControllerPhase.IdleState;
    private IMicrophoneCapture? recorder;
    private ISystemAudioCapture? systemRecorder;
    private WavWriter? writer;
    private Exception? writeError;
    private LevelMeter meter = new();
    private int sampleCount;
    private Task? startTask;
    private Task? consumer;
    private bool stopRequestedWhileStarting;
    private AudioDeviceListObservation? deviceObservation;
    private string recordingDeviceName = Strings.TheInputDevice;
    private MicrophoneRecorderException? noAudioFailure;
    private CancellationTokenSource? watchdog;

    // Transcription state
    private List<float> recordedSamples = [];
    private LiveChunker chunker = new();
    private int chunkedSamples;
    private ChannelWriter<LiveJob>? liveWriter;
    private Task? liveTask;
    private CancellationTokenSource? liveCancellation;
    private CancellationTokenSource? transcription;
    private Task? transcriptionTask;
    private string? pendingSpoolWav;
    private string? retryableRecording;
    private int session;
    private int liveJobCount;
    private string? liveLocation;
    private Task? detectionTask;
    private List<TaskCompletionSource> languageWaiters = [];
    private float[]? rerunSamples;
    private ChineseScript? sessionChineseScript;
    private List<TranscriptSegment> liveSegments = [];

    public RecordingController(
        AppSettings settings, ModelStore modelStore, TranscriptionEngine engine, RecordingSpool spool,
        CaptureSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelStore);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(spool);
        this.settings = settings;
        this.modelStore = modelStore;
        this.engine = engine;
        this.spool = spool;
        this.sources = sources ?? CaptureSources.Live;
        context = SynchronizationContext.Current;
        LanguageTracker = new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A finished SRT is waiting for the notes flow; see <see cref="TakeNotesRequest"/>.</summary>
    public event EventHandler? NotesRequested;

    /// <summary>A live job's progress, for the debug replay's timing log.</summary>
    public Action<LiveJobEvent>? LiveJobObserver { get; set; }

    /// <summary>Debug only: the replay never starts the notes flow, so no transcript reaches an AI provider.</summary>
    internal bool SuppressNotesRequests { get; set; }

    public ControllerPhase Phase
    {
        get => phase;
        private set
        {
            if (phase == value) return;
            phase = value;
            Notify(nameof(Phase));
        }
    }

    public IReadOnlyList<AudioInputDevice> Devices { get; private set; } = [];

    /// <summary>The Record tab's microphone; the default input when nothing else is chosen.</summary>
    public string? SelectedDeviceUid { get; set; }

    public double Elapsed { get; private set; }

    public double LevelFraction { get; private set; }

    public double MicLevelFraction { get; private set; }

    /// <summary>Null when system audio is not part of the current recording.</summary>
    public double? SystemLevelFraction { get; private set; }

    /// <summary>Why system audio is not being captured, shown as a badge.</summary>
    public string? SystemAudioNotice { get; private set; }

    public string? SilenceWarning { get; private set; }

    /// <summary>Where the last recording ended up, also after a failure.</summary>
    public string? FinishedRecording { get; private set; }

    /// <summary>The SRT of the last recording: the final transcript, or the live preview saved after a failed final pass.</summary>
    public string? FinishedTranscript { get; private set; }

    /// <summary>Cues of the live preview, in recording time, in order.</summary>
    public IReadOnlyList<TranscriptSegment> LiveSegments => liveSegments;

    /// <summary>Live chunks queued or being transcribed.</summary>
    public int LiveChunksWaiting { get; private set; }

    /// <summary>The newest non-empty live line, for the tray.</summary>
    public string? LatestLiveLine { get; private set; }

    /// <summary>Why the live preview is off or incomplete, shown under it.</summary>
    public string? LiveNotice { get; private set; }

    /// <summary>The current session transcribes live chunks.</summary>
    public bool IsLivePreviewEnabled { get; private set; }

    /// <summary>"Use live preview instead" was chosen for the current final pass.</summary>
    public bool IsUsingLivePreview { get; private set; }

    /// <summary>The last failure was the missing model; the view links to Models.</summary>
    public bool NeedsModel { get; private set; }

    /// <summary>A recording the File tab should pick up ("Transcribe this file"); the File tab clears it.</summary>
    public string? TranscribeFileRequest { get; set; }

    /// <summary>A finished SRT waiting for the notes flow; see <see cref="TakeNotesRequest"/>.</summary>
    public NotesRequest? PendingNotesRequest { get; private set; }

    /// <summary>When the session detects its language and what it settled on (PLAN.md section 1, "Languages").</summary>
    public SessionLanguageTracker LanguageTracker { get; private set; }

    /// <summary>The suggestion or fallback banner of the current or last recording.</summary>
    public LanguageNotice? LanguageNotice { get; private set; }

    /// <summary>Why the last "Transcribe again" failed, shown under the transcript.</summary>
    public string? RerunError { get; private set; }

    /// <summary>A "Transcribe again" pass over the finished recording is running.</summary>
    public bool IsRerunning { get; private set; }

    /// <summary>The last detection result, for the debug replay.</summary>
    public DetectionResult? LastDetection { get; private set; }

    /// <summary>The Record tab picker: Auto or a fixed language, persisted (shared with the File tab).</summary>
    public LanguageChoice LanguageChoice
    {
        get => settings.LanguageChoice;
        set => settings.LanguageChoice = value;
    }

    /// <summary>"Also capture system audio", persisted as the default.</summary>
    public bool CaptureSystemAudio
    {
        get => settings.CaptureSystemAudio;
        set => settings.CaptureSystemAudio = value;
    }

    /// <summary>The language of the current or last recording; null while Auto is still undecided.</summary>
    public TranscriptLanguage? SessionLanguage => LanguageTracker.Language;

    /// <summary>The current or last session's decision, for the debug replay.</summary>
    public LanguageDecision? SessionDecision => LanguageTracker.Decision;

    /// <summary>Auto has not decided yet, and a model is there to decide it: "Detecting language…".</summary>
    public bool IsDetectingLanguage
    {
        get
        {
            if (!LanguageTracker.IsUndecided) return false;
            return phase switch
            {
                ControllerPhase.Recording or ControllerPhase.Paused or ControllerPhase.Stopping => IsLivePreviewEnabled,
                ControllerPhase.Transcribing => true,
                _ => false,
            };
        }
    }

    /// <summary>The language notice's buttons apply now.</summary>
    public bool CanChangeSessionLanguage => phase switch
    {
        ControllerPhase.Recording or ControllerPhase.Paused or ControllerPhase.Stopping => true,
        ControllerPhase.Finished finished => finished.Srt is not null && (finished.Wav is not null || rerunSamples is not null),
        _ => false,
    };

    /// <summary>A session is being set up, is running, or is being saved: recording settings are locked and quitting asks first.</summary>
    public bool IsSessionActive =>
        phase is ControllerPhase.Starting or ControllerPhase.Recording or ControllerPhase.Paused or ControllerPhase.Stopping;

    /// <summary>The final pass or a retry is running.</summary>
    public bool IsTranscribing => phase is ControllerPhase.Transcribing;

    public double? TranscriptionProgress => phase is ControllerPhase.Transcribing t ? t.Progress : null;

    /// <summary>Start is allowed: nothing is recording or transcribing.</summary>
    public bool CanStart => !IsSessionActive && !IsTranscribing;

    /// <summary>The live preview has fallen more than one chunk behind.</summary>
    public bool IsLiveLagging => LiveChunksWaiting > 1;

    /// <summary>"Use live preview instead" applies to the running pass.</summary>
    public bool CanUseLivePreview => IsTranscribing && IsLivePreviewEnabled && pendingSpoolWav is not null && !IsUsingLivePreview;

    /// <summary>A failed transcription can be retried from the kept WAV.</summary>
    public bool CanRetryTranscription => phase is ControllerPhase.Failed && retryableRecording is not null;

    /// <summary>Audio is being captured or is paused; Stop applies.</summary>
    public bool IsCapturing => phase is ControllerPhase.Recording or ControllerPhase.Paused;

    public string? ErrorMessage => phase is ControllerPhase.Failed failed ? failed.Message : null;

    /// <summary>
    /// The files History must not rename (PLAN.md 4.8): the recording's WAV
    /// and SRT while it is recorded or transcribed.
    /// </summary>
    public IReadOnlyList<string> BusyFiles =>
        IsSessionActive || IsTranscribing
            ? new[] { FinishedTranscript, FinishedRecording, pendingSpoolWav, retryableRecording }
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    // MARK: - Devices

    /// <summary>Loads the device list and starts watching for device changes. Safe to call more than once.</summary>
    public void Activate()
    {
        RefreshDevices();
        if (deviceObservation is not null) return;
        try
        {
            deviceObservation = AudioDeviceList.ObserveChanges(RefreshDevices);
        }
        catch (COMException error)
        {
            AppLog.Write($"recording: cannot watch audio devices: {error.Message}");
        }
    }

    public void RefreshDevices()
    {
        try
        {
            Devices = AudioDeviceList.InputDevices();
            SelectedDeviceUid = AudioDeviceList.ResolveSelection(SelectedDeviceUid, Devices, AudioDeviceList.DefaultInputDevice()?.Uid);
        }
        catch (COMException error)
        {
            AppLog.Write($"recording: cannot list audio devices: {error.Message}");
            Devices = [];
        }
        Notify(nameof(Devices));
    }

    // MARK: - Controls

    /// <summary>Starts a new recording unless one is active. Returns at once; the phase moves through starting to recording or failed.</summary>
    public void Start()
    {
        if (!CanStart) return;
        Phase = new ControllerPhase.Starting();
        FinishedRecording = null;
        FinishedTranscript = null;
        retryableRecording = null;
        TranscribeFileRequest = null;
        PendingNotesRequest = null;
        NeedsModel = false;
        LanguageNotice = null;
        RerunError = null;
        rerunSamples = null;
        session += 1;
        ResetLivePreview();
        stopRequestedWhileStarting = false;
        startTask = RunStartAsync();
        Notify();
    }

    private async Task RunStartAsync()
    {
        try
        {
            await PerformStartAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"recording: start failed: {error}");
            Phase = new ControllerPhase.Failed(Strings.Describe(error));
        }
        startTask = null;
    }

    public void Pause()
    {
        if (phase is not ControllerPhase.Recording || recorder is null) return;
        recorder.Pause();
        systemRecorder?.Pause();
        LevelFraction = 0;
        MicLevelFraction = 0;
        if (SystemLevelFraction is not null) SystemLevelFraction = 0;
        Phase = new ControllerPhase.Paused();
    }

    public void Resume()
    {
        if (phase is not ControllerPhase.Paused || recorder is null) return;
        // A failed resume goes through the recorder's own recovery, and
        // RecordingEnded saves what exists (PLAN.md 18.4, W3).
        recorder.Resume();
        systemRecorder?.Resume();
        Phase = new ControllerPhase.Recording();
    }

    /// <summary>Start when nothing is recording, Stop when something is (global hotkey and tray menu).</summary>
    public void ToggleStartStop()
    {
        if (IsCapturing || phase is ControllerPhase.Starting)
        {
            _ = StopAsync();
        }
        else
        {
            Start();
        }
    }

    public void TogglePause()
    {
        switch (phase)
        {
            case ControllerPhase.Recording:
                Pause();
                break;
            case ControllerPhase.Paused:
                Resume();
                break;
        }
    }

    /// <summary>
    /// Stops the recording and returns once the WAV is closed (the final
    /// pass then runs on its own). A start in progress is finished first.
    /// Does nothing when no session is active.
    /// </summary>
    public async Task StopAsync()
    {
        if (phase is ControllerPhase.Starting)
        {
            stopRequestedWhileStarting = true;
            if (startTask is { } starting) await starting.ConfigureAwait(true);
        }
        if (IsCapturing)
        {
            Phase = new ControllerPhase.Stopping();
            recorder?.Stop();
            systemRecorder?.Stop();
        }
        // The consumer finishes once both streams drain, then saves.
        if (consumer is { } running) await running.ConfigureAwait(true);
    }

    /// <summary>"Use live preview instead": cancels the final pass and writes the live segments as the SRT.</summary>
    public void UseLivePreviewInstead()
    {
        if (!CanUseLivePreview) return;
        IsUsingLivePreview = true;
        transcription?.Cancel();
        var id = session;
        Notify();
        _ = CompleteWithLivePreviewAsync(id);
    }

    private async Task CompleteWithLivePreviewAsync(int id)
    {
        if (liveTask is { } live) await live.ConfigureAwait(true);
        if (session != id || !IsTranscribing) return;
        CompleteTranscription(liveSegments.ToList());
    }

    /// <summary>
    /// Quit while transcribing: cancels the pass and the live queue, then
    /// saves the live preview as the SRT when there is one. The WAV is kept
    /// either way, whatever "Keep the recording" says, because the full pass
    /// never ran. Returns once the files are written.
    /// </summary>
    public async Task CancelTranscriptionForQuitAsync()
    {
        if (!IsTranscribing) return;
        if (IsRerunning)
        {
            // The previous SRT stays as it is.
            transcription?.Cancel();
            if (transcriptionTask is { } rerun) await rerun.ConfigureAwait(true);
            return;
        }
        IsUsingLivePreview = true;
        transcription?.Cancel();
        liveWriter?.TryComplete();
        liveWriter = null;
        liveCancellation?.Cancel();
        if (transcriptionTask is { } pass) await pass.ConfigureAwait(true);
        if (!IsTranscribing) return;
        if (liveSegments.Count == 0)
        {
            FailTranscription(Strings.CancelledBecauseQuit);
        }
        else
        {
            CompleteTranscription(liveSegments.ToList(), keepRecording: true);
        }
    }

    /// <summary>Runs the full pass again on the kept WAV of a failed transcription.</summary>
    public void RetryTranscription()
    {
        if (!CanRetryTranscription || retryableRecording is not { } recording) return;
        Phase = new ControllerPhase.Transcribing(0);
        IsUsingLivePreview = false;
        var id = session;
        transcription = new CancellationTokenSource();
        transcriptionTask = RunRetryAsync(recording, id, transcription.Token);
    }

    /// <summary>
    /// A language notice button: transcribe this recording in
    /// <paramref name="language"/>. While recording, the rest of the live
    /// preview and the final pass use it; after a finished recording, the
    /// final pass runs again in it and rewrites the same SRT. Never changes
    /// the language choice or the preferred language.
    /// </summary>
    public void TranscribeAgain(TranscriptLanguage language)
    {
        if (!CanChangeSessionLanguage) return;
        LanguageTracker.Choose(language);
        LanguageNotice = null;
        RerunError = null;
        LanguageSettled();
        Notify();
        if (phase is not ControllerPhase.Finished { Srt: { } srt } finished) return;
        var samples = rerunSamples;
        Phase = new ControllerPhase.Transcribing(0);
        IsRerunning = true;
        IsUsingLivePreview = false;
        var id = session;
        transcription = new CancellationTokenSource();
        transcriptionTask = RunRerunAsync(srt, finished.Wav, samples, language, id, transcription.Token);
    }

    /// <summary>"Dismiss" on the suggestion banner.</summary>
    public void DismissLanguageNotice()
    {
        LanguageNotice = null;
        if (!IsTranscribing) rerunSamples = null;
        Notify();
    }

    /// <summary>Hands the kept WAV to the File tab ("Transcribe this file").</summary>
    public void RequestTranscribeFile()
    {
        TranscribeFileRequest = retryableRecording ?? FinishedRecording;
        Notify(nameof(TranscribeFileRequest));
    }

    /// <summary>The finished SRT waiting for the notes flow, once.</summary>
    public NotesRequest? TakeNotesRequest()
    {
        var request = PendingNotesRequest;
        PendingNotesRequest = null;
        return request;
    }

    /// <summary>Debug only (<see cref="RecordingReplay"/>): the replay's fake capture and its own spool. Idle only.</summary>
    internal void UseForDebug(CaptureSources debugSources, RecordingSpool debugSpool)
    {
        if (!CanStart) throw new InvalidOperationException("A recording is active.");
        sources = debugSources;
        spool = debugSpool;
    }

    // MARK: - Start

    private async Task PerformStartAsync()
    {
        SystemAudioNotice = null;
        ISystemAudioCapture? system = null;
        if (settings.CaptureSystemAudio)
        {
            try
            {
                system = await Task.Run(sources.MakeSystemAudio).ConfigureAwait(true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                SystemAudioNotice = Strings.SystemAudioOff(Strings.Describe(error));
            }
        }
        if (stopRequestedWhileStarting)
        {
            system?.Stop();
            Phase = ControllerPhase.IdleState;
            return;
        }

        RefreshDevices();
        var device = Devices.FirstOrDefault(d => d.Uid == SelectedDeviceUid);
        var path = spool.NewRecordingPath(Timestamps.Now());
        WavWriter newWriter;
        try
        {
            newWriter = new WavWriter(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WavException)
        {
            system?.Stop();
            Phase = new ControllerPhase.Failed(Strings.CouldNotCreateRecording(error.Message));
            return;
        }
        var mic = sources.MakeMicrophone();
        try
        {
            await Task.Run(() => mic.Start(device)).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            system?.Stop();
            newWriter.Dispose();
            TryDelete(path);
            Phase = new ControllerPhase.Failed(Strings.Describe(error));
            return;
        }

        writer = newWriter;
        recorder = mic;
        systemRecorder = system;
        recordingDeviceName = mic.Diagnostics.DeviceName ?? device?.Name ?? Strings.TheInputDevice;
        noAudioFailure = null;
        writeError = null;
        meter = new LevelMeter();
        sampleCount = 0;
        Elapsed = 0;
        LevelFraction = 0;
        MicLevelFraction = 0;
        SystemLevelFraction = system is null ? null : 0;
        SilenceWarning = null;
        recordedSamples = [];
        LanguageTracker = new SessionLanguageTracker(settings.LanguageChoice, settings.PreferredLanguage);
        LastDetection = null;
        sessionChineseScript = LanguageTracker.Language?.ChineseScript();
        StartLivePreview();
        Phase = new ControllerPhase.Recording();

        var mixed = AudioMixer.Mix(
            mic.TimedSamples.ReadAllAsync(),
            system?.TimedSamples.ReadAllAsync(),
            source => Post(() => SourceEnded(source)));
        consumer = ConsumeAsync(mixed);
        StartNoAudioWatchdog(mic);
    }

    private async Task ConsumeAsync(IAsyncEnumerable<MixedChunk> mixed)
    {
        try
        {
            await foreach (var chunk in mixed.ConfigureAwait(true))
            {
                Consume(chunk);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"recording: mixer failed: {error}");
            writeError ??= error;
        }
        RecordingEnded();
    }

    /// <summary>Stops the recording loudly when the microphone delivers nothing within <see cref="NoAudioWatchdog.Timeout"/> seconds of recording.</summary>
    private void StartNoAudioWatchdog(IMicrophoneCapture mic)
    {
        watchdog?.Cancel();
        var cancel = new CancellationTokenSource();
        watchdog = cancel;
        _ = RunWatchdogAsync(mic, session, cancel.Token);
    }

    private async Task RunWatchdogAsync(IMicrophoneCapture mic, int id, CancellationToken token)
    {
        var state = new NoAudioWatchdog();
        while (!token.IsCancellationRequested)
        {
            if (session != id || !IsCapturing || !ReferenceEquals(recorder, mic)) return;
            var delivered = mic.Diagnostics.SamplesDelivered;
            if (delivered > 0) return;
            if (state.Check(HostClock.NowSeconds(), phase is ControllerPhase.Recording, delivered))
            {
                FailNoAudio();
                return;
            }
            try
            {
                await Task.Delay(250, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void FailNoAudio()
    {
        if (!IsCapturing) return;
        noAudioFailure = new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoAudio, recordingDeviceName);
        Phase = new ControllerPhase.Stopping();
        recorder?.Stop();
        systemRecorder?.Stop();
    }

    /// <summary>The no-audio message plus what capture saw, for the error display (English, as on the Mac).</summary>
    private string NoAudioMessage(MicrophoneRecorderException error)
    {
        if (recorder?.Diagnostics is not { } diagnostics) return Strings.Describe(error);
        var detail = string.Create(CultureInfo.InvariantCulture, $"Capture details: {diagnostics.Callbacks} buffers received");
        if (diagnostics.ConversionFailures > 0)
        {
            detail += string.Create(CultureInfo.InvariantCulture, $", {diagnostics.ConversionFailures} failed to convert");
            if (diagnostics.LastConversionError is { } last) detail += $" ({last})";
        }
        if (diagnostics.LastRuntimeError is { } runtime) detail += $", capture error: {runtime}";
        return Strings.Describe(error) + "\n" + detail + ".";
    }

    // MARK: - Pipeline

    private void Consume(MixedChunk chunk)
    {
        if (writer is null || writeError is not null) return;
        try
        {
            writer.Append(chunk.Mixed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WavException or ObjectDisposedException)
        {
            writeError = error;
            recorder?.Stop();
            systemRecorder?.Stop();
            return;
        }
        sampleCount += chunk.Mixed.Length;
        recordedSamples.AddRange(chunk.Mixed);
        if (liveWriter is not null) FeedChunker(final: false);
        StartDetectionIfDue();
        var time = (double)sampleCount / WavWriter.SampleRate;
        var level = LevelMeter.RmsDB(chunk.Mixed);
        if (!meter.Observe(level, time)) return;
        Elapsed = time;
        LevelFraction = LevelMeter.LevelFraction(level);
        MicLevelFraction = LevelMeter.LevelFraction(chunk.MicRmsDB);
        if (SystemLevelFraction is not null) SystemLevelFraction = LevelMeter.LevelFraction(chunk.SystemRmsDB);
        SilenceWarning = meter.IsSilenceWarning ? Strings.SilenceWarning((int)meter.SilenceSeconds) : null;
        Notify();
    }

    /// <summary>
    /// A source's stream finished. The microphone ending on its own (device
    /// unplugged) ends the recording; system audio ending on its own leaves
    /// the recording going mic-only with a notice.
    /// </summary>
    private void SourceEnded(AudioSource source)
    {
        if (!IsCapturing) return;
        switch (source)
        {
            case AudioSource.Mic:
                if (recorder?.Failure is not null)
                {
                    Phase = new ControllerPhase.Stopping();
                    systemRecorder?.Stop();
                }
                break;
            case AudioSource.System:
                if (systemRecorder?.Failure is { } failure)
                {
                    SystemAudioNotice = Strings.SystemAudioOff(Strings.Describe(failure));
                    SystemLevelFraction = null;
                    Notify();
                }
                break;
        }
    }

    private void RecordingEnded()
    {
        Phase = new ControllerPhase.Stopping();
        watchdog?.Cancel();
        watchdog = null;
        Elapsed = (double)sampleCount / WavWriter.SampleRate;
        var problems = new List<string>();
        var micDelivered = recorder?.Diagnostics.SamplesDelivered ?? 0;
        if (noAudioFailure is { } noAudio)
        {
            problems.Add(NoAudioMessage(noAudio));
        }
        else if (sampleCount == 0 && micDelivered == 0 && recorder?.Failure is null)
        {
            // Stopped before the watchdog fired, with nothing captured.
            problems.Add(NoAudioMessage(new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoAudio, recordingDeviceName)));
        }
        noAudioFailure = null;
        if (recorder?.Failure is { } failure)
        {
            problems.Add(Strings.Describe(failure));
            RefreshDevices();
        }
        if (writeError is { } write)
        {
            problems.Add(Strings.WritingRecordingFailed(write.Message));
        }

        string? closed = null;
        if (writer is { } open)
        {
            closed = open.FilePath;
            try
            {
                open.Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WavException)
            {
                problems.Add(Strings.ClosingRecordingFailed(error.Message));
            }
            open.Dispose();
        }

        recorder = null;
        systemRecorder = null;
        writer = null;
        consumer = null;
        LevelFraction = 0;
        MicLevelFraction = 0;
        SystemLevelFraction = null;
        SilenceWarning = null;

        // The live preview gets the tail, then its queue closes.
        if (liveWriter is not null) FeedChunker(final: true);
        liveWriter?.TryComplete();
        liveWriter = null;
        Notify();

        if (closed is null)
        {
            Phase = problems.Count == 0 ? ControllerPhase.IdleState : new ControllerPhase.Failed(string.Join("\n", problems));
            return;
        }
        if (sampleCount == 0)
        {
            // Nothing was captured: never keep a zero-length recording.
            TryDelete(closed);
            var lines = problems.Count == 0
                ? new List<string> { Strings.Describe(new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoAudio, recordingDeviceName)) }
                : problems;
            lines.Add(Strings.NothingRecorded);
            Phase = new ControllerPhase.Failed(string.Join("\n", lines));
            return;
        }
        pendingSpoolWav = closed;
        if (problems.Count > 0)
        {
            // Capture failed: keep what exists and let the user transcribe it.
            FailTranscription(string.Join("\n", problems));
            return;
        }
        Phase = new ControllerPhase.Transcribing(0);
        IsUsingLivePreview = false;
        var id = session;
        transcription = new CancellationTokenSource();
        transcriptionTask = RunFinalPassAsync(id, transcription.Token);
    }

    // MARK: - Live preview

    private void ResetLivePreview()
    {
        liveWriter?.TryComplete();
        liveWriter = null;
        liveTask = null;
        liveCancellation?.Cancel();
        liveCancellation = null;
        liveSegments = [];
        LiveChunksWaiting = 0;
        LatestLiveLine = null;
        LiveNotice = null;
        IsLivePreviewEnabled = false;
        IsUsingLivePreview = false;
        chunker = new LiveChunker();
        chunkedSamples = 0;
        liveJobCount = 0;
        liveLocation = null;
        detectionTask = null;
        // Waiters of an older session wake up, see the session changed, and drop their job.
        ResumeLanguageWaiters();
    }

    /// <summary>
    /// Opens the live queue when a model is ready and fast enough (PLAN.md
    /// 18.4, "Speed"); otherwise the recording goes on without a preview.
    /// </summary>
    private void StartLivePreview()
    {
        ResetLivePreview();
        string location;
        try
        {
            location = sources.ModelPath(modelStore);
        }
        catch (WhisperEngineException error)
        {
            LiveNotice = Strings.LivePreviewOff(error.Message);
            return;
        }
        if (engine.KnownSpeed(location) is { LivePreviewFeasible: false })
        {
            LiveNotice = Strings.LivePreviewTooSlow;
            return;
        }
        IsLivePreviewEnabled = true;
        liveLocation = location;
        var channel = Channel.CreateUnbounded<LiveJob>(new UnboundedChannelOptions { SingleReader = true });
        liveWriter = channel.Writer;
        liveCancellation = new CancellationTokenSource();
        liveTask = RunLiveQueueAsync(channel.Reader, location, engine.MeasureSpeedAsync(location), session, liveCancellation.Token);
    }

    /// <summary>One consumer, so chunks are transcribed strictly in order. In Auto, chunks that close before the language is decided wait here.</summary>
    private async Task RunLiveQueueAsync(
        ChannelReader<LiveJob> reader, string location, Task<SpeedProbeResult> speed, int id, CancellationToken token)
    {
        var feasible = true;
        try
        {
            feasible = (await speed.ConfigureAwait(true)).LivePreviewFeasible;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A model that cannot load fails each chunk with its own message.
            AppLog.Write($"recording: speed probe failed: {error.Message}");
        }
        if (!feasible && session == id)
        {
            IsLivePreviewEnabled = false;
            LiveNotice = Strings.LivePreviewTooSlow;
            liveLocation = null;
            liveWriter?.TryComplete();
            liveWriter = null;
            LiveChunksWaiting = 0;
            Notify();
        }
        await foreach (var job in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(true))
        {
            if (!feasible || token.IsCancellationRequested)
            {
                if (session == id) LiveChunksWaiting = Math.Max(0, LiveChunksWaiting - 1);
                continue;
            }
            if (await WaitForSessionLanguageAsync(id).ConfigureAwait(true) is not { } language) continue;
            if (token.IsCancellationRequested)
            {
                if (session == id) LiveChunksWaiting = Math.Max(0, LiveChunksWaiting - 1);
                continue;
            }
            var script = sessionChineseScript;
            LiveJobObserver?.Invoke(new LiveJobEvent.Started(job.Index));
            IReadOnlyList<TranscriptSegment>? cues = null;
            Exception? failure = null;
            try
            {
                var result = await engine.TranscribeAsync(location, job.Samples, language, null, token).ConfigureAwait(true);
                cues = result.Cues(job.Start, script);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = error;
            }
            LiveChunkDone(cues, failure, job.Index, id);
        }
    }

    // MARK: - Session language

    /// <summary>The session language once it is known; null when the session changed while waiting.</summary>
    private async Task<TranscriptLanguage?> WaitForSessionLanguageAsync(int id)
    {
        while (session == id && LanguageTracker.Language is null)
        {
            var waiter = new TaskCompletionSource();
            languageWaiters.Add(waiter);
            await waiter.Task.ConfigureAwait(true);
        }
        return session == id ? LanguageTracker.Language : null;
    }

    private void ResumeLanguageWaiters()
    {
        var waiters = languageWaiters;
        languageWaiters = [];
        foreach (var waiter in waiters) waiter.TrySetResult();
    }

    /// <summary>The tracker settled or the user picked a language: the Chinese conversion, the banner, and waiting live jobs follow it.</summary>
    private void LanguageSettled()
    {
        if (LanguageTracker.Language is { } language) sessionChineseScript = language.ChineseScript();
        ResumeLanguageWaiters();
    }

    /// <summary>Runs one detection attempt when the tracker says one is due and none is in flight (every 30 s of audio).</summary>
    private void StartDetectionIfDue()
    {
        if (detectionTask is not null || liveLocation is not { } location
            || LanguageTracker.AttemptDue(recordedSamples.Count) is not { } count)
        {
            return;
        }
        var samples = CollectionsMarshal.AsSpan(recordedSamples)[..count].ToArray();
        detectionTask = RunDetectionAsync(location, samples, count, session);
    }

    private async Task RunDetectionAsync(string location, float[] samples, int count, int id)
    {
        DetectionResult? result = null;
        try
        {
            result = await engine.DetectLanguageAsync(location, samples).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"recording: detection failed: {error.Message}");
        }
        DetectionDone(result, count, id);
    }

    private void DetectionDone(DetectionResult? result, int samplesUsed, int id)
    {
        if (session != id) return;
        detectionTask = null;
        LastDetection = result;
        var decision = LanguageTracker.Record(result?.Detection, samplesUsed);
        LiveJobObserver?.Invoke(new LiveJobEvent.Detection((double)samplesUsed / WavWriter.SampleRate, result, decision));
        if (decision is not null)
        {
            LanguageNotice = LanguageNotice.From(LanguageTracker.Decision);
            LanguageSettled();
            Notify();
        }
        else if (IsCapturing)
        {
            StartDetectionIfDue();
        }
    }

    /// <summary>
    /// Settles the session language before a final pass: waits for an attempt
    /// in flight, then, if the language is still open, detects over the whole
    /// recording and locks the result (the preferred language when detection
    /// is unsure or fails).
    /// </summary>
    private async Task SettleLanguageAsync(float[] samples, string location, int id)
    {
        if (detectionTask is { } running) await running.ConfigureAwait(true);
        if (session != id || LanguageTracker.IsSettled) return;
        DetectionResult? result = null;
        try
        {
            result = await engine.DetectLanguageAsync(location, samples).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"recording: detection failed: {error.Message}");
        }
        if (session != id || LanguageTracker.IsSettled) return;
        LastDetection = result;
        var decision = LanguageTracker.Finish(result?.Detection);
        LiveJobObserver?.Invoke(new LiveJobEvent.Detection((double)samples.Length / WavWriter.SampleRate, result, decision));
        LanguageNotice = LanguageNotice.From(decision);
        LanguageSettled();
        Notify();
    }

    /// <summary>Measures the new samples in 0.1 s windows and queues every chunk the chunker closes; <paramref name="final"/> also flushes the open chunk.</summary>
    private void FeedChunker(bool final)
    {
        var window = LiveChunker.WindowSamples;
        while (recordedSamples.Count - chunkedSamples >= window || (final && recordedSamples.Count > chunkedSamples))
        {
            var end = Math.Min(chunkedSamples + window, recordedSamples.Count);
            var level = LevelMeter.RmsDB(CollectionsMarshal.AsSpan(recordedSamples)[chunkedSamples..end]);
            chunkedSamples = end;
            foreach (var range in chunker.Observe(end, level)) EnqueueLive(range);
        }
        if (final && chunker.Flush() is { } tail) EnqueueLive(tail);
    }

    private void EnqueueLive(SampleRange range)
    {
        if (liveWriter is not { } queue || range.End > recordedSamples.Count) return;
        LiveChunksWaiting += 1;
        liveJobCount += 1;
        var start = (double)range.Start / LiveChunker.SampleRate;
        LiveJobObserver?.Invoke(new LiveJobEvent.Queued(liveJobCount, start, (double)range.Count / LiveChunker.SampleRate));
        queue.TryWrite(new LiveJob(liveJobCount, CollectionsMarshal.AsSpan(recordedSamples)[range.Start..range.End].ToArray(), start));
    }

    private void LiveChunkDone(IReadOnlyList<TranscriptSegment>? cues, Exception? failure, int index, int id)
    {
        if (session != id) return;
        LiveJobObserver?.Invoke(new LiveJobEvent.Finished(index, cues?.Count ?? 0, failure is null ? null : TranscriptionEngine.Describe(failure)));
        LiveChunksWaiting = Math.Max(0, LiveChunksWaiting - 1);
        if (cues is not null)
        {
            liveSegments.AddRange(cues);
            if (cues.LastOrDefault(cue => cue.Text.Length > 0) is { Text.Length: > 0 } last) LatestLiveLine = last.Text;
        }
        else if (failure is not null)
        {
            LiveNotice = Strings.LivePreviewMissedChunk(TranscriptionEngine.Describe(failure));
        }
        Notify();
    }

    // MARK: - Final pass

    private async Task RunFinalPassAsync(int id, CancellationToken token)
    {
        string location;
        try
        {
            location = sources.ModelPath(modelStore);
        }
        catch (WhisperEngineException error)
        {
            if (liveTask is { } live) await live.ConfigureAwait(true);
            if (session != id) return;
            FailTranscription(error.Message, missingModel: true);
            return;
        }
        var samples = recordedSamples.ToArray();
        // Lock the language first: live chunks still waiting for it are
        // transcribed in it, and the final pass uses it.
        await SettleLanguageAsync(samples, location, id).ConfigureAwait(true);
        // Finish the live preview first so "Use live preview instead" always has every chunk.
        if (liveTask is { } preview) await preview.ConfigureAwait(true);
        if (session != id || !IsTranscribing || IsUsingLivePreview) return;
        if (LanguageTracker.Language is not { } language)
        {
            FailTranscription(Strings.LanguageNotDecided);
            return;
        }
        try
        {
            var result = await engine.TranscribeAsync(location, samples, language, ProgressHandler(id), token).ConfigureAwait(true);
            if (session != id || !IsTranscribing || IsUsingLivePreview) return;
            CompleteTranscription(result.Cues(0, sessionChineseScript));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Cancelled by "Use live preview instead" or by quitting; whoever cancelled writes the SRT.
            if (TranscriptionEngine.IsCancellation(error)) return;
            if (session != id || !IsTranscribing || IsUsingLivePreview) return;
            FailTranscription(Strings.TranscriptionFailed(TranscriptionEngine.Describe(error)));
        }
    }

    private async Task RunRetryAsync(string recording, int id, CancellationToken token)
    {
        string location;
        try
        {
            location = sources.ModelPath(modelStore);
        }
        catch (WhisperEngineException error)
        {
            if (session != id) return;
            FailTranscription(error.Message, missingModel: true);
            return;
        }
        try
        {
            var samples = recordedSamples.Count > 0
                ? recordedSamples.ToArray()
                : await Task.Run(() => AudioFileLoader.LoadMono16k(recording), token).ConfigureAwait(true);
            await SettleLanguageAsync(samples, location, id).ConfigureAwait(true);
            if (session != id || !IsTranscribing) return;
            if (LanguageTracker.Language is not { } language)
            {
                FailTranscription(Strings.LanguageNotDecided);
                return;
            }
            var result = await engine.TranscribeAsync(location, samples, language, ProgressHandler(id), token).ConfigureAwait(true);
            if (session != id || !IsTranscribing || IsUsingLivePreview) return;
            CompleteTranscription(result.Cues(0, sessionChineseScript));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (TranscriptionEngine.IsCancellation(error)) return;
            if (session != id || !IsTranscribing || IsUsingLivePreview) return;
            FailTranscription(Strings.TranscriptionFailed(TranscriptionEngine.Describe(error)));
        }
    }

    /// <summary>
    /// "Transcribe again" after a finished recording: one full pass in
    /// <paramref name="language"/> over the same audio, written over the same
    /// SRT. On failure or cancellation the previous SRT stays and the phase
    /// returns to finished.
    /// </summary>
    private async Task RunRerunAsync(string srt, string? wav, float[]? held, TranscriptLanguage language, int id, CancellationToken token)
    {
        void Restore(string? message)
        {
            if (session != id) return;
            RerunError = message;
            Phase = new ControllerPhase.Finished(srt, wav);
            Notify();
        }
        try
        {
            string location;
            try
            {
                location = sources.ModelPath(modelStore);
            }
            catch (WhisperEngineException error)
            {
                Restore(Strings.CouldNotTranscribeAgain(error.Message));
                return;
            }
            float[] samples;
            if (held is not null)
            {
                samples = held;
            }
            else if (wav is not null)
            {
                samples = await Task.Run(() => AudioFileLoader.LoadMono16k(wav), token).ConfigureAwait(true);
            }
            else
            {
                Restore(Strings.CouldNotTranscribeAgainNotKept);
                return;
            }
            var result = await engine.TranscribeAsync(location, samples, language, ProgressHandler(id), token).ConfigureAwait(true);
            if (session != id || !IsTranscribing) return;
            if (!File.Exists(srt))
            {
                Restore(Strings.CouldNotTranscribeAgainMoved(Path.GetFileName(srt)));
                return;
            }
            TranscriptOutput.WriteSrt(result.Cues(0, sessionChineseScript), srt);
            rerunSamples = null;
            Restore(null);
            RequestNotes(srt);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Restore(TranscriptionEngine.IsCancellation(error) ? null : Strings.CouldNotTranscribeAgain(TranscriptionEngine.Describe(error)));
        }
        finally
        {
            if (session == id)
            {
                IsRerunning = false;
                Notify();
            }
        }
    }

    private Progress<double> ProgressHandler(int id) => new(value =>
    {
        if (session != id || !IsTranscribing) return;
        Phase = new ControllerPhase.Transcribing(Math.Clamp(value, 0, 1));
    });

    /// <summary>
    /// Writes <c>&lt;stem&gt;.srt</c> into the output folder beside the
    /// recording and then keeps or deletes the WAV as the setting says. After
    /// a failed pass the WAV is already in the output folder and its SRT
    /// (possibly the saved live preview) is replaced.
    /// <paramref name="keepRecording"/> overrides the setting (quit keeps the WAV).
    /// </summary>
    private void CompleteTranscription(IReadOnlyList<TranscriptSegment> cues, bool? keepRecording = null)
    {
        string folder;
        try
        {
            folder = TranscriptOutput.ResolveFolder(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FailTranscription(Strings.OutputFolderOpenFailed(error.Message));
            return;
        }

        // The WAV is either still in the spool (after a recording) or already
        // in the output folder (after a failed pass).
        if ((pendingSpoolWav ?? retryableRecording) is not { } source)
        {
            FailTranscription(Strings.RecordingMissing);
            return;
        }
        var inSpool = pendingSpoolWav is not null;
        var baseStem = Path.GetFileNameWithoutExtension(source);
        var directory = inSpool ? folder : Path.GetDirectoryName(source) ?? folder;
        var stem = inSpool ? TranscriptOutput.FreeStem(baseStem, directory, [".srt", ".wav"]) : baseStem;
        var srt = Path.Combine(directory, stem + ".srt");
        try
        {
            TranscriptOutput.WriteSrt(cues, srt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FailTranscription(Strings.CouldNotWriteTranscript(error.Message));
            return;
        }

        string? wav = null;
        if (keepRecording ?? settings.KeepRecording)
        {
            if (inSpool)
            {
                var destination = Path.Combine(directory, stem + ".wav");
                try
                {
                    File.Move(source, destination, overwrite: false);
                    wav = destination;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    wav = source;
                    LiveNotice = Strings.CouldNotMoveRecordingKeptAt(error.Message, source);
                }
            }
            else
            {
                wav = source;
            }
        }
        else
        {
            TryDelete(source);
        }

        pendingSpoolWav = null;
        retryableRecording = null;
        // A re-run needs the audio; hold it only when the WAV is gone.
        rerunSamples = LanguageNotice is not null && wav is null ? recordedSamples.ToArray() : null;
        recordedSamples = [];
        NeedsModel = false;
        FinishedTranscript = srt;
        FinishedRecording = wav;
        Phase = new ControllerPhase.Finished(srt, wav);
        RequestNotes(srt);
        Notify();
    }

    /// <summary>
    /// Keeps the WAV (moved to the output folder when it is still in the
    /// spool), saves the live preview as its SRT when there is one, and
    /// reports <paramref name="message"/> with the WAV path.
    /// </summary>
    private void FailTranscription(string message, bool missingModel = false)
    {
        var lines = new List<string> { message };
        var wav = pendingSpoolWav ?? retryableRecording;
        string? folder = null;
        try
        {
            folder = TranscriptOutput.ResolveFolder(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"recording: output folder: {error.Message}");
        }

        if (pendingSpoolWav is { } spoolWav)
        {
            if (folder is not null)
            {
                try
                {
                    wav = RecordingSpool.Finalize(spoolWav, keep: true, folder);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    wav = spoolWav;
                    lines.Add(Strings.CouldNotMoveRecording(error.Message));
                }
            }
            pendingSpoolWav = null;
        }

        if (wav is not null && liveSegments.Count > 0 && FinishedTranscript is null)
        {
            var srt = Path.ChangeExtension(wav, ".srt");
            try
            {
                TranscriptOutput.WriteSrt(liveSegments, srt);
                FinishedTranscript = srt;
                lines.Add(Strings.LivePreviewSavedAs(srt));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lines.Add(Strings.LivePreviewNotSaved(error.Message));
            }
        }
        if (wav is not null) lines.Add(Strings.RecordingKeptAt(wav));
        retryableRecording = wav;
        FinishedRecording = wav;
        NeedsModel = missingModel;
        Phase = new ControllerPhase.Failed(string.Join("\n", lines));
        Notify();
    }

    private void RequestNotes(string srt)
    {
        if (SessionLanguage is not { } language || SuppressNotesRequests) return;
        PendingNotesRequest = new NotesRequest(srt, language);
        NotesRequested?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - UI snapshots

    /// <summary>
    /// Debug only (<c>HEARSAY_UI_SNAPSHOTS</c>): shows a stubbed state without
    /// recording anything, as the Mac's snapshots render sample state.
    /// </summary>
    internal void ShowSample(RecordingSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Elapsed = sample.Elapsed;
        LevelFraction = sample.Level;
        MicLevelFraction = sample.MicLevel;
        SystemLevelFraction = sample.SystemLevel;
        SilenceWarning = sample.SilenceWarning;
        SystemAudioNotice = sample.SystemAudioNotice;
        liveSegments = [.. sample.LiveSegments];
        LiveChunksWaiting = sample.LiveChunksWaiting;
        LiveNotice = sample.LiveNotice;
        IsLivePreviewEnabled = sample.LivePreviewEnabled;
        LanguageTracker = sample.Tracker;
        LanguageNotice = sample.Notice;
        FinishedTranscript = sample.FinishedTranscript;
        FinishedRecording = sample.FinishedRecording;
        pendingSpoolWav = sample.CanUseLivePreview ? sample.FinishedRecording ?? "sample.wav" : null;
        rerunSamples = sample.Notice is not null ? Array.Empty<float>() : null;
        phase = sample.Phase;
        Notify();
    }

    /// <summary>Stops watching devices and releases the cancellation sources (at quit, after the recording was saved).</summary>
    public void Dispose()
    {
        deviceObservation?.Dispose();
        deviceObservation = null;
        watchdog?.Cancel();
        watchdog?.Dispose();
        watchdog = null;
        liveCancellation?.Dispose();
        liveCancellation = null;
        transcription?.Dispose();
        transcription = null;
    }

    // MARK: - Helpers

    private void Post(Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"recording: could not delete {path}: {error.Message}");
        }
    }

    /// <summary>Tells the views something changed (<paramref name="name"/>, or everything when null).</summary>
    private void Notify(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed record LiveJob(int Index, float[] Samples, double Start);
}

/// <summary>A live job's progress, for the debug replay's timing log (the Mac's <c>LiveJobEvent</c>).</summary>
internal abstract record LiveJobEvent
{
    private LiveJobEvent()
    {
    }

    public sealed record Queued(int Index, double Start, double Seconds) : LiveJobEvent;

    public sealed record Started(int Index) : LiveJobEvent;

    public sealed record Finished(int Index, int Cues, string? Error) : LiveJobEvent;

    /// <summary>A language detection over the first <paramref name="Seconds"/> finished; <paramref name="Decision"/> is set when it settled the session.</summary>
    public sealed record Detection(double Seconds, DetectionResult? Result, LanguageDecision? Decision) : LiveJobEvent;
}

/// <summary>A stubbed Record tab state for the UI snapshots.</summary>
internal sealed record RecordingSample(ControllerPhase Phase, SessionLanguageTracker Tracker)
{
    public double Elapsed { get; init; }

    public double Level { get; init; }

    public double MicLevel { get; init; }

    public double? SystemLevel { get; init; }

    public string? SilenceWarning { get; init; }

    public string? SystemAudioNotice { get; init; }

    public IReadOnlyList<TranscriptSegment> LiveSegments { get; init; } = [];

    public int LiveChunksWaiting { get; init; }

    public string? LiveNotice { get; init; }

    public bool LivePreviewEnabled { get; init; }

    public LanguageNotice? Notice { get; init; }

    public string? FinishedTranscript { get; init; }

    public string? FinishedRecording { get; init; }

    public bool CanUseLivePreview { get; init; }
}
