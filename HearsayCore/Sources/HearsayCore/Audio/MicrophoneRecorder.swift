@preconcurrency import AVFoundation
import CoreAudio
import CoreMedia
import os

/// Microphone access as reported by AVFoundation.
public enum MicrophonePermission: Sendable, Equatable {
    case notDetermined
    case denied
    case restricted
    case authorized
}

/// Why a recording could not start or stopped on its own.
public enum MicrophoneRecorderError: Error, Equatable, Sendable, CustomStringConvertible {
    case permissionDenied
    case noInputDevice
    case cannotSelectDevice(name: String, status: OSStatus)
    /// The chosen device is not available to AVFoundation capture.
    case deviceNotFound(name: String)
    case engineFailed(String)
    /// Nothing arrived from the device within `NoAudioWatchdog.timeout`
    /// seconds of recording.
    case noAudio(deviceName: String)
    /// The input device disappeared, could not be restarted after a
    /// configuration change, or kept changing. Recording stopped.
    case configurationChanged
    case invalidState(String)

    public var description: String {
        switch self {
        case .permissionDenied:
            "Hearsay has no microphone access. Allow it in System Settings > Privacy & Security > Microphone."
        case .noInputDevice:
            "No input device is available."
        case .cannotSelectDevice(let name, let status):
            "Could not select \(name) (CoreAudio error \(status))."
        case .deviceNotFound(let name):
            "\(name) is not available for recording. Reconnect it or choose another input."
        case .engineFailed(let detail):
            "Audio capture failed: \(detail)"
        case .noAudio(let name):
            "No audio from \(name). Nothing arrived within \(Int(NoAudioWatchdog.timeout)) seconds, "
                + "so the recording was stopped. Check that the device is "
                + "connected and not muted, or choose another input."
        case .configurationChanged:
            "The input device changed or was disconnected, so recording stopped."
        case .invalidState(let detail):
            detail
        }
    }
}

/// What the capture path saw, for diagnosing a device that records silence.
public struct MicrophoneDiagnostics: Sendable, Equatable, CustomStringConvertible {
    public var deviceName: String?
    /// CoreAudio nominal sample rate of the device when recording started.
    public var deviceNominalSampleRate: Double?
    /// The format capture was asked for (the output settings of the
    /// capture output).
    public var requestedFormat: String?
    /// The device's active capture format when recording started.
    public var deviceFormat: String?
    /// The format of the first buffer that arrived.
    public var deliveredFormat: String?
    /// Buffers delivered by the capture callback.
    public var callbacks = 0
    public var framesReceived = 0
    /// 16 kHz samples handed to `samples` / `timedSamples`.
    public var samplesDelivered = 0
    public var conversionFailures = 0
    public var lastConversionError: String?
    public var runtimeErrors = 0
    public var lastRuntimeError: String?
    public var interruptions = 0
    /// Host time now minus the stamped host time of the newest chunk's end,
    /// in seconds; near zero when timestamps are on the host clock.
    public var timestampLagSeconds: Double?

    public init() {}

    public var description: String {
        func rate(_ value: Double?) -> String { value.map { String(format: "%.0f Hz", $0) } ?? "unknown" }
        return [
            "device: \(deviceName ?? "system default")",
            "device nominal rate: \(rate(deviceNominalSampleRate))",
            "requested format: \(requestedFormat ?? "none")",
            "device format: \(deviceFormat ?? "unknown")",
            "delivered format: \(deliveredFormat ?? "none")",
            "callbacks: \(callbacks), frames: \(framesReceived), samples delivered: \(samplesDelivered)",
            "conversion failures: \(conversionFailures)\(lastConversionError.map { " (last: \($0))" } ?? "")",
            "runtime errors: \(runtimeErrors)\(lastRuntimeError.map { " (last: \($0))" } ?? ""), interruptions: \(interruptions)",
            "timestamp lag: \(timestampLagSeconds.map { String(format: "%.3f s", $0) } ?? "n/a")",
        ].joined(separator: "\n")
    }
}

