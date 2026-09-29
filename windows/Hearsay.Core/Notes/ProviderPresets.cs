namespace Hearsay.Core.Notes;

/// <summary>
/// How the API token is sent with a chat-completions request.
/// Port of <c>AuthHeaderStyle</c> in mac/HearsayCore/Sources/HearsayCore/Notes/ProviderPresets.swift.
/// </summary>
public enum AuthHeaderStyle
{
    /// <summary><c>Authorization: Bearer &lt;token&gt;</c> (most OpenAI-compatible servers).</summary>
    Bearer,

    /// <summary><c>api-key: &lt;token&gt;</c> (Azure OpenAI and similar servers; chosen under Custom).</summary>
    ApiKey,

    /// <summary>No token (local servers such as Ollama or LM Studio).</summary>
    None,
}

/// <summary>
/// How a preset sends the prompt: over HTTP to a chat-completions endpoint,
/// or through a locally installed CLI that uses its own login.
/// Port of <c>ProviderKind</c> in ProviderPresets.swift.
/// </summary>
public enum ProviderKind
{
    Http,
    CopilotCli,
    ClaudeCodeCli,
    CodexCli,
    AntigravityCli,
}

/// <summary>Swift raw values and computed properties of <see cref="AuthHeaderStyle"/>.</summary>
public static class AuthHeaderStyles
{
    /// <summary>Every style, in the Swift <c>allCases</c> order.</summary>
    public static IReadOnlyList<AuthHeaderStyle> All { get; } =
        [AuthHeaderStyle.Bearer, AuthHeaderStyle.ApiKey, AuthHeaderStyle.None];

    /// <summary>The stored value ("bearer", "apiKey", "none").</summary>
    public static string RawValue(this AuthHeaderStyle style) => style switch
    {
        AuthHeaderStyle.Bearer => "bearer",
        AuthHeaderStyle.ApiKey => "apiKey",
        AuthHeaderStyle.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };

    public static AuthHeaderStyle? FromRawValue(string rawValue) => rawValue switch
    {
        "bearer" => AuthHeaderStyle.Bearer,
        "apiKey" => AuthHeaderStyle.ApiKey,
        "none" => AuthHeaderStyle.None,
        _ => null,
    };

    /// <summary>
    /// The picker label. "No token" is the English text; the app localizes
    /// it (Swift: "Settings > AI > Token header picker: send no token").
    /// </summary>
    public static string DisplayName(this AuthHeaderStyle style) => style switch
    {
        AuthHeaderStyle.Bearer => "Authorization: Bearer",
        AuthHeaderStyle.ApiKey => "api-key",
        AuthHeaderStyle.None => "No token",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };

    public static bool NeedsToken(this AuthHeaderStyle style) => style != AuthHeaderStyle.None;
}

