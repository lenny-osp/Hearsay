import Foundation

/// The state `Transcriber.transcribeStep` carries between 30 s windows, so a
/// suspended transcription can continue where it stopped (PLAN.md 4.9,
/// "Resumable decoding"). Resuming with it gives the same segments and text
/// as one uninterrupted `transcribe` call.
public struct TranscriptionCheckpoint: Sendable, Equatable {
    /// Mel frame (10 ms) where the next window starts.
    public let seek: Int
    /// Content frames of the input (the mel length minus the 30 s padding).
    public let contentFrames: Int
    /// Initial prompt tokens followed by every segment's tokens so far.
    public let allTokens: [Int]
    /// Segments decoded so far.
    public let segments: [TranscriptSegment]
    /// Index into `allTokens` where the next window's prompt starts.
    public let promptResetSince: Int
    /// ISO code of the language used (given or detected on the first call).
    public let language: String
    /// Tokens of `options.initialPrompt` at the start of `allTokens`; the
    /// final text leaves them out.
    public let initialPromptTokenCount: Int

    /// What the checkpoint was made from; a resume with other input throws
    /// `TranscriptionCheckpointError`.
    public let sampleCount: Int
    public let sampleFingerprint: UInt64
    public let options: TranscriptionOptions

    /// Fraction of content frames done, 0...1 (what `progress` last reported).
    public var fractionDone: Double {
        Double(min(contentFrames, seek)) / Double(max(contentFrames, 1))
    }
}

/// The result of one `Transcriber.transcribeStep` call.
public enum TranscriptionStep: Sendable, Equatable {
    /// Every window is decoded.
    case finished(Transcription)
    /// `shouldYield` asked to stop before the next window; pass the
    /// checkpoint back as `resumingFrom` to continue.
    case suspended(TranscriptionCheckpoint)
}

/// A checkpoint was passed with input it was not made from.
public enum TranscriptionCheckpointError: Error, Equatable, CustomStringConvertible {
    /// The samples differ (count or content) from the checkpoint's.
    case samplesMismatch
    /// The options differ from the checkpoint's.
    case optionsMismatch

    public var description: String {
        switch self {
        case .samplesMismatch:
            return "The checkpoint was made from different audio samples."
        case .optionsMismatch:
            return "The checkpoint was made with different transcription options."
        }
    }
}

/// FNV-1a over the sample count and the bit patterns of every 1600th sample
/// (one per 0.1 s) and the last one: cheap on an hour of audio, and enough to
/// catch a checkpoint handed to the wrong recording.
func sampleFingerprint(_ samples: [Float]) -> UInt64 {
    var hash: UInt64 = 0xcbf2_9ce4_8422_2325
    func mix(_ value: UInt64) {
        var v = value
        for _ in 0..<8 {
            hash ^= v & 0xff
            hash &*= 0x0000_0100_0000_01b3
            v >>= 8
        }
    }
    mix(UInt64(samples.count))
    var index = 0
    while index < samples.count {
        mix(UInt64(samples[index].bitPattern))
        index += 1600
    }
    if let last = samples.last {
        mix(UInt64(last.bitPattern))
    }
    return hash
}
