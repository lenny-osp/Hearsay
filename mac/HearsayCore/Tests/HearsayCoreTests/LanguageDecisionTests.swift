import Foundation
import Testing
@testable import HearsayCore

struct TranscriptLanguageTests {
    @Test func codesAndLabels() {
        #expect(TranscriptLanguage.allCases.map(\.rawValue) == ["en", "zh-TW", "zh-CN", "de", "es"])
        #expect(TranscriptLanguage.allCases.map(\.shortLabel) == ["EN", "ZH-TW", "ZH-CN", "DE", "ES"])
        #expect(TranscriptLanguage.allCases.map(\.displayName)
            == ["English", "繁體中文", "简体中文", "Deutsch", "Español"])
        #expect(TranscriptLanguage.allCases.map(\.whisperCode) == ["en", "zh", "zh", "de", "es"])
        #expect(TranscriptLanguage.allCases.map(\.chineseScript) == [nil, .traditional, .simplified, nil, nil])
        #expect(TranscriptLanguage.whisperCodes == ["en", "zh", "de", "es"])
    }

    @Test func whisperCodeMapsZhToPreferredVariant() {
        typealias L = TranscriptLanguage
        #expect(L(whisperCode: "zh", preferred: .chineseMainland) == .chineseMainland)
        #expect(L(whisperCode: "zh", preferred: .chineseTaiwan) == .chineseTaiwan)
        for preferred in [L.english, .german, .spanish] {
            #expect(L(whisperCode: "zh", preferred: preferred) == .chineseTaiwan)
        }
        #expect(L(whisperCode: "en", preferred: .chineseMainland) == .english)
        #expect(L(whisperCode: "de", preferred: .english) == .german)
        #expect(L(whisperCode: "es", preferred: .english) == .spanish)
        #expect(L(whisperCode: "fr", preferred: .english) == nil)
        #expect(L(whisperCode: "zh-TW", preferred: .english) == nil)
    }

    @Test func storedValueMigratesLegacyZh() {
        typealias L = TranscriptLanguage
        #expect(L(storedValue: "zh", legacyChineseScript: nil) == .chineseTaiwan)
        #expect(L(storedValue: "zh", legacyChineseScript: "traditional") == .chineseTaiwan)
        #expect(L(storedValue: "zh", legacyChineseScript: "asIs") == .chineseTaiwan)
        #expect(L(storedValue: "zh", legacyChineseScript: "simplified") == .chineseMainland)
        for language in L.allCases {
            #expect(L(storedValue: language.rawValue, legacyChineseScript: "simplified") == language)
        }
        #expect(L(storedValue: "fr", legacyChineseScript: nil) == nil)
    }

    @Test func debugValueAcceptsZhAsTaiwanAlias() {
        #expect(LanguageChoice(debugValue: "auto") == .auto)
        #expect(LanguageChoice(debugValue: "en") == .fixed(.english))
        #expect(LanguageChoice(debugValue: "zh-TW") == .fixed(.chineseTaiwan))
        #expect(LanguageChoice(debugValue: "zh-CN") == .fixed(.chineseMainland))
        #expect(LanguageChoice(debugValue: "zh") == .fixed(.chineseTaiwan))
        #expect(LanguageChoice(debugValue: "de") == .fixed(.german))
        #expect(LanguageChoice(debugValue: "es") == .fixed(.spanish))
        #expect(LanguageChoice(debugValue: "fr") == nil)
    }

    @Test func choiceStorageRoundTrips() {
        #expect(LanguageChoice.allCases.map(\.storageValue) == ["auto", "en", "zh-TW", "zh-CN", "de", "es"])
        #expect(LanguageChoice.allCases.map(\.shortLabel) == ["Auto", "EN", "ZH-TW", "ZH-CN", "DE", "ES"])
        for choice in LanguageChoice.allCases {
            #expect(LanguageChoice(storageValue: choice.storageValue) == choice)
        }
        #expect(LanguageChoice(storageValue: "fr") == nil)
        #expect(LanguageChoice(storageValue: "") == nil)
        #expect(LanguageChoice(storageValue: "Auto") == nil)
        #expect(LanguageChoice(storageValue: "zh") == nil)
    }

    @Test func choiceCodableUsesStorageString() throws {
        let data = try JSONEncoder().encode([LanguageChoice.auto, .fixed(.german)])
        #expect(String(decoding: data, as: UTF8.self) == #"["auto","de"]"#)
        #expect(try JSONDecoder().decode([LanguageChoice].self, from: data) == [.auto, .fixed(.german)])
        #expect(throws: DecodingError.self) {
            try JSONDecoder().decode([LanguageChoice].self, from: Data(#"["fr"]"#.utf8))
        }
    }
}

