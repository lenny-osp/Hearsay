import Foundation

/// Which language a session is transcribed in, and why (PLAN.md section 1,
/// "Languages"). Pure rules: the detection itself (restricted to the four
/// supported languages, no-speech windows skipped) happens elsewhere.
public struct LanguageDecision: Equatable, Sendable {
    public enum Reason: Equatable, Sendable {
        /// The user picked a fixed language.
        case chosen
        /// Auto: detection was confident enough.
        case detected(confidence: Float)
        /// Auto: detection was below `autoThreshold`, or found no speech
        /// (`detectedConfidence` nil), so the preferred language is used.
        case fallbackToPreferred(detectedConfidence: Float?)
    }

    /// Auto uses the detected language at or above this confidence.
    public static let autoThreshold: Float = 0.7
    /// With a fixed choice, a different detected language at or above this
    /// confidence is offered as a re-run suggestion.
    public static let mismatchThreshold: Float = 0.85

    public var language: TranscriptLanguage
    public var reason: Reason
    /// A different language to offer re-running in; never applied automatically.
    public var suggestion: TranscriptLanguage?

    public init(language: TranscriptLanguage, reason: Reason, suggestion: TranscriptLanguage? = nil) {
        self.language = language
        self.reason = reason
        self.suggestion = suggestion
    }

    /// Applies the rules.
    /// - Parameters:
    ///   - choice: what the user picked.
    ///   - preferred: the preferred language from Settings > General.
    ///   - detection: the detection result, or nil when detection did not run.
    ///     A nil `code` means no speech was detected. A code outside the
    ///     supported languages is treated like no speech.
    public static func decide(
        choice: LanguageChoice,
        preferred: TranscriptLanguage,
        detection: (code: String?, confidence: Float)?
    ) -> LanguageDecision {
        let detected = detection.flatMap { result in
            result.code.flatMap(TranscriptLanguage.init(rawValue:)).map { ($0, result.confidence) }
        }
        switch choice {
        case .auto:
            if let (language, confidence) = detected, confidence >= autoThreshold {
                return LanguageDecision(language: language, reason: .detected(confidence: confidence))
            }
            return LanguageDecision(
                language: preferred,
                reason: .fallbackToPreferred(detectedConfidence: detected?.1)
            )
        case .fixed(let language):
            var suggestion: TranscriptLanguage?
            if let (other, confidence) = detected, other != language, confidence >= mismatchThreshold {
                suggestion = other
            }
            return LanguageDecision(language: language, reason: .chosen, suggestion: suggestion)
        }
    }
}
