import Foundation
import Testing
@testable import HearsayCore

struct SessionLanguageTrackerTests {
    typealias T = SessionLanguageTracker
    /// A small rate keeps the arithmetic readable: 1 sample per second.
    static let rate = 1

    @Test func schedule() {
        #expect(T.attemptInterval == 30)
        #expect(T.limit == 90)
    }

    @Test func autoStartsUndecidedAndFixedStartsChosen() {
        let auto = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        #expect(auto.isUndecided)
        #expect(auto.language == nil)
        let fixed = T(choice: .fixed(.german), preferred: .english, sampleRate: Self.rate)
        #expect(!fixed.isUndecided)
        #expect(fixed.decision == LanguageDecision(language: .german, reason: .chosen))
    }

    @Test func firstAttemptIsDueAt30Seconds() {
        let tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        #expect(tracker.attemptDue(totalSamples: 29) == nil)
        #expect(tracker.attemptDue(totalSamples: 30) == 30)
        #expect(tracker.attemptDue(totalSamples: 41) == 41)
    }

    @Test func attemptNeverUsesMoreThanTheLimit() {
        let tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        #expect(tracker.attemptDue(totalSamples: 200) == 90)
    }

    @Test func autoConfidentResultLocks() {
        var tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        let decision = tracker.record(detection: ("de", 0.95), samplesUsed: 30)
        #expect(decision == LanguageDecision(language: .german, reason: .detected(confidence: 0.95)))
        #expect(tracker.isSettled)
        #expect(tracker.language == .german)
        #expect(tracker.attemptDue(totalSamples: 60) == nil)
        // Later results change nothing: the language is locked.
        #expect(tracker.record(detection: ("en", 0.99), samplesUsed: 60) == nil)
        #expect(tracker.finish(detection: ("en", 0.99)).language == .german)
    }

    @Test func autoUnsureRetriesEvery30SecondsThenFallsBackAt90() {
        var tracker = T(choice: .auto, preferred: .spanish, sampleRate: Self.rate)
        #expect(tracker.record(detection: ("en", 0.5), samplesUsed: 30) == nil)
        #expect(tracker.isUndecided)
        #expect(tracker.attemptDue(totalSamples: 59) == nil)
        #expect(tracker.attemptDue(totalSamples: 60) == 60)
        #expect(tracker.record(detection: (nil, 0), samplesUsed: 60) == nil)
        #expect(tracker.attemptDue(totalSamples: 89) == nil)
        #expect(tracker.attemptDue(totalSamples: 90) == 90)
        let decision = tracker.record(detection: ("en", 0.6), samplesUsed: 90)
        #expect(decision == LanguageDecision(
            language: .spanish, reason: .fallbackToPreferred(detectedConfidence: 0.6)))
        #expect(tracker.isSettled)
    }

    @Test func lateAttemptSchedulesTheNextMultipleOf30() {
        var tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        tracker.record(detection: ("en", 0.5), samplesUsed: 45)
        #expect(tracker.attemptDue(totalSamples: 59) == nil)
        #expect(tracker.attemptDue(totalSamples: 60) == 60)
    }

    @Test func autoFailedDetectionRetries() {
        var tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        #expect(tracker.record(detection: nil, samplesUsed: 30) == nil)
        #expect(tracker.attemptDue(totalSamples: 60) == 60)
    }

    @Test func autoFinishBeforeDecisionUsesTheWholeRecordingResult() {
        var tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        #expect(tracker.finish(detection: ("zh", 0.8))
            == LanguageDecision(language: .chinese, reason: .detected(confidence: 0.8)))
        var unsure = T(choice: .auto, preferred: .german, sampleRate: Self.rate)
        #expect(unsure.finish(detection: (nil, 0))
            == LanguageDecision(language: .german, reason: .fallbackToPreferred(detectedConfidence: nil)))
        #expect(unsure.isSettled)
    }

    @Test func fixedCheckSettlesOnFirstSpeechAndKeepsTheSuggestion() {
        var tracker = T(choice: .fixed(.english), preferred: .english, sampleRate: Self.rate)
        #expect(tracker.record(detection: (nil, 0), samplesUsed: 30) == nil)
        #expect(tracker.language == .english)
        let decision = tracker.record(detection: ("de", 0.97), samplesUsed: 60)
        #expect(decision == LanguageDecision(language: .english, reason: .chosen, suggestion: .german))
        #expect(tracker.isSettled)
    }

    @Test func fixedCheckWithSpeechButNoMismatchSettlesWithoutSuggestion() {
        var tracker = T(choice: .fixed(.english), preferred: .german, sampleRate: Self.rate)
        let decision = tracker.record(detection: ("de", 0.6), samplesUsed: 30)
        #expect(decision == LanguageDecision(language: .english, reason: .chosen))
        #expect(tracker.isSettled)
    }

    @Test func chooseSettlesWithTheUsersLanguage() {
        var tracker = T(choice: .auto, preferred: .english, sampleRate: Self.rate)
        tracker.finish(detection: (nil, 0))
        tracker.choose(.spanish)
        #expect(tracker.decision == LanguageDecision(language: .spanish, reason: .chosen))
        #expect(tracker.choice == .auto)
        #expect(tracker.preferred == .english)
    }

    @Test func defaultSampleRateIs16k() {
        let tracker = T(choice: .auto, preferred: .english)
        #expect(tracker.attemptDue(totalSamples: 30 * 16_000 - 1) == nil)
        #expect(tracker.attemptDue(totalSamples: 30 * 16_000) == 30 * 16_000)
        #expect(tracker.attemptDue(totalSamples: 200 * 16_000) == 90 * 16_000)
    }
}

