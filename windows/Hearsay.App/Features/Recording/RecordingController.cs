using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
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
/// <c>RecordingController.Phase</c>). A finished recording is no longer a
/// phase: Stop hands it to the <see cref="TranscriptionQueue"/> and the
/// controller is idle again (PLAN.md 4.9).
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

    /// <summary>The last recording or the last start had a problem; captured audio is at <see cref="RecordingController.FinishedRecording"/>.</summary>
    public sealed record Failed(string Message) : ControllerPhase;
}

/// <summary>
/// Owns the one recording session the app can run at a time (PLAN.md 4.1,
/// 4.4, 4.9). It lives in <see cref="AppShell"/>, not in a view, so closing
/// the main window never stops a recording; the Record tab, the tray menu,
/// the global hotkeys, and the quit prompt all drive this same object.
/// Port of mac/Hearsay/Features/Recording/RecordingController.swift.
/// </summary>
/// <remarks>
/// <para>The microphone and, when enabled, the system audio run through
/// <see cref="AudioMixer"/>; the mixed stream feeds the spool WAV and the main
/// meter, and each source's level feeds its small meter. System audio that
/// cannot start never blocks a recording: it goes on mic-only with a notice.
/// Elapsed time is derived from the number of samples written, so paused
/// time never counts and the display matches the WAV exactly.</para>
/// <para>Transcription (PLAN.md 4.1 step 4, 4.9): while recording,
/// <see cref="LiveChunker"/> cuts the mixed stream into chunks that the
/// engine transcribes in order for the live preview (through the session's
/// <see cref="LiveSink"/>). Stop closes the WAV and hands the session to the
/// <see cref="TranscriptionQueue"/> as a job (the WAV, the live segments and
/// the live tail still in flight, the language tracker and script, the
/// keep-recording choice); the controller is ready for the next Start at
/// once, and the queue runs the final pass. A capture problem keeps the WAV
/// and the live preview in the output folder instead (PLAN.md 4.1 failure
/// rules); "Try Again" queues that WAV. Without an installed model the
/// recording still works; the live preview is skipped and the job fails with
/// the WAV kept.</para>
/// <para>Phase transitions: idle/failed → starting → recording ⇄ paused →
/// stopping → idle (handed to the queue) or failed(message), as on the Mac.
/// <c>starting</c> can also end in idle (stop while starting) or failed.</para>
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
    private readonly TranscriptionQueue queue;
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
    private bool continuesWithNextSession;
    private bool isQuitting;
    private AudioDeviceListObservation? deviceObservation;
    private string recordingDeviceName = Strings.TheInputDevice;
    private MicrophoneRecorderException? noAudioFailure;
    private CancellationTokenSource? watchdog;

    // Transcription state
    private List<float> recordedSamples = [];
    private LiveChunker chunker = new();
    private int chunkedSamples;
    private ChannelWriter<LiveJob>? liveWriter;
    private LiveSink? liveSink;
    private EventHandler? liveSinkChanged;
    private string? retryableRecording;
    private int session;
    private int liveJobCount;
    private string? liveLocation;
    private Task? detectionTask;
    private CancellationTokenSource? detectionCancellation;
    private ChineseScript? sessionChineseScript;
    private List<TranscriptSegment> liveSegments = [];
    private bool? cpuRuntime;
    private bool checkingRuntime;
    private bool startedThisRun;

    public RecordingController(
        AppSettings settings, ModelStore modelStore, TranscriptionEngine engine, TranscriptionQueue queue,
        RecordingSpool spool, CaptureSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelStore);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(spool);
        this.settings = settings;
        this.modelStore = modelStore;
        this.engine = engine;
        this.queue = queue;
        this.spool = spool;
        this.sources = sources ?? CaptureSources.Live;
        context = SynchronizationContext.Current;
        LanguageTracker = new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A live job's progress, for the debug replay's timing log.</summary>
    public Action<LiveJobEvent>? LiveJobObserver { get; set; }

    /// <summary>Debug only: a session reached <see cref="ControllerPhase.Recording"/> (the replay measures the Stop &amp; Start Next gap with it); gets the session's WAV path.</summary>
    public Action<string>? SessionStartObserver { get; set; }

    /// <summary>The background queue the ended sessions go to.</summary>
    public TranscriptionQueue Queue => queue;

    public ControllerPhase Phase
    {
        get => phase;
        private set
        {
            if (phase == value) return;
            phase = value;
            queue.SetSessionActive(IsSessionActive);
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

    /// <summary>Where the recording of a capture failure was kept.</summary>
    public string? FinishedRecording { get; private set; }

    /// <summary>The live preview saved after a capture failure.</summary>
    public string? FinishedTranscript { get; private set; }

    /// <summary>Cues of the live preview of the current session, in recording time, in order.</summary>
    public IReadOnlyList<TranscriptSegment> LiveSegments => liveSegments;

    /// <summary>Live chunks of the current session queued or being transcribed.</summary>
    public int LiveChunksWaiting => liveSink?.Waiting ?? 0;

    /// <summary>The newest non-empty live line, for the tray.</summary>
    public string? LatestLiveLine { get; private set; }

    /// <summary>Why the live preview is off or incomplete, shown under it.</summary>
    public string? LiveNotice { get; private set; }

    /// <summary>
    /// PLAN.md 18.4, "Speed" (Windows only): on whisper.cpp's CPU runtime,
    /// until the first recording of this run starts, how long the final pass
    /// will take. The runtime is looked up when the Record tab activates with
    /// a model installed (<see cref="TranscriptionEngine.IsCpuRuntimeAsync"/>).
    /// </summary>
    public string? CpuSpeedNotice =>
        cpuRuntime == true && !startedThisRun && phase is ControllerPhase.Idle ? Strings.CpuFinalPassNotice : null;

    /// <summary>The current session transcribes live chunks.</summary>
    public bool IsLivePreviewEnabled { get; private set; }

    /// <summary>The last failure was the missing model; the view links to Models.</summary>
    public bool NeedsModel { get; private set; }

    /// <summary>A recording the File tab should pick up ("Transcribe this file"); the File tab clears it.</summary>
    public string? TranscribeFileRequest { get; set; }

    /// <summary>When the session detects its language and what it settled on (PLAN.md section 1, "Languages").</summary>
    public SessionLanguageTracker LanguageTracker { get; private set; }

    /// <summary>The suggestion or fallback banner of the current session.</summary>
    public LanguageNotice? LanguageNotice { get; private set; }

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

    /// <summary>The language of the current session; null while Auto is still undecided.</summary>
    public TranscriptLanguage? SessionLanguage => LanguageTracker.Language;

    /// <summary>The current or last session's decision, for the debug replay.</summary>
    public LanguageDecision? SessionDecision => LanguageTracker.Decision;

    /// <summary>Auto has not decided yet, and a model is there to decide it: "Detecting language…".</summary>
    public bool IsDetectingLanguage =>
        LanguageTracker.IsUndecided
        && phase is ControllerPhase.Recording or ControllerPhase.Paused or ControllerPhase.Stopping
        && IsLivePreviewEnabled;

    /// <summary>The language notice's buttons apply now: while recording, the rest of the live preview and the final pass use the picked language.</summary>
    public bool CanChangeSessionLanguage => phase is ControllerPhase.Recording or ControllerPhase.Paused or ControllerPhase.Stopping;

    /// <summary>A session is being set up, is running, or is being saved: recording settings are locked and quitting asks first.</summary>
    public bool IsSessionActive =>
        phase is ControllerPhase.Starting or ControllerPhase.Recording or ControllerPhase.Paused or ControllerPhase.Stopping;

    /// <summary>Start is allowed: nothing is recording. Finished recordings being transcribed in the queue never block it (PLAN.md 4.9).</summary>
    public bool CanStart => !IsSessionActive;

    /// <summary>The live preview has fallen more than one chunk behind.</summary>
    public bool IsLiveLagging => LiveChunksWaiting > 1;

    /// <summary>A capture failure's kept WAV can be queued again.</summary>
    public bool CanRetryTranscription => phase is ControllerPhase.Failed && retryableRecording is not null;

    /// <summary>Audio is being captured or is paused; Stop applies.</summary>
    public bool IsCapturing => phase is ControllerPhase.Recording or ControllerPhase.Paused;

    public string? ErrorMessage => phase is ControllerPhase.Failed failed ? failed.Message : null;

    // MARK: - Devices

    /// <summary>Loads the device list and starts watching for device changes. Safe to call more than once.</summary>
    public void Activate()
    {
        RefreshDevices();
        _ = CheckRuntimeAsync();
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

    /// <summary>Finds out once whether transcription runs on the CPU, for <see cref="CpuSpeedNotice"/>.</summary>
    private async Task CheckRuntimeAsync()
    {
        if (cpuRuntime is not null || checkingRuntime || startedThisRun || modelStore.Installed.Count == 0) return;
        checkingRuntime = true;
        try
        {
            cpuRuntime = await engine.IsCpuRuntimeAsync().ConfigureAwait(true);
        }
        finally
        {
            checkingRuntime = false;
        }
        Notify(nameof(CpuSpeedNotice));
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

    /// <summary>
    /// Starts a new recording unless one is active. Returns at once; the phase
    /// moves through starting to recording or failed. A plain Start clears
    /// finished queue rows whose notes already opened, as it cleared the
    /// finished card before; Stop &amp; Start Next keeps them.
    /// </summary>
    public void Start() => Start(clearingFinished: true);

    private void Start(bool clearingFinished)
    {
        if (!CanStart || isQuitting) return;
        BeginStart(clearingFinished);
    }

    private void BeginStart(bool clearingFinished)
    {
        startedThisRun = true;
        Phase = new ControllerPhase.Starting();
        if (clearingFinished) queue.DismissFinishedForNewSession();
        FinishedRecording = null;
        FinishedTranscript = null;
        retryableRecording = null;
        TranscribeFileRequest = null;
        NeedsModel = false;
        LanguageNotice = null;
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

    /// <summary>
    /// Stop &amp; Start Next (PLAN.md 4.9 item 2): the session becomes a queue
    /// job and a new one starts with the same input device, system-audio
    /// choice, and language choice (Auto detects again). The device and the
    /// two choices cannot change while a session is active, so the new session
    /// reads the same values. The new session starts right at the handover, so
    /// the session-active flag never drops in between (a whenIdle job does not
    /// start in the gap). A capture failure keeps its failure card and starts
    /// nothing. Returns at once.
    /// </summary>
    public void StopAndStartNext()
    {
        if (!IsCapturing || isQuitting) return;
        continuesWithNextSession = true;
        _ = StopAsync();
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
    /// Stops the recording and returns once the WAV is closed and handed to
    /// the queue (or kept after a capture problem). A start in progress is
    /// finished first. Does nothing when no session is active.
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
        // The consumer finishes once both streams drain, then hands over.
        if (consumer is { } running) await running.ConfigureAwait(true);
    }

    /// <summary>
    /// Quit: stops the session like <see cref="StopAsync"/>, but a Stop &amp;
    /// Start Next still in progress starts no new session, and nothing starts
    /// afterwards. The recording becomes a queue job the next launch continues.
    /// </summary>
    public async Task StopForQuitAsync()
    {
        isQuitting = true;
        continuesWithNextSession = false;
        await StopAsync().ConfigureAwait(true);
        // A Stop & Start Next may have started the next session just before.
        if (IsSessionActive) await StopAsync().ConfigureAwait(true);
    }

    /// <summary>The quit did not happen after all.</summary>
    public void QuitCancelled() => isQuitting = false;

    /// <summary>"Try Again" after a capture failure: the kept WAV goes to the queue.</summary>
    public void RetryTranscription()
    {
        if (!CanRetryTranscription || retryableRecording is not { } recording) return;
        queue.EnqueueRetry(
            new TranscriptOutput.PendingRecording(recording, InSpool: false), LanguageTracker, sessionChineseScript,
            settings.KeepRecording, liveSegments.ToList(), FinishedTranscript);
        retryableRecording = null;
        FinishedRecording = null;
        FinishedTranscript = null;
        NeedsModel = false;
        liveSegments = [];
        LiveNotice = null;
        LanguageNotice = null;
        Phase = ControllerPhase.IdleState;
        Notify();
    }

    /// <summary>
    /// A language notice button while recording: the rest of the live preview
    /// and the final pass use <paramref name="language"/>. Never changes the
    /// language choice or the preferred language.
    /// </summary>
    public void TranscribeAgain(TranscriptLanguage language)
    {
        if (!CanChangeSessionLanguage) return;
        LanguageTracker.Choose(language);
        LanguageNotice = null;
        LanguageSettled();
        Notify();
    }

    /// <summary>"Dismiss" on the suggestion banner.</summary>
    public void DismissLanguageNotice()
    {
        LanguageNotice = null;
        Notify();
    }

    /// <summary>Hands the kept WAV to the File tab ("Transcribe this file"); <paramref name="file"/> is the recording, or the last one kept after a failure.</summary>
    public void RequestTranscribeFile(string? file = null)
    {
        TranscribeFileRequest = file ?? retryableRecording ?? FinishedRecording;
        Notify(nameof(TranscribeFileRequest));
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
        SessionStartObserver?.Invoke(path);

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
        var continues = continuesWithNextSession;
        continuesWithNextSession = false;
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
        if (problems.Count > 0)
        {
            // Capture failed: keep what exists and let the user transcribe it.
            FailCapture(string.Join("\n", problems), closed);
            return;
        }
        HandOver(closed, startingNext: continues);
    }

    /// <summary>
    /// The session ended normally: it becomes a queue job with its live tail
    /// still in flight, and the controller is ready for the next Start.
    /// </summary>
    private void HandOver(string recording, bool startingNext)
    {
        // A detection still running belongs to this session; the job detects
        // over the whole recording when the language is still open.
        detectionCancellation?.Cancel();
        detectionCancellation = null;
        detectionTask = null;
        session += 1;
        var handover = new RecordingHandover(
            recording, DateTimeOffset.Now, LanguageTracker, sessionChineseScript, settings.KeepRecording,
            liveSegments.ToList(), IsLivePreviewEnabled, liveSink, LanguageNotice, LiveNotice);
        DetachLiveSink();
        recordedSamples = [];
        liveSegments = [];
        LatestLiveLine = null;
        LiveNotice = null;
        LanguageNotice = null;
        IsLivePreviewEnabled = false;
        // The job owns the tracker now; the next session makes its own.
        LanguageTracker = new SessionLanguageTracker(settings.LanguageChoice, settings.PreferredLanguage);
        sessionChineseScript = null;
        queue.Enqueue(handover);
        if (startingNext && !isQuitting)
        {
            BeginStart(clearingFinished: false);
        }
        else
        {
            Phase = ControllerPhase.IdleState;
        }
        Notify();
    }

    /// <summary>A capture problem: keeps the WAV (in the output folder) and the live preview as its SRT, and reports <paramref name="message"/> with the paths.</summary>
    private void FailCapture(string message, string recording)
    {
        var kept = TranscriptOutput.KeepAfterFailure(
            message, new TranscriptOutput.PendingRecording(recording, InSpool: true), liveSegments, null, settings);
        // "Try Again" reads the kept WAV.
        recordedSamples = [];
        retryableRecording = kept.Recording?.Path;
        FinishedRecording = kept.Recording?.Path;
        FinishedTranscript = kept.Srt;
        NeedsModel = false;
        Phase = new ControllerPhase.Failed(kept.Message);
        Notify();
    }


    // MARK: - Live preview

    private void ResetLivePreview()
    {
        liveWriter?.TryComplete();
        liveWriter = null;
        // A sink still here was never handed over; its chunks are dropped.
        liveSink?.Close();
        DetachLiveSink();
        liveSegments = [];
        LatestLiveLine = null;
        LiveNotice = null;
        IsLivePreviewEnabled = false;
        chunker = new LiveChunker();
        chunkedSamples = 0;
        liveJobCount = 0;
        liveLocation = null;
        detectionCancellation?.Cancel();
        detectionCancellation = null;
        detectionTask = null;
    }

    /// <summary>The session's sink goes (to the queue job, or away); the controller stops listening to it.</summary>
    private void DetachLiveSink()
    {
        if (liveSink is { } sink && liveSinkChanged is { } handler) sink.Changed -= handler;
        liveSink = null;
        liveSinkChanged = null;
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
        var id = session;
        var sink = new LiveSink(LanguageTracker.Language);
        sink.OnResult = (index, outcome) => LiveChunkDone(outcome, index, id);
        liveSinkChanged = (_, _) => Notify();
        sink.Changed += liveSinkChanged;
        liveSink = sink;
        // One consumer, so chunks are transcribed strictly in order. In Auto,
        // chunks that close before the language is decided wait in the sink.
        // After Stop the sink (and this task) belong to the queue job.
        sink.Task = RunLiveQueueAsync(sink, channel.Reader, location, engine.MeasureSpeedAsync(location), id);
    }

    private async Task RunLiveQueueAsync(
        LiveSink sink, ChannelReader<LiveJob> reader, string location, Task<SpeedProbeResult> speed, int id)
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
        if (!feasible)
        {
            sink.Close();
            if (session == id)
            {
                IsLivePreviewEnabled = false;
                LiveNotice = Strings.LivePreviewTooSlow;
                liveLocation = null;
                liveWriter?.TryComplete();
                liveWriter = null;
                Notify();
            }
        }
        await foreach (var job in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(true))
        {
            if (await sink.WaitForLanguageAsync().ConfigureAwait(true) is not { } language)
            {
                sink.Deliver(job.Index, null);
                continue;
            }
            var script = sink.Script;
            LiveJobObserver?.Invoke(new LiveJobEvent.Started(id, job.Index));
            SinkOutcome outcome;
            try
            {
                var result = await engine.TranscribeAsync(location, job.Samples, language).ConfigureAwait(true);
                var cues = result.Cues(job.Start, script);
                LiveJobObserver?.Invoke(new LiveJobEvent.Finished(id, job.Index, cues.Count, null));
                outcome = new SinkOutcome(cues, null);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LiveJobObserver?.Invoke(new LiveJobEvent.Finished(id, job.Index, 0, TranscriptionEngine.Describe(error)));
                outcome = new SinkOutcome(null, error);
            }
            sink.Deliver(job.Index, outcome);
        }
    }

    // MARK: - Session language

    /// <summary>The tracker settled or the user picked a language: the Chinese conversion, the banner, and waiting live jobs follow it.</summary>
    private void LanguageSettled()
    {
        if (LanguageTracker.Language is not { } language) return;
        sessionChineseScript = language.ChineseScript();
        liveSink?.SetLanguage(language);
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
        detectionCancellation = new CancellationTokenSource();
        detectionTask = RunDetectionAsync(location, samples, count, session, detectionCancellation.Token);
    }

    private async Task RunDetectionAsync(string location, float[] samples, int count, int id, CancellationToken token)
    {
        DetectionResult? result = null;
        try
        {
            result = await engine.DetectLanguageAsync(location, samples, token).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!TranscriptionEngine.IsCancellation(error)) AppLog.Write($"recording: detection failed: {error.Message}");
        }
        DetectionDone(result, count, id);
    }

    private void DetectionDone(DetectionResult? result, int samplesUsed, int id)
    {
        if (session != id) return;
        detectionTask = null;
        LastDetection = result;
        var decision = LanguageTracker.Record(result?.Detection, samplesUsed);
        LiveJobObserver?.Invoke(new LiveJobEvent.Detection(id, (double)samplesUsed / WavWriter.SampleRate, result, decision));
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
        if (liveWriter is not { } queueWriter || range.End > recordedSamples.Count) return;
        liveSink?.ChunkQueued();
        liveJobCount += 1;
        var start = (double)range.Start / LiveChunker.SampleRate;
        LiveJobObserver?.Invoke(new LiveJobEvent.Queued(session, liveJobCount, start, (double)range.Count / LiveChunker.SampleRate));
        queueWriter.TryWrite(new LiveJob(liveJobCount, CollectionsMarshal.AsSpan(recordedSamples)[range.Start..range.End].ToArray(), start));
    }

    private void LiveChunkDone(SinkOutcome outcome, int index, int id)
    {
        if (session != id) return;
        if (outcome.Cues is { } cues)
        {
            liveSegments.AddRange(cues);
            if (cues.LastOrDefault(cue => cue.Text.Length > 0) is { Text.Length: > 0 } last) LatestLiveLine = last.Text;
        }
        else if (outcome.Error is { } error)
        {
            LiveNotice = Strings.LivePreviewMissedChunk(TranscriptionEngine.Describe(error));
        }
        Notify();
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
        DetachLiveSink();
        if (sample.LiveChunksWaiting > 0)
        {
            liveSink = new LiveSink(null);
            for (var i = 0; i < sample.LiveChunksWaiting; i++) liveSink.ChunkQueued();
        }
        LiveNotice = sample.LiveNotice;
        IsLivePreviewEnabled = sample.LivePreviewEnabled;
        LanguageTracker = sample.Tracker;
        LanguageNotice = sample.Notice;
        FinishedTranscript = sample.FinishedTranscript;
        FinishedRecording = sample.FinishedRecording;
        retryableRecording = sample.FinishedRecording;
        cpuRuntime = sample.CpuRuntime;
        phase = sample.Phase;
        queue.SetSessionActive(IsSessionActive);
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
        detectionCancellation?.Cancel();
        detectionCancellation = null;
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

/// <summary>
/// A live job's progress, for the debug replay's timing log (the Mac's
/// <c>LiveJobEvent</c>). <c>Session</c> tells the sessions apart: the tail of
/// an ended session still reports after the next one started.
/// </summary>
internal abstract record LiveJobEvent
{
    private LiveJobEvent()
    {
    }

    public sealed record Queued(int Session, int Index, double Start, double Seconds) : LiveJobEvent;

    public sealed record Started(int Session, int Index) : LiveJobEvent;

    public sealed record Finished(int Session, int Index, int Cues, string? Error) : LiveJobEvent;

    /// <summary>A language detection over the first <paramref name="Seconds"/> finished; <paramref name="Decision"/> is set when it settled the session.</summary>
    public sealed record Detection(int Session, double Seconds, DetectionResult? Result, LanguageDecision? Decision) : LiveJobEvent;
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

    /// <summary>True shows the CPU runtime's final-pass notice while idle.</summary>
    public bool? CpuRuntime { get; init; }
}
