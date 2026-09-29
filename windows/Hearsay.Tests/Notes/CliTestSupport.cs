using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hearsay.Core.Notes;

namespace Hearsay.Tests.Notes;

/// <summary>
/// Records every runner call and answers from a canned result, the
/// counterpart of <c>FakeCLIRunner</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/CopilotCLITests.swift. The
/// responder also gets the working folder, so a fake can write the Codex
/// reply file. No process is started.
/// </summary>
internal sealed class FakeCliRunner
{
    private readonly Lock gate = new();
    private readonly List<CliRunRequest> recorded = [];
    private readonly Func<IReadOnlyList<string>, string, CliRunResult> respond;

    public FakeCliRunner(Func<IReadOnlyList<string>, CliRunResult> respond)
    {
        this.respond = (argv, _) => respond(argv);
    }

    public FakeCliRunner(Func<IReadOnlyList<string>, string, CliRunResult> respond)
    {
        this.respond = respond;
    }

    public IReadOnlyList<CliRunRequest> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. recorded];
            }
        }
    }

    public CliRunner Runner => (request, _) =>
    {
        lock (gate)
        {
            recorded.Add(request);
        }
        return Task.FromResult(respond(request.Argv, request.Directory));
    };
}

/// <summary>Shared fixtures of the CLI tests (the Swift file-level helpers).</summary>
internal static class CliFixtures
{
    public const string Home = @"C:\Users\test";

    /// <summary>What the fake child sees as its inherited environment.</summary>
    public static readonly IReadOnlyDictionary<string, string> BaseEnvironment =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows\System32;C:\Windows",
            ["USERPROFILE"] = Home,
        };

    /// <summary>The Mac's <c>/usr/bin/copilot</c>: a program outside the well-known folders.</summary>
    public const string CopilotPath = @"C:\Tools\copilot.exe";

    public const string ClaudePath = @"C:\Users\test\.local\bin\claude.exe";

    public const string CodexPath = @"C:\Users\test\AppData\Roaming\npm\codex.cmd";

    public const string AgyPath = @"C:\Users\test\AppData\Local\agy\bin\agy.exe";

    public const string CopilotSrt = "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n";

    public const string LaunchNotes =
        """{"filename": "Product Launch Plan", "markdown": "# Notes\n", "transcript_markdown": "# Transcript\n\n## 00:00 — Launch\n"}""";

    /// <summary>A locator that finds only <paramref name="found"/> (default: <see cref="CopilotPath"/>), with an empty PATH.</summary>
    public static CliLocator Locator(IEnumerable<string>? found = null, Func<string, IReadOnlyList<string>>? directoryContents = null)
    {
        var set = new HashSet<string>(found ?? [CopilotPath], StringComparer.OrdinalIgnoreCase);
        return new CliLocator(
            CliSearchFolders.ForHome(Home),
            pathVariable: string.Empty,
            isExecutable: set.Contains,
            directoryContents: directoryContents ?? (_ => []));
    }

    public static CliClient Client(FakeCliRunner fake, CliLocator? locator = null,
        AntigravityHousekeeping? antigravity = null, CliVersionCache? versions = null) =>
        new(fake.Runner, locator ?? Locator(), antigravity ?? FakeHousekeeping(), versions ?? new CliVersionCache(),
            () => BaseEnvironment);

    /// <summary>
    /// What <c>agy --print --output-format json --json-schema …</c> prints on
    /// success (shape observed with agy 1.2.12): <c>response</c> carries agy's
    /// own extra keys, <c>structured_output</c> the schema-checked object.
    /// </summary>
    public static string AntigravityEnvelope(string response, string? structured = null, string status = "SUCCESS")
    {
        var envelope = new JsonObject
        {
            ["conversation_id"] = "6cfd8496-ae02-41ac-afc4-1460ec1eaf0c",
            ["status"] = status,
            ["response"] = response,
            ["duration_seconds"] = 24.2,
            ["num_turns"] = 1,
        };
        if (structured is not null) envelope["structured_output"] = JsonNode.Parse(structured);
        return envelope.ToJsonString();
    }

    /// <summary>Housekeeping on an in-memory <c>C:\Users\test</c>, so no test touches <c>.gemini</c>.</summary>
    public static AntigravityHousekeeping FakeHousekeeping(FakeAntigravityFileSystem? fileSystem = null,
        FakeConversationIndex? index = null, bool agyIsRunning = false) =>
        new(Home, fileSystem ?? new FakeAntigravityFileSystem(), index ?? new FakeConversationIndex(), () => agyIsRunning);

    /// <summary>The verified agy version, known up front, so no <c>--version</c> call runs.</summary>
    public static CliVersionCache KnownAgyVersion() => new(new Dictionary<string, string> { [AgyPath] = "1.2.12" });

    /// <summary>The value after <paramref name="flag"/> in <paramref name="argv"/>, or null.</summary>
    public static string? ValueAfter(string flag, IReadOnlyList<string> argv)
    {
        var list = argv.ToList();
        var index = list.IndexOf(flag);
        return index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
    }

    public static async Task<CliProviderError> CliErrorAsync(Func<Task> action) =>
        (await Assert.ThrowsAsync<CliProviderException>(action)).Error;
}

