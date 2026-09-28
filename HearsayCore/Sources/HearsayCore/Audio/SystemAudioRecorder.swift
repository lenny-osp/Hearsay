@preconcurrency import AVFoundation
import CoreGraphics
import CoreMedia
@preconcurrency import ScreenCaptureKit

/// Screen & System Audio Recording access. CoreGraphics only reports
/// whether access is granted; "not yet asked" and "denied" look the same.
public enum SystemAudioPermission: Sendable, Equatable {
    case authorized
    case notAuthorized
}

/// Why system audio capture could not start or stopped on its own.
public enum SystemAudioRecorderError: Error, Equatable, Sendable, CustomStringConvertible {
    case permissionDenied
    case noDisplay
    case startFailed(String)
    /// ScreenCaptureKit stopped the stream (`stream(_:didStopWithError:)`).
    case streamStopped(String)
    case invalidState(String)

    public var description: String {
        switch self {
        case .permissionDenied:
            String(localized: "Hearsay has no system audio access. Allow it in System Settings > Privacy & Security > Screen & System Audio Recording, then start a new recording.",
                   bundle: .module, comment: "Recording error. Use the macOS wording for the System Settings path.")
        case .noDisplay:
            String(localized: "No display is available to capture system audio from.", bundle: .module,
                   comment: "Recording error")
        case .startFailed(let detail):
            String(localized: "System audio capture could not start: \(detail)", bundle: .module,
                   comment: "Recording error. %@ is a technical detail from macOS.")
        case .streamStopped(let detail):
            String(localized: "System audio capture stopped: \(detail)", bundle: .module,
                   comment: "Recording error. %@ is a technical detail from macOS.")
        case .invalidState(let detail):
            detail
        }
    }
}

/// Records the audio the Mac plays (other apps, calls) through
/// ScreenCaptureKit and delivers 16 kHz mono Float32 chunks on `samples` and
/// `timedSamples` (PLAN.md 4.1, step 2).
///
/// Same isolation design as `MicrophoneRecorder`: a `@MainActor` class driven
/// by the UI; the sample-buffer callbacks run nonisolated on a private serial
/// queue and only touch a thread-safe `ChunkFanout`.
///
/// ScreenCaptureKit has no audio-only configuration (checked against the
/// macOS 27 SDK: `SCStreamConfiguration` offers no way to turn video off on
/// any version), so every macOS version uses the smallest video setup: a 2x2
/// frame at 1 fps whose sample buffers are dropped unread.
///
/// Hearsay's own audio is excluded. Pause keeps the stream running and drops
/// the buffers, so resuming is instant. One recorder makes one recording.
@MainActor
public final class SystemAudioRecorder {
    public enum State: Sendable, Equatable {
        case idle
        case starting
        case recording
        case paused
        case stopped
    }

    public var samples: AsyncStream<[Float]> { output.samples }
    /// The same chunks as `samples` stamped with host time (see `TimedChunk`).
    public var timedSamples: AsyncStream<TimedChunk> { output.timedSamples }
    public private(set) var state: State = .idle
    /// Set when capture stopped on its own.
    public private(set) var failure: SystemAudioRecorderError?

    private let output = ChunkFanout()
    private let sampleQueue = DispatchQueue(label: "tw.og1o.hearsay.system-audio", qos: .userInitiated)
    private var stream: SCStream?
    private var receiver: StreamReceiver?

    public init() {}

    // MARK: - Permission

    public static var permission: SystemAudioPermission {
        CGPreflightScreenCaptureAccess() ? .authorized : .notAuthorized
    }

    /// Shows the system prompt the first time; afterwards it only reports
    /// the current answer. A grant usually takes effect after a relaunch.
    @discardableResult
    public static func requestPermission() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    // MARK: - Control

    public func start() async throws {
        guard state == .idle else {
            throw SystemAudioRecorderError.invalidState("This recorder has already been started.")
        }
        guard Self.permission == .authorized else { throw SystemAudioRecorderError.permissionDenied }
        state = .starting

        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            state = .idle
            throw Self.mapStartError(error)
        }
        guard state == .starting else { return }
        let mainID = CGMainDisplayID()
        guard let display = content.displays.first(where: { $0.displayID == mainID }) ?? content.displays.first else {
            state = .idle
            throw SystemAudioRecorderError.noDisplay
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = Self.makeConfiguration()
        let receiver = StreamReceiver(output: output) { [weak self] message in
            Task { @MainActor in
                self?.finish(failure: .streamStopped(message))
            }
        }
        let stream = SCStream(filter: filter, configuration: configuration, delegate: receiver)
        do {
            try stream.addStreamOutput(receiver, type: .audio, sampleHandlerQueue: sampleQueue)
            // Without a screen output ScreenCaptureKit logs every dropped
            // frame; this one discards them.
            try stream.addStreamOutput(receiver, type: .screen, sampleHandlerQueue: sampleQueue)
            try await stream.startCapture()
        } catch {
            state = .idle
            throw Self.mapStartError(error)
        }
        // `stop()` may have been called while starting.
        guard state == .starting else {
            try? await stream.stopCapture()
            return
        }
        self.stream = stream
        self.receiver = receiver
        state = .recording
    }

