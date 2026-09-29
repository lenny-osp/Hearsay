using Hearsay.Core.Notes;

namespace Hearsay.Tests.Notes;

/// <summary>
/// Port of the preset assertions in mac/HearsayCore/Tests/HearsayCoreTests:
/// NotesPipelineTests.swift (presetTable, temperatureIsOnlyForHTTPPresets),
/// CopilotCLITests.swift, ClaudeCodeCodexCLITests.swift and
/// AntigravityCLITests.swift (presetShape). The assertions on
/// AIProviderConfiguration wait for its port (W6); the default efforts they
/// check are asserted here on the presets.
/// </summary>
public class ProviderPresetsTests
{
    [Fact]
    public void PresetTable()
    {
        Assert.Equal(["copilotCLI", "claudeCodeCLI", "codexCLI", "antigravityCLI", "ollama", "custom"],
            ProviderPreset.All.Select(preset => preset.Id));
        Assert.Equal(
            [
                "GitHub Copilot CLI", "Claude Code CLI (Claude subscription)", "Codex CLI (ChatGPT subscription)",
                "Antigravity CLI", "Ollama / LM Studio", "Custom",
            ],
            ProviderPreset.All.Select(preset => preset.Name));
        Assert.Equal(
            [
                ProviderKind.CopilotCli, ProviderKind.ClaudeCodeCli, ProviderKind.CodexCli,
                ProviderKind.AntigravityCli, ProviderKind.Http, ProviderKind.Http,
            ],
            ProviderPreset.All.Select(preset => preset.Kind));
        foreach (var retired in new[] { "githubModels", "openai", "anthropic", "azureOpenAI" })
        {
            Assert.Null(ProviderPreset.Preset(retired));
            Assert.Contains(retired, ProviderPreset.RetiredIds);
        }
        Assert.Equal(4, ProviderPreset.RetiredIds.Count);
        Assert.Equal("claude-sonnet-5", ProviderPreset.ClaudeCodeCli.DefaultModel);
        Assert.Equal("gpt-6-luna", ProviderPreset.CodexCli.DefaultModel);
        Assert.Equal("high", ProviderPreset.ClaudeCodeCli.DefaultEffort);
        Assert.Equal("max", ProviderPreset.CodexCli.DefaultEffort);
        Assert.Equal("high", ProviderPreset.AntigravityCli.DefaultEffort);
        Assert.Equal("gemini-3.8-flash-high", ProviderPreset.AntigravityCli.DefaultModel);
        Assert.Equal("max", ProviderPreset.CopilotCli.DefaultEffort);
        Assert.Equal("http://localhost:11434/v1/chat/completions", ProviderPreset.Ollama.BaseUrl);
        Assert.Empty(ProviderPreset.Custom.BaseUrl);
        Assert.Equal(AuthHeaderStyle.Bearer, ProviderPreset.Custom.Auth);
        Assert.Equal([AuthHeaderStyle.Bearer, AuthHeaderStyle.ApiKey, AuthHeaderStyle.None], AuthHeaderStyles.All);
        Assert.Equal(AuthHeaderStyle.None, ProviderPreset.Ollama.Auth);
        Assert.Empty(ProviderPreset.Ollama.DefaultModel);
        Assert.False(ProviderPreset.Ollama.SupportsReasoningEffort);
        Assert.Equal("max", ProviderPreset.Custom.DefaultEffort);
        Assert.True(ProviderPreset.Custom.SupportsReasoningEffort);
        Assert.Empty(ProviderPreset.Custom.DefaultModel);
        Assert.Equal("gpt-5.6-luna", ProviderPreset.DefaultOpenAIModel);
        Assert.Equal("max", ProviderPreset.DefaultReasoningEffort);
    }

    /// <summary>No CLI has a temperature option; HTTP presets keep it.</summary>
    [Fact]
    public void TemperatureIsOnlyForHttpPresets()
    {
        foreach (var preset in ProviderPreset.All)
        {
            Assert.Equal(preset.Kind == ProviderKind.Http, preset.SupportsTemperature);
        }
    }

    [Fact]
    public void PresetLookup()
    {
        foreach (var preset in ProviderPreset.All)
        {
            Assert.Same(preset, ProviderPreset.Preset(preset.Id));
        }
        Assert.Null(ProviderPreset.Preset("Custom"));
        foreach (var tool in CliTools.All)
        {
            Assert.Equal(tool, ProviderPreset.Preset(tool).Kind.CliTool());
        }
    }

    [Fact]
    public void CopilotPresetShape()
    {
        var preset = ProviderPreset.CopilotCli;
        Assert.Equal("copilotCLI", preset.Id);
        Assert.Equal("GitHub Copilot CLI", preset.Name);
        Assert.Empty(preset.BaseUrl);
        Assert.Equal("gpt-5.6-luna", preset.DefaultModel);
        Assert.Equal(AuthHeaderStyle.None, preset.Auth);
        Assert.True(preset.SupportsReasoningEffort);
        Assert.Equal(ProviderKind.CopilotCli, preset.Kind);
        Assert.Equal(CliTool.Copilot, preset.Kind.CliTool());
        Assert.Same(preset, ProviderPreset.All[0]);
        Assert.All(ProviderPreset.All.Take(4), p => Assert.True(p.Kind.IsCli()));
        Assert.All(ProviderPreset.All.Skip(4), p => Assert.Equal(ProviderKind.Http, p.Kind));
        Assert.Same(preset, ProviderPreset.Preset(CliTool.Copilot));
    }

