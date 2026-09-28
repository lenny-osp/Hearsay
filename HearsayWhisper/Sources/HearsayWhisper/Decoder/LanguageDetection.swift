import Foundation
import MLX

/// Whisper's language distribution for one 30 s window: `language_probs` from
/// `detect_language` in mlx_whisper 0.4.3, code -> probability over every
/// language the model knows (sums to 1).
public struct LanguageProbabilities: Sendable, Equatable {
    public let probabilities: [String: Float]

    public init(probabilities: [String: Float]) {
        self.probabilities = probabilities
    }

    /// The probabilities of `candidates` only, renormalized to sum to 1.
    /// Codes the model does not know are left out; duplicates count once.
    /// Empty when no candidate has a positive probability.
    public func restricted(to candidates: [String]) -> [String: Float] {
        var kept: [String: Float] = [:]
        for code in candidates {
            if let p = probabilities[code] { kept[code] = p }
        }
        let total = kept.values.reduce(Float(0), +)
        guard total > 0, total.isFinite else { return [:] }
        return kept.mapValues { $0 / total }
    }

    /// The most probable of `candidates` and its renormalized probability;
    /// the earlier candidate wins a tie. nil when `restricted` is empty.
    public func best(among candidates: [String]) -> (code: String, confidence: Float)? {
        bestCandidate(in: restricted(to: candidates), order: candidates)
    }
}

/// Result of `Transcriber.detectLanguage(samples:candidates:...)`.
public struct DetectionResult: Sendable, Equatable {
    /// The detected candidate; nil when no speech window was found.
    public var code: String?
    /// The averaged restricted probability of `code` (0 when `code` is nil).
    public var confidence: Float
    /// Number of speech windows that were averaged.
    public var windowsUsed: Int
    /// Restricted (renormalized over the candidates) probabilities of each
    /// speech window used, in order.
    public var perWindow: [[String: Float]]

    public init(code: String?, confidence: Float, windowsUsed: Int, perWindow: [[String: Float]]) {
        self.code = code
        self.confidence = confidence
        self.windowsUsed = windowsUsed
        self.perWindow = perWindow
    }
}

/// The highest value in `probabilities`, ties broken by position in `order`.
func bestCandidate(in probabilities: [String: Float], order: [String]) -> (code: String, confidence: Float)? {
    var best: (code: String, confidence: Float)? = nil
    for code in order {
        guard let p = probabilities[code] else { continue }
        if let current = best, p <= current.confidence { continue }
        best = (code, p)
    }
    return best
}

/// Average of per-window restricted distributions, per code.
func averageDistributions(_ windows: [[String: Float]], codes: [String]) -> [String: Float] {
    guard !windows.isEmpty else { return [:] }
    var result: [String: Float] = [:]
    for code in codes {
        let sum = windows.reduce(Float(0)) { $0 + ($1[code] ?? 0) }
        result[code] = sum / Float(windows.count)
    }
    return result
}

/// Root mean square of `samples` (0 for an empty slice).
func rootMeanSquare(_ samples: ArraySlice<Float>) -> Float {
    guard !samples.isEmpty else { return 0 }
    var sum = 0.0
    for s in samples { sum += Double(s) * Double(s) }
    return Float((sum / Double(samples.count)).squareRoot())
}

extension Transcriber {
    /// Port of `detect_language` for one 30 s window that starts at sample
    /// `start` (clamped to the buffer): the log-mel spectrogram of that
    /// window, computed as `transcribe()` computes it (`padding=N_SAMPLES`,
    /// then `pad_or_trim` to `N_FRAMES`), the encoder, one decoder step from
    /// `<|startoftranscript|>`, softmax over the language tokens.
    ///
    /// For `start == 0` on audio of 30 s or less this is exactly the input
    /// `transcribe(language: nil)` detects on. For later windows the mel is
    /// computed over the window alone, so its `max - 8` floor is per window
    /// rather than over the whole recording.
    ///
    /// `noSpeechProbability` is the `<|nospeech|>` probability at the `sot`
    /// position, the value `transcribe()` compares with `no_speech_threshold`.
    /// Note: with large-v3-turbo it stays near zero even on digital silence
    /// (measured 1.5e-10 in Python on 30 s of zeros), so it is not a reliable
    /// silence test on its own.
    public func detectLanguage(
        samples: [Float],
        window start: Int = 0
    ) throws -> (probabilities: LanguageProbabilities, noSpeechProbability: Float) {
        guard specials.isMultilingual, !specials.languageCodes.isEmpty else {
            throw TranscriptionError.languageDetectionUnavailable
        }
        let window = Self.windowSlice(samples, start: start)
        return detectLanguage(window: window)
    }