/// Thread-safe holder for `MicrophoneDiagnostics`, written from the capture
/// queue and read from anywhere.
final class DiagnosticsBox: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: MicrophoneDiagnostics())

    var value: MicrophoneDiagnostics { state.withLock { $0 } }

    func update(_ body: @Sendable (inout MicrophoneDiagnostics) -> Void) {
        state.withLock { body(&$0) }
    }
}

/// Records one input device through `AVCaptureSession` and delivers 16 kHz
/// mono Float32 chunks on `samples` (PLAN.md 4.1).
///
/// Why not `AVAudioEngine`: on macOS the engine drives input and output
/// through one IO unit. After `kAudioOutputUnitProperty_CurrentDevice`
/// points that unit at another input, `inputNode.outputFormat(forBus:)`
/// keeps reporting the previous device's sample rate (on the author's Mac a
/// 24 kHz Bluetooth headset showed 48 kHz and the 48 kHz built-in mic
/// showed 24 kHz), the tap is installed with a format the hardware never
/// delivers, and the tap block is never called: the recording is silent
/// without any error. `AVCaptureSession` with an `AVCaptureDeviceInput`
/// for the chosen device is input-only, so the output device does not
/// matter, and `AVCaptureAudioDataOutput` converts to 16 kHz mono Float32
/// itself (`audioSettings`).
///
/// Isolation choice: a `@MainActor` class rather than an actor. Start,
/// pause, resume, and stop are driven by the UI and the session's
/// notifications are handled on the main queue. `startRunning` and
/// `stopRunning` block, so they run on a private serial session queue;
/// sample buffers arrive on a second serial queue and never touch the main
/// actor: a nonisolated receiver converts each one and yields into the
/// (thread-safe) stream continuation.
///
/// One recorder makes one recording: `samples` finishes on `stop()` or when
/// the device goes away, and a new recording needs a new recorder. When the
/// stream finishes without `stop()` having been called, `failure` says why.
///
/// Recovery: `AVCaptureSession.runtimeErrorNotification` restarts the
/// session (at most `ConfigurationRecovery.maxRecoveries` times per window,
/// so a failing device ends the recording instead of looping);
/// `AVCaptureDevice.wasDisconnectedNotification` for the chosen device ends
/// the recording with `.configurationChanged` rather than silently falling
/// back to another microphone. Interruptions are counted in `diagnostics`
/// and the session is started again when they end.
///
/// `timedSamples` carries the same chunks stamped with host time, for
/// `AudioMixer`. Read a recording through one of the two streams: the first
/// one accessed claims the chunks and the other finishes empty.
@MainActor
public final class MicrophoneRecorder {
    public enum State: Sendable, Equatable {
        case idle
        case recording
        case paused
        case stopped
    }

    /// 16 kHz mono Float32 chunks, in capture order.
    public var samples: AsyncStream<[Float]> { output.samples }
    /// The same chunks as `samples`, each stamped with the host time of its
    /// first sample, paused time removed (see `TimedChunk`).
    public var timedSamples: AsyncStream<TimedChunk> { output.timedSamples }
    public private(set) var state: State = .idle
    /// Set when the recorder stopped on its own (see `configurationChanged`).
    public private(set) var failure: MicrophoneRecorderError?
    /// Runtime errors survived by restarting the session.
    public private(set) var configurationRecoveries = 0
    /// What capture has seen so far; see `MicrophoneDiagnostics`.
    public var diagnostics: MicrophoneDiagnostics { diagnosticsBox.value }

    let output = ChunkFanout()
    let diagnosticsBox = DiagnosticsBox()
    private let notificationCenter: NotificationCenter
    private var observers: [NSObjectProtocol] = []
    private var recovery = ConfigurationRecovery()
    private var capture: CaptureSession?
    /// Replaces the capture session in the notification handlers for unit
    /// tests.
    private var testProbe: TestProbe?

    public convenience init() {
        self.init(notificationCenter: .default)
    }

    init(notificationCenter: NotificationCenter) {
        self.notificationCenter = notificationCenter
    }

    // MARK: - Permission

