import Foundation

/// How the API token is sent with a chat-completions request.
public enum AuthHeaderStyle: String, Codable, Sendable, CaseIterable {
    /// `Authorization: Bearer <token>` (most OpenAI-compatible servers).
    case bearer
    /// `api-key: <token>` (Azure OpenAI and similar servers; chosen under
    /// Custom).
    case apiKey
    /// No token (local servers such as Ollama or LM Studio).
    case none

    public var displayName: String {
        switch self {
        case .bearer: "Authorization: Bearer"
        case .apiKey: "api-key"
        case .none:
            String(localized: "No token", bundle: .module, comment: "Settings > AI > Token header picker: send no token")
        }
    }

    public var needsToken: Bool { self != .none }
}

/// How a preset sends the prompt: over HTTP to a chat-completions endpoint,
/// or through a locally installed CLI that uses its own login.
public enum ProviderKind: String, Codable, Sendable {
    case http
    case copilotCLI
    case claudeCodeCLI
    case codexCLI
    case antigravityCLI

    /// The CLI this kind runs, or nil for HTTP.
    public var cliTool: CLITool? {
        switch self {
        case .http: nil
        case .copilotCLI: .copilot
        case .claudeCodeCLI: .claudeCode
        case .codexCLI: .codex
        case .antigravityCLI: .antigravity
        }
    }

    public var isCLI: Bool { cliTool != nil }
}

/// A known meeting-notes provider (PLAN.md section 7): an OpenAI-compatible
/// chat-completions endpoint, or a CLI (GitHub Copilot, Claude Code, Codex,
/// Antigravity).
public struct ProviderPreset: Identifiable, Sendable, Codable, Equatable {
    public var id: String
    public var kind: ProviderKind
    public var name: String
    /// Full `/chat/completions` endpoint; empty when the user must enter it.
    public var baseURL: String
    public var defaultModel: String
    public var auth: AuthHeaderStyle
    public var supportsReasoningEffort: Bool
    /// The reasoning effort a new configuration of this preset starts with,
    /// when it supports one.
    public var defaultEffort: String

    /// Only HTTP presets send `temperature`; none of the CLIs has a
    /// temperature option, so the AI tab hides the field for them.
    public var supportsTemperature: Bool { kind == .http }

    public init(
        id: String,
        name: String,
        baseURL: String,
        defaultModel: String,
        auth: AuthHeaderStyle,
        supportsReasoningEffort: Bool,
        defaultEffort: String = ProviderPreset.defaultReasoningEffort,
        kind: ProviderKind = .http
    ) {
        self.id = id
        self.kind = kind
        self.name = name
        self.baseURL = baseURL
        self.defaultModel = defaultModel
        self.auth = auth
        self.supportsReasoningEffort = supportsReasoningEffort
        self.defaultEffort = defaultEffort
    }

    /// Python `DEFAULT_AI_MODEL`.
    public static let defaultOpenAIModel = "gpt-5.6-luna"
    /// Python `DEFAULT_REASONING_EFFORT`.
    public static let defaultReasoningEffort = "max"

    /// The installed `copilot` binary, using its own login (Python
    /// `AI_PROVIDER=copilot`, the CLI's default provider).
    public static let copilotCLI = ProviderPreset(
        id: "copilotCLI", name: "GitHub Copilot CLI",
        baseURL: "",
        defaultModel: defaultOpenAIModel, auth: .none, supportsReasoningEffort: true,
        kind: .copilotCLI
    )
    /// The installed `claude` binary, using the Claude subscription login.
    /// Claude Sonnet 5 at effort "high" (owner decision, 2026-09-28);
    /// `claude --model` takes the id `claude-sonnet-5`, not `sonnet-5`.
    public static let claudeCodeCLI = ProviderPreset(
        id: "claudeCodeCLI",
        name: String(localized: "Claude Code CLI (Claude subscription)", bundle: .module,
                     comment: "Provider preset name. Keep 'Claude Code CLI' and 'Claude' untranslated."),
        baseURL: "",
        defaultModel: "claude-sonnet-5", auth: .none, supportsReasoningEffort: true,
        defaultEffort: "high",
        kind: .claudeCodeCLI
    )
    /// The installed `codex` binary, using the ChatGPT login: `gpt-6-luna`
    /// at effort "max" (owner decision, 2026-09-28). An empty model lets the
    /// CLI pick its own.
    public static let codexCLI = ProviderPreset(
        id: "codexCLI",
        name: String(localized: "Codex CLI (ChatGPT subscription)", bundle: .module,
                     comment: "Provider preset name. Keep 'Codex CLI' and 'ChatGPT' untranslated."),
        baseURL: "",
        defaultModel: "gpt-6-luna", auth: .none, supportsReasoningEffort: true,
        kind: .codexCLI
    )
    /// The installed `agy` binary, using the Google account it is logged in
    /// with. Gemini 3.8 Flash (High) (owner decision, 2026-09-28); its effort
    /// is part of the model id, so `--effort` is only sent for model ids
    /// without an effort suffix (`CLIArguments.antigravityEffort`).
    public static let antigravityCLI = ProviderPreset(
        id: "antigravityCLI", name: "Antigravity CLI",
        baseURL: "",
        defaultModel: "gemini-3.8-flash-high", auth: .none, supportsReasoningEffort: true,
        defaultEffort: "high",
        kind: .antigravityCLI
    )
    public static let ollama = ProviderPreset(
        id: "ollama", name: "Ollama / LM Studio",
        baseURL: "http://localhost:11434/v1/chat/completions",
        defaultModel: "", auth: .none, supportsReasoningEffort: false
    )
    public static let custom = ProviderPreset(
        id: "custom",
        name: String(localized: "Custom", bundle: .module,
                     comment: "Provider preset name: any OpenAI-compatible endpoint the user enters"),
        baseURL: "",
        defaultModel: "", auth: .bearer, supportsReasoningEffort: true
    )

    public static let all: [ProviderPreset] = [copilotCLI, claudeCodeCLI, codexCLI, antigravityCLI, ollama, custom]

    /// Preset ids that existed in earlier builds and were removed. A stored
    /// configuration with one of these loads as `custom`, keeping its URL and
    /// model. GitHub Models was shut down on 2026-07-30; the OpenAI and
    /// Anthropic API presets were replaced by the Codex and Claude Code CLI
    /// presets, which use the subscriptions instead of API keys. The Azure
    /// OpenAI preset was removed on 2026-09-28; Custom with the `api-key`
    /// header does the same.
    public static let retiredIDs: Set<String> = ["githubModels", "openai", "anthropic", "azureOpenAI"]

    public static func preset(id: String) -> ProviderPreset? {
        all.first { $0.id == id }
    }

    /// The preset that runs `tool`.
    public static func preset(for tool: CLITool) -> ProviderPreset {
        switch tool {
        case .copilot: copilotCLI
        case .claudeCode: claudeCodeCLI
        case .codex: codexCLI
        case .antigravity: antigravityCLI
        }
    }
}
