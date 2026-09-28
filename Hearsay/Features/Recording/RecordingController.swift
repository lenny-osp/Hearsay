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
/// Transcription (PLAN.md 4.1 steps 4 to 7): while recording, `LiveChunker`
/// cuts the mixed stream into chunks that `WhisperEngine` transcribes in
/// order for the live preview. After Stop the WAV is closed and one full
/// pass over the in-memory samples produces `<timestamp>.srt` in the output
/// folder; the WAV is then moved beside it (or deleted when "Keep the
/// recording" is off). If the pass fails, the WAV is always kept, the live
/// preview is saved as the SRT when there is one, and the recording can be
/// retried here or sent to the File tab. Without an installed model the
/// recording still works; the live preview is skipped and the WAV is kept.
///
/// Phase transitions:
/// idle/finished/failed -> starting -> recording <-> paused -> stopping
/// -> transcribing(progress) -> finished(srt:wav:) or failed(message:).
/// `starting` can also end in idle (stop while starting) or failed; a
/// capture problem goes from stopping straight to failed with the WAV kept;
/// a microphone that delivers nothing within `NoAudioWatchdog.timeout`
/// seconds stops the recording, and a recording with no samples at all goes
/// from stopping to failed with the empty WAV deleted;
/// "Use live preview instead" ends transcribing in finished at once;
/// `retryTranscription()` goes from failed to transcribing again.
@MainActor
@Observable
final class RecordingController {
    enum Phase: Equatable {
        case idle
        case starting
        case recording
        case paused
        case stopping
        /// The WAV is closed and the final pass (or a retry) is running.
        case transcribing(progress: Double)
        /// The transcript was written to `srt`; the recording is at `wav`,
        /// or nil when "Keep the recording" is off.
        case finished(srt: URL?, wav: URL?)
        /// The last recording, its transcription, or the last start attempt
        /// had a problem. Any audio that was captured is still at
        /// `finishedRecording`.
        case failed(message: String)
    }

    /// Codes match whisper-tools `MEETING_NOTE_LANGUAGES`.
    static let languages: [(code: String, label: String)] = [("en", "EN"), ("zh", "ZH")]

    static let screenCaptureSettingsURL = URL(
        string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
    )

    private(set) var phase: Phase = .idle
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
    /// Where the last recording ended up, also after a failure.
    private(set) var finishedRecording: URL?
    /// The SRT of the last recording: the final transcript, or the live
    /// preview saved after a failed final pass.
    private(set) var finishedTranscript: URL?

    // Live preview
    /// Cues of the live preview, in recording time, in order.
    private(set) var liveSegments: [CoreSegment] = []
    /// Live chunks queued or being transcribed.
    private(set) var liveChunksWaiting = 0
    /// The newest non-empty live line, for the menu bar.
    private(set) var latestLiveLine: String?
    /// Why the live preview is off or incomplete, shown under it.
    private(set) var liveNotice: String?
    /// The current session transcribes live chunks.
    private(set) var isLivePreviewEnabled = false
    /// "Use live preview instead" was chosen for the current final pass.
    private(set) var isUsingLivePreview = false
    /// The last failure was the missing model; the view links to Models.
    private(set) var needsModel = false
    /// A recording the File tab should pick up ("Transcribe this file").
    /// The File tab clears it once taken.
    var transcribeFileRequest: URL?
    /// A finished SRT waiting for the notes flow; see `takeNotesRequest()`.
    private(set) var notesRequest: URL?
    /// Language of the last recording, for the notes flow.
    private(set) var sessionLanguageCode = "en"
    /// Script the last recording's cues are converted to (zh only).
    @ObservationIgnored private var sessionChineseScript: ChineseScript = .asIs

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
    @ObservationIgnored private var deviceObservation: AudioDeviceListObservation?
    @ObservationIgnored private var engineObservation: NotificationToken?
    /// Name of the device being recorded, for error messages.
    @ObservationIgnored private var recordingDeviceName = "the input device"
    /// Set when the watchdog stopped a recording that never got audio.
    @ObservationIgnored private var noAudioFailure: MicrophoneRecorderError?
    @ObservationIgnored private var watchdogTask: Task<Void, Never>?