    [Fact]
    public void ClaudeCodePresetShape()
    {
        var preset = ProviderPreset.ClaudeCodeCli;
        Assert.Equal("claudeCodeCLI", preset.Id);
        Assert.Equal("Claude Code CLI (Claude subscription)", preset.Name);
        Assert.Equal(ProviderKind.ClaudeCodeCli, preset.Kind);
        Assert.Equal(CliTool.ClaudeCode, preset.Kind.CliTool());
        Assert.Equal("claude-sonnet-5", preset.DefaultModel);
        Assert.Equal("high", preset.DefaultEffort);
        Assert.Equal(AuthHeaderStyle.None, preset.Auth);
        Assert.True(preset.SupportsReasoningEffort);
        Assert.False(preset.SupportsTemperature);
        Assert.Same(preset, ProviderPreset.All[1]);
    }

    [Fact]
    public void CodexPresetShape()
    {
        var preset = ProviderPreset.CodexCli;
        Assert.Equal("codexCLI", preset.Id);
        Assert.Equal("Codex CLI (ChatGPT subscription)", preset.Name);
        Assert.Equal(ProviderKind.CodexCli, preset.Kind);
        Assert.Equal(CliTool.Codex, preset.Kind.CliTool());
        Assert.Equal("gpt-6-luna", preset.DefaultModel);
        Assert.Equal("max", preset.DefaultEffort);
        Assert.Equal(AuthHeaderStyle.None, preset.Auth);
        Assert.True(preset.SupportsReasoningEffort);
        Assert.False(preset.SupportsTemperature);
        Assert.Same(preset, ProviderPreset.All[2]);
    }

    [Fact]
    public void AntigravityPresetShape()
    {
        var preset = ProviderPreset.AntigravityCli;
        Assert.Equal("antigravityCLI", preset.Id);
        Assert.Equal("Antigravity CLI", preset.Name);
        Assert.Equal(ProviderKind.AntigravityCli, preset.Kind);
        Assert.Equal(CliTool.Antigravity, preset.Kind.CliTool());
        Assert.Equal("gemini-3.8-flash-high", preset.DefaultModel);
        Assert.Equal("high", preset.DefaultEffort);
        Assert.Equal(AuthHeaderStyle.None, preset.Auth);
        Assert.True(preset.SupportsReasoningEffort);
        Assert.False(preset.SupportsTemperature);
        Assert.Same(preset, ProviderPreset.All[3]);
        Assert.Same(preset, ProviderPreset.Preset(CliTool.Antigravity));
        Assert.Equal([CliTool.Copilot, CliTool.ClaudeCode, CliTool.Codex, CliTool.Antigravity], CliTools.All);
    }

    [Fact]
    public void RawValuesMatchSwift()
    {
        Assert.Equal(["bearer", "apiKey", "none"], AuthHeaderStyles.All.Select(style => style.RawValue()));
        Assert.Equal(["copilot", "claudeCode", "codex", "antigravity"], CliTools.All.Select(tool => tool.RawValue()));
        Assert.Equal(["copilotCLI", "claudeCodeCLI", "codexCLI", "antigravityCLI", "http", "http"],
            ProviderPreset.All.Select(preset => preset.Kind.RawValue()));
        foreach (var style in AuthHeaderStyles.All)
        {
            Assert.Equal(style, AuthHeaderStyles.FromRawValue(style.RawValue()));
        }
        foreach (var tool in CliTools.All)
        {
            Assert.Equal(tool, CliTools.FromRawValue(tool.RawValue()));
        }
        foreach (var preset in ProviderPreset.All)
        {
            Assert.Equal(preset.Kind, ProviderKinds.FromRawValue(preset.Kind.RawValue()));
        }
        Assert.Null(AuthHeaderStyles.FromRawValue("Bearer"));
        Assert.Null(ProviderKinds.FromRawValue("cli"));
        Assert.Null(CliTools.FromRawValue("claude"));
        Assert.Null(ProviderKind.Http.CliTool());
        Assert.False(ProviderKind.Http.IsCli());
    }

    [Fact]
    public void AuthHeaderStyleNamesAndTokens()
    {
        Assert.Equal(["Authorization: Bearer", "api-key", "No token"], AuthHeaderStyles.All.Select(style => style.DisplayName()));
        Assert.True(AuthHeaderStyle.Bearer.NeedsToken());
        Assert.True(AuthHeaderStyle.ApiKey.NeedsToken());
        Assert.False(AuthHeaderStyle.None.NeedsToken());
    }
}
