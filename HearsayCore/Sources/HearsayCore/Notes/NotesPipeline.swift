import Foundation

public enum NotesPipelineError: Error, LocalizedError, Equatable {
    /// The SRT has no caption text once indexes and timings are removed.
    case emptyTranscript

    public var errorDescription: String? {
        switch self {
        case .emptyTranscript:
            return String(localized: "Error: The SRT contains no transcript content to summarize.",
                          bundle: .module, comment: "Meeting notes error")
        }
    }
}

/// SRT text in, validated `NotesResponse` out: Python
/// `generate_meeting_notes` without any file handling. The preset kind picks
/// a CLI branch (`CLIClient`: Copilot, Claude Code, Codex, Antigravity) or
/// the API branch (`client`).
///
/// As in Python, `SRT.cleanText` only guards against a transcript with no
/// caption text; the prompt carries the SRT as is (timings included), since
/// the prompt asks the model to preserve useful timestamps. The CLI branches
/// strip surrounding whitespace first, as Python's Copilot branch does with
/// `srt_text.strip()`, and never pass a token.
public struct NotesPipeline: Sendable {
    public let client: any ChatCompleting
    public let cliClient: CLIClient

    public init(
        client: any ChatCompleting = ChatCompletionsClient(),
        cliClient: CLIClient = CLIClient()
    ) {
        self.client = client
        self.cliClient = cliClient
    }

    /// The client that serves `configuration`'s preset.
    public func client(for configuration: AIProviderConfiguration) -> any ChatCompleting {
        configuration.preset.kind.isCLI ? cliClient : client
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
        let isCLI = configuration.preset.kind.isCLI
        let transcript = isCLI ? srtText.trimmingCharacters(in: .whitespacesAndNewlines) : srtText
        let prompt = try MeetingPrompt.build(transcript: transcript, languageCode: languageCode, template: template)
        let content = try await client(for: configuration).complete(
            systemMessage: MeetingPrompt.systemMessage,
            userMessage: prompt,
            configuration: configuration,
            token: isCLI ? nil : token
        )
        return try NotesResponse.parse(content)
    }

    /// Where the preset's CLI is, its `--version` output, and its login
    /// state (Settings > AI).
    public func checkInstallation(configuration: AIProviderConfiguration) async throws -> CLIInstallation {
        let preset = configuration.preset
        guard let tool = preset.kind.cliTool else { throw CLIProviderError.notACLIPreset(preset.name) }
        return try await cliClient.checkInstallation(tool, configuredPath: configuration.cliPath(for: tool))
    }
}
