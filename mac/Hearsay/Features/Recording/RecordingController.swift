import AppKit
import AVFoundation
import HearsayCore
import HearsayWhisper
import Observation

/// Owns the one recording session the app can run at a time (PLAN.md 4.1,
/// 4.4). It lives in `AppDelegate`, not in a view, so closing the main
/// window never stops a recording; the Record tab, the menu bar extra, the
/// global hotkeys, and the quit prompt all drive this same object.
///
/// The microphone and, when enabled and permitted, the system audio run
/// through `AudioMixer`; the mixed stream feeds the spool WAV and the main
/// meter, and each source's level feeds its small meter. A missing
/// screen-capture permission never blocks a recording: it goes on mic-only
/// with a notice.
///
/// Elapsed time is derived from the number of samples written, so paused
/// time never counts and the display matches the WAV exactly.
///
/// Transcription (PLAN.md 4.1 step 4, 4.9): while recording, `LiveChunker`
/// cuts the mixed stream into chunks that `WhisperEngine` transcribes in
/// order for the live preview (through the session's `LiveSink`). Stop
/// closes the WAV and hands the session to `TranscriptionQueue` as a job
/// (the WAV, the live segments and the live tail still in flight, the
/// language tracker and script, the keep-recording choice); the controller
/// is ready for the next Start at once, and the queue runs the final pass.
/// A capture problem keeps the WAV and the live preview in the output
/// folder instead (PLAN.md 4.1 failure rules); "Try Again" queues that WAV.
/// Without an installed model the recording still works; the live preview
/// is skipped and the job fails with the WAV kept.
///
/// Phase transitions:
/// idle/failed -> starting -> recording <-> paused -> stopping -> idle
/// (handed to the queue) or failed(message:).
/// `starting` can also end in idle (stop while starting) or failed; a
/// capture problem goes from stopping straight to failed with the WAV kept;
/// a microphone that delivers nothing within `NoAudioWatchdog.timeout`
/// seconds stops the recording, and a recording with no samples at all goes
/// from stopping to failed with the empty WAV deleted.
@MainActor
@Observable
final class RecordingController {
    enum Phase: Equatable {
        case idle
        case starting
        case recording
        case paused
        case stopping
        /// The last recording or the last start attempt had a problem. Any
        /// audio that was captured is still at `finishedRecording`.
        case failed(message: String)
    }

    static let screenCaptureSettingsURL = URL(
        string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
    )

    private(set) var phase: Phase = .idle {
        didSet {
            guard phase != oldValue else { return }
            queue.setSessionActive(isSessionActive)
            phaseObserver?(phase)
        }
    }
    /// The Record tab's notice for a recording that Hearsay started for a
    /// Microsoft Teams meeting (PLAN.md 4.10). Set and cleared by
    /// `MeetingAutoRecord`; a plain `start()` clears it.
    var automaticStartNotice: String?
    /// Called after every phase change, in order, on the main actor
    /// (`MeetingAutoRecord` follows the session this way).
    @ObservationIgnored var phaseObserver: (@MainActor (Phase) -> Void)?
    private(set) var devices: [AudioInputDevice] = []
    var selectedDeviceUID: String?
    private(set) var elapsed: TimeInterval = 0
    private(set) var levelFraction: Double = 0
    private(set) var micLevelFraction: Double = 0
    /// nil when system audio is not part of the current recording.
    private(set) var systemLevelFraction: Double?
    /// Why system audio is not being captured, shown as a badge.
    private(set) var systemAudioNotice: String?
    /// The notice is about the Screen & System Audio Recording permission,
    /// so the badge offers to open System Settings.
    private(set) var systemAudioDenied = false
    private(set) var silenceWarning: String?
    /// Where the recording of a capture failure was kept.
    private(set) var finishedRecording: URL?
    /// The live preview saved after a capture failure.
    private(set) var finishedTranscript: URL?

    // Live preview
    /// Cues of the live preview, in recording time, in order.
    private(set) var liveSegments: [CoreSegment] = []
    /// The newest non-empty live line, for the menu bar.
    private(set) var latestLiveLine: String?
    /// Why the live preview is off or incomplete, shown under it.
    private(set) var liveNotice: String?
    /// The current session transcribes live chunks.
    private(set) var isLivePreviewEnabled = false
    /// The current session has no live preview because Settings turned it
    /// off (PLAN.md 4.12); read once at each Start.
    private(set) var isLivePreviewTurnedOff = false
    /// The last failure was the missing model; the view links to Models.
    private(set) var needsModel = false
    /// A recording the File tab should pick up ("Transcribe this file").
    /// The File tab clears it once taken.
    var transcribeFileRequest: URL?
    /// When the session detects its language and what it settled on
    /// (PLAN.md section 1, "Languages"). In Auto the language is undecided
    /// at Start; detection runs each time another 30 s of audio exists until
    /// it is confident or 90 s have passed, then the language is locked.
    private(set) var languageTracker = SessionLanguageTracker(choice: .auto, preferred: .english)
    /// The suggestion or fallback banner of the current recording.
    private(set) var languageNotice: LanguageNotice?
    /// The last detection result, for the debug replay.
    @ObservationIgnored private(set) var lastDetection: DetectionResult?
    /// Script the session's cues are converted to (zh only).
    @ObservationIgnored private var sessionChineseScript: ChineseScript?
    /// The current session's live sink; it moves to the queue job at Stop.
    private var liveSink: LiveSink?