    public static var permission: MicrophonePermission {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: .authorized
        case .denied: .denied
        case .restricted: .restricted
        case .notDetermined: .notDetermined
        @unknown default: .denied
        }
    }

    /// Asks for microphone access if it has not been decided; returns whether
    /// access is granted.
    public static func requestPermission() async -> Bool {
        switch permission {
        case .authorized: return true
        case .denied, .restricted: return false
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        }
    }

    // MARK: - Control

    /// Starts recording from `device`, or from the system default input when
    /// nil. Requires microphone permission. Returns once the session is
    /// configured; it starts running on the session queue, so the first
    /// samples arrive a little later (Bluetooth headsets can take a second
    /// to switch to their microphone profile).
    public func start(device: AudioInputDevice?) throws {
        guard state == .idle else {
            throw MicrophoneRecorderError.invalidState("This recorder has already been started.")
        }
        guard Self.permission == .authorized else { throw MicrophoneRecorderError.permissionDenied }

        let captureDevice: AVCaptureDevice
        if let device {
            // For audio devices AVFoundation's uniqueID is the CoreAudio UID.
            guard let found = AVCaptureDevice(uniqueID: device.uid) else {
                throw MicrophoneRecorderError.deviceNotFound(name: device.name)
            }
            captureDevice = found
        } else {
            guard let found = AVCaptureDevice.default(for: .audio) else {
                throw MicrophoneRecorderError.noInputDevice
            }
            captureDevice = found
        }

        let session = try CaptureSession(device: captureDevice, output: output, diagnostics: diagnosticsBox)
        let coreAudioDevice = device ?? AudioDeviceList.inputDevice(uid: captureDevice.uniqueID)
        let nominal = coreAudioDevice.flatMap { AudioDeviceList.nominalSampleRate($0.id) }
        let name = device?.name ?? captureDevice.localizedName
        let deviceFormat = Self.describe(captureDevice.activeFormat.formatDescription)
        diagnosticsBox.update {
            $0.deviceName = name
            $0.deviceNominalSampleRate = nominal
            $0.requestedFormat = CaptureSession.requestedFormatDescription
            $0.deviceFormat = deviceFormat
        }

        let uid = captureDevice.uniqueID
        capture = session
        observe(sessionObject: session.notificationObject) { object in
            (object as? AVCaptureDevice)?.uniqueID == uid
        }
        session.startRunning()
        state = .recording
    }

    /// Stops capture without ending the recording; no samples arrive while
    /// paused.
    public func pause() {
        guard state == .recording else { return }
        capture?.stopRunning()
        output.pause(at: currentHostTimeSeconds())
        state = .paused
    }

    /// Continues a paused recording. Failures to restart capture arrive as
    /// a runtime error and are handled like any other.
    public func resume() throws {
        guard state == .paused else { return }
        output.resume(at: currentHostTimeSeconds())
        capture?.startRunning()
        state = .recording
    }

    /// Ends the recording. Chunks already captured are still delivered, then
    /// `samples` finishes.
    public func stop() {
        finish(failure: nil)
    }

    // MARK: - Runtime errors and device changes

    /// What `handleConfigurationChange` did.
    enum ConfigurationChangeOutcome: Equatable {
        /// Not recording (idle or already stopped).
        case ignored
        /// Capture restarted unless paused.
        case recovered
        /// Gave up and finished with `.configurationChanged`.
        case finished
    }

    /// The recovery state machine, independent of the session so it can be
    /// unit-tested. `inputFormatValid` is false when the device is gone.
    /// `reinstall` rebuilds what needs rebuilding; `restart` starts capture
    /// again and is skipped while paused.
    @discardableResult
    func handleConfigurationChange(
        inputFormatValid: Bool,
        now: TimeInterval = ProcessInfo.processInfo.systemUptime,
        reinstall: () throws -> Void = {},
        restart: () throws -> Void
    ) -> ConfigurationChangeOutcome {
        guard state == .recording || state == .paused else { return .ignored }
        guard inputFormatValid, recovery.allowRecovery(at: now) else {
            finish(failure: .configurationChanged)
            return .finished
        }
        do {
            try reinstall()
            if state == .recording {
                try restart()
            }
        } catch {
            finish(failure: .configurationChanged)
            return .finished
        }
        configurationRecoveries += 1
        return .recovered
    }

    private func onRuntimeError(_ message: String) {
        guard state == .recording || state == .paused else { return }
        diagnosticsBox.update {
            $0.runtimeErrors += 1
            $0.lastRuntimeError = message
        }
        if let testProbe {
            handleConfigurationChange(inputFormatValid: testProbe.deviceConnected(), restart: testProbe.restart)
            return
        }
        guard let capture else { return }
        handleConfigurationChange(
            inputFormatValid: capture.device.isConnected,
            restart: { capture.startRunning() }
        )
    }

    private func onDisconnect() {
        // Do not silently fall back to another microphone.
        handleConfigurationChange(inputFormatValid: false, restart: {})
    }

    private func onInterruption(ended: Bool) {
        guard state == .recording || state == .paused else { return }
        if !ended {
            diagnosticsBox.update { $0.interruptions += 1 }
            return
        }
        if state == .recording {
            if let testProbe {
                try? testProbe.restart()
            } else {
                capture?.startRunning()
            }
        }
    }

    /// Observes the session's runtime errors and interruptions (posted for
    /// `sessionObject`) and the disconnection of any device for which
    /// `isChosenDevice` is true.
    private func observe(sessionObject: AnyObject, isChosenDevice: @escaping @Sendable (Any?) -> Bool) {
        guard observers.isEmpty else { return }
        let center = notificationCenter
        observers.append(center.addObserver(
            forName: AVCaptureSession.runtimeErrorNotification, object: sessionObject, queue: .main
        ) { [weak self] note in
            let error = note.userInfo?[AVCaptureSessionErrorKey] as? NSError
            let message = error.map { "\($0.localizedDescription) (\($0.domain) \($0.code))" } ?? "unknown error"
            MainActor.assumeIsolated { self?.onRuntimeError(message) }
        })
        observers.append(center.addObserver(
            forName: AVCaptureSession.wasInterruptedNotification, object: sessionObject, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.onInterruption(ended: false) }
        })
        observers.append(center.addObserver(
            forName: AVCaptureSession.interruptionEndedNotification, object: sessionObject, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.onInterruption(ended: true) }
        })
        observers.append(center.addObserver(
            forName: AVCaptureDevice.wasDisconnectedNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard isChosenDevice(note.object) else { return }
            MainActor.assumeIsolated { self?.onDisconnect() }
        })
    }

    private func stopObserving() {
        for observer in observers {
            notificationCenter.removeObserver(observer)
        }
        observers = []
    }

    // MARK: - Plumbing

    private func finish(failure: MicrophoneRecorderError?) {
        guard state == .recording || state == .paused else {
            if state == .idle {
                state = .stopped
                output.finish()
            }
            return
        }
        stopObserving()
        self.failure = failure
        state = .stopped
        if let capture {
            // Buffers still queued are delivered before the stream ends.
            capture.stop { [output] in output.finish() }
            self.capture = nil
        } else {
            output.finish()
        }
    }

    private static func describe(_ description: CMFormatDescription) -> String {
        guard let asbd = description.audioStreamBasicDescription else { return String(describing: description) }
        return MicrophoneDiagnostics.describe(asbd)
    }

    // MARK: - Test support

    struct TestProbe {
        var deviceConnected: () -> Bool
        var restart: () throws -> Void
    }

    /// The object session notifications are posted for in tests.
    let configurationNotificationObject: AnyObject = NSObject()
    /// The object a device disconnection is posted for in tests.
    let deviceNotificationObject = NSObject()

    /// Puts the recorder in `.recording` without a capture session (no
    /// microphone permission needed) and observes the session and device
    /// notifications on the injected notification center, answering them
    /// through `probe`.
    func beginForTesting(probe: TestProbe) {
        testProbe = probe
        state = .recording
        let device = ObjectIdentifier(deviceNotificationObject)
        observe(sessionObject: configurationNotificationObject) { object in
            (object as AnyObject?).map { ObjectIdentifier($0) == device } ?? false
        }
    }
}

