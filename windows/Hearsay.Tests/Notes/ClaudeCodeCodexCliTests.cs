using Hearsay.Core;
using Hearsay.Core.Notes;
using static Hearsay.Tests.Notes.CliFixtures;

namespace Hearsay.Tests.Notes;

// Port of mac/HearsayCore/Tests/HearsayCoreTests/ClaudeCodeCodexCLITests.swift,
// one test per Swift test. Windows difference (CliArguments.WindowsInvocation):
// the runs carry the prompt on stdin, so the argv the runner sees ends before
// `-- <prompt>` (Claude Code) or with `-- -` (Codex); the argv builders
// themselves match the Mac flag for flag.

public sealed class ClaudeCodeCliTests
{
    private static CliLocator CliLocator() => Locator([ClaudePath, CodexPath]);

    private static AIProviderConfiguration ClaudeConfiguration(string model = "claude-sonnet-5", string? effort = "high") =>
        AIProviderConfiguration.FromPreset(ProviderPreset.ClaudeCodeCli) with
        {
            Model = model,
            ReasoningEffort = effort,
            ClaudeCodePath = ClaudePath,
        };

    // MARK: - Preset

    [Fact]
    public void PresetShape()
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

    // MARK: - argv

    [Fact]
    public void ArgvForNamedModel()
    {
        Assert.Equal(
            [
                ClaudePath, "--print", "--output-format", "text",
                "--tools", "", "--setting-sources", "", "--strict-mcp-config",
                "--no-session-persistence", "--permission-mode", "dontAsk",
                "--disable-slash-commands", "--no-chrome",
                "--system-prompt", "SYSTEM",
                "--model", "opus", "--effort", "high",
                "--", "PROMPT",
            ],
            CliArguments.ClaudeCode(ClaudePath, "PROMPT", "SYSTEM", "opus", "high"));
    }

    [Fact]
    public void ArgvForPresetDefaults()
    {
        var configuration = AIProviderConfiguration.FromPreset(ProviderPreset.ClaudeCodeCli);
        var argv = CliArguments.ClaudeCode(ClaudePath, "P", MeetingPrompt.SystemMessage, configuration.Model,
            configuration.ReasoningEffort);
        Assert.Equal("claude-sonnet-5", ValueAfter("--model", argv));
        Assert.Equal("high", ValueAfter("--effort", argv));
        Assert.Equal(MeetingPrompt.SystemMessage, ValueAfter("--system-prompt", argv));
        Assert.DoesNotContain("--temperature", argv);
        Assert.Equal(["--", "P"], argv.TakeLast(2));
    }

    /// <summary>An empty model or effort leaves the choice to the CLI.</summary>
    [Fact]
    public void EmptyModelAndEffortAreOmitted()
    {
        var argv = CliArguments.ClaudeCode(ClaudePath, "-starts with a dash", "", "  ", " ");
        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("--effort", argv);
        Assert.DoesNotContain("--system-prompt", argv);
        Assert.Equal(["--", "-starts with a dash"], argv.TakeLast(2));
    }

    [Theory]
    [InlineData("none", "low")]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    [InlineData(" MAX ", "max")]
    [InlineData("", null)]
    [InlineData("turbo", null)]
    [InlineData(null, null)]
    public void EffortMapping(string? input, string? output) => Assert.Equal(output, CliArguments.ClaudeCodeEffort(input));