struct LanguageDecisionTests {
    typealias D = LanguageDecision

    @Test func thresholds() {
        #expect(D.autoThreshold == 0.7)
        #expect(D.mismatchThreshold == 0.85)
    }

    // MARK: Auto

    @Test func autoConfidentUsesDetected() {
        let decision = D.decide(choice: .auto, preferred: .english, detection: ("de", 0.93))
        #expect(decision == D(language: .german, reason: .detected(confidence: 0.93), suggestion: nil))
    }

    @Test func autoAtThresholdUsesDetected() {
        let decision = D.decide(choice: .auto, preferred: .english, detection: ("es", D.autoThreshold))
        #expect(decision == D(language: .spanish, reason: .detected(confidence: D.autoThreshold)))
    }

    @Test func autoJustBelowThresholdFallsBack() {
        let below = D.autoThreshold.nextDown
        let decision = D.decide(choice: .auto, preferred: .chineseMainland, detection: ("es", below))
        #expect(decision == D(language: .chineseMainland, reason: .fallbackToPreferred(detectedConfidence: below)))
    }

    @Test func autoLowConfidenceFallsBackToPreferred() {
        let decision = D.decide(choice: .auto, preferred: .english, detection: ("zh", 0.4))
        #expect(decision == D(language: .english, reason: .fallbackToPreferred(detectedConfidence: 0.4)))
        #expect(decision.suggestion == nil)
    }

    @Test func autoDetectedZhUsesPreferredChineseVariant() {
        let mainland = D.decide(choice: .auto, preferred: .chineseMainland, detection: ("zh", 0.9))
        #expect(mainland == D(language: .chineseMainland, reason: .detected(confidence: 0.9)))
        let taiwan = D.decide(choice: .auto, preferred: .chineseTaiwan, detection: ("zh", 0.9))
        #expect(taiwan == D(language: .chineseTaiwan, reason: .detected(confidence: 0.9)))
    }

    @Test func autoDetectedZhDefaultsToTaiwanForOtherPreferred() {
        for preferred in [TranscriptLanguage.english, .german, .spanish] {
            let decision = D.decide(choice: .auto, preferred: preferred, detection: ("zh", 0.9))
            #expect(decision == D(language: .chineseTaiwan, reason: .detected(confidence: 0.9)))
        }
    }

    @Test func autoDetectedPreferredLanguageIsDetected() {
        let decision = D.decide(choice: .auto, preferred: .english, detection: ("en", 0.9))
        #expect(decision == D(language: .english, reason: .detected(confidence: 0.9)))
    }

    @Test func autoNoSpeechFallsBackWithoutConfidence() {
        let decision = D.decide(choice: .auto, preferred: .german, detection: (nil, 0))
        #expect(decision == D(language: .german, reason: .fallbackToPreferred(detectedConfidence: nil)))
    }

    @Test func autoNoDetectionFallsBackWithoutConfidence() {
        let decision = D.decide(choice: .auto, preferred: .spanish, detection: nil)
        #expect(decision == D(language: .spanish, reason: .fallbackToPreferred(detectedConfidence: nil)))
    }

    @Test func autoUnsupportedCodeCountsAsNoSpeech() {
        let decision = D.decide(choice: .auto, preferred: .english, detection: ("fr", 0.99))
        #expect(decision == D(language: .english, reason: .fallbackToPreferred(detectedConfidence: nil)))
    }

    // MARK: Fixed

    @Test func fixedSameLanguageNoSuggestion() {
        let decision = D.decide(choice: .fixed(.chineseTaiwan), preferred: .english, detection: ("zh", 0.99))
        #expect(decision == D(language: .chineseTaiwan, reason: .chosen, suggestion: nil))
    }

    @Test func fixedChineseVariantNeverSuggestsTheOtherVariant() {
        for preferred in TranscriptLanguage.allCases {
            for fixed in [TranscriptLanguage.chineseTaiwan, .chineseMainland] {
                let decision = D.decide(choice: .fixed(fixed), preferred: preferred, detection: ("zh", 0.99))
                #expect(decision == D(language: fixed, reason: .chosen, suggestion: nil))
            }
        }
    }

    @Test func fixedMismatchToZhSuggestsPreferredVariant() {
        let mainland = D.decide(choice: .fixed(.english), preferred: .chineseMainland, detection: ("zh", 0.95))
        #expect(mainland.suggestion == .chineseMainland)
        let other = D.decide(choice: .fixed(.english), preferred: .german, detection: ("zh", 0.95))
        #expect(other.suggestion == .chineseTaiwan)
    }