extension MicrophoneDiagnostics {
    static func describe(_ asbd: AudioStreamBasicDescription) -> String {
        let flags = asbd.mFormatFlags
        let kind: String
        if asbd.mFormatID == kAudioFormatLinearPCM {
            let float = flags & kAudioFormatFlagIsFloat != 0
            kind = "\(float ? "Float" : "Int")\(asbd.mBitsPerChannel)"
                + (flags & kAudioFormatFlagIsNonInterleaved != 0 ? " non-interleaved" : " interleaved")
        } else {
            kind = "format \(asbd.mFormatID)"
        }
        return String(format: "%d ch, %.0f Hz, %@", Int(asbd.mChannelsPerFrame), asbd.mSampleRate, kind)
    }
}

/// Watches for a recording that never receives audio: `check` returns true
/// once `timeout` seconds of recording (paused time excluded) passed without
/// a single sample.
public struct NoAudioWatchdog: Sendable, Equatable {
    public static let timeout: TimeInterval = 3

    private var recordingTime: TimeInterval = 0
    private var lastCheck: TimeInterval?
    private var wasRecording = false
    private var heardAudio = false

    public init() {}

    /// Call periodically with a monotonic `now` in seconds, whether capture
    /// is currently running (not paused), and the samples delivered so far.
    public mutating func check(now: TimeInterval, isRecording: Bool, samplesDelivered: Int) -> Bool {
        if samplesDelivered > 0 { heardAudio = true }
        guard !heardAudio else { return false }
        if isRecording, wasRecording, let lastCheck {
            recordingTime += max(0, now - lastCheck)
        }
        lastCheck = now
        wasRecording = isRecording
        return recordingTime >= Self.timeout
    }
}

