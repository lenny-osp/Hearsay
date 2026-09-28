import Foundation

public enum MeetingPromptError: Error, Equatable, LocalizedError {
    case unsupportedLanguage(String)

    public var errorDescription: String? {
        switch self {
        case .unsupportedLanguage(let code):
            return "unsupported meeting-note language: \(code)"
        }
    }
}

/// Port of `build_meeting_prompt` from `run_whisper.py`, with a template slot
/// for the notes section. With `.generalMeeting` the result is byte-identical
/// to the Python prompt.
public enum MeetingPrompt {
    /// Port of `MEETING_NOTE_LANGUAGES` keyed by `TranscriptLanguage` raw
    /// value (Python "zh" is "zh-TW"), plus Simplified Chinese, German and
    /// Spanish (PLAN.md section 1, "Languages": notes are written in the
    /// transcript language).
    public static let languages: [String: String] = [
        "en": "English",
        "zh-TW": "Traditional Chinese",
        "zh-CN": "Simplified Chinese",
        "de": "German",
        "es": "Spanish",
    ]

    /// The system message sent with the prompt (Python `generate_meeting_notes`).
    public static let systemMessage = "You are a professional and efficient meeting-note assistant."

    /// The fixed filename and JSON-shape rules, always appended after the
    /// template so `NotesResponse.parse` keeps working.
    public static let responseRules = """
        Choose a short, descriptive English filename based on the main topic, regardless of the selected output language. Use only lowercase ASCII letters, digits, and hyphens. Do not include a date, time, path, or extension in the filename. Begin each Markdown document with a heading. Return only valid JSON in exactly this shape:
        {"filename": "short-descriptive-name", "markdown": "the complete meeting notes in Markdown", "transcript_markdown": "the complete structured transcript in Markdown"}
        """

    /// Builds the user message. Throws for a language code not in `languages`,
    /// matching the Python `ValueError`.
    public static func build(
        transcript: String,
        languageCode: String,
        template: PromptTemplate = .generalMeeting
    ) throws -> String {
        guard let outputLanguage = languages[languageCode] else {
            throw MeetingPromptError.unsupportedLanguage(languageCode)
        }
        let notes = template.instructions.replacingOccurrences(
            of: PromptTemplate.outputLanguagePlaceholder,
            with: outputLanguage
        )
        return notes
            + "\n\n" + responseRules
            + "\n\nThe source SRT is below:\n---\n" + transcript + "\n---"
    }
}
