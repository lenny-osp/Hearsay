import Foundation

/// Why an AI response could not be used. Messages match the Python
/// `parse_ai_result` diagnostics (without the "Parsing Error: " prefix).
public enum NotesResponseError: Error, Equatable, LocalizedError {
    case invalidJSON(String)
    case notAnObject
    case missingFilename
    case missingMarkdown
    case missingTranscriptMarkdown

    public var message: String {
        switch self {
        case .invalidJSON(let detail):
            return "AI output is not valid JSON: \(detail)"
        case .notAnObject:
            return "AI output must be a JSON object."
        case .missingFilename:
            return "AI output did not contain a usable filename."
        case .missingMarkdown:
            return "AI output did not contain usable Markdown notes."
        case .missingTranscriptMarkdown:
            return "AI output did not contain a usable structured transcript."
        }
    }

    public var errorDescription: String? { "Parsing Error: \(message)" }
}

/// The validated AI result. Port of `parse_ai_result` in `run_whisper.py`.
public struct NotesResponse: Sendable, Equatable {
    public var filename: String
    public var markdown: String
    public var transcriptMarkdown: String

    public init(filename: String, markdown: String, transcriptMarkdown: String) {
        self.filename = filename
        self.markdown = markdown
        self.transcriptMarkdown = transcriptMarkdown
    }

    // Python: re.fullmatch(r"```(?:json)?\s*(.*?)\s*```", text, DOTALL | IGNORECASE)
    private static let fencePattern: NSRegularExpression? = try? NSRegularExpression(
        pattern: #"\A```(?:json)?\s*(.*?)\s*```\z"#,
        options: [.dotMatchesLineSeparators, .caseInsensitive]
    )

    public static func parse(_ raw: String) throws -> NotesResponse {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if let pattern = fencePattern {
            let range = NSRange(text.startIndex..., in: text)
            if let match = pattern.firstMatch(in: text, range: range),
               let inner = Range(match.range(at: 1), in: text) {
                text = String(text[inner])
            }
        }

        let object: Any
        do {
            object = try JSONSerialization.jsonObject(with: Data(text.utf8), options: [.fragmentsAllowed])
        } catch {
            throw NotesResponseError.invalidJSON(error.localizedDescription)
        }
        guard let result = object as? [String: Any] else {
            throw NotesResponseError.notAnObject
        }
        guard let filename = usableString(result["filename"]) else {
            throw NotesResponseError.missingFilename
        }
        guard let markdown = usableString(result["markdown"]) else {
            throw NotesResponseError.missingMarkdown
        }
        guard let transcriptMarkdown = usableString(result["transcript_markdown"]) else {
            throw NotesResponseError.missingTranscriptMarkdown
        }
        return NotesResponse(filename: filename, markdown: markdown, transcriptMarkdown: transcriptMarkdown)
    }

    /// A string value with at least one non-whitespace character, returned
    /// unmodified (Python returns the original, unstripped value).
    private static func usableString(_ value: Any?) -> String? {
        guard let string = value as? String,
              !string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        return string
    }
}
