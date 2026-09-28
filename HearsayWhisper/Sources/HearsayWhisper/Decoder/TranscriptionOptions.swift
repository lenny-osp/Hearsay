import Foundation

/// Options for `Transcriber.transcribe`. Defaults match `mlx_whisper.transcribe`
/// 0.4.3 as the `mlx_whisper` CLI calls it, except `conditionOnPreviousText`,
/// which defaults to false because whisper-tools passes
/// `--condition-on-previous-text False`.
public struct TranscriptionOptions: Sendable, Equatable {
    /// ISO code ("en", "zh") or English name ("Chinese"). nil detects the
    /// language on the first 30 s window.
    public var language: String?
    /// Text fed as `<|startofprev|>` context for the first window.
    public var initialPrompt: String?
    /// Feed the previous windows' tokens as the prompt of the next window.
    public var conditionOnPreviousText: Bool
    /// Temperatures tried in order when a window fails the thresholds below.
    /// Decoding temperatures tried in order. The default `[0]` matches the
    /// `mlx_whisper` CLI that whisper-tools uses (temperature 0, no fallback).
    /// Pass `[0, 0.2, 0.4, 0.6, 0.8, 1.0]` for the `mlx_whisper.transcribe()`
    /// library default with fallback on compression ratio / logprob.
    public var temperatures: [Float]
    /// A window whose text compresses better than this (zlib ratio) is too
    /// repetitive and is retried at the next temperature.
    public var compressionRatioThreshold: Float?
    /// A window whose average token log probability is below this is retried.
    public var logprobThreshold: Float?
    /// A window whose `<|nospeech|>` probability is above this (and whose
    /// average log probability is below `logprobThreshold`) is skipped as silence.
    public var noSpeechThreshold: Float?
    /// Kept for parity with `--hallucination-silence-threshold`. In
    /// mlx_whisper 0.4.3 every use of it sits inside `if word_timestamps:`,
    /// and word timestamps are not ported, so it has no effect here, exactly
    /// as it has no effect in the Python CLI run by whisper-tools.
    public var hallucinationSilenceThreshold: Float?
    /// Number of sampled candidates per window when the temperature is above
    /// zero (`--best-of`, CLI default 5). Ignored at temperature zero.
    public var bestOf: Int?

    public init(
        language: String? = nil,
        initialPrompt: String? = nil,
        conditionOnPreviousText: Bool = false,
        temperatures: [Float] = [0],
        compressionRatioThreshold: Float? = 2.4,
        logprobThreshold: Float? = -1.0,
        noSpeechThreshold: Float? = 0.6,
        hallucinationSilenceThreshold: Float? = 2.0,
        bestOf: Int? = 5
    ) {
        self.language = language
        self.initialPrompt = initialPrompt
        self.conditionOnPreviousText = conditionOnPreviousText
        self.temperatures = temperatures
        self.compressionRatioThreshold = compressionRatioThreshold
        self.logprobThreshold = logprobThreshold
        self.noSpeechThreshold = noSpeechThreshold
        self.hallucinationSilenceThreshold = hallucinationSilenceThreshold
        self.bestOf = bestOf
    }
}

/// One transcript segment; the same fields as the segment dicts returned by
/// `mlx_whisper.transcribe`.
public struct TranscriptSegment: Sendable, Equatable {
    public var id: Int
    /// Mel frame (10 ms) where the window that produced this segment started.
    public var seek: Int
    public var start: TimeInterval
    public var end: TimeInterval
    public var text: String
    public var tokens: [Int]
    public var temperature: Float
    public var avgLogprob: Float
    public var compressionRatio: Float
    public var noSpeechProb: Float

    public init(
        id: Int, seek: Int, start: TimeInterval, end: TimeInterval, text: String,
        tokens: [Int], temperature: Float, avgLogprob: Float,
        compressionRatio: Float, noSpeechProb: Float
    ) {
        self.id = id
        self.seek = seek
        self.start = start
        self.end = end
        self.text = text
        self.tokens = tokens
        self.temperature = temperature
        self.avgLogprob = avgLogprob
        self.compressionRatio = compressionRatio
        self.noSpeechProb = noSpeechProb
    }
}

public struct Transcription: Sendable, Equatable {
    public var segments: [TranscriptSegment]
    /// ISO code of the language used (given or detected).
    public var language: String
    public var text: String

    public init(segments: [TranscriptSegment], language: String, text: String) {
        self.segments = segments
        self.language = language
        self.text = text
    }
}

public enum TranscriptionError: Error, Equatable, CustomStringConvertible {
    case modelNotLoaded
    case unsupportedLanguage(String)
    case missingSpecialToken(String)
    case noTemperatures

    public var description: String {
        switch self {
        case .modelNotLoaded: return "The Whisper model has no tokenizer loaded."
        case .unsupportedLanguage(let language): return "Unsupported language: \(language)"
        case .missingSpecialToken(let name): return "The tokenizer has no \(name) token."
        case .noTemperatures: return "TranscriptionOptions.temperatures is empty."
        }
    }
}
