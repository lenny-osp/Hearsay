using System.Net;
using System.Text.Json.Nodes;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Tests.Naming;
using Hearsay.Tests.Settings;

namespace Hearsay.Tests.Notes;

// Port of mac/HearsayCore/Tests/HearsayCoreTests/NotesPipelineTests.swift,
// one test per Swift test and one class per Swift suite (a parameterized
// Swift test is a theory). The Swift InMemorySecretStoreTests are covered by
// Settings/SecretStoreTests.cs. Windows-only additions are at the end of
// NotesPipelineTests.

/// <summary>The Swift file-level helpers of NotesPipelineTests.swift.</summary>
internal static class ChatFixtures
{
    public static readonly FakeChatHandler Handler = new();

    public const string Srt = "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n";

    public const string GoodNotes =
        """{"filename": "Quarterly Planning", "markdown": "# API Notes", "transcript_markdown": "# Transcript\n\nDiscuss launch"}""";

    public static string UniqueUrl() => $"https://{Guid.NewGuid():D}.example.test/chat/completions";

    public static string Completion(string content) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }
            .ToJsonString();

    public static AIProviderConfiguration Configuration(string url, AuthHeaderStyle auth = AuthHeaderStyle.Bearer,
        string? reasoningEffort = "max", string presetId = "custom", IReadOnlyDictionary<string, string>? extraHeaders = null) =>
        new(presetId: presetId, baseUrl: url, model: "test-model", reasoningEffort: reasoningEffort, auth: auth,
            extraHeaders: extraHeaders);

    public static ChatCompletionsClient Client() => new(new ForwardingHandler(Handler));

    public static NotesPipeline Pipeline() => new(Client(), CliFixtures.Client(new FakeCliRunner(_ => new CliRunResult(1, "", ""))));

    public static JsonObject Payload(byte[] body) =>
        JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("payload is not an object");

    public static JsonObject Payload(HttpRequestMessage request) =>
        Payload(request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? []);

    public static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values)
        : request.Content?.Headers.TryGetValues(name, out var contentValues) == true ? string.Join(",", contentValues)
        : null;

    /// <summary>Lets every client share <see cref="Handler"/> without disposing it.</summary>
    private sealed class ForwardingHandler(FakeChatHandler inner) : DelegatingHandler(inner)
    {
        // The shared handler outlives each client: never dispose the inner one.
        protected override void Dispose(bool disposing) => base.Dispose(false);
    }
}

public sealed class NotesPipelineTests : IDisposable
{
    private readonly TemporaryDirectory scratch = new("NotesPipelineTests");

    public void Dispose() => scratch.Dispose();

