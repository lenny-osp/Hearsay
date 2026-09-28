import AppKit
import AVFoundation
import HearsayCore
import Observation

/// Drives the Record tab (PLAN.md 4.1): device and language choice, the
/// system audio toggle, Start / Pause / Resume / Stop, the level meters, and
/// moving the finished spool WAV into the output folder.
///
/// The microphone and, when enabled and permitted, the system audio run
/// through `AudioMixer`; the mixed stream feeds the WAV and the main meter,
/// and each source's level feeds its small meter. A missing screen-capture
/// permission never blocks a recording: it goes on mic-only with a notice.
///
/// Elapsed time is derived from the number of samples written, so paused
/// time never counts and the display matches the WAV exactly.
@MainActor
@Observable
final class RecordViewModel {
    enum Phase: Equatable {
        case idle
        case starting
        case recording
        case paused
        case saving
    }

    /// Codes match whisper-tools `MEETING_NOTE_LANGUAGES`.
    static let languages: [(code: String, label: String)] = [("en", "EN"), ("zh", "ZH")]

    private(set) var devices: [AudioInputDevice] = []
    var selectedDeviceUID: String?
    private(set) var phase: Phase = .idle
    private(set) var elapsed: TimeInterval = 0
    private(set) var levelFraction: Double = 0
    private(set) var micLevelFraction: Double = 0
    /// nil when system audio is not part of the current recording.
    private(set) var systemLevelFraction: Double?
    /// Why system audio is not being captured, shown as a badge.
    private(set) var systemAudioNotice: String?
    /// The notice is about the Screen & System Audio Recording permission,
    /// so the badge offers to open System Settings.
    private(set) var systemAudioPermissionDenied = false
    private(set) var silenceWarning: String?
    private(set) var errorMessage: String?
    /// Where the last recording ended up.
    private(set) var savedRecording: URL?

    @ObservationIgnored private let spool: RecordingSpool
    @ObservationIgnored private var recorder: MicrophoneRecorder?
    @ObservationIgnored private var systemRecorder: SystemAudioRecorder?
    @ObservationIgnored private var writer: WavWriter?
    @ObservationIgnored private var writeError: Error?
    @ObservationIgnored private var meter = LevelMeter()
    @ObservationIgnored private var sampleCount = 0
    @ObservationIgnored private var consumer: Task<Void, Never>?
    @ObservationIgnored private var settings: AppSettings?
    @ObservationIgnored private var deviceObservation: AudioDeviceListObservation?
    @ObservationIgnored private var engineObservation: NotificationToken?

    init(spool: RecordingSpool = RecordingSpool()) {
        self.spool = spool
    }

    var isBusy: Bool { phase != .idle }

    static let screenCaptureSettingsURL = URL(
        string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
    )

    /// Loads the device list and starts watching for device changes. Safe to
    /// call on every appearance.
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

    func start(settings: AppSettings) async {
        guard phase == .idle else { return }
        phase = .starting
        errorMessage = nil
        savedRecording = nil
        guard await MicrophoneRecorder.requestPermission() else {
            errorMessage = MicrophoneRecorderError.permissionDenied.description
            phase = .idle
            return
        }

        systemAudioNotice = nil
        systemAudioPermissionDenied = false
        let systemRecorder = settings.captureSystemAudio ? await startSystemAudio() : nil

        let device = devices.first { $0.uid == selectedDeviceUID }
        let url = spool.newRecordingURL(timestamp: Timestamps.now())
        let writer: WavWriter
        do {
            writer = try WavWriter(url: url)
        } catch {
            systemRecorder?.stop()
            errorMessage = "Could not create the recording file: \(error.localizedDescription)"
            phase = .idle
            return
        }
        let recorder = MicrophoneRecorder()
        do {
            try recorder.start(device: device)
        } catch {
            systemRecorder?.stop()
            try? writer.close()
            try? FileManager.default.removeItem(at: url)
            errorMessage = String(describing: error)
            phase = .idle
            return
        }

        self.settings = settings
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
            systemAudioPermissionDenied = true
            systemAudioNotice = "System audio off: permission denied"
            return nil
        }
        let recorder = SystemAudioRecorder()
        do {
            try await recorder.start()
            return recorder
        } catch SystemAudioRecorderError.permissionDenied {
            systemAudioPermissionDenied = true
            systemAudioNotice = "System audio off: permission denied"
        } catch {
            systemAudioNotice = "System audio off: \(error)"
        }
        return nil
    }

    func openScreenCaptureSettings() {
        guard let url = Self.screenCaptureSettingsURL else { return }
        NSWorkspace.shared.open(url)
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
            // The recorder has stopped; `recordingEnded` saves what exists.
            errorMessage = String(describing: error)
        }
    }

    func stop() {
        guard phase == .recording || phase == .paused, let recorder else { return }
        phase = .saving
        recorder.stop()
        systemRecorder?.stop()
    }

    func revealInFinder() {
        guard let savedRecording else { return }
        NSWorkspace.shared.activateFileViewerSelecting([savedRecording])
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
        guard phase == .recording || phase == .paused else { return }
        switch source {
        case .mic:
            if recorder?.failure != nil {
                phase = .saving
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
        phase = .saving
        elapsed = Double(sampleCount) / Double(WavWriter.sampleRate)
        var problems: [String] = []
        if let failure = recorder?.failure {
            problems.append(failure.description)
            refreshDevices()
        }
        if let writeError {
            problems.append("Writing the recording failed: \(writeError.localizedDescription)")
        }

        if let writer {
            let spoolURL = writer.url
            do {
                try writer.close()
                savedRecording = try finalize(spoolURL)
            } catch {
                savedRecording = spoolURL
                problems.append("Could not move the recording to the output folder: "
                    + "\(error.localizedDescription) It is kept at \(spoolURL.path).")
            }
        }

        errorMessage = problems.isEmpty ? nil : problems.joined(separator: "\n")
        recorder = nil
        systemRecorder = nil
        writer = nil
        consumer = nil
        settings = nil
        levelFraction = 0
        micLevelFraction = 0
        systemLevelFraction = nil
        silenceWarning = nil
        phase = .idle
    }

    /// No transcription exists yet in this phase, so the WAV is always kept.
    private func finalize(_ spoolURL: URL) throws -> URL? {
        let bookmark = settings?.outputFolderBookmark
        let folder = try OutputLocation.resolve(bookmark: bookmark)
        defer { folder.stopAccessing() }
        if let refreshed = folder.refreshedBookmark {
            settings?.outputFolderBookmark = refreshed
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