    [Fact]
    public void EnvironmentDropsApiKeysAndNestedSessionVariables()
    {
        var environment = CliClient.Environment(CliTool.ClaudeCode, ClaudePath, new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows\System32;C:\Windows",
            ["USERPROFILE"] = Home,
            ["ANTHROPIC_API_KEY"] = "sk",
            ["anthropic_auth_token"] = "t",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CLAUDE_CONFIG_DIR"] = @"C:\Users\test\.claude",
        });
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PATH"] = @"C:\Users\test\.local\bin;C:\Windows\System32;C:\Windows",
                ["USERPROFILE"] = Home,
                ["CLAUDE_CONFIG_DIR"] = @"C:\Users\test\.claude",
            },
            environment);
    }

    // MARK: - Runs

    [Fact]
    public async Task SuccessReturnsStdoutAndRunsInATemporaryFolder()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(0, LaunchNotes, ""));
        var pipeline = new NotesPipeline(cliClient: Client(fake, CliLocator()));
        var response = await pipeline.GenerateAsync("\n" + CopilotSrt + "\n", "en", PromptTemplate.GeneralMeeting,
            ClaudeConfiguration(), "ignored");
        Assert.Equal("Product Launch Plan", response.Filename);
        var call = Assert.Single(fake.Calls);
        var prompt = MeetingPrompt.Build(CopilotSrt.Trim(), "en");
        var macArgv = CliArguments.ClaudeCode(ClaudePath, prompt, MeetingPrompt.SystemMessage, "claude-sonnet-5", "high");
        // Windows: everything before `-- <prompt>`, and the prompt on stdin.
        Assert.Equal(macArgv.Take(macArgv.Count - 2), call.Argv);
        Assert.Equal(prompt, call.StandardInput);
        Assert.DoesNotContain("ignored", call.Argv);
        Assert.Equal(TimeSpan.FromSeconds(600), call.Timeout);
        Assert.StartsWith("Hearsay-claudeCode-", Path.GetFileName(call.Directory), StringComparison.Ordinal);
        Assert.StartsWith(@"C:\Users\test\.local\bin;", call.Environment["PATH"], StringComparison.Ordinal);
        Assert.False(Directory.Exists(call.Directory));
    }

    /// <summary>Claude Code prints "Not logged in · Please run /login" on stdout and exits 1.</summary>
    [Fact]
    public async Task NotLoggedInIsDetectedFromStdout()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "Not logged in · Please run /login\n", ""));
        var error = await CliErrorAsync(() => Client(fake, CliLocator()).CompleteAsync("s", "u", ClaudeConfiguration(), null));
        Assert.Equal(new CliProviderError.NotLoggedIn(CliTool.ClaudeCode, "Not logged in · Please run /login\n"), error);
        Assert.Contains("Run `claude` once in Terminal", error.Description, StringComparison.Ordinal);
        Assert.Contains("Claude subscription", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonzeroExitKeepsAStderrExcerpt()
    {
        var stderr = "API Error: model not found " + new string('y', 700);
        var fake = new FakeCliRunner(_ => new CliRunResult(2, "", stderr));
        Assert.Equal(new CliProviderError.Failed(CliTool.ClaudeCode, 2, stderr[..500]),
            await CliErrorAsync(() => Client(fake, CliLocator()).CompleteAsync("s", "u", ClaudeConfiguration(), null)));
        var message = new CliProviderError.Failed(CliTool.ClaudeCode, 2, "bad model").Description;
        Assert.Contains("Claude Code CLI call failed (exit code 2)", message, StringComparison.Ordinal);
        Assert.Contains("bad model", message, StringComparison.Ordinal);
        Assert.DoesNotContain("log in", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutEmptyOutputAndLaunchFailure()
    {
        (CliRunResult? Result, CliProviderError Expected)[] cases =
        [
            (new CliRunResult(15, "partial", "", TimedOut: true), new CliProviderError.TimedOut(CliTool.ClaudeCode)),
            (new CliRunResult(0, "\n ", ""), new CliProviderError.EmptyOutput(CliTool.ClaudeCode)),
            (null, new CliProviderError.LaunchFailed(CliTool.ClaudeCode, "boom")),
        ];
        foreach (var (result, expected) in cases)
        {
            var fake = new FakeCliRunner(_ => result ?? throw new InvalidOperationException("boom"));
            Assert.Equal(expected,
                await CliErrorAsync(() => Client(fake, CliLocator()).CompleteAsync("s", "u", ClaudeConfiguration(), null)));
        }
        Assert.Equal("Claude Code CLI did not answer within 10 minutes and was stopped.",
            new CliProviderError.TimedOut(CliTool.ClaudeCode).Description);
    }

    [Fact]
    public async Task NotInstalledNamesTheInstallCommand()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", ""));
        var configuration = ClaudeConfiguration() with { ClaudeCodePath = null };
        var error = await CliErrorAsync(() => Client(fake, Locator([])).CompleteAsync("s", "u", configuration, null));
        var notInstalled = Assert.IsType<CliProviderError.NotInstalled>(error);
        Assert.Equal(
            [@"C:\Users\test\.local\bin\claude.exe", @"C:\Users\test\.local\bin\claude.cmd",
             @"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\claude.exe"],
            notInstalled.Searched.Take(3));
        Assert.Equal("PATH (claude.exe, claude.cmd, claude.bat)", notInstalled.Searched[^1]);
        Assert.Contains("irm https://claude.ai/install.ps1 | iex", error.Description, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    // MARK: - Check

    [Fact]
    public async Task CheckRunsVersionAndAuthStatus()
    {
        var fake = new FakeCliRunner(argv => string.Join(" ", argv.Skip(1)) switch
        {
            "--version" => new CliRunResult(0, "2.1.278 (Claude Code)\n", ""),
            "auth status" => new CliRunResult(0,
                """{"loggedIn": true, "authMethod": "claude.ai", "email": "a@b.test", "subscriptionType": "max"}""", ""),
            _ => throw new InvalidOperationException($"unexpected {string.Join(" ", argv)}"),
        });
        var pipeline = new NotesPipeline(cliClient: Client(fake, CliLocator()));
        var installation = await pipeline.CheckInstallationAsync(ClaudeConfiguration());
        Assert.Equal(new CliInstallation(CliTool.ClaudeCode, ClaudePath, "2.1.278 (Claude Code)",
            "Logged in via claude.ai (max plan)", true)
        {
            LocalizedLoginStatus = new LocalizedMessage("Logged in via %@", "claude.ai")
                .Appending(" ", new LocalizedMessage("(%@ plan)", "max")),
        }, installation);
        Assert.Equal([[ClaudePath, "--version"], [ClaudePath, "auth", "status"]], fake.Calls.Select(call => call.Argv.ToArray()));
    }

    [Fact]
    public void AuthStatusLoggedOut()
    {
        var (text, loggedIn) = CliClient.LoginStatus(CliTool.ClaudeCode,
            new CliRunResult(1, """{"loggedIn": false, "authMethod": "none"}""", ""));
        Assert.False(loggedIn);
        Assert.Equal("Not logged in. Run `claude` once in Terminal to log in.", text);
    }
}

public sealed class CodexCliTests
{
    private static CliLocator CliLocator() => Locator([ClaudePath, CodexPath]);

    private static AIProviderConfiguration CodexConfiguration(string model = "gpt-6-luna", string? effort = "max") =>
        AIProviderConfiguration.FromPreset(ProviderPreset.CodexCli) with
        {
            Model = model,
            ReasoningEffort = effort,
            CodexPath = CodexPath,
        };

    // MARK: - Preset

    [Fact]
    public void PresetShape()
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

    // MARK: - argv

    [Fact]
    public void ArgvForNamedModel()
    {
        Assert.Equal(
            [
                CodexPath, "exec", "--sandbox", "read-only", "--cd", @"C:\t\w",
                "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules",
                "--color", "never", "--output-last-message", @"C:\t\w\last-message.txt",
                "--model", "gpt-6-astra", "-c", "model_reasoning_effort=\"high\"",
                "--", "PROMPT",
            ],
            CliArguments.Codex(CodexPath, "PROMPT", "gpt-6-astra", "high", @"C:\t\w", @"C:\t\w\last-message.txt"));
    }

    [Fact]
    public void ArgvForPresetDefaults()
    {
        var configuration = AIProviderConfiguration.FromPreset(ProviderPreset.CodexCli);
        var argv = CliArguments.Codex(CodexPath, "P", configuration.Model, configuration.ReasoningEffort, @"C:\w", @"C:\w\o");
        Assert.Equal("gpt-6-luna", ValueAfter("--model", argv));
        Assert.Equal("model_reasoning_effort=\"max\"", ValueAfter("-c", argv));
        Assert.DoesNotContain(argv, argument => argument.Contains("temperature", StringComparison.Ordinal));
        Assert.Equal(["--", "P"], argv.TakeLast(2));
    }

    /// <summary>An empty model omits <c>--model</c>, so the CLI's own default applies.</summary>
    [Fact]
    public void ArgvForEmptyModel()
    {
        var argv = CliArguments.Codex(CodexPath, "P", " ", null, @"C:\w", @"C:\w\o");
        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("-c", argv);
    }

    [Fact]
    public void EffortMapping()
    {
        foreach (var effort in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" })
        {
            Assert.Equal(effort, CliArguments.CodexEffort(effort));
        }
        Assert.Equal("high", CliArguments.CodexEffort(" High "));
        Assert.Null(CliArguments.CodexEffort(""));
        Assert.Null(CliArguments.CodexEffort(null));
        Assert.Null(CliArguments.CodexEffort("x\" = 1"));
        Assert.DoesNotContain("-c", CliArguments.Codex(CodexPath, "P", "", "turbo", @"C:\w", @"C:\w\o"));
    }

    [Fact]
    public void EnvironmentDropsApiKeys()
    {
        var environment = CliClient.Environment(CliTool.Codex, CodexPath, new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows",
            ["OPENAI_API_KEY"] = "sk",
            ["CODEX_API_KEY"] = "k",
            ["CODEX_HOME"] = @"C:\c",
        });
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PATH"] = @"C:\Users\test\AppData\Roaming\npm;C:\Windows",
                ["CODEX_HOME"] = @"C:\c",
            },
            environment);
    }

    // MARK: - Reply file

    [Fact]
    public async Task ReplyIsReadFromTheOutputFileNotStdout()
    {
        var fake = new FakeCliRunner((argv, directory) =>
        {
            var file = ValueAfter("--output-last-message", argv);
            Assert.Equal(Path.Combine(directory, "last-message.txt"), file);
            Assert.Equal(directory, ValueAfter("--cd", argv));
            File.WriteAllText(file ?? throw new InvalidOperationException("no reply file"), LaunchNotes);
            return new CliRunResult(0, "codex\nsomething else\ntokens used\n1,234\n", "banner");
        });
        var pipeline = new NotesPipeline(cliClient: Client(fake, CliLocator()));
        var response = await pipeline.GenerateAsync(CopilotSrt, "en", PromptTemplate.GeneralMeeting, CodexConfiguration(), "ignored");
        Assert.Equal("Product Launch Plan", response.Filename);
        var call = Assert.Single(fake.Calls);
        // Windows: `-- -` on the command line, the prompt on stdin.
        Assert.Equal(["--", "-"], call.Argv.TakeLast(2));
        Assert.Equal(MeetingPrompt.Build(CopilotSrt.Trim(), "en"), call.StandardInput);
        Assert.StartsWith("Hearsay-codex-", Path.GetFileName(call.Directory), StringComparison.Ordinal);
        Assert.False(Directory.Exists(call.Directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \n")]
    public async Task MissingOrEmptyReplyFileIsEmptyOutputEvenWithStdout(string? contents)
    {
        var fake = new FakeCliRunner((argv, _) =>
        {
            if (contents is not null && ValueAfter("--output-last-message", argv) is { } file)
            {
                File.WriteAllText(file, contents);
            }
            return new CliRunResult(0, LaunchNotes, "");
        });
        Assert.Equal(new CliProviderError.EmptyOutput(CliTool.Codex),
            await CliErrorAsync(() => Client(fake, CliLocator()).CompleteAsync("s", "u", CodexConfiguration(), null)));
    }

    // MARK: - Errors

    /// <summary>
    /// Without a login every request ends in "401 Unauthorized"; stderr also
    /// carries the banner and "tokens used", which must not count as a hint.
    /// </summary>
    [Fact]
    public async Task UnauthorizedMapsToNotLoggedInWithTheLastErrorLine()
    {
        const string stderr = "OpenAI Codex v0.156.0\r\n--------\r\nmodel: gpt-6-astra\r\n--------\r\nuser\r\nprompt\r\n"
            + "ERROR: Reconnecting... 5/5\r\n"
            + "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header";
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", stderr));
        var error = await CliErrorAsync(() => Client(fake, CliLocator()).CompleteAsync("s", "u", CodexConfiguration(), null));
        Assert.Equal(new CliProviderError.NotLoggedIn(CliTool.Codex,
            "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header"), error);
        Assert.Contains("Run `codex login` once in Terminal", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherFailureKeepsTheLastErrorLine()
    {
        const string stderr = "OpenAI Codex v0.156.0\ntokens used\nERROR: model 'nope' does not exist\n";
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", stderr));
        Assert.Equal(new CliProviderError.Failed(CliTool.Codex, 1, "ERROR: model 'nope' does not exist"),
            await CliErrorAsync(() => Client(fake, CliLocator()).CompleteAsync("s", "u", CodexConfiguration(), null)));
        var noErrorLine = CliClient.Excerpt(CliTool.Codex, new CliRunResult(1, "", new string('z', 600) + "END"));
        Assert.Equal(500, noErrorLine.Length);
        Assert.EndsWith("END", noErrorLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutAndNotInstalled()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(15, "", "", TimedOut: true));
        var client = Client(fake, CliLocator());
        Assert.Equal(new CliProviderError.TimedOut(CliTool.Codex),
            await CliErrorAsync(() => client.CompleteAsync("s", "u", CodexConfiguration(), null)));
        Assert.Equal(new CliProviderError.NotInstalled(CliTool.Codex, [@"C:\nowhere\codex.cmd"]),
            await CliErrorAsync(() => client.LocateAsync(CliTool.Codex, @"C:\nowhere\codex.cmd")));
        Assert.Contains("npm install -g @openai/codex",
            new CliProviderError.NotInstalled(CliTool.Codex, []).Description, StringComparison.Ordinal);
    }

    // MARK: - Check

    [Fact]
    public async Task CheckRunsVersionAndLoginStatus()
    {
        var fake = new FakeCliRunner(argv => string.Join(" ", argv.Skip(1)) switch
        {
            "--version" => new CliRunResult(0, "codex-cli 0.156.0\n", ""),
            "login status" => new CliRunResult(0, "", "Logged in using ChatGPT\n"),
            _ => new CliRunResult(1, "", ""),
        });
        var pipeline = new NotesPipeline(cliClient: Client(fake, CliLocator()));
        var installation = await pipeline.CheckInstallationAsync(CodexConfiguration());
        Assert.Equal(new CliInstallation(CliTool.Codex, CodexPath, "codex-cli 0.156.0", "Logged in using ChatGPT", true),
            installation);
        Assert.Equal([[CodexPath, "--version"], [CodexPath, "login", "status"]], fake.Calls.Select(call => call.Argv.ToArray()));

        var (text, loggedIn) = CliClient.LoginStatus(CliTool.Codex, new CliRunResult(1, "", "Not logged in\n"));
        Assert.False(loggedIn);
        Assert.Equal("Not logged in. Run `codex login` once in Terminal to log in.", text);
    }
}

public sealed class CliRoutingTests
{
    [Fact]
    public async Task EveryCliPresetGoesToTheCliClientAndHttpPresetsDoNot()
    {
        var fake = new FakeCliRunner((argv, directory) =>
        {
            if (argv.Contains("--json-schema"))
            {
                return new CliRunResult(0, AntigravityEnvelope(LaunchNotes), "");
            }
            if (argv.Contains("exec"))
            {
                File.WriteAllText(Path.Combine(directory, CliArguments.CodexReplyFileName), LaunchNotes);
                return new CliRunResult(0, "", "");
            }
            return new CliRunResult(0, LaunchNotes, "");
        });
        var http = new RecordingChatClient();
        var pipeline = new NotesPipeline(http,
            Client(fake, Locator([CopilotPath, ClaudePath, CodexPath, AgyPath]), FakeHousekeeping(), KnownAgyVersion()));
        AIProviderConfiguration[] configurations =
        [
            AIProviderConfiguration.FromPreset(ProviderPreset.CopilotCli) with { CopilotPath = CopilotPath },
            AIProviderConfiguration.FromPreset(ProviderPreset.ClaudeCodeCli) with { ClaudeCodePath = ClaudePath },
            AIProviderConfiguration.FromPreset(ProviderPreset.CodexCli) with { CodexPath = CodexPath },
            AIProviderConfiguration.FromPreset(ProviderPreset.AntigravityCli) with { AntigravityPath = AgyPath },
        ];
        foreach (var configuration in configurations)
        {
            Assert.IsType<CliClient>(pipeline.ClientFor(configuration));
            _ = await pipeline.GenerateAsync(CopilotSrt, "en", PromptTemplate.GeneralMeeting, configuration, "t");
        }
        Assert.Equal([CopilotPath, ClaudePath, CodexPath, AgyPath], fake.Calls.Select(call => call.Argv[0]));
        Assert.Equal(0, http.Count);

        foreach (var preset in new[] { ProviderPreset.Ollama, ProviderPreset.Custom })
        {
            Assert.IsType<RecordingChatClient>(pipeline.ClientFor(AIProviderConfiguration.FromPreset(preset)));
        }
        Assert.Equal(new CliProviderError.NotACliPreset("Custom"),
            await CliErrorAsync(() => pipeline.CheckInstallationAsync(AIProviderConfiguration.FromPreset(ProviderPreset.Custom))));
    }
}