/// <summary>Swift raw values and computed properties of <see cref="ProviderKind"/>.</summary>
public static class ProviderKinds
{
    /// <summary>The stored value ("http", "copilotCLI", ...).</summary>
    public static string RawValue(this ProviderKind kind) => kind switch
    {
        ProviderKind.Http => "http",
        ProviderKind.CopilotCli => "copilotCLI",
        ProviderKind.ClaudeCodeCli => "claudeCodeCLI",
        ProviderKind.CodexCli => "codexCLI",
        ProviderKind.AntigravityCli => "antigravityCLI",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static ProviderKind? FromRawValue(string rawValue) => rawValue switch
    {
        "http" => ProviderKind.Http,
        "copilotCLI" => ProviderKind.CopilotCli,
        "claudeCodeCLI" => ProviderKind.ClaudeCodeCli,
        "codexCLI" => ProviderKind.CodexCli,
        "antigravityCLI" => ProviderKind.AntigravityCli,
        _ => null,
    };

    /// <summary>The CLI this kind runs, or null for HTTP.</summary>
    public static Notes.CliTool? CliTool(this ProviderKind kind) => kind switch
    {
        ProviderKind.Http => null,
        ProviderKind.CopilotCli => Notes.CliTool.Copilot,
        ProviderKind.ClaudeCodeCli => Notes.CliTool.ClaudeCode,
        ProviderKind.CodexCli => Notes.CliTool.Codex,
        ProviderKind.AntigravityCli => Notes.CliTool.Antigravity,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool IsCli(this ProviderKind kind) => kind.CliTool() is not null;
}

/// <summary>
/// A known meeting-notes provider (PLAN.md section 7): an OpenAI-compatible
/// chat-completions endpoint, or a CLI (GitHub Copilot, Claude Code, Codex,
/// Antigravity). Port of <c>ProviderPreset</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/ProviderPresets.swift. Preset
/// names are the English ones; the app localizes the two the Swift
/// localizes (Claude Code, Codex) and "Custom".
/// </summary>
public sealed record ProviderPreset
{
    /// <summary>Python <c>DEFAULT_AI_MODEL</c>.</summary>
    public const string DefaultOpenAIModel = "gpt-5.6-luna";

    /// <summary>Python <c>DEFAULT_REASONING_EFFORT</c>.</summary>
    public const string DefaultReasoningEffort = "max";

    public ProviderPreset(
        string id,
        string name,
        string baseUrl,
        string defaultModel,
        AuthHeaderStyle auth,
        bool supportsReasoningEffort,
        string defaultEffort = DefaultReasoningEffort,
        ProviderKind kind = ProviderKind.Http)
    {
        Id = id;
        Kind = kind;
        Name = name;
        BaseUrl = baseUrl;
        DefaultModel = defaultModel;
        Auth = auth;
        SupportsReasoningEffort = supportsReasoningEffort;
        DefaultEffort = defaultEffort;
    }

    public string Id { get; init; }

    public ProviderKind Kind { get; init; }

    public string Name { get; init; }

    /// <summary>Full <c>/chat/completions</c> endpoint; empty when the user must enter it.</summary>
    public string BaseUrl { get; init; }

    public string DefaultModel { get; init; }

    public AuthHeaderStyle Auth { get; init; }

    public bool SupportsReasoningEffort { get; init; }

    /// <summary>The reasoning effort a new configuration of this preset starts with, when it supports one.</summary>
    public string DefaultEffort { get; init; }

    /// <summary>
    /// Only HTTP presets send <c>temperature</c>; none of the CLIs has a
    /// temperature option, so the AI tab hides the field for them.
    /// </summary>
    public bool SupportsTemperature => Kind == ProviderKind.Http;

    /// <summary>
    /// The installed <c>copilot</c> binary, using its own login (Python
    /// <c>AI_PROVIDER=copilot</c>, the CLI's default provider).
    /// </summary>
    public static ProviderPreset CopilotCli { get; } = new(
        id: "copilotCLI", name: "GitHub Copilot CLI",
        baseUrl: "",
        defaultModel: DefaultOpenAIModel, auth: AuthHeaderStyle.None, supportsReasoningEffort: true,
        kind: ProviderKind.CopilotCli);

    /// <summary>
    /// The installed <c>claude</c> binary, using the Claude subscription login.
    /// Claude Sonnet 5 at effort "high" (owner decision, 2026-09-28);
    /// <c>claude --model</c> takes the id <c>claude-sonnet-5</c>, not <c>sonnet-5</c>.
    /// </summary>
    public static ProviderPreset ClaudeCodeCli { get; } = new(
        id: "claudeCodeCLI", name: "Claude Code CLI (Claude subscription)",
        baseUrl: "",
        defaultModel: "claude-sonnet-5", auth: AuthHeaderStyle.None, supportsReasoningEffort: true,
        defaultEffort: "high",
        kind: ProviderKind.ClaudeCodeCli);

    /// <summary>
    /// The installed <c>codex</c> binary, using the ChatGPT login: <c>gpt-6-luna</c>
    /// at effort "max" (owner decision, 2026-09-28). An empty model lets the
    /// CLI pick its own.
    /// </summary>
    public static ProviderPreset CodexCli { get; } = new(
        id: "codexCLI", name: "Codex CLI (ChatGPT subscription)",
        baseUrl: "",
        defaultModel: "gpt-6-luna", auth: AuthHeaderStyle.None, supportsReasoningEffort: true,
        kind: ProviderKind.CodexCli);

    /// <summary>
    /// The installed <c>agy</c> binary, using the Google account it is logged
    /// in with. Gemini 3.8 Flash (High) (owner decision, 2026-09-28); its
    /// effort is part of the model id, so <c>--effort</c> is only sent for
    /// model ids without an effort suffix.
    /// </summary>
    public static ProviderPreset AntigravityCli { get; } = new(
        id: "antigravityCLI", name: "Antigravity CLI",
        baseUrl: "",
        defaultModel: "gemini-3.8-flash-high", auth: AuthHeaderStyle.None, supportsReasoningEffort: true,
        defaultEffort: "high",
        kind: ProviderKind.AntigravityCli);

    public static ProviderPreset Ollama { get; } = new(
        id: "ollama", name: "Ollama / LM Studio",
        baseUrl: "http://localhost:11434/v1/chat/completions",
        defaultModel: "", auth: AuthHeaderStyle.None, supportsReasoningEffort: false);

    public static ProviderPreset Custom { get; } = new(
        id: "custom", name: "Custom",
        baseUrl: "",
        defaultModel: "", auth: AuthHeaderStyle.Bearer, supportsReasoningEffort: true);

    public static IReadOnlyList<ProviderPreset> All { get; } =
        [CopilotCli, ClaudeCodeCli, CodexCli, AntigravityCli, Ollama, Custom];

    /// <summary>
    /// Preset ids that existed in earlier builds and were removed. A stored
    /// configuration with one of these loads as <see cref="Custom"/>, keeping
    /// its URL and model. GitHub Models was shut down on 2026-07-30; the
    /// OpenAI and Anthropic API presets were replaced by the Codex and Claude
    /// Code CLI presets, which use the subscriptions instead of API keys. The
    /// Azure OpenAI preset was removed on 2026-09-28; Custom with the
    /// <c>api-key</c> header does the same.
    /// </summary>
    public static IReadOnlySet<string> RetiredIds { get; } =
        new HashSet<string>(["githubModels", "openai", "anthropic", "azureOpenAI"], StringComparer.Ordinal);

    public static ProviderPreset? Preset(string id) =>
        All.FirstOrDefault(preset => preset.Id == id);

    /// <summary>The preset that runs <paramref name="tool"/>.</summary>
    public static ProviderPreset Preset(CliTool tool) => tool switch
    {
        CliTool.Copilot => CopilotCli,
        CliTool.ClaudeCode => ClaudeCodeCli,
        CliTool.Codex => CodexCli,
        CliTool.Antigravity => AntigravityCli,
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };
}
