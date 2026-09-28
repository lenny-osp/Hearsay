import Foundation

public enum NotesPipelineError: Error, LocalizedError, Equatable {
    /// The SRT has no caption text once indexes and timings are removed.
    case emptyTranscript

    public var errorDescription: String? {
        switch self {
        case .emptyTranscript:
            return "Error: The SRT contains no transcript content to summarize."
        }
    }
}

/// SRT text in, validated `NotesResponse` out: the API branch of Python
/// `generate_meeting_notes` without any file handling.
///
/// As in Python, `SRT.cleanText` only guards against a transcript with no
/// caption text; the prompt carries the SRT as is (timings included), since
/// the prompt asks the model to preserve useful timestamps.
public struct NotesPipeline: Sendable {
    public let client: any ChatCompleting

    public init(client: any ChatCompleting = ChatCompletionsClient()) {
        self.client = client
    }

    public func generate(
        srtText: String,
        languageCode: String,
        template: PromptTemplate,
        configuration: AIProviderConfiguration,
        token: String?
    ) async throws -> NotesResponse {
        guard !SRT.cleanText(srtText).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw NotesPipelineError.emptyTranscript
        }
        let prompt = try MeetingPrompt.build(transcript: srtText, languageCode: languageCode, template: template)
        let content = try await client.complete(
            systemMessage: MeetingPrompt.systemMessage,
            userMessage: prompt,
            configuration: configuration,
            token: token
        )
        return try NotesResponse.parse(content)
    }
}
