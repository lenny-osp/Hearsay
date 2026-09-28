@preconcurrency import AVFoundation
import AudioToolbox
import CoreAudio

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
    case engineFailed(String)
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
        case .engineFailed(let detail):
            "The audio engine failed: \(detail)"
        case .configurationChanged:
            "The input device changed or was disconnected, so recording stopped."
        case .invalidState(let detail):
            detail
        }
    }
}

/// Records the microphone through `AVAudioEngine` and delivers 16 kHz mono
/// Float32 chunks on `samples` (PLAN.md 4.1).
///
/// Isolation choice: a `@MainActor` class rather than an actor. Start, pause,
/// resume, and stop are driven by the UI, `AVAudioEngine` is not `Sendable`,
/// and its configuration-change notification is handled on the main queue,
/// so keeping the engine on the main actor needs no hops. The realtime tap
/// never touches the main actor: it runs a nonisolated handler that converts
/// each buffer and yields into the (thread-safe) stream continuation.
///
/// One recorder makes one recording: `samples` finishes on `stop()` or when
/// the device goes away, and a new recording needs a new recorder. When the
/// stream finishes without `stop()` having been called, `failure` says why.
///
/// `AVAudioEngineConfigurationChange` means "the engine stopped itself and
/// must be restarted", not "the device is gone". CoreAudio posts it whenever
/// the input unit's device or stream format is (re)configured, which
/// routinely happens right after `engine.start()` (for example once the
/// device chosen with `kAudioOutputUnitProperty_CurrentDevice` settles on its
/// hardware format), and also on sample-rate switches or when another app
/// changes the device. The recorder therefore reinstalls the tap when the
/// input format changed and restarts the engine, and only gives up (see
/// `ConfigurationRecovery`) when the device really disappeared, the restart
/// fails, or the device keeps flapping.
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
    /// Configuration changes survived by reinstalling and restarting.
    public private(set) var configurationRecoveries = 0

    let output = ChunkFanout()
    private let engine = AVAudioEngine()
    private let notificationCenter: NotificationCenter
    private var configurationObserver: NSObjectProtocol?
    private var recovery = ConfigurationRecovery()
    /// True once the tap is installed; the engine is left alone otherwise.
    private var engineConfigured = false
    private var isStarting = false
    private var pendingConfigurationChange = false
    private var tapFormat: AVAudioFormat?
    private var selectedDevice: AudioInputDevice?
    /// Replaces the engine in `onConfigurationChange` for unit tests.
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
    /// nil. Requires microphone permission.
    public func start(device: AudioInputDevice?) throws {
        guard state == .idle, !isStarting else {
            throw MicrophoneRecorderError.invalidState("This recorder has already been started.")
        }
        guard Self.permission == .authorized else { throw MicrophoneRecorderError.permissionDenied }

        let input = engine.inputNode
        if let device {
            try select(device, on: input)
        }
        selectedDevice = device
        let format = input.outputFormat(forBus: 0)
        guard Self.isUsable(format) else {
            throw MicrophoneRecorderError.noInputDevice
        }
        do {
            try installTap(format: format)
        } catch {
            throw MicrophoneRecorderError.engineFailed(String(describing: error))
        }

        // Observe before starting: CoreAudio often posts a configuration
        // change right after the engine starts.
        isStarting = true
        pendingConfigurationChange = false
        observeConfigurationChanges()
        engine.prepare()
        do {
            try engine.start()
        } catch {
            isStarting = false
            stopObservingConfigurationChanges()
            removeTap()
            throw MicrophoneRecorderError.engineFailed(error.localizedDescription)
        }
        isStarting = false
        state = .recording
        if pendingConfigurationChange {
            pendingConfigurationChange = false
            onConfigurationChange()
        }
    }

    /// Stops capture without ending the recording; no samples arrive while
    /// paused.
    public func pause() {
        guard state == .recording else { return }
        if engineConfigured {
            engine.pause()
        }
        output.pause(at: currentHostTimeSeconds())
        state = .paused
    }

    public func resume() throws {
        guard state == .paused else { return }
        if engineConfigured {
            do {
                engine.prepare()
                try engine.start()
            } catch {
                finish(failure: .engineFailed(error.localizedDescription))
                throw MicrophoneRecorderError.engineFailed(error.localizedDescription)
            }
        }
        output.resume(at: currentHostTimeSeconds())
        state = .recording
    }

    /// Ends the recording. Chunks already captured are still delivered, then
    /// `samples` finishes.
    public func stop() {
        finish(failure: nil)
    }

    // MARK: - Configuration changes

    /// What `handleConfigurationChange` did.
    enum ConfigurationChangeOutcome: Equatable {
        /// Not recording (idle or already stopped).
        case ignored
        /// Tap reinstalled if needed; engine restarted unless paused.
        case recovered
        /// Gave up and finished with `.configurationChanged`.
        case finished
    }

    /// The recovery state machine, independent of the engine so it can be
    /// unit-tested. `inputFormatValid` is false when the device is gone
    /// (zero channels or sample rate, or the chosen device disconnected).
    /// `reinstall` swaps the tap when the format changed; `restart` starts
    /// the engine again and is skipped while paused.
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

    private func onConfigurationChange() {
        if isStarting {
            pendingConfigurationChange = true
            return
        }
        if let testProbe {
            handleConfigurationChange(inputFormatValid: testProbe.inputFormatValid(), restart: testProbe.restart)
            return
        }
        guard engineConfigured else { return }
        let input = engine.inputNode
        if let selectedDevice, AudioDeviceList.inputDevice(uid: selectedDevice.uid) == nil {
            // The chosen device was unplugged; do not silently fall back to
            // another microphone.
            handleConfigurationChange(inputFormatValid: false, restart: {})
            return
        }
        let format = input.outputFormat(forBus: 0)
        handleConfigurationChange(
            inputFormatValid: Self.isUsable(format),
            reinstall: { [self] in
                if let tapFormat, tapFormat == format { return }
                try installTap(format: format)
            },
            restart: { [engine] in
                engine.prepare()
                try engine.start()
            }
        )
    }

    private func observeConfigurationChanges() {
        guard configurationObserver == nil else { return }
        configurationObserver = notificationCenter.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.onConfigurationChange()
            }
        }
    }

    private func stopObservingConfigurationChanges() {
        if let configurationObserver {
            notificationCenter.removeObserver(configurationObserver)
            self.configurationObserver = nil
        }
    }

    // MARK: - Engine plumbing

    private static func isUsable(_ format: AVAudioFormat) -> Bool {
        format.sampleRate > 0 && format.channelCount > 0
    }

    /// Installs (or replaces) the tap with a fresh handler and resampler for
    /// `format`, so a format change never reuses a stale converter.
    private func installTap(format: AVAudioFormat) throws {
        let handler = TapHandler(resampler: try MonoResampler(inputFormat: format), output: output)
        let input = engine.inputNode
        if engineConfigured {
            input.removeTap(onBus: 0)
        }
        // 0.1 s at the hardware rate, like the Python pump chunk; the system
        // may pick another size.
        let bufferSize = AVAudioFrameCount(max(1024, format.sampleRate / 10))
        input.installTap(onBus: 0, bufferSize: bufferSize, format: format, block: Self.makeTapBlock(handler))
        tapFormat = format
        engineConfigured = true
    }

    private func removeTap() {
        guard engineConfigured else { return }
        engine.inputNode.removeTap(onBus: 0)
        engineConfigured = false
        tapFormat = nil
    }

    private func finish(failure: MicrophoneRecorderError?) {
        guard state == .recording || state == .paused else {
            if state == .idle {
                state = .stopped
                output.finish()
            }
            return
        }
        stopObservingConfigurationChanges()
        if engineConfigured {
            removeTap()
            engine.stop()
        }
        self.failure = failure
        state = .stopped
        output.finish()
    }

    private func select(_ device: AudioInputDevice, on input: AVAudioInputNode) throws {
        guard let unit = input.audioUnit else {
            throw MicrophoneRecorderError.cannotSelectDevice(name: device.name, status: OSStatus(kAudioUnitErr_Uninitialized))
        }
        var deviceID = device.id
        let status = AudioUnitSetProperty(
            unit,
            kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global,
            0,
            &deviceID,
            UInt32(MemoryLayout<AudioDeviceID>.size)
        )
        guard status == noErr else {
            throw MicrophoneRecorderError.cannotSelectDevice(name: device.name, status: status)
        }
    }

    /// Built outside the main actor so the closure is not main-actor isolated;
    /// it runs on the realtime audio thread.
    private nonisolated static func makeTapBlock(_ handler: TapHandler) -> AVAudioNodeTapBlock {
        { buffer, when in handler.handle(buffer, when: when) }
    }

    // MARK: - Test support

    struct TestProbe {
        var inputFormatValid: () -> Bool
        var restart: () throws -> Void
    }

    /// The object `AVAudioEngineConfigurationChange` is posted for.
    var configurationNotificationObject: AnyObject { engine }

    /// Puts the recorder in `.recording` without touching the audio engine
    /// (no microphone permission needed) and observes configuration changes
    /// on the injected notification center, answering them through `probe`.
    func beginForTesting(probe: TestProbe) {
        testProbe = probe
        state = .recording
        observeConfigurationChanges()
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

/// Converts tap buffers and yields them. Only ever used from the engine's
/// tap thread, which calls it serially, hence `@unchecked Sendable`.
private final class TapHandler: @unchecked Sendable {
    private let resampler: MonoResampler
    private let output: ChunkFanout

    init(resampler: MonoResampler, output: ChunkFanout) {
        self.resampler = resampler
        self.output = output
    }

    func handle(_ buffer: AVAudioPCMBuffer, when: AVAudioTime) {
        // A failed conversion drops this buffer only; the recording goes on.
        guard let chunk = try? resampler.convert(buffer), !chunk.isEmpty else { return }
        let hostTime = when.isHostTimeValid
            ? AVAudioTime.seconds(forHostTime: when.hostTime)
            : currentHostTimeSeconds()
        output.yield(chunk, hostTime: hostTime)
    }
}