/// <summary>Counts calls and answers <see cref="CliFixtures.LaunchNotes"/>. Swift <c>RecordingChatClient</c>.</summary>
internal sealed class RecordingChatClient : IChatCompleting
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public Task<string> CompleteAsync(string systemMessage, string userMessage, AIProviderConfiguration configuration,
        string? token, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return Task.FromResult(CliFixtures.LaunchNotes);
    }
}

/// <summary>An in-memory file tree: files by full path; folders are implied. Swift <c>FakeAntigravityFileSystem</c>.</summary>
internal sealed class FakeAntigravityFileSystem : IAntigravityFileSystem
{
    internal sealed class Failure : Exception
    {
        public Failure()
            : base("simulated failure")
        {
        }
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly List<string> removedPaths = [];
    private readonly List<string> writtenPaths = [];
    private readonly List<string> replacedPaths = [];
    private readonly HashSet<string> failing = new(StringComparer.Ordinal);

    public bool FailWrites { get; set; }

    public void Add(string path, string text = "") => Add(path, Encoding.UTF8.GetBytes(text));

    public void Add(string path, byte[] data)
    {
        lock (gate)
        {
            files[path] = data;
        }
    }

    /// <summary><see cref="RemoveItem"/> fails for this path.</summary>
    public void FailRemoval(string path)
    {
        lock (gate)
        {
            failing.Add(path);
        }
    }

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (gate)
            {
                return files.Keys.Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    public IReadOnlyList<string> Removed
    {
        get
        {
            lock (gate)
            {
                return [.. removedPaths];
            }
        }
    }

    public IReadOnlyList<string> Written
    {
        get
        {
            lock (gate)
            {
                return [.. writtenPaths];
            }
        }
    }

    public IReadOnlyList<string> Replaced
    {
        get
        {
            lock (gate)
            {
                return [.. replacedPaths];
            }
        }
    }

    public byte[]? Bytes(string path)
    {
        lock (gate)
        {
            return files.TryGetValue(path, out var data) ? data : null;
        }
    }

    public string? Text(string path) => Bytes(path) is { } data ? Encoding.UTF8.GetString(data) : null;

    public JsonObject? Json(string path) => Bytes(path) is { } data ? ParseObject(data) : null;

    private static JsonObject? ParseObject(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ContentsOfDirectory(string path)
    {
        var prefix = path + @"\";
        lock (gate)
        {
            return files.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    public byte[]? Contents(string path) => Bytes(path);

    public void Write(byte[] data, string path)
    {
        lock (gate)
        {
            if (FailWrites) throw new IOException("simulated write failure");
            files[path] = data;
            writtenPaths.Add(path);
        }
    }

    public void ReplaceKeepingBackup(byte[] data, string path, byte[] expectedOriginal)
    {
        lock (gate)
        {
            if (FailWrites) throw new Failure();
            if (!files.TryGetValue(path, out var current) || !current.AsSpan().SequenceEqual(expectedOriginal))
            {
                throw new Failure();
            }
            files[path + LocalAntigravityFileSystem.BackupSuffix] = expectedOriginal;
            files[path] = data;
            replacedPaths.Add(path);
        }
    }

    public void RemoveItem(string path)
    {
        lock (gate)
        {
            if (failing.Contains(path)) throw new Failure();
            var matching = files.Keys.Where(key => key == path || key.StartsWith(path + @"\", StringComparison.Ordinal)).ToList();
            if (matching.Count == 0) throw new Failure();
            foreach (var key in matching)
            {
                files.Remove(key);
            }
            removedPaths.Add(path);
        }
    }
}

/// <summary>Swift <c>FakeConversationIndex</c>.</summary>
internal sealed class FakeConversationIndex : IAntigravityConversationIndex
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> childrenByParent;
    private readonly List<string> removedIds = [];

    public FakeConversationIndex(Dictionary<string, IReadOnlyList<string>>? children = null)
    {
        childrenByParent = children ?? [];
    }

    public bool FailRemovals { get; set; }

    public IReadOnlyList<string> Removed
    {
        get
        {
            lock (gate)
            {
                return [.. removedIds];
            }
        }
    }

    public IReadOnlyList<string> ChildConversations(string id)
    {
        lock (gate)
        {
            return childrenByParent.TryGetValue(id, out var children) ? children : [];
        }
    }

    public void RemoveSummary(string id)
    {
        lock (gate)
        {
            if (FailRemovals) throw new FakeAntigravityFileSystem.Failure();
            removedIds.Add(id);
        }
    }
}

/// <summary>
/// Serves canned chat-completions responses keyed by URL and records each
/// request with its body: the <c>ChatStubURLProtocol</c> of
/// NotesPipelineTests.swift as an <see cref="HttpMessageHandler"/> (see
/// ModelStore/FakeHub.cs). Never touches the network.
/// </summary>
internal sealed class FakeChatHandler : HttpMessageHandler
{
    internal sealed record Recorded(HttpRequestMessage Request, byte[] Body, string? ContentType);

    private readonly Lock gate = new();
    private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Recorded>> recorded = new(StringComparer.Ordinal);

    public void Register(string url, int status, string body) => Register(url, status, Encoding.UTF8.GetBytes(body));

    public void Register(string url, int status, byte[] body)
    {
        lock (gate)
        {
            responses[url] = ((HttpStatusCode)status, body);
        }
    }

    /// <summary>Makes requests to <paramref name="url"/> throw <paramref name="failure"/>.</summary>
    public void RegisterFailure(string url, Exception failure)
    {
        lock (gate)
        {
            failures[url] = failure;
        }
    }

    public IReadOnlyList<Recorded> Requests(string url)
    {
        lock (gate)
        {
            return recorded.TryGetValue(url, out var list) ? [.. list] : [];
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri?.AbsoluteUri ?? string.Empty;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        (HttpStatusCode Status, byte[] Body)? canned;
        Exception? failure;
        lock (gate)
        {
            if (!recorded.TryGetValue(key, out var list))
            {
                list = [];
                recorded[key] = list;
            }
            list.Add(new Recorded(request, body, request.Content?.Headers.ContentType?.ToString()));
            canned = responses.TryGetValue(key, out var response) ? response : null;
            failure = failures.GetValueOrDefault(key);
        }
        if (failure is not null) throw failure;
        if (canned is not { } found)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Could not connect to the server.");
        }
        var message = new HttpResponseMessage(found.Status) { Content = new ByteArrayContent(found.Body) };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return message;
    }
}