    /// The background transcription queue the ended sessions go to.
    let queue: TranscriptionQueue
    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let modelStore: ModelStore
    @ObservationIgnored private let engine: WhisperEngine
    @ObservationIgnored private let spool: RecordingSpool
    @ObservationIgnored private let sources: CaptureSources
    @ObservationIgnored private var recorder: (any MicrophoneCapture)?
    @ObservationIgnored private var systemRecorder: (any SystemAudioCapture)?
    @ObservationIgnored private var writer: WavWriter?
    @ObservationIgnored private var writeError: Error?
    @ObservationIgnored private var meter = LevelMeter()
    @ObservationIgnored private var sampleCount = 0
    @ObservationIgnored private var startTask: Task<Void, Never>?
    @ObservationIgnored private var consumer: Task<Void, Never>?
    @ObservationIgnored private var stopRequestedWhileStarting = false
    /// Stop & Start Next was pressed: the handover starts the next session.
    @ObservationIgnored private var continuesWithNextSession = false
    /// Hearsay is quitting: no new session starts (Stop & Start Next, hotkeys).
    @ObservationIgnored private var isQuitting = false
    @ObservationIgnored private var deviceObservation: AudioDeviceListObservation?
    @ObservationIgnored private var engineObservation: NotificationToken?
    /// Name of the device being recorded, for error messages.
    @ObservationIgnored private var recordingDeviceName = String(
        localized: "the input device", comment: "Used in recording errors when the device has no name")
    /// Set when the watchdog stopped a recording that never got audio.
    @ObservationIgnored private var noAudioFailure: MicrophoneRecorderError?
    @ObservationIgnored private var watchdogTask: Task<Void, Never>?

    // Transcription state
    /// Every mixed sample of the current recording, for the live chunks and
    /// the language detection. Dropped at Stop: the queue reads the WAV.
    @ObservationIgnored private var recordedSamples: [Float] = []
    @ObservationIgnored private var chunker = LiveChunker()
    /// Samples already measured and handed to `chunker`.
    @ObservationIgnored private var chunkedSamples = 0
    @ObservationIgnored private var liveContinuation: AsyncStream<LiveJob>.Continuation?
    /// The kept WAV of a capture failure, for `retryTranscription()`.
    @ObservationIgnored private var retryableRecording: TranscriptOutput.PendingRecording?
    /// Bumped on every start and handover; late results of an older session
    /// are ignored.
    @ObservationIgnored private var session = 0
    /// Debug only: sees every live job (see `RecordingReplay`).
    @ObservationIgnored var liveJobObserver: (@MainActor (LiveJobEvent) -> Void)?
    /// Debug only: a session reached `.recording` (see `RecordingReplay`).
    @ObservationIgnored var sessionStartObserver: (@MainActor (URL) -> Void)?
    @ObservationIgnored private var liveJobCount = 0
    /// The model the live preview and the detection use this session.
    @ObservationIgnored private var liveLocation: WhisperModelLocation?
    /// A detection attempt in flight during the recording.
    @ObservationIgnored private var detectionTask: Task<Void, Never>?

    /// A live job's progress, for the debug replay's timing log.
    /// `session` tells the sessions apart (the tail of an ended session
    /// still reports after the next one started).
    enum LiveJobEvent: Sendable {
        case queued(session: Int, index: Int, start: TimeInterval, seconds: TimeInterval)
        case started(session: Int, index: Int)
        case finished(session: Int, index: Int, cues: Int, error: String?)
        /// A language detection over the first `seconds` of audio finished;
        /// `decision` is set when it settled the session.
        case detection(session: Int, seconds: TimeInterval, result: DetectionResult?, decision: LanguageDecision?)
    }

    private struct LiveJob: Sendable {
        var index: Int
        var samples: [Float]
        /// Offset of the chunk in the recording, in seconds.
        var start: TimeInterval
    }

    init(
        settings: AppSettings,
        modelStore: ModelStore,
        engine: WhisperEngine,
        queue: TranscriptionQueue,
        spool: RecordingSpool = RecordingSpool(),
        sources: CaptureSources = .live
    ) {
        self.settings = settings
        self.modelStore = modelStore
        self.engine = engine
        self.queue = queue
        self.spool = spool
        self.sources = sources
    }

    // MARK: - State

    /// The Record tab picker: Auto or a fixed language, persisted.
    var languageChoice: LanguageChoice {
        get { settings.languageChoice }
        set { settings.languageChoice = newValue }
    }