    /// Detect the language among `candidates` (codes or English names, as
    /// `TranscriptionOptions.language` accepts) by walking 30 s windows from
    /// the start of `samples`.
    ///
    /// A window counts as speech when its RMS is at least `silenceRMS` and
    /// its no-speech probability is at most `noSpeechThreshold`. The RMS gate
    /// is needed because large-v3-turbo's no-speech probability does not flag
    /// silence (see `detectLanguage(samples:window:)`); the default 0.001 is
    /// -60 dBFS. Pass 0 to rely on the no-speech probability alone.
    ///
    /// Up to `maxSpeechWindows` speech windows are combined by **averaging
    /// their restricted probabilities**, not by summing log probabilities.
    /// Summing logs treats windows as independent evidence, so one window
    /// that is confidently wrong (music, a quoted name, a phrase in another
    /// language) can drive a candidate to near zero and outvote the others;
    /// averaging keeps each window's say bounded and keeps the confidence on
    /// the same 0...1 scale as a single window, which is what the caller's
    /// confidence threshold is written against.
    ///
    /// `code` is nil when no speech window was found.
    public func detectLanguage(
        samples: [Float],
        candidates: [String],
        maxSpeechWindows: Int = 3,
        noSpeechThreshold: Float = 0.6,
        silenceRMS: Float = 0.001
    ) throws -> DetectionResult {
        guard specials.isMultilingual, !specials.languageCodes.isEmpty else {
            throw TranscriptionError.languageDetectionUnavailable
        }
        var codes: [String] = []
        for candidate in candidates {
            guard let code = WhisperLanguages.resolve(candidate), specials.languageToken(code) != nil else {
                throw TranscriptionError.unsupportedLanguage(candidate)
            }
            if !codes.contains(code) { codes.append(code) }
        }

        let windowLength = WhisperAudioConfig.chunkLengthSamples
        var perWindow: [[String: Float]] = []
        var start = 0
        while start < samples.count, perWindow.count < maxSpeechWindows {
            let window = Self.windowSlice(samples, start: start)
            start += windowLength
            guard rootMeanSquare(window[...]) >= silenceRMS else { continue }
            let (probabilities, noSpeech) = detectLanguage(window: window)
            if noSpeech > noSpeechThreshold { continue }
            let restricted = probabilities.restricted(to: codes)
            if !restricted.isEmpty { perWindow.append(restricted) }
        }

        let averaged = averageDistributions(perWindow, codes: codes)
        let best = bestCandidate(in: averaged, order: codes)
        return DetectionResult(
            code: best?.code,
            confidence: best?.confidence ?? 0,
            windowsUsed: perWindow.count,
            perWindow: perWindow
        )
    }

    /// Up to 30 s of samples from `start`, clamped to the buffer.
    static func windowSlice(_ samples: [Float], start: Int) -> [Float] {
        let lower = min(max(start, 0), samples.count)
        let upper = min(lower + WhisperAudioConfig.chunkLengthSamples, samples.count)
        return Array(samples[lower..<upper])
    }

    private func detectLanguage(window: [Float]) -> (probabilities: LanguageProbabilities, noSpeechProbability: Float) {
        let mel = logMelSpectrogram(
            samples: window,
            nMels: model.config.numMelBins,
            padding: WhisperAudioConfig.chunkLengthSamples
        )
        let melWindow = padOrTrimFrames(mel, length: WhisperAudioConfig.nFrames)
        let features = decoder.encode(melWindow: melWindow)
        let (probabilities, noSpeech) = decoder.languageProbabilities(audioFeatures: features)
        Memory.clearCache()
        return (LanguageProbabilities(probabilities: probabilities), noSpeech)
    }
}
