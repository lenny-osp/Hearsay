import Foundation

/// When a session runs language detection and when its language is settled
/// (PLAN.md section 1, "Languages"). Pure bookkeeping: the caller runs the
/// detection and reports the result.
///
/// - Auto: the language is undecided at the start. A detection is due each
///   time another `attemptInterval` of audio exists. A confident result
///   (`LanguageDecision.Reason.detected`) locks the language; a result over
///   `limit` or more of audio locks whatever `LanguageDecision.decide` says,
///   which is the preferred language when detection is still unsure.
/// - Fixed: the language is known from the start. The same schedule runs
///   one background check, settled by the first result that found speech
///   (or by `limit`); its `suggestion` is offered, never applied.
///
/// `finish(detection:)` is the end of the audio (Stop, or a whole file): it
/// settles with that result whatever it is. `choose(_:)` is the user picking
/// a language to re-run in; it settles without detection.
public struct SessionLanguageTracker: Equatable, Sendable {
    /// Audio between two detection attempts.
    public static let attemptInterval: TimeInterval = 30
    /// Audio after which the session stops waiting for a confident result.
    public static let limit: TimeInterval = 90

    public let choice: LanguageChoice
    public let preferred: TranscriptLanguage
    /// The current decision: nil while Auto is undecided; for a fixed choice
    /// `.chosen` from the start, with a suggestion once the check found one.
    public private(set) var decision: LanguageDecision?
    /// No more detection attempts are due.
    public private(set) var isSettled = false

    private let sampleRate: Int
    private var nextAttemptSamples: Int

    public init(choice: LanguageChoice, preferred: TranscriptLanguage, sampleRate: Int = 16_000) {
        self.choice = choice
        self.preferred = preferred
        self.sampleRate = sampleRate
        self.nextAttemptSamples = Int(Self.attemptInterval * Double(sampleRate))
        if let fixed = choice.fixedLanguage {
            decision = LanguageDecision(language: fixed, reason: .chosen)
        }
    }

    /// The language to transcribe in now; nil while Auto is undecided.
    public var language: TranscriptLanguage? { decision?.language }

    /// Auto has not locked a language yet.
    public var isUndecided: Bool { decision == nil }

    private var limitSamples: Int { Int(Self.limit * Double(sampleRate)) }

    /// How many leading samples to run detection on now that `totalSamples`
    /// exist, or nil when no attempt is due. Never more than `limit` of audio,
    /// since detection uses at most three 30 s windows anyway.
    public func attemptDue(totalSamples: Int) -> Int? {
        guard !isSettled, totalSamples >= nextAttemptSamples else { return nil }
        return min(totalSamples, limitSamples)
    }

    /// Records a detection over the first `samplesUsed` samples (nil when it
    /// failed). Returns the decision when this settled the session.
    @discardableResult
    public mutating func record(
        detection: (code: String?, confidence: Float)?, samplesUsed: Int
    ) -> LanguageDecision? {
        guard !isSettled else { return nil }
        let result = LanguageDecision.decide(choice: choice, preferred: preferred, detection: detection)
        let conclusive: Bool
        switch choice {
        case .auto:
            if case .detected = result.reason { conclusive = true } else { conclusive = false }
        case .fixed:
            conclusive = detection?.code != nil
        }
        if conclusive || samplesUsed >= limitSamples {
            decision = result
            isSettled = true
            return result
        }
        let interval = Int(Self.attemptInterval * Double(sampleRate))
        nextAttemptSamples = (samplesUsed / interval + 1) * interval
        return nil
    }

    /// The audio ended: settles with `detection` (nil when it failed or did
    /// not run) unless the session is already settled. Returns the decision.
    @discardableResult
    public mutating func finish(detection: (code: String?, confidence: Float)?) -> LanguageDecision {
        if isSettled, let decision { return decision }
        let result = LanguageDecision.decide(choice: choice, preferred: preferred, detection: detection)
        decision = result
        isSettled = true
        return result
    }

    /// The user picked `language` for this session (a re-run or "Transcribe
    /// again"). Settles; the choice and preferred language are unchanged.
    public mutating func choose(_ language: TranscriptLanguage) {
        decision = LanguageDecision(language: language, reason: .chosen)
        isSettled = true
    }
}