    // Transcription state
    /// Every mixed sample of the current recording, for the final pass.
    @ObservationIgnored private var recordedSamples: [Float] = []
    @ObservationIgnored private var chunker = LiveChunker()
    /// Samples already measured and handed to `chunker`.
    @ObservationIgnored private var chunkedSamples = 0
    @ObservationIgnored private var liveContinuation: AsyncStream<LiveJob>.Continuation?
    @ObservationIgnored private var liveTask: Task<Void, Never>?
    @ObservationIgnored private var transcriptionTask: Task<Void, Never>?
    /// The closed WAV still in the spool while the final pass runs.
    @ObservationIgnored private var pendingSpoolWAV: URL?
    /// The kept WAV of a failed transcription, for `retryTranscription()`.
    @ObservationIgnored private var retryableRecording: URL?
    /// Bumped on every start; late results of an older session are ignored.
    @ObservationIgnored private var session = 0
    /// Debug only: sees every live job (see `RecordingReplay`).
    @ObservationIgnored var liveJobObserver: (@MainActor (LiveJobEvent) -> Void)?
    @ObservationIgnored private var liveJobCount = 0

    /// A live job's progress, for the debug replay's timing log.
    enum LiveJobEvent: Sendable {
        case queued(index: Int, start: TimeInterval, seconds: TimeInterval)
        case started(index: Int)
        case finished(index: Int, cues: Int, error: String?)
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
        spool: RecordingSpool = RecordingSpool(),
        sources: CaptureSources = .live
    ) {
        self.settings = settings
        self.modelStore = modelStore
        self.engine = engine
        self.spool = spool
        self.sources = sources
    }

    // MARK: - State