    /// The language of the current recording: what the live preview uses.
    /// nil while Auto is still undecided.
    var sessionLanguage: TranscriptLanguage? { languageTracker.language }

    /// The current or last session's decision, for the debug replay.
    var sessionDecision: LanguageDecision? { languageTracker.decision }

    /// Auto has not decided the session language yet, and a model is there
    /// to decide it: the live area and the menu bar say "Detecting language…".
    var isDetectingLanguage: Bool {
        guard languageTracker.isUndecided else { return false }
        switch phase {
        case .recording, .paused, .stopping: return isLivePreviewEnabled
        case .idle, .starting, .failed: return false
        }
    }

    /// The language notice's buttons apply now: while recording, the rest
    /// of the live preview and the final pass use the picked language.
    var canChangeSessionLanguage: Bool {
        switch phase {
        case .recording, .paused, .stopping: true
        case .idle, .starting, .failed: false
        }
    }

    /// "Also capture system audio", persisted as the default.
    var captureSystemAudio: Bool {
        get { settings.captureSystemAudio }
        set { settings.captureSystemAudio = newValue }
    }

    /// A session is being set up, is running, or is being saved. Settings
    /// that shape the recording are locked and quitting asks first.
    var isSessionActive: Bool {
        switch phase {
        case .starting, .recording, .paused, .stopping: true
        case .idle, .failed: false
        }
    }

    /// Start is allowed: nothing is recording. Finished recordings being
    /// transcribed in the queue never block it (PLAN.md 4.9).
    var canStart: Bool {
        !isSessionActive
    }

    /// Live chunks of the current session queued or being transcribed.
    var liveChunksWaiting: Int { liveSink?.waiting ?? 0 }

    /// The live preview has fallen more than one chunk behind.
    var isLiveLagging: Bool {
        liveChunksWaiting > 1
    }

    /// A capture failure's kept WAV can be queued again.
    var canRetryTranscription: Bool {
        if case .failed = phase, retryableRecording != nil { return true }
        return false
    }

    /// Audio is being captured or is paused; Stop applies.
    var isCapturing: Bool {
        phase == .recording || phase == .paused
    }

    var errorMessage: String? {
        if case .failed(let message) = phase { return message }
        return nil
    }

    // MARK: - Devices