/// What the Record and File tabs tell the user about a session's language
/// (PLAN.md section 1, "Languages").
public enum LanguageNotice: Equatable, Sendable {
    /// The user picked a language and detection is confident it is another.
    case suggestion(TranscriptLanguage)
    /// Auto could not tell, so the preferred language was used.
    case fallback(preferred: TranscriptLanguage)

    /// The notice for `decision`, or nil when there is nothing to say.
    public init?(decision: LanguageDecision?) {
        guard let decision else { return nil }
        if let suggestion = decision.suggestion {
            self = .suggestion(suggestion)
        } else if case .fallbackToPreferred = decision.reason {
            self = .fallback(preferred: decision.language)
        } else {
            return nil
        }
    }

    public var message: String {
        switch self {
        case .suggestion(let language):
            "This sounds like \(language.displayName). Transcribe again in \(language.displayName)?"
        case .fallback(let preferred):
            "Couldn't tell the language, so this was transcribed in \(preferred.displayName) "
                + "(your preferred language)."
        }
    }

    /// The languages offered as re-run buttons: the suggestion, or every
    /// other supported language after a fallback, in picker order.
    public var rerunLanguages: [TranscriptLanguage] {
        switch self {
        case .suggestion(let language): [language]
        case .fallback(let preferred): TranscriptLanguage.allCases.filter { $0 != preferred }
        }
    }
}

extension LanguageDecision {
    /// One line for the debug paths: language, reason, confidence, suggestion.
    public var debugSummary: String {
        let reasonText: String
        let confidence: String
        switch reason {
        case .chosen:
            reasonText = "chosen"
            confidence = "-"
        case .detected(let value):
            reasonText = "detected"
            confidence = String(format: "%.4f", value)
        case .fallbackToPreferred(let value):
            reasonText = "fallback-to-preferred"
            confidence = value.map { String(format: "%.4f", $0) } ?? "none"
        }
        return "language \(language.code), reason \(reasonText), confidence \(confidence), "
            + "suggestion \(suggestion?.code ?? "none")"
    }
}

/// The notes language for an SRT whose transcript language was not stored
/// (History "Generate notes…" on an older transcript): the fixed choice, or
/// the preferred language for Auto, with the confirm sheet's explanation.
public enum StoredTranscriptLanguage {
    public static func assumed(
        choice: LanguageChoice, preferred: TranscriptLanguage
    ) -> (language: TranscriptLanguage, note: String) {
        switch choice {
        case .fixed(let language):
            return (language, "\(language.displayName) (your language choice; "
                + "this transcript's language was not recorded)")
        case .auto:
            return (preferred, "\(preferred.displayName) (your preferred language; "
                + "this transcript's language was not recorded)")
        }
    }
}

extension StoredTranscriptLanguage {
    /// The default notes language for an SRT with no stored language: the
    /// language detected from its text when `TranscriptTextLanguage` is
    /// confident, otherwise `assumed(choice:preferred:)`. The note names the
    /// language and where it came from, for the confirm sheet's caption.
    public static func resolve(
        srtText: String, choice: LanguageChoice, preferred: TranscriptLanguage
    ) -> (language: TranscriptLanguage, note: String) {
        if let (language, probability) = TranscriptTextLanguage.detect(srtText: srtText),
           probability >= TranscriptTextLanguage.confidenceThreshold {
            return (language, "\(language.displayName) (detected from the text)")
        }
        return assumed(choice: choice, preferred: preferred)
    }
}

/// The confirm sheet's lines under the "Notes language" picker.
public enum NotesLanguageCaption {
    /// "Transcript language: 中文", or with the caller's note, e.g.
    /// "Transcript language: Deutsch (detected from the text)".
    public static func transcriptLine(_ language: TranscriptLanguage, note: String? = nil) -> String {
        "Transcript language: \(note ?? language.displayName)"
    }

    /// "Notes will be written in Deutsch." when the choice differs from the
    /// transcript language; nil otherwise.
    public static func notesLine(transcript: TranscriptLanguage, notes: TranscriptLanguage) -> String? {
        notes == transcript ? nil : "Notes will be written in \(notes.displayName)."
    }
}
