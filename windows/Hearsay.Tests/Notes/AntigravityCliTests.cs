using System.Text.Json.Nodes;
using Hearsay.Core.Notes;
using static Hearsay.Tests.Notes.CliFixtures;

namespace Hearsay.Tests.Notes;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/AntigravityCLITests.swift,
/// one test per Swift test. agy's prompt stays in <c>--print=</c> on
/// Windows, as on the Mac.
/// </summary>
public sealed class AntigravityCliTests
{
    private const string AppData = @"C:\Users\test\.gemini\antigravity-cli";

    private static AIProviderConfiguration AgyConfiguration(string model = "gemini-3.8-flash-high", string? effort = "high") =>
        AIProviderConfiguration.FromPreset(ProviderPreset.AntigravityCli) with
        {
            Model = model,
            ReasoningEffort = effort,
            AntigravityPath = AgyPath,
        };

    private static CliClient AgyClient(FakeCliRunner fake, FakeAntigravityFileSystem? fileSystem = null) =>
        Client(fake, Locator([AgyPath]), FakeHousekeeping(fileSystem), KnownAgyVersion());

    // MARK: - Preset

    [Fact]
    public void PresetShape()
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
        Assert.Null(AIProviderConfiguration.FromPreset(preset).Temperature);
        Assert.Null(AIProviderConfiguration.FromPreset(preset).EffectiveTemperature);
        Assert.Equal("agy", CliTool.Antigravity.BinaryName());
        Assert.Equal([CliTool.Copilot, CliTool.ClaudeCode, CliTool.Codex, CliTool.Antigravity], CliTools.All);
    }

    // MARK: - argv

    [Fact]
    public void ArgvForNamedModelWithoutAnEffortSuffix()
    {
        Assert.Equal(
            [
                AgyPath, "--output-format", "json", "--json-schema", @"C:\t\reply-schema.json",
                "--disable-slash-commands", "--sandbox", "--print-timeout", "570s",
                "--log-file", @"C:\t\agy.log", "--project", "hearsay-notes",
                "--model", "gemini-3.1-pro", "--effort", "medium",
                "--print=PROMPT",
            ],
            CliArguments.Antigravity(AgyPath, "PROMPT", "gemini-3.1-pro", "medium", @"C:\t\reply-schema.json", @"C:\t\agy.log"));
    }

    /// <summary>
    /// The preset default names its effort in the id, so <c>--effort</c> is
    /// left out (agy rejects a different one and needs none for the same one).
    /// </summary>
    [Fact]
    public void ArgvForPresetDefaults()
    {
        var configuration = AIProviderConfiguration.FromPreset(ProviderPreset.AntigravityCli);
        var argv = CliArguments.Antigravity(AgyPath, "P", configuration.Model, configuration.ReasoningEffort,
            @"C:\t\s.json", @"C:\t\l.log");
        Assert.Equal(
            [
                AgyPath, "--output-format", "json", "--json-schema", @"C:\t\s.json",
                "--disable-slash-commands", "--sandbox", "--print-timeout", "570s",
                "--log-file", @"C:\t\l.log", "--project", "hearsay-notes",
                "--model", "gemini-3.8-flash-high",
                "--print=P",
            ],
            argv);
        Assert.DoesNotContain(argv, argument => argument.Contains("temperature", StringComparison.Ordinal));
        Assert.DoesNotContain("--dangerously-skip-permissions", argv);
        Assert.DoesNotContain("--", argv);
        Assert.Equal((argv, (string?)null), CliArguments.WindowsInvocation(CliTool.Antigravity, argv));
    }

    /// <summary>
    /// agy takes the argument after <c>--print</c> as its value and rejects a
    /// prompt after <c>--</c>, so the prompt is attached with <c>=</c>, dash or not.
    /// </summary>
    [Fact]
    public void PromptIsAttachedToPrint()
    {
        var argv = CliArguments.Antigravity(AgyPath, "-starts with a dash\n--output-format text", " ", " ", @"C:\s", @"C:\l");
        Assert.Equal("--print=-starts with a dash\n--output-format text", argv[^1]);
        Assert.DoesNotContain("--model", argv);
        Assert.DoesNotContain("--effort", argv);
    }

    /// <summary>
    /// agy has no system prompt option and acts as an agent, so the system
    /// message and a no-tools instruction go before the user message.
    /// </summary>
    [Fact]
    public void PromptCarriesSystemMessageAndNoToolsInstruction()
    {
        Assert.Equal("SYSTEM\n\n" + CliArguments.AntigravityInstructions + "\n\nUSER",
            CliArguments.AntigravityPrompt("SYSTEM", "USER"));
        Assert.Equal(CliArguments.AntigravityInstructions + "\n\nUSER", CliArguments.AntigravityPrompt(" \n", "USER"));
        Assert.Contains("Do not run commands", CliArguments.AntigravityInstructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none", "low")]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "max")]
    [InlineData("max", "max")]
    [InlineData(" MAX ", "max")]
    [InlineData("", null)]
    [InlineData("turbo", null)]
    public void EffortMapping(string input, string? output)
    {
        Assert.Equal(output, CliArguments.AntigravityEffort(input, "gemini-3.8-flash"));
        Assert.Equal(output, CliArguments.AntigravityEffort(input, ""));
    }

    [Fact]
    public void EffortIsOmittedForModelsThatNameTheirEffort()
    {
        Assert.Null(CliArguments.AntigravityEffort(null, "gemini-3.8-flash"));
        foreach (var model in new[] { "gemini-3.8-flash-high", "gemini-3.8-flash-medium", "gemini-3.1-pro-low", "gpt-oss-120b-medium", "X-HIGH" })
        {
            Assert.Null(CliArguments.AntigravityEffort("low", model));
        }
        Assert.Equal("high", CliArguments.AntigravityEffort("high", "claude-opus-4-6-thinking"));
    }

    [Fact]
    public void SchemaMatchesTheReplyShape()
    {
        var schema = Assert.IsType<JsonObject>(JsonNode.Parse(CliArguments.NotesReplySchema));
        Assert.Equal("object", (string?)schema["type"]);
        Assert.Equal(["filename", "markdown", "transcript_markdown"],
            schema["required"]?.AsArray().Select(item => (string?)item));
        Assert.False((bool?)schema["additionalProperties"]);
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        Assert.Equal(["filename", "markdown", "transcript_markdown"], properties.Select(pair => pair.Key));
        Assert.All(properties, pair => Assert.Equal("""{"type":"string"}""", pair.Value?.ToJsonString()));
        Assert.Equal(
            """{"type":"object","required":["filename","markdown","transcript_markdown"],"properties":{"filename":{"type":"string"},"markdown":{"type":"string"},"transcript_markdown":{"type":"string"}},"additionalProperties":false}""",
            CliArguments.NotesReplySchema);
    }

    [Fact]
    public void EnvironmentDropsGoogleKeys()
    {
        var environment = CliClient.Environment(CliTool.Antigravity, AgyPath, new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows",
            ["USERPROFILE"] = Home,
            ["GEMINI_API_KEY"] = "g",
            ["GOOGLE_API_KEY"] = "k",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = @"C:\c.json",
            ["GOOGLE_CLOUD_PROJECT"] = "p",
        });
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PATH"] = @"C:\Users\test\AppData\Local\agy\bin;C:\Windows",
                ["USERPROFILE"] = Home,
                ["GOOGLE_CLOUD_PROJECT"] = "p",
            },
            environment);
    }

    // MARK: - Runs

    [Fact]
    public async Task SuccessParsesStructuredOutputAndRunsInAnEmptyWorkingFolder()
    {
        const string agyResponse = """{"filename":"x","markdown":"m","toolAction":"Writing","toolSummary":"s","transcript_markdown":"t"}""";
        List<(string Workspace, bool Empty, string? Schema)> seen = [];
        var inspecting = new FakeCliRunner((argv, directory) =>
        {
            var root = Path.GetDirectoryName(directory) ?? string.Empty;
            var schemaFile = ValueAfter("--json-schema", argv);
            Assert.Equal(Path.Combine(root, "reply-schema.json"), schemaFile);
            Assert.Equal(Path.Combine(root, "agy.log"), ValueAfter("--log-file", argv));
            seen.Add((Path.GetFileName(directory), Directory.GetFileSystemEntries(directory).Length == 0,
                schemaFile is null ? null : File.ReadAllText(schemaFile)));
            return new CliRunResult(0, AntigravityEnvelope(agyResponse, LaunchNotes), "");
        });
        var pipeline = new NotesPipeline(cliClient: AgyClient(inspecting));
        var response = await pipeline.GenerateAsync("\n" + CopilotSrt + "\n", "en", PromptTemplate.GeneralMeeting,
            AgyConfiguration(), "ignored");
        Assert.Equal(new NotesResponse("Product Launch Plan", "# Notes\n", "# Transcript\n\n## 00:00 — Launch\n"), response);
        Assert.Equal([("hearsay-notes", true, (string?)CliArguments.NotesReplySchema)], seen);

        var call = Assert.Single(inspecting.Calls);
        var root = Path.GetDirectoryName(call.Directory) ?? string.Empty;
        var prompt = MeetingPrompt.Build(CopilotSrt.Trim(), "en");
        Assert.Equal(
            CliArguments.Antigravity(AgyPath,
                MeetingPrompt.SystemMessage + "\n\n" + CliArguments.AntigravityInstructions + "\n\n" + prompt,
                "gemini-3.8-flash-high", "high",
                Path.Combine(root, "reply-schema.json"), Path.Combine(root, "agy.log")),
            call.Argv);
        Assert.Null(call.StandardInput);
        Assert.DoesNotContain("ignored", call.Argv);
        Assert.Equal(TimeSpan.FromSeconds(600), call.Timeout);
        Assert.StartsWith("Hearsay-antigravity-", Path.GetFileName(root), StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    /// <summary>Before agy starts, the project with the deny rules exists; after it ends, that run's conversation is deleted.</summary>
    [Fact]
    public async Task RunEnsuresTheProjectAndDeletesItsConversation()
    {
        const string id = "c0f98fe5-eaac-452a-9332-470822a1d3ed";
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\conversations\{id}.db");
        files.Add($@"{AppData}\brain\{id}\.system_generated\logs\transcript.jsonl");
        files.Add($@"{AppData}\conversations\0cb76a4c-28ef-4515-99ad-6f6ee04c3723.db");
        var projectInPlace = false;
        var fake = new FakeCliRunner(_ =>
        {
            // The project file is in place by the time agy runs.
            projectInPlace = files.Paths.Any(path => path.StartsWith(@"C:\Users\test\.gemini\config\projects\", StringComparison.Ordinal));
            return new CliRunResult(0, AntigravityEnvelope(LaunchNotes).Replace("6cfd8496-ae02-41ac-afc4-1460ec1eaf0c", id, StringComparison.Ordinal), "");
        });
        var reply = await AgyClient(fake, files).CompleteAsync("s", "u", AgyConfiguration(), null);
        Assert.True(projectInPlace);
        Assert.Equal("Product Launch Plan", NotesResponse.Parse(reply).Filename);
        Assert.DoesNotContain(files.Paths, path => path.Contains(id, StringComparison.Ordinal));
        Assert.Contains($@"{AppData}\conversations\0cb76a4c-28ef-4515-99ad-6f6ee04c3723.db", files.Paths);
    }

    /// <summary>A failed run with no conversation id deletes nothing; a failed run with one still deletes it.</summary>
    [Fact]
    public async Task FailedRunsDeleteOnlyWhenThereIsAnId()
    {
        const string id = "a4955120-504f-425d-beda-a27a3123f471";
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\conversations\{id}.db");
        var noId = new FakeCliRunner(_ => new CliRunResult(1, "", "error: invalid model selection"));
        await CliErrorAsync(() => AgyClient(noId, files).CompleteAsync("s", "u", AgyConfiguration(), null));
        Assert.Empty(files.Removed);
        var withId = new FakeCliRunner(_ =>
            new CliRunResult(3, $$"""{"conversation_id":"{{id}}","status":"ERROR","response":"partial"}""", "AGY_ERROR x"));
        await CliErrorAsync(() => AgyClient(withId, files).CompleteAsync("s", "u", AgyConfiguration(), null));
        Assert.Equal([$@"{AppData}\conversations\{id}.db"], files.Removed);
    }

    /// <summary>Without the deny rules agy must not start.</summary>
    [Fact]
    public async Task ProjectWriteFailureStopsTheRun()
    {
        var files = new FakeAntigravityFileSystem { FailWrites = true };
        var fake = new FakeCliRunner(_ => new CliRunResult(0, LaunchNotes, ""));
        var error = await CliErrorAsync(() => AgyClient(fake, files).CompleteAsync("s", "u", AgyConfiguration(), null));
        var launch = Assert.IsType<CliProviderError.LaunchFailed>(error);
        Assert.Equal(CliTool.Antigravity, launch.Tool);
        Assert.Contains(@"hearsay-notes project in C:\Users\test\.gemini\config\projects", launch.Detail, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    /// <summary>Without <c>structured_output</c>, <c>response</c> is parsed; stdout that is not the JSON envelope is used as is.</summary>
    [Fact]
    public void ReplyFallsBackToResponseThenStdout()
    {
        var fromResponse = CliClient.AntigravityReply(new CliRunResult(0, AntigravityEnvelope("```json\n" + LaunchNotes + "\n```"), ""));
        Assert.Equal("Product Launch Plan", NotesResponse.Parse(fromResponse).Filename);
        Assert.Equal(LaunchNotes, CliClient.AntigravityReply(new CliRunResult(0, LaunchNotes, "")));
        var structured = CliClient.AntigravityReply(new CliRunResult(0, AntigravityEnvelope("junk", LaunchNotes), ""));
        Assert.Equal("# Notes\n", NotesResponse.Parse(structured).Markdown);
        Assert.StartsWith("""{"filename":""", structured, StringComparison.Ordinal);
    }

    // MARK: - Errors

    /// <summary>
    /// A tool call that needs a permission is auto-denied in print mode: exit
    /// 0, an empty <c>response</c>, and the reason on stderr.
    /// </summary>
    [Fact]
    public async Task DeniedToolAndEmptyReplyIsEmptyOutput()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(0, AntigravityEnvelope(""),
            "jetski: no output produced — a tool required the \"command\" permission that headless mode cannot prompt for, so it was auto-denied.\n"));
        Assert.Equal(new CliProviderError.EmptyOutput(CliTool.Antigravity),
            await CliErrorAsync(() => AgyClient(fake).CompleteAsync("s", "u", AgyConfiguration(), null)));
        Assert.Equal("Antigravity CLI returned empty output.", new CliProviderError.EmptyOutput(CliTool.Antigravity).Description);
    }

    [Fact]
    public async Task NotLoggedInIsDetectedWithoutShowingTheLoginUrl()
    {
        const string stderr = "Authentication required. Please visit the URL to log in:\n"
            + "  https://accounts.google.com/o/oauth2/auth?access_type=offline&client_id=x\n\n"
            + "Waiting for authentication (timeout 60s)...\n"
            + "Or, paste the authorization code here and press Enter:\n"
            + "Error: authentication timed out.\n"
            + "error: authentication failed or timed out";
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", stderr));
        var error = await CliErrorAsync(() => AgyClient(fake).CompleteAsync("s", "u", AgyConfiguration(), null));
        Assert.Equal(new CliProviderError.NotLoggedIn(CliTool.Antigravity, "error: authentication failed or timed out"), error);
        Assert.Contains("Antigravity CLI is not logged in. Run `agy` once in Terminal", error.Description, StringComparison.Ordinal);
        Assert.Contains("Google account", error.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("accounts.google.com", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonzeroExitKeepsTheLastErrorLine()
    {
        const string stderr = "error: invalid model selection (--model \"gemini-3.8-flash-high\" --effort \"low\"): --model gemini-3.8-flash-high conflicts with --effort=low\n";
        var fake = new FakeCliRunner(_ => new CliRunResult(1, "", stderr));
        Assert.Equal(new CliProviderError.Failed(CliTool.Antigravity, 1,
                "error: invalid model selection (--model \"gemini-3.8-flash-high\" --effort \"low\"): --model gemini-3.8-flash-high conflicts with --effort=low"),
            await CliErrorAsync(() => AgyClient(fake).CompleteAsync("s", "u", AgyConfiguration(), null)));
        var message = new CliProviderError.Failed(CliTool.Antigravity, 3, "AGY_ERROR quota").Description;
        Assert.Contains("Antigravity CLI call failed (exit code 3)", message, StringComparison.Ordinal);
        Assert.Contains("AGY_ERROR quota", message, StringComparison.Ordinal);
        var noErrorLine = CliClient.Excerpt(CliTool.Antigravity, new CliRunResult(2, "", new string('z', 600) + "END\n"));
        Assert.Equal(500, noErrorLine.Length);
        Assert.EndsWith("END", noErrorLine, StringComparison.Ordinal);
        Assert.Equal("out", CliClient.Excerpt(CliTool.Antigravity, new CliRunResult(2, "out", " \n")));
    }

    [Fact]
    public void FailedStatusInTheEnvelopeFails()
    {
        var envelope = AntigravityEnvelope("partial", status: "ERROR")
            .Replace("\"status\"", "\"error\":\"model overloaded\",\"status\"", StringComparison.Ordinal);
        var error = Assert.Throws<CliProviderException>(() => CliClient.AntigravityReply(new CliRunResult(0, envelope, "")));
        Assert.Equal(new CliProviderError.Failed(CliTool.Antigravity, 0, "model overloaded"), error.Error);
    }

    [Fact]
    public async Task TimeoutAndNotInstalled()
    {
        var fake = new FakeCliRunner(_ => new CliRunResult(15, "", "", TimedOut: true));
        Assert.Equal(new CliProviderError.TimedOut(CliTool.Antigravity),
            await CliErrorAsync(() => AgyClient(fake).CompleteAsync("s", "u", AgyConfiguration(), null)));
        Assert.Equal("Antigravity CLI did not answer within 10 minutes and was stopped.",
            new CliProviderError.TimedOut(CliTool.Antigravity).Description);

        var missing = new FakeCliRunner(_ => new CliRunResult(1, "", ""));
        var client = Client(missing, Locator([]), FakeHousekeeping(), new CliVersionCache());
        var error = await CliErrorAsync(() => client.CompleteAsync("s", "u", AgyConfiguration() with { AntigravityPath = null }, null));
        var notInstalled = Assert.IsType<CliProviderError.NotInstalled>(error);
        Assert.Equal(CliTool.Antigravity, notInstalled.Tool);
        Assert.Equal(@"C:\Users\test\.local\bin\agy.exe", notInstalled.Searched[0]);
        Assert.Contains(@"C:\Users\test\AppData\Local\agy\bin\agy.exe", notInstalled.Searched);
        Assert.Equal("PATH (agy.exe, agy.cmd, agy.bat)", notInstalled.Searched[^1]);
        Assert.Equal(
            "Antigravity CLI not found. Install it with `curl -fsSL https://antigravity.google/cli/install.cmd -o install.cmd && install.cmd && del install.cmd` or set the path in Settings > AI.",
            error.Description);
    }

    // MARK: - Check

    [Fact]
    public async Task CheckRunsVersionAndModels()
    {
        var fake = new FakeCliRunner(argv => (argv.Count, argv[^1]) switch
        {
            (2, "--version") => new CliRunResult(0, "1.2.12\n", ""),
            (4, "models") => new CliRunResult(0,
                "Fetching available models...\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\ngemini-3.1-pro-low\tGemini 3.1 Pro (Low)\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\n",
                ""),
            _ => throw new InvalidOperationException($"unexpected {string.Join(" ", argv)}"),
        });
        var pipeline = new NotesPipeline(cliClient: AgyClient(fake));
        var installation = await pipeline.CheckInstallationAsync(AgyConfiguration());
        Assert.Equal(new CliInstallation(CliTool.Antigravity, AgyPath, "1.2.12", "Logged in; 3 models available", true), installation);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal([AgyPath, "--version"], fake.Calls[0].Argv);
        var models = fake.Calls[^1];
        Assert.Equal([AgyPath, "--log-file", Path.Combine(models.Directory, "agy.log"), "models"], models.Argv);
        Assert.Equal([TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20)], fake.Calls.Select(call => call.Timeout));
    }

    [Fact]
    public void ModelsCheckWhenLoggedOutOrFailing()
    {
        var loggedOut = CliClient.LoginStatus(CliTool.Antigravity, new CliRunResult(1,
            "Fetching available models...\nError: Please sign in to view available models. Launch the CLI without arguments to sign in.\n", ""));
        Assert.False(loggedOut.LoggedIn);
        Assert.Equal("Not logged in. Run `agy` once in Terminal to log in.", loggedOut.Text);
        var failing = CliClient.LoginStatus(CliTool.Antigravity, new CliRunResult(4, "", "network down"));
        Assert.False(failing.LoggedIn);
        Assert.Equal("`agy models` failed (exit code 4). Run `agy` once in Terminal to log in.", failing.Text);
        Assert.Equal(("The login status check did not answer.", false),
            CliClient.LoginStatus(CliTool.Antigravity, new CliRunResult(15, "", "", TimedOut: true)));
        Assert.Equal("Logged in; 1 model available",
            CliClient.LoginStatus(CliTool.Antigravity, new CliRunResult(0, "gemini-3.8-flash-high\tG\r\n", "")).Text);
    }
}