    /// Loads the device list and starts watching for device changes. Safe to
    /// call more than once.
    func activate() {
        refreshDevices()
        guard deviceObservation == nil else { return }
        deviceObservation = AudioDeviceList.observeChanges { [weak self] in
            // Delivered on the main queue.
            MainActor.assumeIsolated { self?.refreshDevices() }
        }
        let token = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshDevices() }
        }
        engineObservation = NotificationToken(token)
    }

    func refreshDevices() {
        devices = AudioDeviceList.inputDevices()
        if let selectedDeviceUID, devices.contains(where: { $0.uid == selectedDeviceUID }) {
            return
        }
        selectedDeviceUID = AudioDeviceList.defaultInputDevice()?.uid ?? devices.first?.uid
    }

    // MARK: - Controls

    /// Starts a new recording unless one is already active. Returns at once;
    /// `phase` moves through `.starting` to `.recording` or `.failed`.
    /// A plain Start clears finished queue rows whose notes already opened,
    /// as it cleared the finished card before; Stop & Start Next keeps them.
    func start() {
        // A session the user starts is not an automatic one (PLAN.md 4.10).
        if canStart { automaticStartNotice = nil }
        start(clearingFinished: true)
    }

    private func start(clearingFinished: Bool) {
        guard canStart, !isQuitting else { return }
        beginStart(clearingFinished: clearingFinished)
    }

    private func beginStart(clearingFinished: Bool) {
        phase = .starting
        if clearingFinished { queue.dismissFinishedForNewSession() }
        finishedRecording = nil
        finishedTranscript = nil
        retryableRecording = nil
        transcribeFileRequest = nil
        needsModel = false
        languageNotice = nil
        session += 1
        resetLivePreview()
        stopRequestedWhileStarting = false
        startTask = Task { [weak self] in
            await self?.performStart()
            self?.startTask = nil
        }
    }

    /// Stop & Start Next (PLAN.md 4.9 item 2): the session becomes a queue
    /// job and a new one starts with the same input device, system-audio
    /// choice, and language choice (Auto detects again). The device and
    /// the two choices cannot change while a session is active, so the new
    /// session reads the same values.
    ///
    /// The new session starts right at the handover, so the session-active
    /// flag never drops in between (a whenIdle job does not start in the
    /// gap). A capture failure keeps its failure card and starts nothing.
    func stopAndStartNext() {
        guard isCapturing, !isQuitting else { return }
        continuesWithNextSession = true
        Task { await stop() }
    }

    func pause() {
        guard phase == .recording, let recorder else { return }
        recorder.pause()
        systemRecorder?.pause()
        phase = .paused
        levelFraction = 0
        micLevelFraction = 0
        if systemLevelFraction != nil {
            systemLevelFraction = 0
        }
    }

    func resume() {
        guard phase == .paused, let recorder else { return }
        do {
            try recorder.resume()
            systemRecorder?.resume()
            phase = .recording
        } catch {
            // The recorder stopped itself and recorded the failure;
            // `recordingEnded` saves what exists and reports it.
        }
    }

    /// Start when nothing is recording, Stop when something is (global
    /// hotkey and menu bar button).
    func toggleStartStop() {
        if isCapturing || phase == .starting {
            Task { await stop() }
        } else {
            start()
        }
    }

    func togglePause() {
        switch phase {
        case .recording: pause()
        case .paused: resume()
        default: break
        }
    }

    /// Stops the recording and returns once the WAV is closed and handed to
    /// the queue (or kept after a capture problem). A start in progress is
    /// cancelled first. Does nothing when no session is active.
    func stop() async {
        if phase == .starting {
            stopRequestedWhileStarting = true
            await startTask?.value
        }
        if isCapturing {
            phase = .stopping
            recorder?.stop()
            systemRecorder?.stop()
        }
        // The consumer finishes once both streams drain, then saves.
        await consumer?.value
    }

    /// Quit: stops the session like `stop()`, but a Stop & Start Next still
    /// in progress starts no new session, and nothing starts afterwards.
    func stopForQuit() async {
        isQuitting = true
        continuesWithNextSession = false
        await stop()
        // A Stop & Start Next may have started the next session just before.
        if isSessionActive { await stop() }
    }

    /// The quit did not happen after all (a restart failed).
    func quitCancelled() {
        isQuitting = false
    }

    func revealInFinder() {
        let files = [finishedTranscript, finishedRecording].compactMap { $0 }
        guard !files.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(files)
    }

    /// "Try Again" after a capture failure: the kept WAV goes to the queue.
    func retryTranscription() {
        guard canRetryTranscription, let recording = retryableRecording else { return }
        queue.enqueueRetry(
            recording: recording, tracker: languageTracker, chineseScript: sessionChineseScript,
            keepRecording: settings.keepRecording, liveSegments: liveSegments, transcript: finishedTranscript
        )
        retryableRecording = nil
        finishedRecording = nil
        finishedTranscript = nil
        needsModel = false
        liveSegments = []
        liveNotice = nil
        languageNotice = nil
        phase = .idle
    }

    /// A language notice button while recording: the rest of the live
    /// preview and the final pass use `language`. Never changes
    /// `languageChoice` or `preferredLanguage`.
    func transcribeAgain(in language: TranscriptLanguage) {
        guard canChangeSessionLanguage else { return }
        languageTracker.choose(language)
        languageNotice = nil
        languageSettled()
    }

    /// "Dismiss" on the suggestion banner.
    func dismissLanguageNotice() {
        languageNotice = nil
    }

    /// Hands the kept WAV to the File tab ("Transcribe this file").
    func requestTranscribeFile() {
        transcribeFileRequest = retryableRecording?.url ?? finishedRecording
    }

    func openScreenCaptureSettings() {
        guard let url = Self.screenCaptureSettingsURL else { return }
        NSWorkspace.shared.open(url)
    }

    /// Shows a session recording `elapsed` seconds, with the live preview
    /// as Settings has it and no capture behind it (UI snapshots only).
    func showSampleRecording(elapsed: TimeInterval) {
        guard !isSessionActive else { return }
        languageTracker = SessionLanguageTracker(
            choice: settings.languageChoice, preferred: settings.preferredLanguage
        )
        startLivePreview()
        self.elapsed = elapsed
        levelFraction = 0.6
        micLevelFraction = 0.6
        phase = .recording
    }

    /// Ends `showSampleRecording(elapsed:)` (UI snapshots only).
    func endSampleRecording() {
        guard recorder == nil, isCapturing else { return }
        resetLivePreview()
        elapsed = 0
        levelFraction = 0
        micLevelFraction = 0
        phase = .idle
    }

    // MARK: - Start

    private func performStart() async {
        guard await sources.requestMicrophonePermission() else {
            phase = .failed(message: MicrophoneRecorderError.permissionDenied.description)
            return
        }
        if stopRequestedWhileStarting {
            phase = .idle
            return
        }

        systemAudioNotice = nil
        systemAudioDenied = false
        let systemRecorder = settings.captureSystemAudio ? await startSystemAudio() : nil
        if stopRequestedWhileStarting {
            systemRecorder?.stop()
            phase = .idle
            return
        }

        refreshDevices()
        let device = devices.first { $0.uid == selectedDeviceUID }
        let url = spool.newRecordingURL(timestamp: Timestamps.now())
        let writer: WavWriter
        do {
            writer = try WavWriter(url: url)
        } catch {
            systemRecorder?.stop()
            phase = .failed(message: String(localized: "Could not create the recording file: \(error.localizedDescription)",
                                            comment: "Recording error. %@ is the system error message."))
            return
        }
        let recorder = sources.makeMicrophone()
        do {
            try recorder.start(device: device)
        } catch {
            systemRecorder?.stop()
            try? writer.close()
            try? FileManager.default.removeItem(at: url)
            phase = .failed(message: String(describing: error))
            return
        }

        self.writer = writer
        self.recorder = recorder
        self.systemRecorder = systemRecorder
        recordingDeviceName = recorder.diagnostics.deviceName ?? device?.name
            ?? String(localized: "the input device", comment: "Used in recording errors when the device has no name")
        noAudioFailure = nil
        writeError = nil
        meter = LevelMeter()
        sampleCount = 0
        elapsed = 0
        levelFraction = 0
        micLevelFraction = 0
        systemLevelFraction = systemRecorder == nil ? nil : 0
        silenceWarning = nil
        recordedSamples = []
        languageTracker = SessionLanguageTracker(
            choice: settings.languageChoice, preferred: settings.preferredLanguage
        )
        lastDetection = nil
        sessionChineseScript = languageTracker.language?.chineseScript
        startLivePreview()
        phase = .recording
        sessionStartObserver?(url)

        let mixed = AudioMixer.mix(
            mic: recorder.timedSamples,
            system: systemRecorder?.timedSamples
        ) { [weak self] source in
            Task { @MainActor in self?.sourceEnded(source) }
        }
        consumer = Task { [weak self] in
            for await chunk in mixed {
                self?.consume(chunk)
            }
            self?.recordingEnded()
        }
        startNoAudioWatchdog(for: recorder)
    }

    /// Stops the recording loudly when the microphone delivers nothing
    /// within `NoAudioWatchdog.timeout` seconds of recording, instead of
    /// silently recording nothing.
    private func startNoAudioWatchdog(for recorder: any MicrophoneCapture) {
        watchdogTask?.cancel()
        let id = session
        watchdogTask = Task { [weak self, weak recorder] in
            var watchdog = NoAudioWatchdog()
            while !Task.isCancelled {
                guard let self, let recorder, self.session == id, self.isCapturing,
                      self.recorder === recorder
                else { return }
                let delivered = recorder.diagnostics.samplesDelivered
                if delivered > 0 { return }
                let fired = watchdog.check(
                    now: ProcessInfo.processInfo.systemUptime,
                    isRecording: self.phase == .recording,
                    samplesDelivered: delivered
                )
                if fired {
                    self.failNoAudio()
                    return
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
    }

    private func failNoAudio() {
        guard isCapturing else { return }
        noAudioFailure = .noAudio(deviceName: recordingDeviceName)
        phase = .stopping
        recorder?.stop()
        systemRecorder?.stop()
    }

    /// The no-audio message plus what capture saw, for the error display.
    private func noAudioMessage(_ error: MicrophoneRecorderError) -> String {
        guard let diagnostics = recorder?.diagnostics else { return error.description }
        var detail = "Capture details: \(diagnostics.callbacks) buffers received"
        if diagnostics.conversionFailures > 0 {
            detail += ", \(diagnostics.conversionFailures) failed to convert"
            if let last = diagnostics.lastConversionError { detail += " (\(last))" }
        }
        if let last = diagnostics.lastRuntimeError {
            detail += ", capture error: \(last)"
        }
        return error.description + "\n" + detail + "."
    }

    /// Starts system audio capture, or records why it is off and returns nil
    /// so the recording continues mic-only.
    private func startSystemAudio() async -> (any SystemAudioCapture)? {
        if let make = sources.makeSystemAudio {
            do {
                return try await make()
            } catch {
                systemAudioNotice = String(localized: "System audio off: \(String(describing: error))",
                                           comment: "Record tab notice. %@ is the reason.")
                return nil
            }
        }
        if SystemAudioRecorder.permission != .authorized, !SystemAudioRecorder.requestPermission() {
            systemAudioDenied = true
            systemAudioNotice = String(localized: "System audio off: permission denied",
                                       comment: "Record tab notice: no Screen & System Audio Recording permission")
            return nil
        }
        let recorder = SystemAudioRecorder()
        do {
            try await recorder.start()
            return recorder
        } catch SystemAudioRecorderError.permissionDenied {
            systemAudioDenied = true
            systemAudioNotice = String(localized: "System audio off: permission denied",
                                       comment: "Record tab notice: no Screen & System Audio Recording permission")
        } catch {
            systemAudioNotice = String(localized: "System audio off: \(String(describing: error))",
                                           comment: "Record tab notice. %@ is the reason.")
        }
        return nil
    }

    // MARK: - Pipeline

    private func consume(_ chunk: MixedChunk) {
        guard let writer, writeError == nil else { return }
        do {
            try writer.append(chunk.mixed)
        } catch {
            writeError = error
            recorder?.stop()
            systemRecorder?.stop()
            return
        }
        sampleCount += chunk.mixed.count
        recordedSamples.append(contentsOf: chunk.mixed)
        if liveContinuation != nil {
            feedChunker(final: false)
        }
        startDetectionIfDue()
        let time = Double(sampleCount) / Double(WavWriter.sampleRate)
        let level = LevelMeter.rmsDB(floatSamples: chunk.mixed)
        guard meter.observe(rmsDB: level, at: time) else { return }
        elapsed = time
        levelFraction = LevelMeter.levelFraction(rmsDB: level)
        micLevelFraction = LevelMeter.levelFraction(rmsDB: chunk.micRMSDB)
        if systemLevelFraction != nil {
            systemLevelFraction = LevelMeter.levelFraction(rmsDB: chunk.systemRMSDB)
        }
        silenceWarning = meter.isSilenceWarning
            ? String(localized: "Silent for \(Int(meter.silenceSeconds))s \u{2014} check the input device",
                     comment: "Silence warning while recording. %lld is a number of seconds.")
            : nil
    }

    /// A source's stream finished. The microphone ending on its own (device
    /// unplugged) ends the recording; system audio ending on its own leaves
    /// the recording going mic-only with a notice.
    private func sourceEnded(_ source: AudioMixer.Source) {
        guard isCapturing else { return }
        switch source {
        case .mic:
            if recorder?.failure != nil {
                phase = .stopping
                systemRecorder?.stop()
            }
        case .system:
            if let failure = systemRecorder?.failure {
                systemAudioNotice = String(localized: "System audio off: \(failure.description)",
                                           comment: "Record tab notice. %@ is the reason.")
                systemLevelFraction = nil
            }
        }
    }

    private func recordingEnded() {
        phase = .stopping
        let continues = continuesWithNextSession
        continuesWithNextSession = false
        watchdogTask?.cancel()
        watchdogTask = nil
        elapsed = Double(sampleCount) / Double(WavWriter.sampleRate)
        var problems: [String] = []
        let micDelivered = recorder?.diagnostics.samplesDelivered ?? 0
        if let noAudioFailure {
            problems.append(noAudioMessage(noAudioFailure))
        } else if sampleCount == 0, micDelivered == 0, recorder?.failure == nil {
            // Stopped before the watchdog fired, with nothing captured.
            problems.append(noAudioMessage(.noAudio(deviceName: recordingDeviceName)))
        }
        noAudioFailure = nil
        if let failure = recorder?.failure {
            problems.append(failure.description)
            refreshDevices()
        }
        if let writeError {
            problems.append(String(localized: "Writing the recording failed: \(writeError.localizedDescription)",
                                   comment: "Recording error. %@ is the system error message."))
        }

        var closed: URL?
        if let writer {
            closed = writer.url
            do {
                try writer.close()
            } catch {
                problems.append(String(localized: "Closing the recording failed: \(error.localizedDescription)",
                                       comment: "Recording error. %@ is the system error message."))
            }
        }

        recorder = nil
        systemRecorder = nil
        writer = nil
        consumer = nil
        levelFraction = 0
        micLevelFraction = 0
        systemLevelFraction = nil
        silenceWarning = nil

        // The live preview gets the tail, then its queue closes.
        if liveContinuation != nil {
            feedChunker(final: true)
        }
        liveContinuation?.finish()
        liveContinuation = nil

        guard let closed else {
            phase = problems.isEmpty ? .idle : .failed(message: problems.joined(separator: "\n"))
            return
        }
        if sampleCount == 0 {
            // Nothing was captured: never keep a zero-length recording.
            try? FileManager.default.removeItem(at: closed)
            var lines = problems.isEmpty
                ? [MicrophoneRecorderError.noAudio(deviceName: recordingDeviceName).description]
                : problems
            lines.append(String(localized: "Nothing was recorded, so no file was kept.", comment: "Recording error"))
            phase = .failed(message: lines.joined(separator: "\n"))
            return
        }
        if !problems.isEmpty {
            // Capture failed: keep what exists and let the user transcribe it.
            failCapture(problems.joined(separator: "\n"), recording: closed)
            return
        }
        handOver(closed, startingNext: continues)
    }

    /// The session ended normally: it becomes a queue job with its live
    /// tail still in flight, and the controller is ready for the next Start.
    private func handOver(_ recording: URL, startingNext: Bool) {
        // A detection still running belongs to this session; the job
        // detects over the whole recording when the language is still open.
        detectionTask?.cancel()
        detectionTask = nil
        session += 1
        let handover = RecordingHandover(
            recording: recording,
            stopTime: Date(),
            tracker: languageTracker,
            chineseScript: sessionChineseScript,
            keepRecording: settings.keepRecording,
            liveSegments: liveSegments,
            liveEnabled: isLivePreviewEnabled,
            liveSink: liveSink,
            languageNotice: languageNotice,
            // "Live preview is off" is about recording; the job has nothing
            // to show under it.
            liveNotice: isLivePreviewTurnedOff ? nil : liveNotice
        )
        liveSink = nil
        recordedSamples = []
        liveSegments = []
        latestLiveLine = nil
        liveNotice = nil
        languageNotice = nil
        isLivePreviewEnabled = false
        isLivePreviewTurnedOff = false
        queue.enqueue(handover)
        if startingNext, !isQuitting {
            beginStart(clearingFinished: false)
        } else {
            phase = .idle
        }
    }

    /// A capture problem: keeps the WAV (in the output folder) and the live
    /// preview as its SRT, and reports `message` with the paths.
    private func failCapture(_ message: String, recording: URL) {
        let kept = TranscriptOutput.keepAfterFailure(
            message, recording: .init(url: recording, inSpool: true), liveSegments: liveSegments,
            existingTranscript: nil, settings: settings, spool: spool
        )
        // "Try Again" reads the kept WAV.
        recordedSamples = []
        retryableRecording = kept.recording
        finishedRecording = kept.recording?.url
        finishedTranscript = kept.srt
        needsModel = false
        phase = .failed(message: kept.message)
    }

    // MARK: - Live preview

    private func resetLivePreview() {
        liveContinuation?.finish()
        liveContinuation = nil
        // A sink still here was never handed over; its chunks are dropped.
        liveSink?.close()
        liveSink = nil
        liveSegments = []
        latestLiveLine = nil
        liveNotice = nil
        isLivePreviewEnabled = false
        isLivePreviewTurnedOff = false
        chunker = LiveChunker()
        chunkedSamples = 0
        liveJobCount = 0
        liveLocation = nil
        detectionTask?.cancel()
        detectionTask = nil
    }

    /// Opens the live queue when a model is ready and Settings has the
    /// preview on; otherwise the recording goes on without a preview. With
    /// the preview off there is no live model location either, so Auto
    /// detects nothing during the session: the queue detects over the whole
    /// recording at Stop (PLAN.md 4.12).
    private func startLivePreview() {
        resetLivePreview()
        guard settings.livePreviewMode.showsPreview else {
            isLivePreviewTurnedOff = true
            liveNotice = String(localized: "Live preview is off. Turn it on in Settings > General.",
                                comment: "Record tab notice in the live preview area while the live preview is turned off in Settings. Quote the translated names of the Settings tab and its General section exactly.")
            return
        }
        let location: WhisperModelLocation
        do {
            location = try sources.modelLocation(modelStore)
        } catch {
            liveNotice = String(localized: "Live preview off: \(Self.describe(error))",
                                comment: "Record tab notice. %@ is the reason.")
            return
        }
        isLivePreviewEnabled = true
        liveLocation = location
        let (stream, continuation) = AsyncStream.makeStream(of: LiveJob.self, bufferingPolicy: .unbounded)
        liveContinuation = continuation
        let sink = LiveSink(language: languageTracker.language)
        let id = session
        sink.onResult = { [weak self] index, outcome in
            self?.liveChunkDone(outcome, index: index, session: id)
        }
        liveSink = sink
        let engine = engine
        // One consumer, so chunks are transcribed strictly in order. In Auto,
        // chunks that close before the language is decided wait in the sink.
        // After Stop the sink (and this task) belong to the queue job.
        sink.task = Task { [weak self, sink] in
            for await job in stream {
                guard let language = await sink.waitForLanguage() else {
                    sink.deliver(index: job.index, outcome: nil)
                    continue
                }
                let script = sink.script
                self?.liveJobObserver?(.started(session: id, index: job.index))
                let outcome: Result<[CoreSegment], Error>
                do {
                    let result = try await engine.transcribe(
                        samples: job.samples, location: location,
                        options: TranscriptionOptions.app(language: language), progress: { _ in }
                    )
                    outcome = .success(result.cues(offset: job.start, script: script))
                } catch {
                    outcome = .failure(error)
                }
                switch outcome {
                case .success(let cues):
                    self?.liveJobObserver?(.finished(session: id, index: job.index, cues: cues.count, error: nil))
                case .failure(let error):
                    self?.liveJobObserver?(.finished(session: id, index: job.index, cues: 0, error: Self.describe(error)))
                }
                sink.deliver(index: job.index, outcome: outcome)
            }
        }
    }

    // MARK: - Session language

    /// The tracker settled or the user picked a language: the Chinese
    /// conversion, the banner, and any waiting live jobs follow it.
    private func languageSettled() {
        if let language = languageTracker.language {
            sessionChineseScript = language.chineseScript
            liveSink?.setLanguage(language)
        }
    }

    /// Runs one detection attempt when the tracker says one is due and none
    /// is in flight (every 30 s of audio, see `SessionLanguageTracker`).
    private func startDetectionIfDue() {
        guard detectionTask == nil, let location = liveLocation,
              let count = languageTracker.attemptDue(totalSamples: recordedSamples.count)
        else { return }
        let samples = Array(recordedSamples[0..<count])
        let engine = engine
        let id = session
        detectionTask = Task { [weak self] in
            let result = try? await engine.detectLanguage(samples: samples, location: location)
            self?.detectionDone(result, samplesUsed: count, session: id)
        }
    }

    private func detectionDone(_ result: DetectionResult?, samplesUsed: Int, session id: Int) {
        guard session == id else { return }
        detectionTask = nil
        lastDetection = result
        let decision = languageTracker.record(detection: result?.decisionInput, samplesUsed: samplesUsed)
        liveJobObserver?(.detection(
            session: id, seconds: Double(samplesUsed) / Double(WavWriter.sampleRate), result: result, decision: decision
        ))
        if decision != nil {
            languageNotice = LanguageNotice(decision: languageTracker.decision)
            languageSettled()
        } else if isCapturing {
            startDetectionIfDue()
        }
    }

    /// Measures the new samples in 0.1 s windows and queues every chunk
    /// the chunker closes. `final` also measures a short last window and
    /// flushes the open chunk.
    private func feedChunker(final: Bool) {
        let window = LiveChunker.windowSamples
        while recordedSamples.count - chunkedSamples >= window
            || (final && recordedSamples.count > chunkedSamples) {
            let end = min(chunkedSamples + window, recordedSamples.count)
            let level = LevelMeter.rmsDB(floatSamples: Array(recordedSamples[chunkedSamples..<end]))
            chunkedSamples = end
            for range in chunker.observe(totalSamples: end, rmsDB: level) {
                enqueueLive(range)
            }
        }
        if final, let tail = chunker.flush() {
            enqueueLive(tail)
        }
    }

    private func enqueueLive(_ range: Range<Int>) {
        guard let liveContinuation, range.upperBound <= recordedSamples.count else { return }
        liveSink?.chunkQueued()
        liveJobCount += 1
        let start = Double(range.lowerBound) / Double(LiveChunker.sampleRate)
        liveJobObserver?(.queued(
            session: session, index: liveJobCount, start: start,
            seconds: Double(range.count) / Double(LiveChunker.sampleRate)
        ))
        liveContinuation.yield(LiveJob(index: liveJobCount, samples: Array(recordedSamples[range]), start: start))
    }

    private func liveChunkDone(_ outcome: Result<[CoreSegment], Error>, index: Int, session id: Int) {
        guard session == id else { return }
        switch outcome {
        case .success(let cues):
            liveSegments.append(contentsOf: cues)
            if let line = cues.last(where: { !$0.text.isEmpty })?.text {
                latestLiveLine = line
            }
        case .failure(let error):
            liveNotice = String(localized: "Live preview missed a chunk: \(Self.describe(error))",
                                comment: "Record tab notice. %@ is the reason.")
        }
    }

    static func describe(_ error: Error) -> String {
        if let localized = error as? LocalizedError, let text = localized.errorDescription {
            return text
        }
        return String(describing: error)
    }
}


// MARK: - Capture seam

/// What `RecordingController` needs from a microphone recorder. Production
/// uses `MicrophoneRecorder`; the debug replay feeds a WAV instead.
@MainActor
protocol MicrophoneCapture: AnyObject {
    var timedSamples: AsyncStream<TimedChunk> { get }
    var diagnostics: MicrophoneDiagnostics { get }
    var failure: MicrophoneRecorderError? { get }
    func start(device: AudioInputDevice?) throws
    func pause()
    func resume() throws
    func stop()
}

/// What `RecordingController` needs from a system-audio recorder.
@MainActor
protocol SystemAudioCapture: AnyObject {
    var timedSamples: AsyncStream<TimedChunk> { get }
    var failure: SystemAudioRecorderError? { get }
    func pause()
    func resume()
    func stop()
}

extension MicrophoneRecorder: MicrophoneCapture {}
extension SystemAudioRecorder: SystemAudioCapture {}

/// Where a recording's audio and model come from. `.live` is the app;
/// only the debug replay (`RecordingReplay`) passes anything else.
struct CaptureSources: Sendable {
    var requestMicrophonePermission: @MainActor @Sendable () async -> Bool
    var makeMicrophone: @MainActor @Sendable () -> any MicrophoneCapture
    /// nil: the real system audio (permission check and `SystemAudioRecorder`).
    var makeSystemAudio: (@MainActor @Sendable () async throws -> any SystemAudioCapture)?
    var modelLocation: @MainActor @Sendable (ModelStore) throws -> WhisperModelLocation

    static let live = CaptureSources(
        requestMicrophonePermission: { await MicrophoneRecorder.requestPermission() },
        makeMicrophone: { MicrophoneRecorder() },
        makeSystemAudio: nil,
        modelLocation: { try WhisperModelLocation.active(in: $0) }
    )
}

/// Removes a block-based notification observer when released.
private final class NotificationToken {
    private let token: NSObjectProtocol

    init(_ token: NSObjectProtocol) {
        self.token = token
    }

    deinit {
        NotificationCenter.default.removeObserver(token)
    }
}
