import AppKit
import AVFoundation
import HearsayCore
import Observation

/// Drives the Record tab (PLAN.md 4.1, microphone only for now): device and
/// language choice, Start / Pause / Resume / Stop, the level meter, and
/// moving the finished spool WAV into the output folder.
///
/// Elapsed time is derived from the number of samples captured, so paused
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
    private(set) var silenceWarning: String?
    private(set) var errorMessage: String?
    /// Where the last recording ended up.
    private(set) var savedRecording: URL?

    @ObservationIgnored private let spool: RecordingSpool
    @ObservationIgnored private var recorder: MicrophoneRecorder?
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

        let device = devices.first { $0.uid == selectedDeviceUID }
        let url = spool.newRecordingURL(timestamp: Timestamps.now())
        let writer: WavWriter
        do {
            writer = try WavWriter(url: url)
        } catch {
            errorMessage = "Could not create the recording file: \(error.localizedDescription)"
            phase = .idle
            return
        }
        let recorder = MicrophoneRecorder()
        do {
            try recorder.start(device: device)
        } catch {
            try? writer.close()
            try? FileManager.default.removeItem(at: url)
            errorMessage = String(describing: error)
            phase = .idle
            return
        }

        self.settings = settings
        self.writer = writer
        self.recorder = recorder
        writeError = nil
        meter = LevelMeter()
        sampleCount = 0
        elapsed = 0
        levelFraction = 0
        silenceWarning = nil
        phase = .recording

        consumer = Task { [weak self] in
            for await chunk in recorder.samples {
                self?.consume(chunk)
            }
            self?.recordingEnded()
        }
    }

    func pause() {
        guard phase == .recording, let recorder else { return }
        recorder.pause()
        phase = .paused
        levelFraction = 0
    }

    func resume() {
        guard phase == .paused, let recorder else { return }
        do {
            try recorder.resume()
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
    }

    func revealInFinder() {
        guard let savedRecording else { return }
        NSWorkspace.shared.activateFileViewerSelecting([savedRecording])
    }

    // MARK: - Pipeline

    private func consume(_ chunk: [Float]) {
        guard let writer, writeError == nil else { return }
        do {
            try writer.append(chunk)
        } catch {
            writeError = error
            recorder?.stop()
            return
        }
        sampleCount += chunk.count
        let time = Double(sampleCount) / Double(WavWriter.sampleRate)
        let level = LevelMeter.rmsDB(floatSamples: chunk)
        guard meter.observe(rmsDB: level, at: time) else { return }
        elapsed = time
        levelFraction = LevelMeter.levelFraction(rmsDB: level)
        silenceWarning = meter.isSilenceWarning
            ? "Silent for \(Int(meter.silenceSeconds))s \u{2014} check the input device"
            : nil
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
        writer = nil
        consumer = nil
        settings = nil
        levelFraction = 0
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
