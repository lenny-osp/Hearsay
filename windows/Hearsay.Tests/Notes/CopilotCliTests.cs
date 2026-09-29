using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Tests.Settings;
using static Hearsay.Tests.Notes.CliFixtures;

namespace Hearsay.Tests.Notes;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/CopilotCLITests.swift, one
/// test per Swift test. Windows paths replace the Mac's; the Mac's last
/// lookup step (a login-shell <c>command -v</c>, which the fake runner saw)
/// is a <c>PATH</c> search here, which starts no process.
/// </summary>
public sealed class CopilotCliTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    private static AIProviderConfiguration CopilotConfiguration(string model) =>
        AIProviderConfiguration.FromPreset(ProviderPreset.CopilotCli) with { Model = model, CopilotPath = CopilotPath };

    private static AIProviderStore Store(string folder, CliTool? installed) =>
        new(new SettingsFile(folder), new InMemorySecretStore(), () => installed);

    // MARK: - Preset

    [Fact]
    public void PresetShape()
    {
        var preset = ProviderPreset.CopilotCli;
        Assert.Equal("copilotCLI", preset.Id);
        Assert.Equal("GitHub Copilot CLI", preset.Name);
        Assert.Empty(preset.BaseUrl);
        Assert.Equal("gpt-5.6-luna", preset.DefaultModel);
        Assert.Equal(AuthHeaderStyle.None, preset.Auth);
        Assert.True(preset.SupportsReasoningEffort);
        Assert.Equal(ProviderKind.CopilotCli, preset.Kind);
        Assert.Same(preset, ProviderPreset.All[0]);
        Assert.All(ProviderPreset.All.Take(4), p => Assert.True(p.Kind.IsCli()));
        Assert.All(ProviderPreset.All.Skip(4), p => Assert.Equal(ProviderKind.Http, p.Kind));
    }

    [Fact]
    public void FirstLaunchPicksTheFirstInstalledCli()
    {
        foreach (var tool in CliTools.All)
        {
            var store = Store(scratch.Make(), tool);
            Assert.Equal(AIProviderConfiguration.FromPreset(ProviderPreset.Preset(tool)), store.Configuration);
        }
        var without = Store(scratch.Make(), null);
        Assert.Equal(AIProviderConfiguration.Default, without.Configuration);
        Assert.Equal("copilotCLI", without.Configuration.PresetId);
    }

    [Fact]
    public void FirstKnownToolFollowsPresetOrder()
    {
        const string localBin = @"C:\Users\test\.local\bin";
        const string npm = @"C:\Users\test\AppData\Roaming\npm";
        Assert.Null(Locator([]).FirstKnownTool());
        Assert.Equal(CliTool.Antigravity, Locator([@"C:\Users\test\AppData\Local\agy\bin\agy.exe"]).FirstKnownTool());
        Assert.Equal(CliTool.Codex, Locator([$@"{localBin}\agy.exe", $@"{npm}\codex.cmd"]).FirstKnownTool());
        Assert.Equal(CliTool.Codex, Locator([$@"{npm}\codex.cmd"]).FirstKnownTool());
        Assert.Equal(CliTool.ClaudeCode, Locator([$@"{npm}\codex.cmd", $@"{localBin}\claude.exe"]).FirstKnownTool());
        Assert.Equal(CliTool.Copilot, Locator([$@"{npm}\codex.cmd", @"C:\Program Files\nodejs\copilot.cmd"]).FirstKnownTool());
    }

    [Fact]
    public void FirstLaunchChoiceIsSavedAndNotReconsidered()
    {
        var folder = scratch.Make();
        _ = Store(folder, CliTool.Codex);
        var later = Store(folder, CliTool.Copilot);
        Assert.Equal("codexCLI", later.Configuration.PresetId);
    }

    [Fact]
    public void CliPathsSurviveReloadAndPresetSwitch()
    {
        var folder = scratch.Make();
        var store = Store(folder, CliTool.Copilot);
        store.Configuration = store.Configuration with { CopilotPath = @"C:\custom\copilot.exe" };
        store.Configuration = store.Configuration.WithCliPath(@"C:\custom\claude.exe", CliTool.ClaudeCode);
        store.Configuration = store.Configuration.WithCliPath(@"C:\custom\codex.cmd", CliTool.Codex);
        store.Configuration = store.Configuration.WithCliPath(@"C:\custom\agy.exe", CliTool.Antigravity);
        store.SelectPreset(ProviderPreset.Ollama);
        store.SelectPreset(ProviderPreset.CopilotCli);
        var reloaded = Store(folder, null);
        Assert.Equal(@"C:\custom\copilot.exe", reloaded.Configuration.CopilotPath);
        Assert.Equal(@"C:\custom\claude.exe", reloaded.Configuration.CliPath(CliTool.ClaudeCode));
        Assert.Equal(@"C:\custom\codex.cmd", reloaded.Configuration.CliPath(CliTool.Codex));
        Assert.Equal(@"C:\custom\agy.exe", reloaded.Configuration.CliPath(CliTool.Antigravity));
        Assert.Equal(@"C:\custom\agy.exe", reloaded.Configuration.AntigravityPath);
        Assert.Equal("copilotCLI", reloaded.Configuration.PresetId);
    }

    // MARK: - argv (Python test_copilot_success_failure_and_auto_model)

    [Fact]
    public void ArgvForNamedModel()
    {
        Assert.Equal(
            [
                CopilotPath, "--prompt", "PROMPT", "--silent", "--no-color", "--no-ask-user",
                "--no-auto-update", "--output-format", "text", "--reasoning-effort", "max",
                "--model", "gpt-5.6-luna",
            ],
            CliArguments.Copilot(CopilotPath, "PROMPT", "gpt-5.6-luna", "max"));
    }

    [Fact]
    public void ArgvForAutoModelOmitsModel()
    {
        var argv = CliArguments.Copilot(CopilotPath, "PROMPT", "auto", "max");
        Assert.Equal(
            [
                CopilotPath, "--prompt", "PROMPT", "--silent", "--no-color", "--no-ask-user",
                "--no-auto-update", "--output-format", "text", "--reasoning-effort", "max",
            ],
            argv);
        Assert.DoesNotContain("--model", argv);
    }

    /// <summary>Python <c>env.get("AI_MODEL") or DEFAULT_AI_MODEL</c> and the same for effort.</summary>
    [Fact]
    public void EmptyModelAndEffortFallBackToDefaults()
    {
        var argv = CliArguments.Copilot(CopilotPath, "P", " ", null);
        Assert.Equal("max", ValueAfter("--reasoning-effort", argv));
        Assert.Equal(["--model", "gpt-5.6-luna"], argv.TakeLast(2));
    }

    [Fact]
    public void EnvironmentPrefixesBinaryFolderOnPath()
    {
        var environment = CliClient.Environment(CliTool.Copilot, @"C:\Users\x\AppData\Local\nvm\v24.16.0\copilot.cmd",
            new Dictionary<string, string> { ["Path"] = @"C:\Windows\System32;C:\Windows", ["USERPROFILE"] = @"C:\Users\x" });
        Assert.Equal(@"C:\Users\x\AppData\Local\nvm\v24.16.0;C:\Windows\System32;C:\Windows", environment["PATH"]);
        Assert.Equal(@"C:\Users\x", environment["USERPROFILE"]);
        // Windows keys ignore case: the inherited "Path" is replaced, not duplicated.
        Assert.Equal(2, environment.Count);
    }

    // MARK: - Pipeline through the CLI

    [Fact]
    public async Task SuccessParsesFencedJsonReplyAndSendsOnlyTheUserPrompt()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(0, "```json\n" + LaunchNotes + "\n```\n", ""));
        var pipeline = new NotesPipeline(cliClient: Client(fake));
        var response = await pipeline.GenerateAsync("\n" + CopilotSrt + "\n\n", "en", PromptTemplate.GeneralMeeting,
            CopilotConfiguration("auto"), "ignored");
        Assert.Equal("Product Launch Plan", response.Filename);
        Assert.Equal("# Notes\n", response.Markdown);
        Assert.Equal("# Transcript\n\n## 00:00 — Launch\n", response.TranscriptMarkdown);

        var call = Assert.Single(fake.Calls);
        Assert.DoesNotContain("--model", call.Argv);
        Assert.Equal("max", ValueAfter("--reasoning-effort", call.Argv));
        // Python: build_meeting_prompt(srt_text.strip(), language_code); no system message.
        Assert.Equal(MeetingPrompt.Build(CopilotSrt.Trim(), "en"), call.Argv[2]);
        Assert.DoesNotContain(MeetingPrompt.SystemMessage, call.Argv);
        Assert.Null(call.StandardInput);
        Assert.Equal(CopilotPath, call.Argv[0]);
        Assert.Equal(TimeSpan.FromSeconds(600), call.Timeout);
        Assert.StartsWith("Hearsay-copilot-", Path.GetFileName(call.Directory), StringComparison.Ordinal);
        Assert.StartsWith(@"C:\Tools;", call.Environment["PATH"], StringComparison.Ordinal);
        // The temporary working folder is removed afterwards.
        Assert.False(Directory.Exists(call.Directory));
    }

    [Fact]
    public async Task NonzeroExitMapsToFailedWithStderrExcerpt()
    {
        var longStderr = "Error: not logged in. " + new string('x', 600);
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", longStderr));
        var error = await CliErrorAsync(() => Client(fake).CompleteAsync("s", "u", CopilotConfiguration("auto"), null));
        Assert.Equal(new CliProviderError.Failed(CliTool.Copilot, 1, longStderr[..500]), error);
        Assert.Contains("exit code 1", error.Description, StringComparison.Ordinal);
        Assert.Contains("not logged in", error.Description, StringComparison.Ordinal);
        Assert.Contains("Run `copilot` once in Terminal to log in.", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureWithoutLoginHintHasNoLoginAdvice()
    {
        var message = new CliProviderError.Failed(CliTool.Copilot, 2, "Unknown model").Description;
        Assert.Contains("Unknown model", message, StringComparison.Ordinal);
        Assert.DoesNotContain("log in", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdoutMapsToEmptyOutput()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(0, " \n", ""));
        Assert.Equal(new CliProviderError.EmptyOutput(CliTool.Copilot),
            await CliErrorAsync(() => Client(fake).CompleteAsync("s", "u", CopilotConfiguration("auto"), null)));
    }

    [Fact]
    public async Task TimeoutMapsToTimedOut()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(15, "partial", "", TimedOut: true));
        Assert.Equal(new CliProviderError.TimedOut(CliTool.Copilot),
            await CliErrorAsync(() => Client(fake).CompleteAsync("s", "u", CopilotConfiguration("auto"), null)));
    }

    [Fact]
    public async Task LaunchErrorMapsToLaunchFailed()
    {
        var fake = new FakeCliRunner(_ => throw new CliLaunchException("boom"));
        Assert.Equal(new CliProviderError.LaunchFailed(CliTool.Copilot, "boom"),
            await CliErrorAsync(() => Client(fake).CompleteAsync("s", "u", CopilotConfiguration("auto"), null)));
    }

    // MARK: - Locating the binary

    [Fact]
    public async Task MissingBinaryMapsToNotInstalled()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", ""));
        var configuration = CopilotConfiguration("auto") with { CopilotPath = null };
        var error = await CliErrorAsync(() => Client(fake, Locator([])).CompleteAsync("s", "u", configuration, null));
        var notInstalled = Assert.IsType<CliProviderError.NotInstalled>(error);
        Assert.Equal(CliTool.Copilot, notInstalled.Tool);
        Assert.Contains(@"C:\Users\test\.local\bin\copilot.exe", notInstalled.Searched);
        Assert.Contains(@"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\copilot.exe", notInstalled.Searched);
        Assert.Contains(@"C:\Users\test\AppData\Roaming\npm\copilot.cmd", notInstalled.Searched);
        Assert.Contains(@"C:\Program Files\nodejs\copilot.cmd", notInstalled.Searched);
        Assert.Equal("PATH (copilot.exe, copilot.cmd, copilot.bat)", notInstalled.Searched[^1]);
        Assert.Contains("npm install -g @github/copilot", error.Description, StringComparison.Ordinal);
        // Windows: the PATH search starts no process, so the runner never ran.
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ConfiguredPathThatDoesNotExistIsNotInstalled()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(0, CopilotPath, ""));
        Assert.Equal(new CliProviderError.NotInstalled(CliTool.Copilot, [@"C:\nowhere\copilot.exe"]),
            await CliErrorAsync(() => Client(fake).LocateAsync(CliTool.Copilot, @"C:\nowhere\copilot.exe")));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void SearchOrderPrefersWingetThenNpmThenNodeFolderThenNewestNvm()
    {
        const string nvm = @"C:\Users\test\AppData\Local\nvm";
        string[] versions = ["v9.11.2", "v24.16.0", "v24.2.0"];
        CliLocator Make(IEnumerable<string> found) =>
            Locator(found, path => path == nvm ? versions : []);
        var allNvm = versions.Select(version => $@"{nvm}\{version}\copilot.cmd").ToList();
        Assert.Equal($@"{nvm}\v24.16.0\copilot.cmd", Make(allNvm).Locate(CliTool.Copilot, null));
        Assert.Equal(@"C:\Program Files\nodejs\copilot.cmd",
            Make([.. allNvm, @"C:\Program Files\nodejs\copilot.cmd"]).Locate(CliTool.Copilot, null));
        Assert.Equal(@"C:\Users\test\AppData\Roaming\npm\copilot.cmd",
            Make([.. allNvm, @"C:\Program Files\nodejs\copilot.cmd", @"C:\Users\test\AppData\Roaming\npm\copilot.cmd"])
                .Locate(CliTool.Copilot, null));
        Assert.Equal(@"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\copilot.exe",
            Make([@"C:\Users\test\AppData\Roaming\npm\copilot.cmd", @"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\copilot.exe"])
                .Locate(CliTool.Copilot, null));
        // In one folder the native program wins over the npm shim.
        Assert.Equal(@"C:\Users\test\AppData\Roaming\npm\copilot.exe",
            Make([@"C:\Users\test\AppData\Roaming\npm\copilot.cmd", @"C:\Users\test\AppData\Roaming\npm\copilot.exe"])
                .Locate(CliTool.Copilot, null));
        Assert.Equal($@"{nvm}\v24.16.0\copilot.cmd", Make(allNvm).KnownInstallation(CliTool.Copilot));
    }

    [Fact]
    public void PathLookupIsTheLastResort()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\opt\tools\copilot.cmd", @"D:\later\copilot.exe" };
        var locator = new CliLocator(CliSearchFolders.ForHome(Home), @"C:\empty;""C:\opt\tools"";relative;D:\later", found.Contains, _ => []);
        Assert.Equal(@"C:\opt\tools\copilot.cmd", locator.Locate(CliTool.Copilot, null));
    }

    [Fact]
    public void ConfiguredPathMayBeQuotedUseVariablesOrLeaveOutTheExtension()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\test\bin\copilot.cmd" };
        var locator = new CliLocator(CliSearchFolders.ForHome(Home), string.Empty, found.Contains, _ => []);
        Assert.Equal(@"C:\Users\test\bin\copilot.cmd", locator.Locate(CliTool.Copilot, @" ""C:\Users\test\bin\copilot.cmd"" "));
        Assert.Equal(@"C:\Users\test\bin\copilot.cmd", locator.Locate(CliTool.Copilot, @"C:\Users\test\bin\copilot"));
        Assert.Equal(@"C:\Users\test\bin\copilot.cmd", locator.Locate(CliTool.Copilot, @"~\bin\copilot.cmd"));
    }

    [Fact]
    public async Task CheckInstallationReportsPathAndVersion()
    {
        var fake = new FakeCliRunner(argv =>
        {
            Assert.Equal([CopilotPath, "--version"], argv);
            return new CliRunResult(0, "1.0.78\n", "");
        });
        var pipeline = new NotesPipeline(cliClient: Client(fake));
        var installation = await pipeline.CheckInstallationAsync(CopilotConfiguration("auto"));
        Assert.Equal(new CliInstallation(CliTool.Copilot, CopilotPath, "1.0.78"), installation);
        // Copilot has no login status command; only `--version` ran.
        Assert.Single(fake.Calls);
    }

    // MARK: - Routing

    [Fact]
    public async Task PipelineRoutesCopilotPresetToTheCliAndOthersToHttp()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(0, LaunchNotes, ""));
        var http = new RecordingChatClient();
        var pipeline = new NotesPipeline(http, Client(fake));

        _ = await pipeline.GenerateAsync(CopilotSrt, "en", PromptTemplate.GeneralMeeting, CopilotConfiguration("gpt-5.6-luna"), null);
        Assert.Single(fake.Calls);
        Assert.Equal(0, http.Count);

        var openAI = AIProviderConfiguration.FromPreset(ProviderPreset.Custom) with
        {
            BaseUrl = "https://example.test/chat/completions",
            CopilotPath = CopilotPath,
        };
        _ = await pipeline.GenerateAsync(CopilotSrt, "en", PromptTemplate.GeneralMeeting, openAI, "t");
        Assert.Single(fake.Calls);
        Assert.Equal(1, http.Count);
        Assert.IsType<CliClient>(pipeline.ClientFor(AIProviderConfiguration.FromPreset(ProviderPreset.CopilotCli)));
        Assert.IsType<RecordingChatClient>(pipeline.ClientFor(openAI));
    }

    // Windows-only: the prompt stays on Copilot's command line, which Windows limits.

    [Fact]
    public async Task CommandLineTooLongMapsToItsOwnError()
    {
        var fake = new FakeCliRunner(_ => throw new CliCommandLineTooLongException(40_000));
        var error = await CliErrorAsync(() => Client(fake).CompleteAsync("s", "u", CopilotConfiguration("auto"), null));
        Assert.Equal(new CliProviderError.CommandLineTooLong(CliTool.Copilot, 40_000), error);
        Assert.Equal(
            "GitHub Copilot CLI cannot take a transcript this long on Windows: the command line would be 40,000 characters, "
            + "and Windows allows 32,766. Use Claude Code, Codex, or an HTTP provider for long meetings.",
            error.Description);
    }

    [Fact]
    public void WindowsFileNamesAndCommands()
    {
        Assert.Equal(["copilot.exe", "copilot.cmd"], CliTool.Copilot.FileNames());
        Assert.Equal(["claude", "codex", "agy"], new[] { CliTool.ClaudeCode, CliTool.Codex, CliTool.Antigravity }.Select(t => t.BinaryName()));
        Assert.Equal("irm https://claude.ai/install.ps1 | iex", CliTool.ClaudeCode.InstallCommand());
        Assert.Equal(["copilot", "claude", "codex login", "agy"], CliTools.All.Select(tool => tool.LoginCommand()));
        Assert.Equal(["Copilot", "Claude Code", "Codex", "Antigravity"], CliTools.All.Select(tool => tool.ShortName()));
        Assert.Null(CliTool.Copilot.LoginStatusArguments());
        Assert.Equal(TimeSpan.FromSeconds(15), CliTool.Codex.LoginStatusTimeout());
    }
}