struct LanguageNoticeTests {
    @Test func suggestionNotice() throws {
        let decision = LanguageDecision(language: .english, reason: .chosen, suggestion: .german)
        let notice = try #require(LanguageNotice(decision: decision))
        #expect(notice == .suggestion(.german))
        #expect(notice.message == "This sounds like Deutsch. Transcribe again in Deutsch?")
        #expect(notice.rerunLanguages == [.german])
    }

    @Test func fallbackNotice() throws {
        let decision = LanguageDecision(language: .english, reason: .fallbackToPreferred(detectedConfidence: 0.4))
        let notice = try #require(LanguageNotice(decision: decision))
        #expect(notice == .fallback(preferred: .english))
        #expect(notice.message
            == "Couldn't tell the language, so this was transcribed in English (your preferred language).")
        #expect(notice.rerunLanguages == [.chinese, .german, .spanish])
        #expect(LanguageNotice.fallback(preferred: .german).rerunLanguages == [.english, .chinese, .spanish])
    }

    @Test func noNoticeForConfidentOrChosen() {
        #expect(LanguageNotice(decision: nil) == nil)
        #expect(LanguageNotice(decision: LanguageDecision(language: .german, reason: .detected(confidence: 0.9))) == nil)
        #expect(LanguageNotice(decision: LanguageDecision(language: .german, reason: .chosen)) == nil)
    }

    @Test func debugSummary() {
        #expect(LanguageDecision(language: .german, reason: .detected(confidence: 0.98761)).debugSummary
            == "language de, reason detected, confidence 0.9876, suggestion none")
        #expect(LanguageDecision(language: .english, reason: .chosen, suggestion: .german).debugSummary
            == "language en, reason chosen, confidence -, suggestion de")
        #expect(LanguageDecision(language: .english, reason: .fallbackToPreferred(detectedConfidence: nil))
            .debugSummary == "language en, reason fallback-to-preferred, confidence none, suggestion none")
    }

    @Test func storedTranscriptLanguage() {
        let fixed = StoredTranscriptLanguage.assumed(choice: .fixed(.spanish), preferred: .english)
        #expect(fixed.language == .spanish)
        #expect(fixed.note == "Español (your language choice; this transcript's language was not recorded)")
        let auto = StoredTranscriptLanguage.assumed(choice: .auto, preferred: .german)
        #expect(auto.language == .german)
        #expect(auto.note == "Deutsch (your preferred language; this transcript's language was not recorded)")
    }
}

struct StoredTranscriptLanguageResolveTests {
    static let germanSRT = """
        1
        00:00:00,000 --> 00:00:04,000
        Wir fangen mit dem Budget an und besprechen dann den Bericht.

        2
        00:00:04,000 --> 00:00:08,000
        Der Bericht muss bis Freitag fertig sein.

        """

    @Test func confidentDetectionWins() {
        let resolved = StoredTranscriptLanguage.resolve(
            srtText: Self.germanSRT, choice: .fixed(.english), preferred: .english)
        #expect(resolved.language == .german)
        #expect(resolved.note == "Deutsch (detected from the text)")
    }

    @Test func unsureFallsBackToAssumed() {
        let srt = "1\n00:00:00,000 --> 00:00:01,000\nOK 好\n"
        let resolved = StoredTranscriptLanguage.resolve(srtText: srt, choice: .auto, preferred: .spanish)
        #expect(resolved.language == .spanish)
        #expect(resolved.note == "Español (your preferred language; this transcript's language was not recorded)")
        let empty = StoredTranscriptLanguage.resolve(srtText: "", choice: .fixed(.chinese), preferred: .english)
        #expect(empty.language == .chinese)
    }
}

struct NotesLanguageCaptionTests {
    @Test func transcriptLine() {
        #expect(NotesLanguageCaption.transcriptLine(.chinese) == "Transcript language: 中文")
        #expect(NotesLanguageCaption.transcriptLine(.german, note: "Deutsch (detected from the text)")
            == "Transcript language: Deutsch (detected from the text)")
    }

    @Test func notesLineOnlyWhenDifferent() {
        #expect(NotesLanguageCaption.notesLine(transcript: .english, notes: .english) == nil)
        #expect(NotesLanguageCaption.notesLine(transcript: .chinese, notes: .german)
            == "Notes will be written in Deutsch.")
    }
}
