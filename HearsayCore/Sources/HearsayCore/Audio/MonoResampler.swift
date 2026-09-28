@preconcurrency import AVFoundation

/// Errors raised while converting audio to Hearsay's 16 kHz mono format.
public enum AudioConversionError: Error, Equatable, Sendable, CustomStringConvertible {
    case unsupportedFormat(String)
    case conversionFailed(String)

    public var description: String {
        switch self {
        case .unsupportedFormat(let detail): "Unsupported audio format: \(detail)"
        case .conversionFailed(let detail): "Audio conversion failed: \(detail)"
        }
    }
}

/// Streaming `AVAudioConverter` wrapper that turns buffers of any PCM format
/// into 16 kHz mono Float32 samples. Keeps the converter's resampling state
/// across calls, so consecutive buffers join without clicks. Not thread-safe:
/// use one instance from one thread at a time.
final class MonoResampler {
    static let sampleRate: Double = 16_000

    static func outputFormat() throws -> AVAudioFormat {
        guard let format = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false
        ) else {
            throw AudioConversionError.unsupportedFormat("cannot create 16 kHz mono Float32")
        }
        return format
    }

    private let converter: AVAudioConverter
    private let inputFormat: AVAudioFormat
    private let outputFormat: AVAudioFormat

    init(inputFormat: AVAudioFormat) throws {
        let outputFormat = try Self.outputFormat()
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0,
              let converter = AVAudioConverter(from: inputFormat, to: outputFormat)
        else {
            throw AudioConversionError.unsupportedFormat(String(describing: inputFormat))
        }
        converter.downmix = true
        self.converter = converter
        self.inputFormat = inputFormat
        self.outputFormat = outputFormat
    }

    /// Converts one input buffer. May return fewer (or zero) samples than the
    /// ratio suggests; the remainder comes out with later buffers or `flush`.
    func convert(_ buffer: AVAudioPCMBuffer) throws -> [Float] {
        try run(input: buffer, endOfStream: false)
    }

    /// Drains what the converter still holds at the end of the input.
    func flush() throws -> [Float] {
        try run(input: nil, endOfStream: true)
    }

    private func run(input: AVAudioPCMBuffer?, endOfStream: Bool) throws -> [Float] {
        let inputFrames = Double(input?.frameLength ?? 0)
        let ratio = outputFormat.sampleRate / inputFormat.sampleRate
        let capacity = AVAudioFrameCount(max(1024, (inputFrames * ratio).rounded(.up) + 64))
        var pending = input
        var result: [Float] = []
        while true {
            guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else {
                throw AudioConversionError.conversionFailed("cannot allocate output buffer")
            }
            var conversionError: NSError?
            let status = converter.convert(to: output, error: &conversionError) { _, outStatus in
                if let buffer = pending {
                    pending = nil
                    outStatus.pointee = .haveData
                    return buffer
                }
                outStatus.pointee = endOfStream ? .endOfStream : .noDataNow
                return nil
            }
            if status == .error {
                throw AudioConversionError.conversionFailed(conversionError?.localizedDescription ?? "unknown error")
            }
            if let channel = output.floatChannelData?[0], output.frameLength > 0 {
                result.append(contentsOf: UnsafeBufferPointer(start: channel, count: Int(output.frameLength)))
            }
            // .haveData means the output filled up and more may be waiting.
            if status != .haveData || output.frameLength == 0 {
                break
            }
        }
        return result
    }
}

/// Turns `CMSampleBuffer`s of linear PCM into 16 kHz mono Float32, copying
/// directly when they already are, resampling otherwise. Not thread-safe:
/// use one instance from one queue.
final class PCMSampleBufferConverter {
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
