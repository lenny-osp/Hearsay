using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearsay.Core.Settings;

namespace Hearsay.Core.Notes;

/// <summary>
/// The notes provider the user configured (PLAN.md sections 7, 8). Port of
/// <c>AIProviderConfiguration</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/AIProviderConfiguration.swift.
/// A value type as in Swift: change it with <c>with</c> and store the result.
/// The JSON keys (<see cref="ToJson"/>) are the Swift <c>CodingKeys</c>.
/// </summary>
public sealed record AIProviderConfiguration
{
    /// <summary>Python <c>generate_meeting_notes</c> payload <c>"temperature": 0.3</c>.</summary>
    public const double DefaultTemperature = 0.3;

    public AIProviderConfiguration(
        string presetId,
        string baseUrl,
        string model,
        string? reasoningEffort,
        AuthHeaderStyle auth,
        double? temperature = DefaultTemperature,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        bool askBeforeSending = true,
        Guid? selectedTemplateId = null,
        string? copilotPath = null,
        string? claudeCodePath = null,
        string? codexPath = null,
        string? antigravityPath = null)
    {
        ArgumentNullException.ThrowIfNull(presetId);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(model);
        PresetId = presetId;
        BaseUrl = baseUrl;
        Model = model;
        ReasoningEffort = reasoningEffort;
        Temperature = temperature;
        Auth = auth;
        ExtraHeaders = extraHeaders ?? new Dictionary<string, string>();
        AskBeforeSending = askBeforeSending;
        SelectedTemplateId = selectedTemplateId ?? PromptTemplate.GeneralMeetingId;
        CopilotPath = copilotPath;
        ClaudeCodePath = claudeCodePath;
        CodexPath = codexPath;
        AntigravityPath = antigravityPath;
    }

    public string PresetId { get; init; }

    /// <summary>Full <c>/chat/completions</c> endpoint.</summary>
    public string BaseUrl { get; init; }

    public string Model { get; init; }

    /// <summary>Sent as <c>reasoning_effort</c> when non-null and non-empty.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Sent as <c>temperature</c> when non-null (Python sends 0.3). Null omits
    /// the field, for models that reject it.
    /// </summary>
    public double? Temperature { get; init; }

