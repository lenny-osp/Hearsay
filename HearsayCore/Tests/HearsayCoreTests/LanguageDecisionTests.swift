import Foundation
import Testing
@testable import HearsayCore

struct TranscriptLanguageTests {
    @Test func codesAndLabels() {
        #expect(TranscriptLanguage.allCases.map(\.rawValue) == ["en", "zh", "de", "es"])
        #expect(TranscriptLanguage.allCases.map(\.shortLabel) == ["EN", "ZH", "DE", "ES"])
        #expect(TranscriptLanguage.allCases.map(\.displayName) == ["English", "中文", "Deutsch", "Español"])
    }

    @Test func choiceStorageRoundTrips() {
        #expect(LanguageChoice.allCases.map(\.storageValue) == ["auto", "en", "zh", "de", "es"])
        for choice in LanguageChoice.allCases {
            #expect(LanguageChoice(storageValue: choice.storageValue) == choice)
        }
        #expect(LanguageChoice(storageValue: "fr") == nil)
        #expect(LanguageChoice(storageValue: "") == nil)
        #expect(LanguageChoice(storageValue: "Auto") == nil)
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
        let decision = D.decide(choice: .auto, preferred: .chinese, detection: ("es", below))
        #expect(decision == D(language: .chinese, reason: .fallbackToPreferred(detectedConfidence: below)))
    }

    @Test func autoLowConfidenceFallsBackToPreferred() {
        let decision = D.decide(choice: .auto, preferred: .english, detection: ("zh", 0.4))
        #expect(decision == D(language: .english, reason: .fallbackToPreferred(detectedConfidence: 0.4)))
        #expect(decision.suggestion == nil)
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
        let decision = D.decide(choice: .fixed(.chinese), preferred: .english, detection: ("zh", 0.99))
        #expect(decision == D(language: .chinese, reason: .chosen, suggestion: nil))
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
            let decision = D.decide(choice: .fixed(.chinese), preferred: preferred, detection: ("zh", 0.2))
            #expect(decision.language == .chinese)
        }
    }
}