    /// Language of the recording, persisted as the default for the next one.
    var languageCode: String {
        get { settings.defaultLanguageCode }
        set { settings.defaultLanguageCode = newValue }
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
        case .idle, .transcribing, .finished, .failed: false
        }
    }

    /// The final pass or a retry is running.
    var isTranscribing: Bool {
        if case .transcribing = phase { return true }
        return false
    }

    var transcriptionProgress: Double? {
        if case .transcribing(let progress) = phase { return progress }
        return nil
    }

    /// Start is allowed: nothing is recording or transcribing.
    var canStart: Bool {
        !isSessionActive && !isTranscribing
    }

    /// The live preview has fallen more than one chunk behind.
    var isLiveLagging: Bool {
        liveChunksWaiting > 1
    }

    /// "Use live preview instead" applies to the running pass.
    var canUseLivePreview: Bool {
        isTranscribing && isLivePreviewEnabled && pendingSpoolWAV != nil && !isUsingLivePreview
    }

    /// A failed transcription can be retried from the kept WAV.
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
    func start() {
        guard canStart else { return }
        phase = .starting
        finishedRecording = nil
        finishedTranscript = nil
        retryableRecording = nil
        transcribeFileRequest = nil
        notesRequest = nil
        needsModel = false
        session += 1
        resetLivePreview()
        stopRequestedWhileStarting = false
        startTask = Task { [weak self] in
            await self?.performStart()
            self?.startTask = nil
        }
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

    /// Stops the recording and returns once the WAV is closed and moved to
    /// the output folder. A start in progress is cancelled first. Does
    /// nothing when no session is active.
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

    func revealInFinder() {
        let files = [finishedTranscript, finishedRecording].compactMap { $0 }
        guard !files.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(files)
    }

    /// "Use live preview instead": cancels the final pass (the engine stops
    /// before its next 30 s window) and writes the live segments as the SRT.
    /// The cancellation is expected and never shown as an error.
    func useLivePreviewInstead() {
        guard canUseLivePreview else { return }
        isUsingLivePreview = true
        transcriptionTask?.cancel()
        let id = session
        Task { [weak self] in
            await self?.liveTask?.value
            guard let self, self.session == id, self.isTranscribing else { return }
            self.completeTranscription(with: self.liveSegments)
        }
    }

    /// Quit while transcribing: cancels the pass and the live queue, then
    /// saves the live preview as the SRT when there is one. The WAV is kept
    /// either way (moved to the output folder), whatever "Keep the
    /// recording" says, because the full pass never ran. Returns once the
    /// files are written.
    func cancelTranscriptionForQuit() async {
        guard isTranscribing else { return }
        isUsingLivePreview = true
        transcriptionTask?.cancel()
        liveTask?.cancel()
        await transcriptionTask?.value
        guard isTranscribing else { return }
        if liveSegments.isEmpty {
            failTranscription("Transcription was cancelled because Hearsay quit.")
        } else {
            completeTranscription(with: liveSegments, keepRecording: true)
        }
    }

    /// Runs the full pass again on the kept WAV of a failed transcription.
    func retryTranscription() {
        guard canRetryTranscription, let recording = retryableRecording else { return }
        phase = .transcribing(progress: 0)
        isUsingLivePreview = false
        let id = session
        transcriptionTask = Task { [weak self] in
            await self?.runRetry(recording: recording, session: id)
        }
    }

    /// Hands the kept WAV to the File tab ("Transcribe this file").
    func requestTranscribeFile() {
        transcribeFileRequest = retryableRecording ?? finishedRecording
    }

    /// The finished SRT waiting for the notes flow, once.
    func takeNotesRequest() -> URL? {
        defer { notesRequest = nil }
        return notesRequest
    }

    func openScreenCaptureSettings() {
        guard let url = Self.screenCaptureSettingsURL else { return }
        NSWorkspace.shared.open(url)
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
            phase = .failed(message: "Could not create the recording file: \(error.localizedDescription)")
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
        recordingDeviceName = recorder.diagnostics.deviceName ?? device?.name ?? "the input device"
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
        sessionLanguageCode = settings.defaultLanguageCode
        sessionChineseScript = ChineseScript.app(languageCode: sessionLanguageCode, settings: settings)
        startLivePreview()
        phase = .recording

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
                systemAudioNotice = "System audio off: \(error)"
                return nil
            }
        }
        if SystemAudioRecorder.permission != .authorized, !SystemAudioRecorder.requestPermission() {
            systemAudioDenied = true
            systemAudioNotice = "System audio off: permission denied"
            return nil
        }
        let recorder = SystemAudioRecorder()
        do {
            try await recorder.start()
            return recorder
        } catch SystemAudioRecorderError.permissionDenied {
            systemAudioDenied = true
            systemAudioNotice = "System audio off: permission denied"
        } catch {
            systemAudioNotice = "System audio off: \(error)"
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
            ? "Silent for \(Int(meter.silenceSeconds))s \u{2014} check the input device"
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
                systemAudioNotice = "System audio off: \(failure)"
                systemLevelFraction = nil
            }
        }
    }

    private func recordingEnded() {
        phase = .stopping
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
            problems.append("Writing the recording failed: \(writeError.localizedDescription)")
        }

        var closed: URL?
        if let writer {
            closed = writer.url
            do {
                try writer.close()
            } catch {
                problems.append("Closing the recording failed: \(error.localizedDescription)")
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
            lines.append("Nothing was recorded, so no file was kept.")
            phase = .failed(message: lines.joined(separator: "\n"))
            return
        }
        if !problems.isEmpty {
            // Capture failed: keep what exists and let the user transcribe it.
            pendingSpoolWAV = closed
            failTranscription(problems.joined(separator: "\n"))
            return
        }
        pendingSpoolWAV = closed
        phase = .transcribing(progress: 0)
        isUsingLivePreview = false
        let id = session
        transcriptionTask = Task { [weak self] in
            await self?.runFinalPass(session: id)
        }
    }

    // MARK: - Live preview

    private func resetLivePreview() {
        liveContinuation?.finish()
        liveContinuation = nil
        liveTask = nil
        liveSegments = []
        liveChunksWaiting = 0
        latestLiveLine = nil
        liveNotice = nil
        isLivePreviewEnabled = false
        isUsingLivePreview = false
        chunker = LiveChunker()
        chunkedSamples = 0
        liveJobCount = 0
    }

    /// Opens the live queue when a model is ready; otherwise the recording
    /// goes on without a preview.
    private func startLivePreview() {
        resetLivePreview()
        let location: WhisperModelLocation
        do {
            location = try sources.modelLocation(modelStore)
        } catch {
            liveNotice = "Live preview off: \(Self.describe(error))"
            return
        }
        isLivePreviewEnabled = true
        let (stream, continuation) = AsyncStream.makeStream(of: LiveJob.self, bufferingPolicy: .unbounded)
        liveContinuation = continuation
        let engine = engine
        let options = TranscriptionOptions.app(languageCode: sessionLanguageCode)
        let script = sessionChineseScript
        let id = session
        // One consumer, so chunks are transcribed strictly in order.
        liveTask = Task { [weak self] in
            for await job in stream {
                self?.liveJobObserver?(.started(index: job.index))
                let outcome: Result<[CoreSegment], Error>
                do {
                    let result = try await engine.transcribe(
                        samples: job.samples, location: location, options: options, progress: { _ in }
                    )
                    outcome = .success(result.cues(offset: job.start, script: script))
                } catch {
                    outcome = .failure(error)
                }
                self?.liveChunkDone(outcome, index: job.index, session: id)
            }
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
        liveChunksWaiting += 1
        liveJobCount += 1
        let start = Double(range.lowerBound) / Double(LiveChunker.sampleRate)
        liveJobObserver?(.queued(
            index: liveJobCount, start: start, seconds: Double(range.count) / Double(LiveChunker.sampleRate)
        ))
        liveContinuation.yield(LiveJob(index: liveJobCount, samples: Array(recordedSamples[range]), start: start))
    }

    private func liveChunkDone(_ outcome: Result<[CoreSegment], Error>, index: Int, session id: Int) {
        guard session == id else { return }
        switch outcome {
        case .success(let cues): liveJobObserver?(.finished(index: index, cues: cues.count, error: nil))
        case .failure(let error): liveJobObserver?(.finished(index: index, cues: 0, error: Self.describe(error)))
        }
        liveChunksWaiting = max(0, liveChunksWaiting - 1)
        switch outcome {
        case .success(let cues):
            liveSegments.append(contentsOf: cues)
            if let line = cues.last(where: { !$0.text.isEmpty })?.text {
                latestLiveLine = line
            }
        case .failure(let error):
            liveNotice = "Live preview missed a chunk: \(Self.describe(error))"
        }
    }

    // MARK: - Final pass

    private func runFinalPass(session id: Int) async {
        let location: WhisperModelLocation
        do {
            location = try sources.modelLocation(modelStore)
        } catch {
            await liveTask?.value
            guard session == id else { return }
            failTranscription(Self.describe(error), missingModel: true)
            return
        }
        // Finish the live preview first so "Use live preview instead" always
        // has every chunk.
        await liveTask?.value
        guard session == id, isTranscribing, !isUsingLivePreview else { return }

        let samples = recordedSamples
        let options = TranscriptionOptions.app(languageCode: sessionLanguageCode)
        do {
            let result = try await engine.transcribe(
                samples: samples, location: location, options: options,
                progress: progressHandler(session: id)
            )
            guard session == id, isTranscribing, !isUsingLivePreview else { return }
            completeTranscription(with: result.cues(script: sessionChineseScript))
        } catch {
            // Cancelled by "Use live preview instead" or by quitting; whoever
            // cancelled writes the SRT.
            guard !error.isTranscriptionCancelled else { return }
            guard session == id, isTranscribing, !isUsingLivePreview else { return }
            failTranscription("Transcription failed: \(Self.describe(error))")
        }
    }

    private func runRetry(recording: URL, session id: Int) async {
        let location: WhisperModelLocation
        do {
            location = try sources.modelLocation(modelStore)
        } catch {
            guard session == id else { return }
            failTranscription(Self.describe(error), missingModel: true)
            return
        }
        let folder = try? TranscriptOutput.resolveFolder(settings: settings)
        defer { folder?.stopAccessing() }
        do {
            var samples = recordedSamples
            if samples.isEmpty {
                samples = try await Task.detached(priority: .userInitiated) {
                    try AudioFileLoader.loadMono16k(url: recording)
                }.value
            }
            let result = try await engine.transcribe(
                samples: samples, location: location,
                options: TranscriptionOptions.app(languageCode: sessionLanguageCode),
                progress: progressHandler(session: id)
            )
            guard session == id, isTranscribing, !isUsingLivePreview else { return }
            completeTranscription(with: result.cues(script: sessionChineseScript))
        } catch {
            guard !error.isTranscriptionCancelled else { return }
            guard session == id, isTranscribing, !isUsingLivePreview else { return }
            failTranscription("Transcription failed: \(Self.describe(error))")
        }
    }

    private func progressHandler(session id: Int) -> @Sendable (Double) -> Void {
        { [weak self] value in
            Task { @MainActor in
                guard let self, self.session == id, self.isTranscribing else { return }
                self.phase = .transcribing(progress: min(max(value, 0), 1))
            }
        }
    }

    /// Writes `<stem>.srt` into the output folder beside the recording and
    /// then keeps or deletes the WAV as the setting says. After a failed
    /// pass the WAV is already in the output folder and its SRT (possibly
    /// the saved live preview) is replaced.
    /// `keepRecording` overrides the setting (quit keeps the WAV).
    private func completeTranscription(with cues: [CoreSegment], keepRecording: Bool? = nil) {
        let folder: ResolvedOutputFolder
        do {
            folder = try TranscriptOutput.resolveFolder(settings: settings)
        } catch {
            failTranscription("Could not open the output folder: \(error.localizedDescription)")
            return
        }
        defer { folder.stopAccessing() }
        let fileManager = FileManager.default

        // The WAV is either still in the spool (after a recording) or already
        // in the output folder (after a failed pass).
        let source = pendingSpoolWAV ?? retryableRecording
        guard let source else {
            failTranscription("The recording is missing.")
            return
        }
        let inSpool = pendingSpoolWAV != nil
        let base = source.deletingPathExtension().lastPathComponent
        let directory = inSpool ? folder.url : source.deletingLastPathComponent()
        let stem = inSpool
            ? TranscriptOutput.freeStem(base: base, directory: directory, suffixes: [".srt", ".wav"])
            : base
        let srt = directory.appendingPathComponent(stem + ".srt")
        do {
            try TranscriptOutput.writeSRT(cues, to: srt)
        } catch {
            failTranscription("Could not write the transcript: \(error.localizedDescription)")
            return
        }

        var wav: URL?
        if keepRecording ?? settings.keepRecording {
            if inSpool {
                let destination = directory.appendingPathComponent(stem + ".wav")
                do {
                    try fileManager.moveItem(at: source, to: destination)
                    wav = destination
                } catch {
                    wav = source
                    liveNotice = "Could not move the recording to the output folder: "
                        + "\(error.localizedDescription) It is kept at \(source.path)."
                }
            } else {
                wav = source
            }
        } else {
            try? fileManager.removeItem(at: source)
        }

        pendingSpoolWAV = nil
        retryableRecording = nil
        recordedSamples = []
        needsModel = false
        finishedTranscript = srt
        finishedRecording = wav
        phase = .finished(srt: srt, wav: wav)
        notesRequest = srt
    }

    /// Keeps the WAV (moved to the output folder when it is still in the
    /// spool), saves the live preview as its SRT when there is one, and
    /// reports `message` with the WAV path.
    private func failTranscription(_ message: String, missingModel: Bool = false) {
        var lines = [message]
        var wav = pendingSpoolWAV ?? retryableRecording
        let folder = try? TranscriptOutput.resolveFolder(settings: settings)
        defer { folder?.stopAccessing() }

        if let spoolWAV = pendingSpoolWAV {
            if let folder {
                do {
                    wav = try spool.finalize(spoolWAV, keep: true, outputFolder: folder.url)
                } catch {
                    wav = spoolWAV
                    lines.append("Could not move the recording to the output folder: \(error.localizedDescription)")
                }
            }
            pendingSpoolWAV = nil
        }

        if let wav, !liveSegments.isEmpty, finishedTranscript == nil {
            let srt = wav.deletingPathExtension().appendingPathExtension("srt")
            do {
                try TranscriptOutput.writeSRT(liveSegments, to: srt)
                finishedTranscript = srt
                lines.append("The live preview was saved as \(srt.path).")
            } catch {
                lines.append("The live preview could not be saved: \(error.localizedDescription)")
            }
        }
        if let wav {
            lines.append("The recording is kept at \(wav.path).")
        }
        retryableRecording = wav
        finishedRecording = wav
        needsModel = missingModel
        phase = .failed(message: lines.joined(separator: "\n"))
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