    public AuthHeaderStyle Auth { get; init; }

    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; }

    /// <summary>Parity with <c>AI_CONFIRM</c>: true is <c>always</c>, false is <c>never</c>.</summary>
    public bool AskBeforeSending { get; init; }

    public Guid SelectedTemplateId { get; init; }

    /// <summary>The <c>copilot</c> program for the Copilot CLI preset. Null or empty auto-detects (<see cref="CliLocator"/>).</summary>
    public string? CopilotPath { get; init; }

    /// <summary>The <c>claude</c> program for the Claude Code CLI preset; null auto-detects.</summary>
    public string? ClaudeCodePath { get; init; }

    /// <summary>The <c>codex</c> program for the Codex CLI preset; null auto-detects.</summary>
    public string? CodexPath { get; init; }

    /// <summary>The <c>agy</c> program for the Antigravity CLI preset; null auto-detects.</summary>
    public string? AntigravityPath { get; init; }

    /// <summary>
    /// The preset's defaults: its URL, model, and auth, its default reasoning
    /// effort when it supports one, and temperature 0.3 when it supports one
    /// (HTTP presets only).
    /// </summary>
    public static AIProviderConfiguration FromPreset(ProviderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new AIProviderConfiguration(
            presetId: preset.Id,
            baseUrl: preset.BaseUrl,
            model: preset.DefaultModel,
            reasoningEffort: preset.SupportsReasoningEffort ? preset.DefaultEffort : null,
            auth: preset.Auth,
            temperature: preset.SupportsTemperature ? DefaultTemperature : null);
    }

    /// <summary>
    /// The configuration used when nothing is stored and no CLI is found:
    /// the GitHub Copilot CLI preset, whose settings explain how to install it.
    /// </summary>
    public static AIProviderConfiguration Default { get; } = FromPreset(ProviderPreset.CopilotCli);

    /// <summary>
    /// The first-launch configuration: the first CLI found, in preset order
    /// (Copilot, Claude Code, Codex, Antigravity), otherwise <see cref="Default"/>.
    /// </summary>
    public static AIProviderConfiguration FirstLaunch(CliTool? installedCli) =>
        installedCli is { } tool ? FromPreset(ProviderPreset.Preset(tool)) : Default;

    public ProviderPreset Preset => ProviderPreset.Preset(PresetId) ?? ProviderPreset.Custom;

    /// <summary>
    /// This configuration with a retired preset id replaced by <c>custom</c>,
    /// or null when the preset is still offered. Every other field is kept.
    /// </summary>
    public AIProviderConfiguration? MigratingRetiredPreset() =>
        ProviderPreset.RetiredIds.Contains(PresetId) ? this with { PresetId = ProviderPreset.Custom.Id } : null;

    /// <summary>The configured program path for <paramref name="tool"/>; null or empty auto-detects.</summary>
    public string? CliPath(CliTool tool) => tool switch
    {
        CliTool.Copilot => CopilotPath,
        CliTool.ClaudeCode => ClaudeCodePath,
        CliTool.Codex => CodexPath,
        CliTool.Antigravity => AntigravityPath,
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>This configuration with <paramref name="path"/> for <paramref name="tool"/> (Swift <c>setCLIPath</c>).</summary>
    public AIProviderConfiguration WithCliPath(string? path, CliTool tool) => tool switch
    {
        CliTool.Copilot => this with { CopilotPath = path },
        CliTool.ClaudeCode => this with { ClaudeCodePath = path },
        CliTool.Codex => this with { CodexPath = path },
        CliTool.Antigravity => this with { AntigravityPath = path },
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>The temperature to send, or null to omit it. Always null for CLI presets.</summary>
    public double? EffectiveTemperature => Preset.SupportsTemperature ? Temperature : null;

    /// <summary>
    /// The model as the confirm sheet shows it. An empty model is what the
    /// provider then uses: the Copilot default (Python <c>or</c>), the CLI's
    /// own default for Claude Code, Codex, and Antigravity, or nothing for
    /// HTTP. "CLI default" and "(none set)" are English until W7.
    /// </summary>
    public string ModelDescription
    {
        get
        {
            var trimmed = Model.Trim();
            if (trimmed.Length > 0) return trimmed;
            return Preset.Kind switch
            {
                ProviderKind.CopilotCli => ProviderPreset.DefaultOpenAIModel,
                ProviderKind.ClaudeCodeCli or ProviderKind.CodexCli or ProviderKind.AntigravityCli => "CLI default",
                _ => "(none set)",
            };
        }
    }

    /// <summary>The reasoning effort to send, or null to omit the field.</summary>
    public string? EffectiveReasoningEffort
    {
        get
        {
            if (!Preset.SupportsReasoningEffort) return null;
            var value = ReasoningEffort?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    public bool Equals(AIProviderConfiguration? other) =>
        other is not null
        && PresetId == other.PresetId
        && BaseUrl == other.BaseUrl
        && Model == other.Model
        && ReasoningEffort == other.ReasoningEffort
        && Nullable.Equals(Temperature, other.Temperature)
        && Auth == other.Auth
        && ExtraHeaders.Count == other.ExtraHeaders.Count
        && ExtraHeaders.All(pair => other.ExtraHeaders.TryGetValue(pair.Key, out var value) && value == pair.Value)
        && AskBeforeSending == other.AskBeforeSending
        && SelectedTemplateId == other.SelectedTemplateId
        && CopilotPath == other.CopilotPath
        && ClaudeCodePath == other.ClaudeCodePath
        && CodexPath == other.CodexPath
        && AntigravityPath == other.AntigravityPath;

    public override int GetHashCode() => HashCode.Combine(PresetId, BaseUrl, Model, Auth, SelectedTemplateId);

    // MARK: - JSON (the Swift Codable form)

    /// <summary>
    /// The Swift <c>encode(to:)</c> shape: optional strings are left out when
    /// null, <c>temperature</c> is written as null when null so "omit"
    /// survives a reload. The template id is a lowercase GUID (PLAN.md 18.4).
    /// </summary>
    public JsonObject ToJson()
    {
        var headers = new JsonObject();
        foreach (var (name, value) in ExtraHeaders.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            headers[name] = value;
        }
        var json = new JsonObject
        {
            ["presetID"] = PresetId,
            ["baseURL"] = BaseUrl,
            ["model"] = Model,
        };
        if (ReasoningEffort is not null) json["reasoningEffort"] = ReasoningEffort;
        json["temperature"] = Temperature is { } temperature ? JsonValue.Create(temperature) : null;
        json["auth"] = Auth.RawValue();
        json["extraHeaders"] = headers;
        json["askBeforeSending"] = AskBeforeSending;
        json["selectedTemplateID"] = SelectedTemplateId.ToString("D");
        if (CopilotPath is not null) json["copilotPath"] = CopilotPath;
        if (ClaudeCodePath is not null) json["claudeCodePath"] = ClaudeCodePath;
        if (CodexPath is not null) json["codexPath"] = CodexPath;
        if (AntigravityPath is not null) json["antigravityPath"] = AntigravityPath;
        return json;
    }

    /// <summary>
    /// The Swift <c>init(from:)</c>: <c>presetID</c>, <c>baseURL</c>,
    /// <c>model</c> and <c>auth</c> are required; a missing <c>temperature</c>
    /// key (configurations saved before the field existed) means 0.3, an
    /// explicit null means omit it. Null when the JSON does not decode.
    /// </summary>
    public static AIProviderConfiguration? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return null;
        try
        {
            var presetId = RequiredString(json, "presetID");
            var baseUrl = RequiredString(json, "baseURL");
            var model = RequiredString(json, "model");
            var auth = AuthHeaderStyles.FromRawValue(RequiredString(json, "auth"))
                ?? throw new FormatException("auth");
            double? temperature = DefaultTemperature;
            if (json.ContainsKey("temperature"))
            {
                temperature = json["temperature"] is { } value ? value.GetValue<double>() : null;
            }
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (json["extraHeaders"] is { } headerNode)
            {
                foreach (var (name, value) in headerNode.AsObject())
                {
                    headers[name] = value?.GetValue<string>() ?? throw new FormatException("extraHeaders");
                }
            }
            var templateId = OptionalString(json, "selectedTemplateID") is { } idText
                ? Guid.Parse(idText)
                : PromptTemplate.GeneralMeetingId;
            return new AIProviderConfiguration(
                presetId: presetId,
                baseUrl: baseUrl,
                model: model,
                reasoningEffort: OptionalString(json, "reasoningEffort"),
                auth: auth,
                temperature: temperature,
                extraHeaders: headers,
                askBeforeSending: json["askBeforeSending"] is { } ask ? ask.GetValue<bool>() : true,
                selectedTemplateId: templateId,
                copilotPath: OptionalString(json, "copilotPath"),
                claudeCodePath: OptionalString(json, "claudeCodePath"),
                codexPath: OptionalString(json, "codexPath"),
                antigravityPath: OptionalString(json, "antigravityPath"));
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonObject json, string key) =>
        OptionalString(json, key) ?? throw new KeyNotFoundException(key);

    private static string? OptionalString(JsonObject json, string key) =>
        json[key] is { } value ? value.GetValue<string>() : null;
}

/// <summary>
/// Persists the provider configuration and the prompt templates in the app's
/// <see cref="SettingsFile"/> (keys <c>aiProviderConfiguration</c> and
/// <c>promptTemplates</c>, as JSON values where the Mac keeps JSON data in
/// <c>UserDefaults</c>), and the per-preset API tokens in an
/// <see cref="ISecretStore"/> (Credential Manager). Port of
/// <c>AIProviderStore</c> in AIProviderConfiguration.swift; the Swift
/// <c>@Observable</c> becomes <see cref="INotifyPropertyChanged"/>. A failed
/// write does not throw: the value holds for this run and
/// <see cref="SaveFailed"/> fires, as in <see cref="AppSettings"/>. Not
/// thread-safe; use it from the UI thread, as the Swift's <c>@MainActor</c>.
/// </summary>
public sealed class AIProviderStore : INotifyPropertyChanged
{
    public static class Key
    {
        public const string Configuration = "aiProviderConfiguration";
        public const string Templates = "promptTemplates";
    }

    private static readonly JsonSerializerOptions TemplateJson = new();

    private readonly SettingsFile file;
    private AIProviderConfiguration configuration;
    private List<PromptTemplate> templates;
    private int tokenRevision;

    /// <param name="file">The app's one settings file (shared with <see cref="AppSettings"/>).</param>
    /// <param name="secrets">Where tokens live.</param>
    /// <param name="installedCli">
    /// Asked only on first launch (nothing stored) to pick the default
    /// preset. The default checks the well-known locations and <c>PATH</c>
    /// and never starts a process.
    /// </param>
    public AIProviderStore(SettingsFile file, ISecretStore secrets, Func<CliTool?>? installedCli = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(secrets);
        this.file = file;
        Secrets = secrets;
        var storedConfiguration = AIProviderConfiguration.FromJson(file.Get(Key.Configuration));
        var userTemplates = LoadTemplates(file.Get(Key.Templates))
            .Where(template => !template.IsBuiltIn && template.Id != PromptTemplate.GeneralMeetingId)
            .ToList();
        templates = [PromptTemplate.GeneralMeeting, .. userTemplates];
        var needsSave = storedConfiguration is null;
        var loaded = storedConfiguration
            ?? AIProviderConfiguration.FirstLaunch((installedCli ?? (() => new CliLocator().FirstKnownTool()))());
        if (loaded.MigratingRetiredPreset() is { } migrated)
        {
            MoveToken(loaded.PresetId, migrated.PresetId, secrets);
            loaded = migrated;
            needsSave = true;
        }
        if (!templates.Any(template => template.Id == loaded.SelectedTemplateId))
        {
            loaded = loaded with { SelectedTemplateId = PromptTemplate.GeneralMeetingId };
        }
        configuration = loaded;
        if (needsSave) SaveConfiguration();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A change could not be written to settings.json; it holds for this run only.</summary>
    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    public ISecretStore Secrets { get; }

    public AIProviderConfiguration Configuration
    {
        get => configuration;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Equals(configuration)) return;
            configuration = value;
            SaveConfiguration();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTemplate));
            OnTokenChanged();
        }
    }

    /// <summary>Always starts with <see cref="PromptTemplate.GeneralMeeting"/>.</summary>
    public IReadOnlyList<PromptTemplate> Templates => templates;

    /// <summary>Bumped on every token change so views re-read <see cref="HasToken"/>.</summary>
    public int TokenRevision => tokenRevision;

    /// <summary>
    /// Moves the stored token of a retired preset to its replacement, once. A
    /// token already stored under the replacement is never overwritten (the
    /// old one is then left in place), and the old one is deleted only after
    /// a successful copy.
    /// </summary>
    private static void MoveToken(string oldId, string newId, ISecretStore secrets)
    {
        string? token;
        try
        {
            token = secrets.Read(oldId);
        }
        catch (SecretStoreException)
        {
            return;
        }
        if (string.IsNullOrEmpty(token)) return;
        string? existing;
        try
        {
            existing = secrets.Read(newId);
        }
        catch (SecretStoreException)
        {
            existing = null;
        }
        if (!string.IsNullOrEmpty(existing)) return;
        try
        {
            secrets.Write(token, newId);
        }
        catch (SecretStoreException)
        {
            return;
        }
        try
        {
            secrets.Delete(oldId);
        }
        catch (SecretStoreException)
        {
        }
    }

    private static List<PromptTemplate> LoadTemplates(JsonNode? node)
    {
        if (node is not JsonArray) return [];
        try
        {
            return node.Deserialize<List<PromptTemplate>>(TemplateJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (ArgumentNullException)
        {
            return [];
        }
    }

    private void SaveConfiguration() => Persist(() => file.Set(Key.Configuration, configuration.ToJson()));

    private void SaveTemplates() => Persist(() => file.Set(Key.Templates,
        JsonSerializer.SerializeToNode(templates.Where(template => !template.IsBuiltIn).ToList(), TemplateJson)));

    private void Persist(Action write)
    {
        try
        {
            write();
        }
        catch (IOException error)
        {
            SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(error));
        }
        catch (UnauthorizedAccessException error)
        {
            SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(error));
        }
    }

    // MARK: - Provider

    /// <summary>
    /// Switches to <paramref name="preset"/>, resetting URL, model, auth,
    /// reasoning effort, and temperature to its defaults. Keeps extra
    /// headers, the ask setting, the template, and the CLI paths.
    /// </summary>
    public void SelectPreset(ProviderPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Configuration = AIProviderConfiguration.FromPreset(preset) with
        {
            ExtraHeaders = configuration.ExtraHeaders,
            AskBeforeSending = configuration.AskBeforeSending,
            SelectedTemplateId = configuration.SelectedTemplateId,
            CopilotPath = configuration.CopilotPath,
            ClaudeCodePath = configuration.ClaudeCodePath,
            CodexPath = configuration.CodexPath,
            AntigravityPath = configuration.AntigravityPath,
        };
    }

    // MARK: - Tokens

    /// <exception cref="SecretStoreException">Credential Manager failed.</exception>
    public string? Token(string presetId) => Secrets.Read(presetId);

    /// <summary>The token for the current preset, or null when unset or unreadable.</summary>
    public string? CurrentToken
    {
        get
        {
            try
            {
                var value = Secrets.Read(configuration.PresetId);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (SecretStoreException)
            {
                return null;
            }
        }
    }

    public bool HasToken => CurrentToken is not null;

    /// <summary>Stores <paramref name="token"/> (trimmed) for the current preset; an empty token deletes it.</summary>
    /// <exception cref="SecretStoreException">Credential Manager failed.</exception>
    public void SetToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var trimmed = token.Trim();
        try
        {
            if (trimmed.Length == 0)
            {
                Secrets.Delete(configuration.PresetId);
            }
            else
            {
                Secrets.Write(trimmed, configuration.PresetId);
            }
        }
        finally
        {
            OnTokenChanged();
        }
    }

    /// <exception cref="SecretStoreException">Credential Manager failed.</exception>
    public void DeleteToken()
    {
        try
        {
            Secrets.Delete(configuration.PresetId);
        }
        finally
        {
            OnTokenChanged();
        }
    }

    private void OnTokenChanged()
    {
        tokenRevision++;
        OnPropertyChanged(nameof(TokenRevision));
        OnPropertyChanged(nameof(CurrentToken));
        OnPropertyChanged(nameof(HasToken));
    }

    // MARK: - Templates

    public PromptTemplate SelectedTemplate => Template(configuration.SelectedTemplateId);

    /// <summary>The template with <paramref name="id"/>, or the built-in one when it no longer exists.</summary>
    public PromptTemplate Template(Guid id) =>
        templates.FirstOrDefault(template => template.Id == id) ?? PromptTemplate.GeneralMeeting;

    public PromptTemplate AddTemplate(string name, string instructions)
    {
        var template = new PromptTemplate(name, instructions);
        templates = [.. templates, template];
        SaveTemplates();
        OnPropertyChanged(nameof(Templates));
        return template;
    }

    /// <summary>Replaces a user template's name and instructions. The built-in template is never changed.</summary>
    public void UpdateTemplate(PromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var index = templates.FindIndex(existing => existing.Id == template.Id);
        if (index < 0 || templates[index].IsBuiltIn) return;
        var updated = templates.ToList();
        updated[index] = updated[index] with { Name = template.Name, Instructions = template.Instructions };
        templates = updated;
        SaveTemplates();
        OnPropertyChanged(nameof(Templates));
        OnPropertyChanged(nameof(SelectedTemplate));
    }

    /// <summary>
    /// Deletes a user template. The built-in template cannot be deleted. If
    /// the deleted template was the default, the built-in one becomes it.
    /// </summary>
    public void DeleteTemplate(Guid id)
    {
        var index = templates.FindIndex(existing => existing.Id == id);
        if (index < 0 || templates[index].IsBuiltIn) return;
        var updated = templates.ToList();
        updated.RemoveAt(index);
        templates = updated;
        SaveTemplates();
        OnPropertyChanged(nameof(Templates));
        if (configuration.SelectedTemplateId == id)
        {
            Configuration = configuration with { SelectedTemplateId = PromptTemplate.GeneralMeetingId };
        }
    }

    public void SetDefaultTemplate(Guid id)
    {
        if (!templates.Any(template => template.Id == id)) return;
        Configuration = configuration with { SelectedTemplateId = id };
    }

    private void OnPropertyChanged([CallerMemberName] string property = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