/// Decides whether another configuration-change recovery is allowed: at most
/// `maxRecoveries` within any `window` seconds, so a flapping device ends the
/// recording instead of restarting forever.
struct ConfigurationRecovery: Equatable {
    static let maxRecoveries = 5
    static let window: TimeInterval = 10

    private(set) var recent: [TimeInterval] = []

    mutating func allowRecovery(at time: TimeInterval) -> Bool {
        recent.removeAll { time - $0 >= Self.window }
        guard recent.count < Self.maxRecoveries else { return false }
        recent.append(time)
        return true
    }
}

/// Owns the `AVCaptureSession`. Configured once on the main actor before it
/// runs; afterwards the session is only touched on `sessionQueue`, and
/// sample buffers only on `sampleQueue`, hence `@unchecked Sendable`.
private final class CaptureSession: @unchecked Sendable {
    /// What `AVCaptureAudioDataOutput` is asked to deliver: the recorder's
    /// own 16 kHz mono Float32 format.
    static var audioSettings: [String: Any] {
        [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: MonoResampler.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: true,
            AVLinearPCMIsBigEndianKey: false,
        ]
    }
    static let requestedFormatDescription = String(
        format: "1 ch, %.0f Hz, Float32 non-interleaved", MonoResampler.sampleRate
    )

    let device: AVCaptureDevice
    private let session = AVCaptureSession()
    private let receiver: SampleReceiver
    private let sessionQueue = DispatchQueue(label: "tw.og1o.hearsay.mic.session")
    private let sampleQueue = DispatchQueue(label: "tw.og1o.hearsay.mic.samples")

    /// Session notifications are posted with the session as the object.
    var notificationObject: AnyObject { session }

    init(device: AVCaptureDevice, output: ChunkFanout, diagnostics: DiagnosticsBox) throws {
        self.device = device
        receiver = SampleReceiver(output: output, diagnostics: diagnostics)
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            throw MicrophoneRecorderError.engineFailed(
                "cannot open \(device.localizedName): \(error.localizedDescription)"
            )
        }
        let dataOutput = AVCaptureAudioDataOutput()
        dataOutput.audioSettings = Self.audioSettings
        session.beginConfiguration()
        guard session.canAddInput(input) else {
            session.commitConfiguration()
            throw MicrophoneRecorderError.engineFailed("cannot record from \(device.localizedName)")
        }
        session.addInput(input)
        guard session.canAddOutput(dataOutput) else {
            session.commitConfiguration()
            throw MicrophoneRecorderError.engineFailed("cannot add an audio output for \(device.localizedName)")
        }
        session.addOutput(dataOutput)
        dataOutput.setSampleBufferDelegate(receiver, queue: sampleQueue)
        session.commitConfiguration()
    }

    func startRunning() {
        sessionQueue.async { [self] in
            guard !session.isRunning else { return }
            session.startRunning()
            receiver.setClock(session.synchronizationClock)
        }
    }

    func stopRunning() {
        sessionQueue.async { [self] in
            if session.isRunning {
                session.stopRunning()
            }
        }
    }

    /// Stops the session, lets the sample queue drain, then calls `done`
    /// on the sample queue.
    func stop(then done: @escaping @Sendable () -> Void) {
        sessionQueue.async { [self] in
            if session.isRunning {
                session.stopRunning()
            }
            sampleQueue.async { [self] in
                receiver.flush()
                done()
            }
        }
    }
}

