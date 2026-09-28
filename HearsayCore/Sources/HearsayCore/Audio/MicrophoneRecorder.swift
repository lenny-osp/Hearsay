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
    /// The audio hardware configuration changed while recording, usually
    /// because the input device was unplugged. Recording stopped.
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

    private let output = ChunkFanout()
    private let engine = AVAudioEngine()
    private var configurationObserver: NSObjectProtocol?

    public init() {}

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
        guard state == .idle else {
            throw MicrophoneRecorderError.invalidState("This recorder has already been started.")
        }
        guard Self.permission == .authorized else { throw MicrophoneRecorderError.permissionDenied }

        let input = engine.inputNode
        if let device {
            try select(device, on: input)
        }
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            throw MicrophoneRecorderError.noInputDevice
        }
        let handler: TapHandler
        do {
            handler = TapHandler(resampler: try MonoResampler(inputFormat: format), output: output)
        } catch {
            throw MicrophoneRecorderError.engineFailed(String(describing: error))
        }
        // 0.1 s at the hardware rate, like the Python pump chunk; the system
        // may pick another size.
        let bufferSize = AVAudioFrameCount(max(1024, format.sampleRate / 10))
        input.installTap(onBus: 0, bufferSize: bufferSize, format: format, block: Self.makeTapBlock(handler))
        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            throw MicrophoneRecorderError.engineFailed(error.localizedDescription)
        }
        state = .recording
        configurationObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.finish(failure: .configurationChanged)
            }
        }
    }

    /// Stops capture without ending the recording; no samples arrive while
    /// paused.
    public func pause() {
        guard state == .recording else { return }
        engine.pause()
        output.pause(at: currentHostTimeSeconds())
        state = .paused
    }

    public func resume() throws {
        guard state == .paused else { return }
        do {
            try engine.start()
        } catch {
            finish(failure: .engineFailed(error.localizedDescription))
            throw MicrophoneRecorderError.engineFailed(error.localizedDescription)
        }
        output.resume(at: currentHostTimeSeconds())
        state = .recording
    }

    /// Ends the recording. Chunks already captured are still delivered, then
    /// `samples` finishes.
    public func stop() {
        finish(failure: nil)
    }

    private func finish(failure: MicrophoneRecorderError?) {
        guard state == .recording || state == .paused else {
            if state == .idle {
                state = .stopped
                output.finish()
            }
            return
        }
        if let configurationObserver {
            NotificationCenter.default.removeObserver(configurationObserver)
            self.configurationObserver = nil
        }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
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