    public func pause() {
        guard state == .recording else { return }
        output.pause(at: currentHostTimeSeconds())
        state = .paused
    }

    public func resume() {
        guard state == .paused else { return }
        output.resume(at: currentHostTimeSeconds())
        state = .recording
    }

    /// Ends the recording. Buffers already captured are still delivered,
    /// then the streams finish.
    public func stop() {
        finish(failure: nil)
    }

    private func finish(failure: SystemAudioRecorderError?) {
        switch state {
        case .stopped:
            return
        case .idle, .starting:
            state = .stopped
            output.finish()
            return
        case .recording, .paused:
            break
        }
        self.failure = failure
        state = .stopped
        let queue = sampleQueue
        let receiver = self.receiver
        let output = self.output
        let drain: @Sendable () -> Void = {
            // After the queue has delivered every pending buffer.
            queue.async {
                receiver?.flush()
                output.finish()
            }
        }
        if failure == nil, let stream {
            // The completion keeps the stream alive until it has stopped.
            stream.stopCapture { _ in
                withExtendedLifetime(stream) { drain() }
            }
        } else {
            drain()
        }
        stream = nil
        self.receiver = nil
    }

    private static func makeConfiguration() -> SCStreamConfiguration {
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = Int(MonoResampler.sampleRate)
        configuration.channelCount = 1
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.showsCursor = false
        configuration.queueDepth = 3
        return configuration
    }

    private static func mapStartError(_ error: Error) -> SystemAudioRecorderError {
        if let scError = error as? SCStreamError, scError.code == .userDeclined {
            return .permissionDenied
        }
        let nsError = error as NSError
        if nsError.domain == SCStreamErrorDomain, nsError.code == SCStreamError.Code.userDeclined.rawValue {
            return .permissionDenied
        }
        return .startFailed(error.localizedDescription)
    }
}

/// Receives ScreenCaptureKit callbacks. Sample buffers arrive serially on the
/// recorder's queue, which is the only place `converter` is used, hence
/// `@unchecked Sendable`.
private final class StreamReceiver: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let output: ChunkFanout
    private let onStop: @Sendable (String) -> Void
    private var converter: SampleBufferConverter?

    init(output: ChunkFanout, onStop: @escaping @Sendable (String) -> Void) {
        self.output = output
        self.onStop = onStop
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid, sampleBuffer.numSamples > 0 else { return }
        do {
            let chunk = try convert(sampleBuffer)
            let hostTime = sampleBuffer.presentationTimeStamp.isNumeric
                ? sampleBuffer.presentationTimeStamp.seconds
                : currentHostTimeSeconds()
            output.yield(chunk, hostTime: hostTime)
        } catch {
            // A failed conversion drops this buffer only; capture goes on.
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onStop(error.localizedDescription)
    }

    /// Emits what a resampler still holds; called on the sample queue.
    func flush() {
        guard let chunk = try? converter?.flush(), !chunk.isEmpty else { return }
        output.yield(chunk, hostTime: currentHostTimeSeconds())
    }

    private func convert(_ sampleBuffer: CMSampleBuffer) throws -> [Float] {
        guard let description = sampleBuffer.formatDescription,
              var streamDescription = description.audioStreamBasicDescription,
              let format = AVAudioFormat(streamDescription: &streamDescription)
        else {
            throw AudioConversionError.unsupportedFormat("system audio buffer without an audio format")
        }
        if converter?.format != format {
            converter = try SampleBufferConverter(format: format)
        }
        guard let converter else {
            throw AudioConversionError.conversionFailed("no converter")
        }
        return try converter.convert(sampleBuffer)
    }
}

/// Turns ScreenCaptureKit audio buffers into 16 kHz mono Float32, copying
/// directly when they already are, resampling otherwise.
private final class SampleBufferConverter {
    let format: AVAudioFormat
    private let resampler: MonoResampler?

    init(format: AVAudioFormat) throws {
        self.format = format
        let isTarget = format.commonFormat == .pcmFormatFloat32
            && format.sampleRate == MonoResampler.sampleRate
            && format.channelCount == 1
        resampler = isTarget ? nil : try MonoResampler(inputFormat: format)
    }

    func convert(_ sampleBuffer: CMSampleBuffer) throws -> [Float] {
        let frames = AVAudioFrameCount(sampleBuffer.numSamples)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else {
            throw AudioConversionError.conversionFailed("cannot allocate a buffer for \(frames) frames")
        }
        buffer.frameLength = frames
        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(
            sampleBuffer, at: 0, frameCount: Int32(frames), into: buffer.mutableAudioBufferList
        )
        guard status == noErr else {
            throw AudioConversionError.conversionFailed("CoreMedia error \(status)")
        }
        if let resampler {
            return try resampler.convert(buffer)
        }
        guard let channel = buffer.floatChannelData?[0] else {
            throw AudioConversionError.conversionFailed("no float channel data")
        }
        return Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
    }

    func flush() throws -> [Float] {
        try resampler?.flush() ?? []
    }
}