    /// <summary>
    /// Mirrors <c>test_api_success_payload_and_token_precedence</c> (payload
    /// shape): model, the system and user messages, temperature 0.3,
    /// reasoning_effort, the 300 s timeout, and the Bearer header.
    /// </summary>
    [Fact]
    public async Task SuccessPayloadShapeAndBearerHeader()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion(ChatFixtures.GoodNotes));

        var result = await ChatFixtures.Pipeline().GenerateAsync(ChatFixtures.Srt, "zh-TW", PromptTemplate.GeneralMeeting,
            ChatFixtures.Configuration(url), "ai-token");
        Assert.Equal(new NotesResponse("Quarterly Planning", "# API Notes", "# Transcript\n\nDiscuss launch"), result);

        var recorded = ChatFixtures.Handler.Requests(url);
        var single = Assert.Single(recorded);
        Assert.Equal(HttpMethod.Post, single.Request.Method);
        using (var client = ChatFixtures.Client())
        {
            Assert.Equal(TimeSpan.FromSeconds(300), client.RequestTimeout);
        }
        Assert.Equal("Bearer ai-token", ChatFixtures.Header(single.Request, "Authorization"));
        Assert.Equal("application/json", single.ContentType);
        Assert.Null(ChatFixtures.Header(single.Request, "api-key"));

        var payload = ChatFixtures.Payload(single.Body);
        Assert.Equal(["messages", "model", "reasoning_effort", "temperature"], payload.Select(pair => pair.Key).Order());
        Assert.Equal("test-model", (string?)payload["model"]);
        Assert.Equal(0.3, (double?)payload["temperature"]);
        Assert.Equal("max", (string?)payload["reasoning_effort"]);
        var messages = Assert.IsType<JsonArray>(payload["messages"]);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", (string?)messages[0]?["role"]);
        Assert.Equal("You are a professional and efficient meeting-note assistant.", (string?)messages[0]?["content"]);
        Assert.Equal(2, messages[0]?.AsObject().Count);
        Assert.Equal("user", (string?)messages[1]?["role"]);
        var user = (string?)messages[1]?["content"] ?? string.Empty;
        Assert.Equal(1, user.Split("Discuss launch").Length - 1);
        Assert.Contains("Write the entire meeting note in Traditional Chinese", user, StringComparison.Ordinal);
        Assert.Equal(MeetingPrompt.Build(ChatFixtures.Srt, "zh-TW"), user);
    }

    [Fact]
    public void ReasoningEffortIsAbsentWhenNilOrEmptyOrUnsupported()
    {
        foreach (var config in new[]
        {
            ChatFixtures.Configuration(ChatFixtures.UniqueUrl(), reasoningEffort: null),
            ChatFixtures.Configuration(ChatFixtures.UniqueUrl(), reasoningEffort: "  "),
            ChatFixtures.Configuration(ChatFixtures.UniqueUrl(), reasoningEffort: "max", presetId: "ollama"),
        })
        {
            using var request = ChatCompletionsClient.MakeRequest("s", "u", config, "t");
            Assert.Equal(["messages", "model", "temperature"], ChatFixtures.Payload(request).Select(pair => pair.Key).Order());
        }
    }

    [Fact]
    public void TemperatureIsSentWhenSetAndAbsentWhenNil()
    {
        var config = ChatFixtures.Configuration(ChatFixtures.UniqueUrl()) with { Temperature = 0.7 };
        using (var set = ChatCompletionsClient.MakeRequest("s", "u", config, "t"))
        {
            Assert.Equal(0.7, (double?)ChatFixtures.Payload(set)["temperature"]);
        }
        config = config with { Temperature = null };
        using var omitted = ChatCompletionsClient.MakeRequest("s", "u", config, "t");
        Assert.Equal(["messages", "model", "reasoning_effort"], ChatFixtures.Payload(omitted).Select(pair => pair.Key).Order());
    }

    [Fact]
    public void TemperatureNilSurvivesAReloadAndMissingKeyMeansDefault()
    {
        var config = AIProviderConfiguration.FromPreset(ProviderPreset.Custom) with { Temperature = null };
        var decoded = AIProviderConfiguration.FromJson(JsonNode.Parse(config.ToJson().ToJsonString()));
        Assert.NotNull(decoded);
        Assert.Null(decoded.Temperature);
        Assert.Equal(config, decoded);

        const string legacy = """{"presetID":"openai","baseURL":"https://api.openai.com/v1/chat/completions","model":"m","auth":"bearer","extraHeaders":{},"askBeforeSending":true,"selectedTemplateID":"6E0B5A10-3C2D-4F51-9A7E-000000000001"}""";
        var old = AIProviderConfiguration.FromJson(JsonNode.Parse(legacy));
        Assert.NotNull(old);
        Assert.Equal(0.3, old.Temperature);
        Assert.Null(old.ReasoningEffort);
        Assert.Equal(PromptTemplate.GeneralMeetingId, old.SelectedTemplateId);
    }

    [Fact]
    public void AuthHeaderPerStyleAndExtraHeaders()
    {
        using (var apiKey = ChatCompletionsClient.MakeRequest("s", "u",
                   ChatFixtures.Configuration(ChatFixtures.UniqueUrl(), AuthHeaderStyle.ApiKey,
                       extraHeaders: new Dictionary<string, string> { ["X-Team"] = "notes" }),
                   "azure-key"))
        {
            Assert.Equal("azure-key", ChatFixtures.Header(apiKey, "api-key"));
            Assert.Null(ChatFixtures.Header(apiKey, "Authorization"));
            Assert.Equal("notes", ChatFixtures.Header(apiKey, "X-Team"));
        }

        using var none = ChatCompletionsClient.MakeRequest("s", "u",
            ChatFixtures.Configuration(ChatFixtures.UniqueUrl(), AuthHeaderStyle.None), null);
        Assert.Null(ChatFixtures.Header(none, "Authorization"));
        Assert.Null(ChatFixtures.Header(none, "api-key"));
        Assert.Equal("application/json", ChatFixtures.Header(none, "Content-Type"));
    }

    [Fact]
    public void MissingTokenAndInvalidUrl()
    {
        foreach (var token in new string?[] { null, "", "   " })
        {
            var error = Assert.Throws<ChatCompletionsException>(() => ChatCompletionsClient.MakeRequest("s", "u",
                ChatFixtures.Configuration(ChatFixtures.UniqueUrl(), AuthHeaderStyle.Bearer), token));
            Assert.Equal(new ChatCompletionsError.MissingToken(), error.Error);
        }
        foreach (var url in new[] { "", "not a url", "ftp://example.test/x", "file:///tmp/x" })
        {
            var error = Assert.Throws<ChatCompletionsException>(() => ChatCompletionsClient.MakeRequest("s", "u",
                ChatFixtures.Configuration(url, AuthHeaderStyle.None), null));
            Assert.Equal(new ChatCompletionsError.InvalidUrl(), error.Error);
        }
    }

    /// <summary>
    /// Mirrors <c>test_api_http_json_and_empty_content_failures_are_atomic</c>:
    /// HTTP error, non-JSON body, and empty content all fail.
    /// </summary>
    [Fact]
    public async Task HttpErrorKeepsAFiveHundredCharacterExcerpt()
    {
        var url = ChatFixtures.UniqueUrl();
        var longBody = "not json " + new string('x', 900);
        ChatFixtures.Handler.Register(url, 500, longBody);
        var error = await Assert.ThrowsAsync<ChatCompletionsException>(() => ChatFixtures.Pipeline().GenerateAsync(
            ChatFixtures.Srt, "en", PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.Equal(new ChatCompletionsError.HttpStatus(500, longBody[..500]), error.Error);
        Assert.StartsWith("API HTTP Error 500: not json xxx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpErrorMessagesReadLikePython()
    {
        Assert.Equal("API HTTP Error 401: Bad credentials",
            new ChatCompletionsError.HttpStatus(401, """{"error": {"message": "Bad credentials"}}""").Description);
        Assert.Equal("API HTTP Error 502: The response body is empty.",
            new ChatCompletionsError.HttpStatus(502, "  ").Description);
    }

    public static TheoryData<int, string, ChatCompletionsError> FailingBodies => new()
    {
        { 200, "{", new ChatCompletionsError.InvalidJson("{") },
        { 200, """{"choices":[{"message":{"content":"   "}}]}""", new ChatCompletionsError.EmptyContent() },
        { 200, """{"choices":[{"message":{}}]}""", new ChatCompletionsError.EmptyContent() },
        { 200, """{"choices":[]}""", new ChatCompletionsError.EmptyContent() },
        { 200, """{"error":{"message":"quota"}}""", new ChatCompletionsError.HttpStatus(200, """{"error":{"message":"quota"}}""") },
    };

    [Theory]
    [MemberData(nameof(FailingBodies))]
    public async Task NonJsonBodyAndEmptyContentFail(int status, string body, ChatCompletionsError expected)
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, status, body);
        var error = await Assert.ThrowsAsync<ChatCompletionsException>(() => ChatFixtures.Pipeline().GenerateAsync(
            ChatFixtures.Srt, "en", PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public async Task TransportFailureIsReported()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.RegisterFailure(url, new TaskCanceledException("The operation timed out."));
        using var client = ChatFixtures.Client();
        var error = await Assert.ThrowsAsync<ChatCompletionsException>(() =>
            client.CompleteAsync("s", "u", ChatFixtures.Configuration(url), "t"));
        var transport = Assert.IsType<ChatCompletionsError.Transport>(error.Error);
        Assert.NotEmpty(transport.Detail);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task UnreachableHostNamesTheHost(HttpRequestError code)
    {
        var host = $"{Guid.NewGuid():D}.example.test";
        var url = $"https://{host}/chat/completions";
        ChatFixtures.Handler.RegisterFailure(url, new HttpRequestException(code, "failed"));
        using var client = ChatFixtures.Client();
        var error = await Assert.ThrowsAsync<ChatCompletionsException>(() =>
            client.CompleteAsync("s", "u", ChatFixtures.Configuration(url), "t"));
        Assert.Equal(new ChatCompletionsError.Unreachable(host), error.Error);
        Assert.Equal($"Cannot reach {host}. Check the base URL in Settings > AI and your network connection.", error.Message);
    }

    [Fact]
    public async Task FencedJsonReplyParses()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion("```json\n" + ChatFixtures.GoodNotes + "\n```"));
        var result = await ChatFixtures.Pipeline().GenerateAsync(ChatFixtures.Srt, "en", PromptTemplate.GeneralMeeting,
            ChatFixtures.Configuration(url, AuthHeaderStyle.None), null);
        Assert.Equal("Quarterly Planning", result.Filename);
    }

    [Fact]
    public async Task InvalidNotesJsonSurfacesTheParseError()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion("""{"filename": "x"}"""));
        var error = await Assert.ThrowsAsync<NotesResponseException>(() => ChatFixtures.Pipeline().GenerateAsync(
            ChatFixtures.Srt, "en", PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.Equal(NotesResponseErrorKind.MissingMarkdown, error.Kind);
    }

    /// <summary>Python: "The SRT contains no transcript content to summarize." and no request is sent.</summary>
    [Fact]
    public async Task EmptyTranscriptNeverCallsTheProvider()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion(ChatFixtures.GoodNotes));
        var error = await Assert.ThrowsAsync<NotesPipelineException>(() => ChatFixtures.Pipeline().GenerateAsync(
            "1\n00:00:01,000 --> 00:00:02,000\n\n", "en", PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.Equal(new NotesPipelineError.EmptyTranscript(), error.Error);
        Assert.Equal("Error: The SRT contains no transcript content to summarize.", error.Message);
        Assert.Empty(ChatFixtures.Handler.Requests(url));
    }

    [Fact]
    public async Task UnsupportedLanguageNeverCallsTheProvider()
    {
        var url = ChatFixtures.UniqueUrl();
        var error = await Assert.ThrowsAsync<MeetingPromptException>(() => ChatFixtures.Pipeline().GenerateAsync(
            ChatFixtures.Srt, "fr", PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.Equal("fr", error.LanguageCode);
        Assert.Empty(ChatFixtures.Handler.Requests(url));
    }

    [Fact]
    public async Task CustomTemplateReplacesTheNotesSection()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion(ChatFixtures.GoodNotes));
        var template = new PromptTemplate("Standup", "Summarize the standup in {output_language}.");
        _ = await ChatFixtures.Pipeline().GenerateAsync(ChatFixtures.Srt, "en", template, ChatFixtures.Configuration(url), "t");
        var body = ChatFixtures.Handler.Requests(url)[0].Body;
        var user = (string?)ChatFixtures.Payload(body)["messages"]?[1]?["content"] ?? string.Empty;
        Assert.StartsWith("Summarize the standup in English.\n\n", user, StringComparison.Ordinal);
        Assert.Contains(MeetingPrompt.ResponseRules, user, StringComparison.Ordinal);
    }

    // Windows-only: the end-to-end file flow the Mac keeps in NotesFlowViewModel.

    [Fact]
    public async Task GenerateAndSaveWritesTheThreeFilesUnderTheAiName()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion(ChatFixtures.GoodNotes));
        var srtPath = Path.Combine(scratch.Url, "2026-09-29_10-15-00.srt");
        await File.WriteAllTextAsync(srtPath, ChatFixtures.Srt);
        await File.WriteAllBytesAsync(Path.Combine(scratch.Url, "2026-09-29_10-15-00.wav"), [1, 2, 3]);

        var outputs = await ChatFixtures.Pipeline().GenerateAndSaveAsync(srtPath, "en", PromptTemplate.GeneralMeeting,
            ChatFixtures.Configuration(url), "t");

        Assert.Equal(Path.Combine(scratch.Url, "2026-09-29_10-15-00_quarterly-planning.srt"), outputs.Srt);
        Assert.Equal(Path.Combine(scratch.Url, "2026-09-29_10-15-00_quarterly-planning.md"), outputs.Markdown);
        Assert.Equal(Path.Combine(scratch.Url, "2026-09-29_10-15-00_quarterly-planning_transcript.md"), outputs.Transcript);
        Assert.Equal([Path.Combine(scratch.Url, "2026-09-29_10-15-00_quarterly-planning.wav")], outputs.Companions);
        Assert.Equal(ChatFixtures.Srt, await File.ReadAllTextAsync(outputs.Srt));
        Assert.Equal("# API Notes\n\n**Meeting Name:** quarterly-planning\n", await File.ReadAllTextAsync(outputs.Markdown));
        Assert.StartsWith("# Transcript\n\n**Meeting Name:** quarterly-planning\n",
            await File.ReadAllTextAsync(outputs.Transcript), StringComparison.Ordinal);
        Assert.False(File.Exists(srtPath));
    }

    [Fact]
    public async Task GenerateAndSaveUsesTheGivenNameAndTimestamp()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 200, ChatFixtures.Completion(ChatFixtures.GoodNotes));
        var srtPath = Path.Combine(scratch.Url, "recording.srt");
        await File.WriteAllTextAsync(srtPath, ChatFixtures.Srt);
        var outputs = await ChatFixtures.Pipeline().GenerateAndSaveAsync(srtPath, "en", PromptTemplate.GeneralMeeting,
            ChatFixtures.Configuration(url), "t", meetingName: "Team Sync", timestamp: "2026-01-01_09-00-00");
        Assert.Equal(Path.Combine(scratch.Url, "2026-01-01_09-00-00_team-sync.srt"), outputs.Srt);
    }

    [Fact]
    public async Task GenerateAndSaveKeepsTheSrtOnFailure()
    {
        var url = ChatFixtures.UniqueUrl();
        ChatFixtures.Handler.Register(url, 500, "down");
        var srtPath = Path.Combine(scratch.Url, "2026-09-29_10-15-00.srt");
        await File.WriteAllTextAsync(srtPath, ChatFixtures.Srt);
        await Assert.ThrowsAsync<ChatCompletionsException>(() => ChatFixtures.Pipeline().GenerateAndSaveAsync(srtPath, "en",
            PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.Equal(["2026-09-29_10-15-00.srt"], Directory.GetFiles(scratch.Url).Select(Path.GetFileName));

        var missing = Path.Combine(scratch.Url, "missing.srt");
        var error = await Assert.ThrowsAsync<NotesPipelineException>(() => ChatFixtures.Pipeline().GenerateAndSaveAsync(
            missing, "en", PromptTemplate.GeneralMeeting, ChatFixtures.Configuration(url), "t"));
        Assert.StartsWith($"Error: SRT file not found or unreadable ({missing}): ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiErrorWithoutAMessageIsShownAsJson()
    {
        Assert.Equal("""API HTTP Error 400: {"code":7}""",
            new ChatCompletionsError.HttpStatus(400, """{"error": {"code": 7}}""").Description);
        Assert.Equal("API HTTP Error 400: plain", new ChatCompletionsError.HttpStatus(400, "plain").Description);
    }

    [Fact]
    public async Task PayloadKeysAreSortedAsTheMacWritesThem()
    {
        using var request = ChatCompletionsClient.MakeRequest("s", "u", ChatFixtures.Configuration(ChatFixtures.UniqueUrl()), "t");
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync();
        Assert.Equal(
            """{"messages":[{"content":"s","role":"system"},{"content":"u","role":"user"}],"model":"test-model","reasoning_effort":"max","temperature":0.3}""",
            text);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)200);
    }
}

/// <summary>Port of <c>AIProviderStoreTests</c> in NotesPipelineTests.swift.</summary>
public sealed class AIProviderStoreTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    private static AIProviderStore Store(string folder, ISecretStore? secrets = null, CliTool? installed = null) =>
        new(new SettingsFile(folder), secrets ?? new InMemorySecretStore(), () => installed);

    [Fact]
    public void PresetTable()
    {
        Assert.Equal(["copilotCLI", "claudeCodeCLI", "codexCLI", "antigravityCLI", "ollama", "custom"],
            ProviderPreset.All.Select(preset => preset.Id));
        Assert.Equal("claude-sonnet-5", ProviderPreset.ClaudeCodeCli.DefaultModel);
        Assert.Equal("gpt-6-luna", ProviderPreset.CodexCli.DefaultModel);
        Assert.Equal("high", AIProviderConfiguration.FromPreset(ProviderPreset.ClaudeCodeCli).ReasoningEffort);
        Assert.Equal("max", AIProviderConfiguration.FromPreset(ProviderPreset.CodexCli).ReasoningEffort);
        Assert.Equal("high", AIProviderConfiguration.FromPreset(ProviderPreset.AntigravityCli).ReasoningEffort);
        Assert.Equal("gemini-3.8-flash-high", AIProviderConfiguration.FromPreset(ProviderPreset.AntigravityCli).Model);
        Assert.Equal("max", AIProviderConfiguration.FromPreset(ProviderPreset.CopilotCli).ReasoningEffort);
        Assert.Equal("copilotCLI", AIProviderConfiguration.Default.PresetId);
        Assert.Equal("max", AIProviderConfiguration.FromPreset(ProviderPreset.Custom).ReasoningEffort);
        Assert.Empty(AIProviderConfiguration.FromPreset(ProviderPreset.Custom).Model);
        Assert.Null(AIProviderConfiguration.FromPreset(ProviderPreset.Ollama).ReasoningEffort);
        Assert.Equal(0.3, AIProviderConfiguration.FromPreset(ProviderPreset.Ollama).Temperature);
        Assert.Equal(0.3, AIProviderConfiguration.FromPreset(ProviderPreset.Custom).Temperature);
    }

    /// <summary>
    /// No CLI has a temperature option: CLI presets start without one, never
    /// report one to send. HTTP presets keep it.
    /// </summary>
    [Fact]
    public void TemperatureIsOnlyForHttpPresets()
    {
        foreach (var preset in ProviderPreset.All)
        {
            Assert.Equal(preset.Kind == ProviderKind.Http, preset.SupportsTemperature);
            var configuration = AIProviderConfiguration.FromPreset(preset);
            Assert.Equal(preset.Kind == ProviderKind.Http ? 0.3 : null, configuration.Temperature);
            configuration = configuration with { Temperature = 0.9 };
            Assert.Equal(preset.Kind == ProviderKind.Http ? 0.9 : null, configuration.EffectiveTemperature);
        }
    }

    [Fact]
    public void ModelDescriptionNamesWhatAnEmptyModelMeans()
    {
        var copilot = AIProviderConfiguration.FromPreset(ProviderPreset.CopilotCli) with { Model = "" };
        Assert.Equal("gpt-5.6-luna", copilot.ModelDescription);
        var codex = AIProviderConfiguration.FromPreset(ProviderPreset.CodexCli);
        Assert.Equal("gpt-6-luna", codex.ModelDescription);
        Assert.Equal("CLI default", (codex with { Model = " " }).ModelDescription);
        Assert.Equal("claude-sonnet-5", AIProviderConfiguration.FromPreset(ProviderPreset.ClaudeCodeCli).ModelDescription);
        Assert.Equal("(none set)", AIProviderConfiguration.FromPreset(ProviderPreset.Custom).ModelDescription);
    }

    [Fact]
    public void DefaultsOnFirstLaunch()
    {
        var store = Store(scratch.Make());
        Assert.Equal(AIProviderConfiguration.Default, store.Configuration);
        Assert.True(store.Configuration.AskBeforeSending);
        Assert.Equal([PromptTemplate.GeneralMeeting], store.Templates);
        Assert.Equal(PromptTemplate.GeneralMeeting, store.SelectedTemplate);
        Assert.False(store.HasToken);
    }

    [Fact]
    public void ConfigurationAndTemplatesRoundTrip()
    {
        var folder = scratch.Make();
        var store = Store(folder);
        store.SelectPreset(ProviderPreset.Custom);
        store.Configuration = store.Configuration with
        {
            Auth = AuthHeaderStyle.ApiKey,
            BaseUrl = "https://me.openai.azure.com/openai/deployments/x/chat/completions",
            ExtraHeaders = new Dictionary<string, string> { ["X-A"] = "b" },
            AskBeforeSending = false,
        };
        var standup = store.AddTemplate("Standup", "Short.");
        store.SetDefaultTemplate(standup.Id);

        var reloaded = Store(folder);
        Assert.Equal(store.Configuration, reloaded.Configuration);
        Assert.Equal("custom", reloaded.Configuration.PresetId);
        Assert.Equal(AuthHeaderStyle.ApiKey, reloaded.Configuration.Auth);
        Assert.Equal([PromptTemplate.GeneralMeeting, standup], reloaded.Templates);
        Assert.Equal(standup, reloaded.SelectedTemplate);
    }

    [Fact]
    public void BuiltInTemplateCannotBeEditedOrDeleted()
    {
        var store = Store(scratch.Make());
        store.UpdateTemplate(PromptTemplate.GeneralMeeting with { Name = "Changed" });
        store.DeleteTemplate(PromptTemplate.GeneralMeetingId);
        Assert.Equal([PromptTemplate.GeneralMeeting], store.Templates);
    }

    [Fact]
    public void EditAndDeleteUserTemplate()
    {
        var folder = scratch.Make();
        var store = Store(folder);
        var template = store.AddTemplate("A", "a") with { Name = "B", Instructions = "b" };
        store.UpdateTemplate(template);
        Assert.Equal("B", store.Templates[^1].Name);
        store.SetDefaultTemplate(template.Id);
        store.DeleteTemplate(template.Id);
        Assert.Equal([PromptTemplate.GeneralMeeting], store.Templates);
        Assert.Equal(PromptTemplate.GeneralMeetingId, store.Configuration.SelectedTemplateId);
        Assert.Equal([PromptTemplate.GeneralMeeting], Store(folder).Templates);
    }

    [Fact]
    public void StoredTemplatesAlwaysStartWithTheBuiltIn()
    {
        var folder = scratch.Make();
        var tampered = PromptTemplate.GeneralMeeting with { Instructions = "tampered" };
        var user = new PromptTemplate("U", "u");
        var raw = ScratchSettings.Raw(folder);
        raw.Set(AIProviderStore.Key.Templates, System.Text.Json.JsonSerializer.SerializeToNode(new[] { user, tampered }));
        raw.Set(AIProviderStore.Key.Configuration,
            (AIProviderConfiguration.Default with { SelectedTemplateId = Guid.NewGuid() }).ToJson());

        var store = Store(folder);
        Assert.Equal([PromptTemplate.GeneralMeeting, user], store.Templates);
        Assert.Equal(PromptTemplate.GeneralMeetingId, store.Configuration.SelectedTemplateId);
    }

    [Fact]
    public void SelectPresetResetsProviderFieldsButKeepsPreferences()
    {
        var store = Store(scratch.Make());
        store.Configuration = store.Configuration with { AskBeforeSending = false, Model = "other" };
        store.SelectPreset(ProviderPreset.Ollama);
        Assert.Equal("http://localhost:11434/v1/chat/completions", store.Configuration.BaseUrl);
        Assert.Empty(store.Configuration.Model);
        Assert.Null(store.Configuration.ReasoningEffort);
        Assert.Equal(AuthHeaderStyle.None, store.Configuration.Auth);
        Assert.False(store.Configuration.AskBeforeSending);
    }

    [Fact]
    public void TokensArePerPreset()
    {
        var secrets = new InMemorySecretStore();
        var store = Store(scratch.Make(), secrets);
        store.SelectPreset(ProviderPreset.Ollama);
        store.SetToken("  sk-azure \n");
        Assert.Equal("sk-azure", store.CurrentToken);
        store.SelectPreset(ProviderPreset.Custom);
        Assert.False(store.HasToken);
        store.SetToken("sk-custom");
        Assert.Equal("sk-azure", secrets.Read("ollama"));
        Assert.Equal("sk-custom", secrets.Read("custom"));
        store.SetToken("");
        Assert.False(store.HasToken);
        store.SelectPreset(ProviderPreset.Ollama);
        store.DeleteToken();
        Assert.Null(secrets.Read("ollama"));
    }

    // Windows-only: the settings.json shape and change notification.

    [Fact]
    public void ConfigurationIsStoredAsAJsonObjectWithTheSwiftKeys()
    {
        var folder = scratch.Make();
        var store = Store(folder);
        store.SelectPreset(ProviderPreset.Custom);
        var stored = Assert.IsType<JsonObject>(ScratchSettings.Raw(folder).Get(AIProviderStore.Key.Configuration));
        Assert.Equal(
            ["askBeforeSending", "auth", "baseURL", "extraHeaders", "model", "presetID", "reasoningEffort", "selectedTemplateID", "temperature"],
            stored.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("custom", (string?)stored["presetID"]);
        Assert.Equal("6e0b5a10-3c2d-4f51-9a7e-000000000001", (string?)stored["selectedTemplateID"]);
        Assert.Null(ScratchSettings.Raw(folder).Get(AIProviderStore.Key.Templates));
    }

    [Fact]
    public void ChangesAreAnnounced()
    {
        var store = Store(scratch.Make());
        List<string?> changed = [];
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        store.SelectPreset(ProviderPreset.Ollama);
        store.SetToken("x");
        store.AddTemplate("T", "t");
        Assert.Contains(nameof(AIProviderStore.Configuration), changed);
        Assert.Contains(nameof(AIProviderStore.HasToken), changed);
        Assert.Contains(nameof(AIProviderStore.Templates), changed);
        Assert.Equal(2, store.TokenRevision);
    }
}

/// <summary>Port of <c>RetiredPresetMigrationTests</c> in NotesPipelineTests.swift.</summary>
public sealed class RetiredPresetMigrationTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    private string Storing(AIProviderConfiguration configuration)
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).Set(AIProviderStore.Key.Configuration, configuration.ToJson());
        return folder;
    }

    private static AIProviderConfiguration? Stored(string folder) =>
        AIProviderConfiguration.FromJson(ScratchSettings.Raw(folder).Get(AIProviderStore.Key.Configuration));

    private static readonly AIProviderConfiguration GithubConfiguration = new(
        presetId: "githubModels",
        baseUrl: "https://my-proxy.example.test/chat/completions",
        model: "my-model",
        reasoningEffort: "high",
        auth: AuthHeaderStyle.Bearer,
        extraHeaders: new Dictionary<string, string> { ["X-A"] = "b" },
        askBeforeSending: false);

    /// <summary>What the removed OpenAI and Anthropic presets stored.</summary>
    private static readonly AIProviderConfiguration OpenAIConfiguration = new(
        presetId: "openai",
        baseUrl: "https://api.openai.com/v1/chat/completions",
        model: "gpt-5.6-luna",
        reasoningEffort: "max",
        auth: AuthHeaderStyle.Bearer,
        temperature: 0.3);

    private static readonly AIProviderConfiguration AnthropicConfiguration = new(
        presetId: "anthropic",
        baseUrl: "https://api.anthropic.com/v1/chat/completions",
        model: "claude-sonnet-5",
        reasoningEffort: null,
        auth: AuthHeaderStyle.Bearer,
        temperature: null,
        askBeforeSending: false);

    [Fact]
    public void GithubModelsLoadsAsCustomKeepingItsFields()
    {
        var folder = Storing(GithubConfiguration);
        var store = new AIProviderStore(new SettingsFile(folder), new InMemorySecretStore(), () => null);
        var expected = GithubConfiguration with { PresetId = "custom" };
        Assert.Equal(expected, store.Configuration);
        Assert.Same(ProviderPreset.Custom, store.Configuration.Preset);
        // The migration is saved, so a second load sees `custom` directly.
        Assert.Equal(expected, Stored(folder));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public void RemovedApiPresetLoadsAsCustomKeepingUrlModelAndToken(string presetId)
    {
        var original = presetId == "openai" ? OpenAIConfiguration : AnthropicConfiguration;
        var secrets = new InMemorySecretStore();
        secrets.Write($"sk-{presetId}", presetId);
        var folder = Storing(original);
        var store = new AIProviderStore(new SettingsFile(folder), secrets, () => CliTool.ClaudeCode);
        var expected = original with { PresetId = "custom" };
        Assert.Equal(expected, store.Configuration);
        Assert.Equal(original.BaseUrl, store.Configuration.BaseUrl);
        Assert.Equal(original.Model, store.Configuration.Model);
        Assert.Equal(original.Temperature, store.Configuration.Temperature);
        Assert.Same(ProviderPreset.Custom, store.Configuration.Preset);
        Assert.Equal($"sk-{presetId}", store.CurrentToken);
        Assert.Null(secrets.Read(presetId));
        Assert.Equal(expected, Stored(folder));
    }

    /// <summary>
    /// What the removed Azure OpenAI preset stored: a deployment URL, the
    /// <c>api-key</c> header, extra headers, and a temperature.
    /// </summary>
    private static readonly AIProviderConfiguration AzureConfiguration = new(
        presetId: "azureOpenAI",
        baseUrl: "https://me.openai.azure.com/openai/deployments/notes/chat/completions?api-version=2026-06-01",
        model: "gpt-5.6-luna",
        reasoningEffort: "max",
        auth: AuthHeaderStyle.ApiKey,
        temperature: 0.2,
        extraHeaders: new Dictionary<string, string> { ["X-Team"] = "notes" },
        askBeforeSending: false);

    [Fact]
    public void AzureOpenAILoadsAsCustomKeepingUrlModelTokenHeadersAndTemperature()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write("azure-key", "azureOpenAI");
        var folder = Storing(AzureConfiguration);
        var store = new AIProviderStore(new SettingsFile(folder), secrets, () => CliTool.Antigravity);
        var expected = AzureConfiguration with { PresetId = "custom" };
        Assert.Equal(expected, store.Configuration);
        Assert.Same(ProviderPreset.Custom, store.Configuration.Preset);
        Assert.Equal(AuthHeaderStyle.ApiKey, store.Configuration.Auth);
        Assert.Equal(new Dictionary<string, string> { ["X-Team"] = "notes" }, store.Configuration.ExtraHeaders);
        Assert.Equal(0.2, store.Configuration.EffectiveTemperature);
        Assert.Equal("azure-key", store.CurrentToken);
        Assert.Null(secrets.Read("azureOpenAI"));
        Assert.Equal(expected, Stored(folder));
        using var request = ChatCompletionsClient.MakeRequest("s", "u", store.Configuration, store.CurrentToken);
        Assert.Equal("azure-key", ChatFixtures.Header(request, "api-key"));
        Assert.Null(ChatFixtures.Header(request, "Authorization"));
    }

    [Fact]
    public void CurrentPresetsAreNotMigrated()
    {
        foreach (var preset in ProviderPreset.All)
        {
            Assert.Null(AIProviderConfiguration.FromPreset(preset).MigratingRetiredPreset());
        }
    }

    [Fact]
    public void GithubModelsTokenMovesToCustom()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write("ghp_old", "githubModels");
        var store = new AIProviderStore(new SettingsFile(Storing(GithubConfiguration)), secrets, () => null);
        Assert.Equal("ghp_old", store.CurrentToken);
        Assert.Equal("ghp_old", secrets.Read("custom"));
        Assert.Null(secrets.Read("githubModels"));
    }

    [Fact]
    public void ExistingCustomTokenIsNotOverwritten()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write("ghp_old", "githubModels");
        secrets.Write("sk-custom", "custom");
        var store = new AIProviderStore(new SettingsFile(Storing(GithubConfiguration)), secrets, () => null);
        Assert.Equal("sk-custom", store.CurrentToken);
        Assert.Equal("ghp_old", secrets.Read("githubModels"));
    }
}
