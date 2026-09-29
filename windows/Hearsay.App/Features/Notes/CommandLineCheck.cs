using Hearsay.Core.Notes;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Windows only (PLAN.md 18.4, "W6 core", command-line length): whether the
/// notes request for a transcript would be refused because its command line
/// is longer than Windows allows, worked out before anything is sent so the
/// confirm sheet can say so. It builds the invocation exactly as
/// <see cref="CliClient.CompleteAsync"/> does (<see cref="MeetingPrompt.Build"/>,
/// the <see cref="CliArguments"/> builder, <see cref="CliArguments.WindowsInvocation"/>,
/// <see cref="CmdShim.Resolve"/>) and measures it with
/// <see cref="WindowsCommandLine.Join"/>; the refusal text is the pipeline's
/// own (<see cref="CliProviderError.CommandLineTooLong"/>). The run folder in
/// the Antigravity and Codex paths is a placeholder of the real length. The
/// pipeline still checks at send time, so this is only the early warning.
/// The Mac has no such limit and no counterpart.
/// </summary>
internal static class CommandLineCheck
{
    /// <summary>The refusal text, or null when the request fits (or is not a CLI request, or would fail for another reason first).</summary>
    public static string? Refusal(string srtText, string languageCode, PromptTemplate template,
        AIProviderConfiguration configuration, CliLocator? locator = null)
    {
        ArgumentNullException.ThrowIfNull(srtText);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Preset.Kind.CliTool() is not { } tool) return null;
        if (Srt.CleanText(srtText).Trim().Length == 0) return null;
        string prompt;
        try
        {
            prompt = MeetingPrompt.Build(srtText.Trim(), languageCode, template);
        }
        catch (MeetingPromptException)
        {
            return null;
        }
        string executable;
        try
        {
            executable = (locator ?? new CliLocator()).Locate(tool, configuration.CliPath(tool));
        }
        catch (CliProviderException)
        {
            return null;
        }
        // The run folder CliClient makes: <temp>\Hearsay-<tool>-<GUID>.
        var directory = Path.Combine(Path.GetTempPath(), tool.TemporaryFolderPrefix() + Guid.Empty.ToString("D"));
        var system = MeetingPrompt.SystemMessage;
        IReadOnlyList<string> argv = tool switch
        {
            CliTool.Copilot => CliArguments.Copilot(executable, prompt, configuration.Model, configuration.ReasoningEffort),
            CliTool.ClaudeCode => CliArguments.ClaudeCode(executable, prompt, system, configuration.Model,
                configuration.ReasoningEffort),
            CliTool.Codex => CliArguments.Codex(executable, prompt, configuration.Model, configuration.ReasoningEffort,
                directory, Path.Combine(directory, CliArguments.CodexReplyFileName)),
            _ => CliArguments.Antigravity(executable, CliArguments.AntigravityPrompt(system, prompt), configuration.Model,
                configuration.ReasoningEffort, Path.Combine(directory, CliArguments.AntigravitySchemaFileName),
                Path.Combine(directory, CliArguments.AntigravityLogFileName)),
        };
        var (invocation, _) = CliArguments.WindowsInvocation(tool, argv);
        try
        {
            invocation = CmdShim.Resolve(invocation, Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        }
        catch (CliLaunchException)
        {
            // The launch itself would fail; the pipeline reports that.
            return null;
        }
        var length = WindowsCommandLine.Join(invocation).Length;
        return length > WindowsCommandLine.MaxLength
            ? new CliProviderError.CommandLineTooLong(tool, length).Description
            : null;
    }
}
