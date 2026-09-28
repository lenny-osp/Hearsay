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

/// SRT text in, validated `NotesResponse` out: Python
/// `generate_meeting_notes` without any file handling. The preset kind picks
/// the Copilot branch (`CopilotCLIClient`) or the API branch (`client`).
///
/// As in Python, `SRT.cleanText` only guards against a transcript with no
/// caption text; the prompt carries the SRT as is (timings included), since
/// the prompt asks the model to preserve useful timestamps. The Copilot branch
/// strips surrounding whitespace first, as Python's `srt_text.strip()` does.
public struct NotesPipeline: Sendable {
    public let client: any ChatCompleting
    public let copilotClient: CopilotCLIClient

    public init(
        client: any ChatCompleting = ChatCompletionsClient(),
        copilotClient: CopilotCLIClient = CopilotCLIClient()
    ) {
        self.client = client
        self.copilotClient = copilotClient
    }

    /// The client that serves `configuration`'s preset.
    public func client(for configuration: AIProviderConfiguration) -> any ChatCompleting {
        switch configuration.preset.kind {
        case .copilotCLI: copilotClient
        case .http: client
        }
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
        let isCopilot = configuration.preset.kind == .copilotCLI
        let transcript = isCopilot ? srtText.trimmingCharacters(in: .whitespacesAndNewlines) : srtText
        let prompt = try MeetingPrompt.build(transcript: transcript, languageCode: languageCode, template: template)
        let content = try await client(for: configuration).complete(
            systemMessage: MeetingPrompt.systemMessage,
            userMessage: prompt,
            configuration: configuration,
            token: isCopilot ? nil : token
        )
        return try NotesResponse.parse(content)
    }

    /// Where the Copilot CLI is and its `--version` output (Settings > AI).
    public func checkInstallation(configuration: AIProviderConfiguration) async throws -> CopilotInstallation {
        try await copilotClient.checkInstallation(configuredPath: configuration.copilotPath)
    }
}
