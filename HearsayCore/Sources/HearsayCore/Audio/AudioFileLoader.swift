@preconcurrency import AVFoundation

/// Decodes any AVFoundation-readable audio file (wav, m4a, mp3, aac, aiff,
/// caf, the audio track of mp4/mov) to 16 kHz mono Float32 (PLAN.md 4.2).
public enum AudioFileLoader {
    /// Frames read from the file per conversion step.
    static let readChunkFrames: AVAudioFrameCount = 65_536

    public static func loadMono16k(url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let format = file.processingFormat
        let resampler = try MonoResampler(inputFormat: format)
        var samples: [Float] = []
        let expected = Double(file.length) * MonoResampler.sampleRate / format.sampleRate
        samples.reserveCapacity(Int(expected.rounded(.up)) + 1024)
        while file.framePosition < file.length {
            // A fresh buffer per read: the converter may still reference the
            // previous one while it holds unconsumed frames.
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: readChunkFrames) else {
                throw AudioConversionError.conversionFailed("cannot allocate read buffer")
            }
            try file.read(into: buffer, frameCount: readChunkFrames)
            if buffer.frameLength == 0 { break }
            samples.append(contentsOf: try resampler.convert(buffer))
        }
        samples.append(contentsOf: try resampler.flush())
        return samples
    }
}
