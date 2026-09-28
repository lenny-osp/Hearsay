import Foundation
import NaturalLanguage

/// Guesses the language of a transcript's text, for SRTs whose transcript
/// language was not stored (History "Generate notes…" on an older file).
/// Uses Apple's NaturalLanguage recognizer restricted to the supported
/// languages; both Chinese scripts map to `.chinese`.
public enum TranscriptTextLanguage {
    /// Characters of text the recognizer looks at; more adds nothing.
    public static let sampleLength = 4_000
    /// The minimum probability for the detected language to be the default.
    public static let confidenceThreshold = 0.6

    /// The most likely supported language and its probability (the two
    /// Chinese scripts summed), or nil when the text gives no hypothesis.
    public static func detect(_ text: String) -> (TranscriptLanguage, Double)? {
        let sample = String(text.prefix(sampleLength))
        guard sample.contains(where: { $0.isLetter }) else { return nil }
        let recognizer = NLLanguageRecognizer()
        recognizer.languageConstraints = [
            .english, .simplifiedChinese, .traditionalChinese, .german, .spanish,
        ]
        recognizer.processString(sample)
        var totals: [TranscriptLanguage: Double] = [:]
        for (language, probability) in recognizer.languageHypotheses(withMaximum: 5) {
            guard let mapped = map(language) else { continue }
            totals[mapped, default: 0] += probability
        }
        // Ties break in picker order so the result is deterministic.
        var best: (TranscriptLanguage, Double)?
        for language in TranscriptLanguage.allCases {
            guard let probability = totals[language] else { continue }
            if best == nil || probability > (best?.1 ?? 0) {
                best = (language, probability)
            }
        }
        return best
    }

    /// The detected language when it is at least `confidenceThreshold`
    /// likely, else nil.
    public static func confident(_ text: String) -> TranscriptLanguage? {
        guard let (language, probability) = detect(text), probability >= confidenceThreshold else {
            return nil
        }
        return language
    }

    /// Detection over an SRT's subtitle text (`SRT.cleanText`).
    public static func detect(srtText: String) -> (TranscriptLanguage, Double)? {
        detect(SRT.cleanText(srtText))
    }

    private static func map(_ language: NLLanguage) -> TranscriptLanguage? {
        switch language {
        case .english: .english
        case .simplifiedChinese, .traditionalChinese: .chinese
        case .german: .german
        case .spanish: .spanish
        default: nil
        }
    }
}