    @Test func fixedConfidentMismatchSuggests() {
        let decision = D.decide(choice: .fixed(.english), preferred: .english, detection: ("de", 0.95))
        #expect(decision == D(language: .english, reason: .chosen, suggestion: .german))
    }

    @Test func fixedMismatchAtThresholdSuggests() {
        let decision = D.decide(
            choice: .fixed(.english), preferred: .english, detection: ("es", D.mismatchThreshold))
        #expect(decision == D(language: .english, reason: .chosen, suggestion: .spanish))
    }

    @Test func fixedMismatchJustBelowThresholdNoSuggestion() {
        let decision = D.decide(
            choice: .fixed(.english), preferred: .english, detection: ("es", D.mismatchThreshold.nextDown))
        #expect(decision == D(language: .english, reason: .chosen, suggestion: nil))
    }

    @Test func fixedMismatchAboveAutoButBelowMismatchNoSuggestion() {
        let decision = D.decide(choice: .fixed(.german), preferred: .english, detection: ("en", 0.8))
        #expect(decision == D(language: .german, reason: .chosen, suggestion: nil))
    }

    @Test func fixedNoSpeechOrNoDetectionNoSuggestion() {
        for detection: (code: String?, confidence: Float)? in [(nil, 0), nil, ("fr", 0.99)] {
            let decision = D.decide(choice: .fixed(.spanish), preferred: .english, detection: detection)
            #expect(decision == D(language: .spanish, reason: .chosen, suggestion: nil))
        }
    }

    @Test func fixedIgnoresPreferred() {
        for preferred in TranscriptLanguage.allCases {
            let decision = D.decide(choice: .fixed(.chineseMainland), preferred: preferred, detection: ("zh", 0.2))
            #expect(decision.language == .chineseMainland)
        }
    }
}

// MARK: - shared/language-decision-tests.json

private struct LanguageDecisionVectors: Decodable {
    struct Detection: Decodable {
        let code: String?
        let confidence: Float
    }

    struct Expect: Decodable {
        let language: String
        let reason: String
        let confidence: Float?
        let suggestion: String?
    }

    struct Case: Decodable {
        let note: String
        let choice: String
        let preferred: String
        let detection: Detection?
        let expect: Expect
    }

    struct Thresholds: Decodable {
        let auto: Float
        let mismatch: Float
    }

    let thresholds: Thresholds
    let cases: [Case]
}

struct SharedLanguageDecisionVectorTests {
    typealias D = LanguageDecision

    private func load() throws -> LanguageDecisionVectors {
        try JSONDecoder().decode(LanguageDecisionVectors.self, from: sharedData("language-decision-tests.json"))
    }

    @Test func thresholdsMatch() throws {
        let vectors = try load()
        #expect(vectors.thresholds.auto == D.autoThreshold)
        #expect(vectors.thresholds.mismatch == D.mismatchThreshold)
        // The "just below" vectors really are the next float down.
        let confidences = vectors.cases.compactMap { $0.detection?.confidence }
        #expect(confidences.contains(D.autoThreshold.nextDown))
        #expect(confidences.contains(D.mismatchThreshold.nextDown))
        #expect(confidences.contains(D.autoThreshold))
        #expect(confidences.contains(D.mismatchThreshold))
    }

    @Test func everyVector() throws {
        let cases = try load().cases
        #expect(!cases.isEmpty)
        for vector in cases {
            let choice = try #require(LanguageChoice(storageValue: vector.choice), "\(vector.note)")
            let preferred = try #require(TranscriptLanguage(rawValue: vector.preferred), "\(vector.note)")
            let language = try #require(TranscriptLanguage(rawValue: vector.expect.language), "\(vector.note)")
            let suggestion = try vector.expect.suggestion.map {
                try #require(TranscriptLanguage(rawValue: $0), "\(vector.note)")
            }
            let reason: D.Reason
            switch vector.expect.reason {
            case "chosen":
                #expect(vector.expect.confidence == nil, "\(vector.note)")
                reason = .chosen
            case "detected":
                reason = .detected(confidence: try #require(vector.expect.confidence, "\(vector.note)"))
            case "fallbackToPreferred":
                reason = .fallbackToPreferred(detectedConfidence: vector.expect.confidence)
            default:
                Issue.record("unknown reason \(vector.expect.reason) in \(vector.note)")
                continue
            }
            let detection = vector.detection.map { (code: $0.code, confidence: $0.confidence) }
            let decision = D.decide(choice: choice, preferred: preferred, detection: detection)
            #expect(decision == D(language: language, reason: reason, suggestion: suggestion), "\(vector.note)")
        }
    }
}
