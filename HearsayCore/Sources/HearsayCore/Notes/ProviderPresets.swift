import Foundation

/// How the API token is sent with a chat-completions request.
public enum AuthHeaderStyle: String, Codable, Sendable, CaseIterable {
    /// `Authorization: Bearer <token>` (OpenAI, GitHub Models, Anthropic).
    case bearer
    /// `api-key: <token>` (Azure OpenAI).
    case apiKey
    /// No token (local servers such as Ollama or LM Studio).
    case none

    public var displayName: String {
        switch self {
        case .bearer: "Authorization: Bearer"
        case .apiKey: "api-key"
        case .none: "No token"
        }
    }

    public var needsToken: Bool { self != .none }
}

/// A known OpenAI-compatible chat-completions provider (PLAN.md section 7).
public struct ProviderPreset: Identifiable, Sendable, Codable, Equatable {
    public var id: String
    public var name: String
    /// Full `/chat/completions` endpoint; empty when the user must enter it.
    public var baseURL: String
    public var defaultModel: String
    public var auth: AuthHeaderStyle
    public var supportsReasoningEffort: Bool

    public init(
        id: String,
        name: String,
        baseURL: String,
        defaultModel: String,
        auth: AuthHeaderStyle,
        supportsReasoningEffort: Bool
    ) {
        self.id = id
        self.name = name
        self.baseURL = baseURL
        self.defaultModel = defaultModel
        self.auth = auth
        self.supportsReasoningEffort = supportsReasoningEffort
    }

    /// Python `DEFAULT_AI_MODEL`.
    public static let defaultOpenAIModel = "gpt-5.6-luna"
    /// Python `DEFAULT_REASONING_EFFORT`.
    public static let defaultReasoningEffort = "max"

    public static let openAI = ProviderPreset(
        id: "openai", name: "OpenAI",
        baseURL: "https://api.openai.com/v1/chat/completions",
        defaultModel: defaultOpenAIModel, auth: .bearer, supportsReasoningEffort: true
    )
    public static let githubModels = ProviderPreset(
        id: "githubModels", name: "GitHub Models",
        baseURL: "https://models.inference.ai.github.com/chat/completions",
        defaultModel: defaultOpenAIModel, auth: .bearer, supportsReasoningEffort: true
    )
    public static let azureOpenAI = ProviderPreset(
        id: "azureOpenAI", name: "Azure OpenAI",
        baseURL: "",
        defaultModel: defaultOpenAIModel, auth: .apiKey, supportsReasoningEffort: true
    )
    public static let anthropic = ProviderPreset(
        id: "anthropic", name: "Anthropic (OpenAI compatible)",
        baseURL: "https://api.anthropic.com/v1/chat/completions",
        defaultModel: "claude-sonnet-5", auth: .bearer, supportsReasoningEffort: false
    )
    public static let ollama = ProviderPreset(
        id: "ollama", name: "Ollama / LM Studio",
        baseURL: "http://localhost:11434/v1/chat/completions",
        defaultModel: "", auth: .none, supportsReasoningEffort: false
    )
    public static let custom = ProviderPreset(
        id: "custom", name: "Custom",
        baseURL: "",
        defaultModel: "", auth: .bearer, supportsReasoningEffort: true
    )

    public static let all: [ProviderPreset] = [openAI, githubModels, azureOpenAI, anthropic, ollama, custom]

    public static func preset(id: String) -> ProviderPreset? {
        all.first { $0.id == id }
    }
}
