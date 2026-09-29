using Hearsay.App.Features.Notes;
using Hearsay.Core.Notes;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="CommandLineCheck.Refusal"/> (PLAN.md 18.4, "W6 core"): the
/// confirm sheet's early warning that a Copilot or Antigravity prompt would
/// not fit on a Windows command line, built exactly as the pipeline builds it.
/// The CLI is found by a fake locator, so nothing on disk is searched.
/// </summary>
public sealed class CommandLineCheckTests
{
    private const string CopilotPath = @"C:\Tools\copilot.exe";

    private static readonly AIProviderConfiguration Copilot =
        AIProviderConfiguration.FromPreset(ProviderPreset.CopilotCli) with { Model = "auto", CopilotPath = CopilotPath };

    private static CliLocator Locator() => new(
        CliSearchFolders.ForHome(@"C:\Users\test"),
        pathVariable: string.Empty,
        isExecutable: path => string.Equals(path, CopilotPath, StringComparison.OrdinalIgnoreCase),
        directoryContents: _ => []);

    private static string Srt(int letters) => "1\n00:00:01,000 --> 00:00:02,000\n" + new string('a', letters) + "\n";

    /// <summary>The command line the pipeline would start for <paramref name="srt"/>, measured the same way.</summary>
    private static int CommandLineLength(string srt)
    {
        var prompt = MeetingPrompt.Build(srt.Trim(), "en", PromptTemplate.GeneralMeeting);
        var argv = CliArguments.Copilot(CopilotPath, prompt, Copilot.Model, Copilot.ReasoningEffort);
        return WindowsCommandLine.Join(CliArguments.WindowsInvocation(CliTool.Copilot, argv).Argv).Length;
    }

    private static string? Refusal(string srt, AIProviderConfiguration? configuration = null) =>
        CommandLineCheck.Refusal(srt, "en", PromptTemplate.GeneralMeeting, configuration ?? Copilot, Locator());

    [Fact]
    public void TheLimitIsWindows()
    {
        Assert.Equal(32_766, WindowsCommandLine.MaxLength);
    }

    [Fact]
    public void BoundaryOnBothSides()
    {
        // Each caption letter adds one character to the command line.
        var fits = WindowsCommandLine.MaxLength - CommandLineLength(Srt(1)) + 1;
        Assert.True(fits > 0);
        Assert.Equal(WindowsCommandLine.MaxLength, CommandLineLength(Srt(fits)));
        Assert.Equal(WindowsCommandLine.MaxLength + 1, CommandLineLength(Srt(fits + 1)));

        Assert.Null(Refusal(Srt(fits)));
        Assert.Equal(new CliProviderError.CommandLineTooLong(CliTool.Copilot, WindowsCommandLine.MaxLength + 1).Description,
            Refusal(Srt(fits + 1)));
    }

    [Fact]
    public void ShortTranscriptsFit()
    {
        Assert.Null(Refusal(Srt(10)));
    }

    [Fact]
    public void NothingToSayWhenAnotherStepWouldFailFirst()
    {
        var huge = Srt(40_000);
        Assert.NotNull(Refusal(huge));
        // No caption text: the pipeline refuses the empty transcript instead.
        Assert.Null(Refusal("1\n00:00:01,000 --> 00:00:02,000\n\n"));
        // The CLI is not installed: the pipeline reports that.
        Assert.Null(Refusal(huge, Copilot with { CopilotPath = @"C:\Missing\copilot.exe" }));
        // Claude Code and Codex read the prompt from stdin; HTTP presets have no command line.
        Assert.Null(CommandLineCheck.Refusal(huge, "en", PromptTemplate.GeneralMeeting,
            AIProviderConfiguration.FromPreset(ProviderPreset.Custom), Locator()));
    }
}
