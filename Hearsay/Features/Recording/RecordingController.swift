import AppKit
import AVFoundation
import HearsayCore
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
@MainActor
@Observable
final class RecordingController {
    enum Phase: Equatable {
        case idle
        case starting
        case recording
        case paused
        case stopping
        /// The last recording was saved at `url` without problems.
        case finished(url: URL)
        /// The last recording or the last start attempt had a problem. Any
        /// audio that was captured is still at `finishedRecording`.
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

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let spool: RecordingSpool
    @ObservationIgnored private var recorder: MicrophoneRecorder?
    @ObservationIgnored private var systemRecorder: SystemAudioRecorder?
    @ObservationIgnored private var writer: WavWriter?
    @ObservationIgnored private var writeError: Error?
    @ObservationIgnored private var meter = LevelMeter()
    @ObservationIgnored private var sampleCount = 0
    @ObservationIgnored private var startTask: Task<Void, Never>?
    @ObservationIgnored private var consumer: Task<Void, Never>?
    @ObservationIgnored private var stopRequestedWhileStarting = false
    @ObservationIgnored private var deviceObservation: AudioDeviceListObservation?
    @ObservationIgnored private var engineObservation: NotificationToken?

    init(settings: AppSettings, spool: RecordingSpool = RecordingSpool()) {
        self.settings = settings
        self.spool = spool
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
        case .idle, .finished, .failed: false
        }
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
        guard !isSessionActive else { return }
        phase = .starting
        finishedRecording = nil
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
        guard let finishedRecording else { return }
        NSWorkspace.shared.activateFileViewerSelecting([finishedRecording])
    }

    func openScreenCaptureSettings() {
        guard let url = Self.screenCaptureSettingsURL else { return }
        NSWorkspace.shared.open(url)
    }

    // MARK: - Start

    private func performStart() async {
        guard await MicrophoneRecorder.requestPermission() else {
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
        let recorder = MicrophoneRecorder()
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
        writeError = nil
        meter = LevelMeter()
        sampleCount = 0
        elapsed = 0
        levelFraction = 0
        micLevelFraction = 0
        systemLevelFraction = systemRecorder == nil ? nil : 0
        silenceWarning = nil
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
    }

    /// Starts system audio capture, or records why it is off and returns nil
    /// so the recording continues mic-only.
    private func startSystemAudio() async -> SystemAudioRecorder? {
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
        elapsed = Double(sampleCount) / Double(WavWriter.sampleRate)
        var problems: [String] = []
        if let failure = recorder?.failure {
            problems.append(failure.description)
            refreshDevices()
        }
        if let writeError {
            problems.append("Writing the recording failed: \(writeError.localizedDescription)")
        }

        var saved: URL?
        if let writer {
            let spoolURL = writer.url
            do {
                try writer.close()
                saved = try finalize(spoolURL)
            } catch {
                saved = spoolURL
                problems.append("Could not move the recording to the output folder: "
                    + "\(error.localizedDescription) It is kept at \(spoolURL.path).")
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
        finishedRecording = saved
        if !problems.isEmpty {
            phase = .failed(message: problems.joined(separator: "\n"))
        } else if let saved {
            phase = .finished(url: saved)
        } else {
            phase = .idle
        }
    }

    /// No transcription exists yet in this phase, so the WAV is always kept.
    private func finalize(_ spoolURL: URL) throws -> URL? {
        let folder = try OutputLocation.resolve(bookmark: settings.outputFolderBookmark)
        defer { folder.stopAccessing() }
        if let refreshed = folder.refreshedBookmark {
            settings.outputFolderBookmark = refreshed
        }
        return try spool.finalize(spoolURL, keep: true, outputFolder: folder.url)
    }
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