/// Receives `AVCaptureAudioDataOutput` sample buffers. They arrive serially
/// on the sample queue, the only place `converter` is used; the clock is
/// guarded by a lock. Hence `@unchecked Sendable`.
private final class SampleReceiver: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate, @unchecked Sendable {
    private let output: ChunkFanout
    private let diagnostics: DiagnosticsBox
    private var converter: PCMSampleBufferConverter?
    private let clockLock = NSLock()
    private var clock: CMClock?

    init(output: ChunkFanout, diagnostics: DiagnosticsBox) {
        self.output = output
        self.diagnostics = diagnostics
    }

    /// The session's synchronization clock; timestamps are converted from
    /// it to the host clock. nil means they already are on the host clock.
    func setClock(_ clock: CMClock?) {
        clockLock.withLock { self.clock = clock }
    }

    func captureOutput(
        _ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection
    ) {
        handle(sampleBuffer)
    }

    private func handle(_ sampleBuffer: CMSampleBuffer) {
        let frames = sampleBuffer.numSamples
        let format = sampleBuffer.formatDescription?.audioStreamBasicDescription.map(MicrophoneDiagnostics.describe)
        diagnostics.update {
            $0.callbacks += 1
            $0.framesReceived += frames
            if $0.deliveredFormat == nil { $0.deliveredFormat = format }
        }
        guard sampleBuffer.isValid, frames > 0 else { return }
        let chunk: [Float]
        do {
            chunk = try convert(sampleBuffer)
        } catch {
            // A failed conversion drops this buffer only; the recording goes on.
            let text = String(describing: error)
            diagnostics.update {
                $0.conversionFailures += 1
                $0.lastConversionError = text
            }
            return
        }
        guard !chunk.isEmpty else { return }
        let hostTime = hostSeconds(sampleBuffer.presentationTimeStamp)
        output.yield(chunk, hostTime: hostTime)
        let lag = currentHostTimeSeconds() - (hostTime + Double(chunk.count) / MonoResampler.sampleRate)
        let count = chunk.count
        diagnostics.update {
            $0.samplesDelivered += count
            $0.timestampLagSeconds = lag
        }
    }

    /// Seconds on the host clock (`mach_absolute_time`, as `AudioMixer` and
    /// ScreenCaptureKit use), from a presentation timestamp on the session's
    /// synchronization clock.
    private func hostSeconds(_ time: CMTime) -> TimeInterval {
        guard time.isNumeric else { return currentHostTimeSeconds() }
        let clock = clockLock.withLock { self.clock }
        guard let clock else { return time.seconds }
        let converted = CMSyncConvertTime(time, from: clock, to: CMClockGetHostTimeClock())
        return converted.isNumeric ? converted.seconds : time.seconds
    }

    private func convert(_ sampleBuffer: CMSampleBuffer) throws -> [Float] {
        guard let description = sampleBuffer.formatDescription else {
            throw AudioConversionError.unsupportedFormat("audio buffer without a format")
        }
        let format = AVAudioFormat(cmAudioFormatDescription: description)
        if converter?.format != format {
            converter = try PCMSampleBufferConverter(format: format)
        }
        guard let converter else {
            throw AudioConversionError.conversionFailed("no converter")
        }
        return try converter.convert(sampleBuffer)
    }

    /// Emits what a resampler still holds; called on the sample queue.
    func flush() {
        guard let chunk = try? converter?.flush(), !chunk.isEmpty else { return }
        output.yield(chunk, hostTime: currentHostTimeSeconds())
    }
}
