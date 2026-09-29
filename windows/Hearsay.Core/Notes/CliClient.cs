using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearsay.Core.Notes;

/// <summary>
/// The argv of one notes request for each CLI, with the resolved program as
/// argv[0]. Port of <c>CLIArguments</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/CLIClient.swift; the lists are
/// the Mac's, flag for flag. How the prompt reaches the program on Windows
/// is <see cref="WindowsInvocation"/>.
/// </summary>
public static class CliArguments
{
    // MARK: - GitHub Copilot CLI

    /// <summary>
    /// Python <c>copilot_argv</c>. An empty model or effort falls back to the
    /// defaults, as Python's <c>or</c> does; model <c>auto</c> omits <c>--model</c>.
    /// </summary>
    public static IReadOnlyList<string> Copilot(string executable, string prompt, string model, string? reasoningEffort)
    {
        var trimmedModel = model.Trim();
        var targetModel = trimmedModel.Length == 0 ? ProviderPreset.DefaultOpenAIModel : trimmedModel;
        var trimmedEffort = reasoningEffort?.Trim() ?? string.Empty;
        var effort = trimmedEffort.Length == 0 ? ProviderPreset.DefaultReasoningEffort : trimmedEffort;
        List<string> argv =
        [
            executable,
            "--prompt", prompt,
            "--silent",
            "--no-color",
            "--no-ask-user",
            "--no-auto-update",
            "--output-format", "text",
            "--reasoning-effort", effort,
        ];
        if (targetModel != "auto")
        {
            argv.AddRange(["--model", targetModel]);
        }
        return argv;
    }

    // MARK: - Claude Code

    /// <summary>
    /// <c>claude --print</c> with no tools, none of the user's settings,
    /// hooks, CLAUDE.md, MCP servers, or skills, and no saved session. The
    /// system message replaces Claude Code's agent prompt. The prompt follows
    /// <c>--</c> so neither a leading dash nor the variadic <c>--tools</c> can
    /// swallow it. An empty model omits <c>--model</c> (the CLI's default);
    /// the effort goes through <see cref="ClaudeCodeEffort"/>.
    /// </summary>
    public static IReadOnlyList<string> ClaudeCode(string executable, string prompt, string systemMessage, string model,
        string? reasoningEffort)
    {
        List<string> argv =
        [
            executable,
            "--print",
            "--output-format", "text",
            "--tools", "",
            "--setting-sources", "",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--permission-mode", "dontAsk",
            "--disable-slash-commands",
            "--no-chrome",
        ];
        if (systemMessage.Trim().Length > 0)
        {
            argv.AddRange(["--system-prompt", systemMessage]);
        }
        var trimmedModel = model.Trim();
        if (trimmedModel.Length > 0)
        {
            argv.AddRange(["--model", trimmedModel]);
        }
        if (ClaudeCodeEffort(reasoningEffort) is { } effort)
        {
            argv.AddRange(["--effort", effort]);
        }
        argv.AddRange(["--", prompt]);
        return argv;
    }

    /// <summary>
    /// <c>claude --effort</c> accepts low, medium, high, xhigh, max. Hearsay's
    /// "none" and "minimal" (OpenAI levels) become "low"; anything else, or an
    /// empty value, omits the flag so the CLI's default applies.
    /// </summary>
    public static string? ClaudeCodeEffort(string? value) => Normalized(value) switch
    {
        "none" or "minimal" or "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        "max" => "max",
        _ => null,
    };

    // MARK: - Codex

    /// <summary>The file <c>codex exec -o</c> writes the final reply to, inside the run's temporary folder.</summary>
    public const string CodexReplyFileName = "last-message.txt";

    /// <summary>
    /// <c>codex exec</c> in a read-only sandbox rooted at the temporary
    /// folder, without <c>config.toml</c> or <c>.rules</c> files, and without
    /// saving the session. The reply is read from <paramref name="outputFile"/>,
    /// not stdout. An empty model omits <c>--model</c>; the effort goes
    /// through <see cref="CodexEffort"/>.
    /// </summary>
    public static IReadOnlyList<string> Codex(string executable, string prompt, string model, string? reasoningEffort,
        string workingDirectory, string outputFile)
    {
        List<string> argv =
        [
            executable,
            "exec",
            "--sandbox", "read-only",
            "--cd", workingDirectory,
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--color", "never",
            "--output-last-message", outputFile,
        ];
        var trimmedModel = model.Trim();
        if (trimmedModel.Length > 0)
        {
            argv.AddRange(["--model", trimmedModel]);
        }
        if (CodexEffort(reasoningEffort) is { } effort)
        {
            argv.AddRange(["-c", $"model_reasoning_effort=\"{effort}\""]);
        }
        argv.AddRange(["--", prompt]);
        return argv;
    }

    private static readonly string[] CodexEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// <c>model_reasoning_effort</c> accepts none, minimal, low, medium, high,
    /// xhigh, max, so Hearsay's values pass through. Anything else, or an
    /// empty value, omits the override.
    /// </summary>
    public static string? CodexEffort(string? value)
    {
        var effort = Normalized(value);
        return CodexEfforts.Contains(effort) ? effort : null;
    }

    // MARK: - Antigravity

    /// <summary>Files of one <c>agy</c> run, at the root of its temporary folder, outside the empty working folder the agent sees.</summary>
    public const string AntigravitySchemaFileName = "reply-schema.json";

    public const string AntigravityLogFileName = "agy.log";

    /// <summary>
    /// The agy project every Hearsay run belongs to (its deny rules are kept
    /// by <see cref="AntigravityHousekeeping"/>), also used as the working folder name.
    /// </summary>
    public const string AntigravityProjectName = "hearsay-notes";

    /// <summary><c>--print-timeout</c>, a little below <see cref="CliClient.Timeout"/> (600 s) so agy stops on its own first.</summary>
    public const string AntigravityPrintTimeout = "570s";

    /// <summary>The reply shape the prompt's response rules ask for, enforced with <c>agy --json-schema</c>.</summary>
    public const string NotesReplySchema =
        """{"type":"object","required":["filename","markdown","transcript_markdown"],"properties":{"filename":{"type":"string"},"markdown":{"type":"string"},"transcript_markdown":{"type":"string"}},"additionalProperties":false}""";

    /// <summary>
    /// agy is an agent that explores its workspace and writes files unless
    /// told otherwise; in print mode a denied file write ends the run with
    /// no reply. Sent before the user message.
    /// </summary>
    public const string AntigravityInstructions =
        "Answer in your reply only. Do not run commands, and do not read, search, or write files: everything you need is in this message.";

    /// <summary>
    /// The <c>--print</c> text: the system message (agy has no system prompt
    /// option), <see cref="AntigravityInstructions"/>, then the user message.
    /// </summary>
    public static string AntigravityPrompt(string systemMessage, string userMessage)
    {
        var system = systemMessage.Trim();
        List<string> parts = system.Length == 0 ? [] : [system];
        parts.Add(AntigravityInstructions);
        parts.Add(userMessage);
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// <c>agy --print</c> with the reply schema enforced (which needs
    /// <c>--output-format json</c>), slash commands and skills off, the
    /// terminal sandbox on, a log file inside the run's folder, and every run
    /// in the <c>hearsay-notes</c> project, whose deny rules block every tool
    /// that runs programs, touches files, or fetches URLs. The prompt is
    /// attached to <c>--print=</c>, because agy takes the next argument as its
    /// value and rejects a prompt after <c>--</c>. An empty model omits
    /// <c>--model</c>; the effort goes through <see cref="AntigravityEffort"/>.
    /// </summary>
    public static IReadOnlyList<string> Antigravity(string executable, string prompt, string model,
        string? reasoningEffort, string schemaFile, string logFile)
    {
        List<string> argv =
        [
            executable,
            "--output-format", "json",
            "--json-schema", schemaFile,
            "--disable-slash-commands",
            "--sandbox",
            "--print-timeout", AntigravityPrintTimeout,
            "--log-file", logFile,
            "--project", AntigravityProjectName,
        ];
        var trimmedModel = model.Trim();
        if (trimmedModel.Length > 0)
        {
            argv.AddRange(["--model", trimmedModel]);
        }
        if (AntigravityEffort(reasoningEffort, trimmedModel) is { } effort)
        {
            argv.AddRange(["--effort", effort]);
        }
        argv.Add("--print=" + prompt);
        return argv;
    }

    private static readonly string[] EffortSuffixes = ["low", "medium", "high", "max"];

    /// <summary>
    /// <c>agy --effort</c> accepts low, medium, high, max. Hearsay's "none"
    /// and "minimal" become "low", "xhigh" becomes "max"; anything else, or
    /// an empty value, omits the flag. A model id that already names its
    /// effort (<c>gemini-3.8-flash-high</c>) also omits it: agy rejects any
    /// other effort for such an id and needs none for the matching one.
    /// </summary>
    public static string? AntigravityEffort(string? value, string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var suffix = model.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (EffortSuffixes.Contains(suffix)) return null;
        return Normalized(value) switch
        {
            "none" or "minimal" or "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "xhigh" or "max" => "max",
            _ => null,
        };
    }

    // MARK: - Windows

    /// <summary>
    /// How the Mac argv of one notes request is started on Windows, where a
    /// command line holds at most <see cref="WindowsCommandLine.MaxLength"/>
    /// characters (the Mac's limit is about 1 MB) and a meeting's prompt is
    /// often longer. Claude Code and Codex read the prompt from stdin when
    /// it is not an argument, so it moves there: Claude Code drops the
    /// trailing <c>-- &lt;prompt&gt;</c>, Codex gets <c>-- -</c> (its "read
    /// stdin" marker). Copilot and Antigravity have no documented stdin
    /// prompt, so theirs stays on the command line (and a too-long one fails
    /// with <see cref="CliProviderError.CommandLineTooLong"/>).
    /// </summary>
    public static (IReadOnlyList<string> Argv, string? StandardInput) WindowsInvocation(CliTool tool,
        IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var count = argv.Count;
        if (count < 2 || argv[count - 2] != "--") return (argv, null);
        var prompt = argv[count - 1];
        return tool switch
        {
            CliTool.ClaudeCode => (argv.Take(count - 2).ToList(), prompt),
            CliTool.Codex => ([.. argv.Take(count - 1), "-"], prompt),
            _ => (argv, null),
        };
    }

    private static string Normalized(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

/// <summary>
/// <c>--version</c> output per program path, for the life of the process
/// (Antigravity's store edits are gated on it). Port of <c>CLIVersionCache</c>.
/// </summary>
public sealed class CliVersionCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> byPath;

    public CliVersionCache(IReadOnlyDictionary<string, string>? initial = null)
    {
        byPath = initial is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(initial, StringComparer.OrdinalIgnoreCase);
    }

    public static CliVersionCache Shared { get; } = new();

    public string? Version(string executable)
    {
        lock (gate)
        {
            return byPath.TryGetValue(executable, out var version) ? version : null;
        }
    }

    public void Set(string version, string executable)
    {
        lock (gate)
        {
            byPath[executable] = version;
        }
    }
}

/// <summary>
/// Meeting notes through a locally installed CLI that uses its own login:
/// GitHub Copilot CLI (port of the Copilot branch of Python
/// <c>generate_meeting_notes</c>), Claude Code, Codex, or Antigravity. The
/// preset's kind picks the tool. No token is sent; each run happens in a
/// fresh temporary folder. Port of <c>CLIClient</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/CLIClient.swift. Windows
/// differences: the prompt may travel on stdin (<see cref="CliArguments.WindowsInvocation"/>),
/// and <see cref="LocateAsync"/> starts no process.
/// </summary>
public sealed class CliClient : IChatCompleting
{
    /// <summary>Generous: a long meeting at reasoning effort "max" takes minutes.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(600);

    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    private readonly CliRunner runner;
    private readonly CliLocator locator;
    private readonly AntigravityHousekeeping antigravity;
    private readonly CliVersionCache versions;
    private readonly Func<IReadOnlyDictionary<string, string>> baseEnvironment;
    private readonly string temporaryRoot;

    /// <param name="runner">Starts the programs; null uses <see cref="CliProcessRunner.Run"/>.</param>
    /// <param name="locator">Finds them; null searches the current user's folders and <c>PATH</c>.</param>
    /// <param name="antigravity">agy housekeeping; null uses the current user's <c>.gemini</c> folder.</param>
    /// <param name="versions">The <c>--version</c> cache; null uses <see cref="CliVersionCache.Shared"/>.</param>
    /// <param name="baseEnvironment">The environment the child starts from; null uses this process's.</param>
    /// <param name="temporaryRoot">Where each run's folder is made; null uses <see cref="Path.GetTempPath"/>.</param>
    public CliClient(
        CliRunner? runner = null,
        CliLocator? locator = null,
        AntigravityHousekeeping? antigravity = null,
        CliVersionCache? versions = null,
        Func<IReadOnlyDictionary<string, string>>? baseEnvironment = null,
        string? temporaryRoot = null)
    {
        this.runner = runner ?? CliProcessRunner.Run;
        this.locator = locator ?? new CliLocator();
        this.antigravity = antigravity ?? new AntigravityHousekeeping();
        this.versions = versions ?? CliVersionCache.Shared;
        this.baseEnvironment = baseEnvironment ?? CurrentEnvironment;
        this.temporaryRoot = temporaryRoot ?? Path.GetTempPath();
    }

    /// <summary>This process's environment, keys compared ignoring case as Windows does.</summary>
    public static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }
        return environment;
    }

    /// <summary>
    /// The inherited environment with the program's folder first on
    /// <c>PATH</c>, so an npm shim finds the <c>node.exe</c> that installed it.
    /// For Claude Code, Codex, and Antigravity, API-key variables are removed
    /// so the subscription (or Google account) login is used, and so are
    /// Claude Code's own session variables. Keys compare ignoring case.
    /// </summary>
    public static Dictionary<string, string> Environment(CliTool tool, string executable,
        IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var source = baseEnvironment ?? CurrentEnvironment();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (!Removed(tool, key)) environment[key] = value;
        }
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        var path = source.FirstOrDefault(pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(path))
        {
            var system = System.Environment.SystemDirectory;
            path = $"{system};{Path.GetDirectoryName(system)};{Path.Combine(system, "Wbem")}";
        }
        foreach (var key in environment.Keys.Where(key => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(key);
        }
        environment["PATH"] = directory + ";" + path;
        return environment;
    }

    private static bool Removed(CliTool tool, string key)
    {
        bool Is(string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
        return tool switch
        {
            CliTool.ClaudeCode => Is("ANTHROPIC_API_KEY") || Is("ANTHROPIC_AUTH_TOKEN") || Is("CLAUDECODE")
                || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase),
            CliTool.Codex => Is("OPENAI_API_KEY") || Is("CODEX_API_KEY"),
            CliTool.Antigravity => Is("GEMINI_API_KEY") || Is("GOOGLE_API_KEY") || Is("GOOGLE_APPLICATION_CREDENTIALS"),
            _ => false,
        };
    }

    /// <summary>
    /// The part of a failed run worth showing, at most
    /// <see cref="ChatCompletionsError.ExcerptLength"/> characters. Copilot:
    /// the start of stderr (Python parity). Claude Code prints its errors on
    /// stdout in <c>--print</c> mode, so stdout is used when stderr is empty.
    /// Codex starts stderr with a banner and the prompt, so its last
    /// <c>ERROR:</c> line (or the end of stderr) is used. Antigravity: its last
    /// <c>Error:</c>, <c>error:</c>, <c>jetski:</c>, or <c>AGY_ERROR</c> line on
    /// stderr, else the end of stderr, else stdout.
    /// </summary>
    public static string Excerpt(CliTool tool, CliRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        const int limit = ChatCompletionsError.ExcerptLength;
        switch (tool)
        {
            case CliTool.Copilot:
                return TextElements.Prefix(result.Stderr, limit);
            case CliTool.ClaudeCode:
                return TextElements.Prefix(result.Stderr.Trim().Length == 0 ? result.Stdout : result.Stderr, limit);
            case CliTool.Codex:
                {
                    var errorLine = Lines(result.Stderr).LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
                    return errorLine is not null
                        ? TextElements.Prefix(errorLine, limit)
                        : TextElements.Suffix(result.Stderr, limit);
                }
            case CliTool.Antigravity:
                {
                    var errorLine = Lines(result.Stderr).LastOrDefault(line =>
                    {
                        var lowered = line.ToLowerInvariant();
                        return lowered.StartsWith("error", StringComparison.Ordinal)
                            || lowered.StartsWith("jetski:", StringComparison.Ordinal)
                            || line.Contains("AGY_ERROR", StringComparison.Ordinal);
                    });
                    if (errorLine is not null) return TextElements.Prefix(errorLine, limit);
                    var stderr = result.Stderr.Trim();
                    if (stderr.Length > 0) return TextElements.Suffix(stderr, limit);
                    return TextElements.Prefix(result.Stdout, limit);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(tool), tool, null);
        }
    }

    /// <summary>Swift <c>split(whereSeparator: \.isNewline)</c>: non-empty lines.</summary>
    private static string[] Lines(string text) =>
        text.Split(NewlineCharacters, StringSplitOptions.RemoveEmptyEntries);

    private static readonly char[] NewlineCharacters =
        [(char)0x0D, (char)0x0A, (char)0x0B, (char)0x0C, (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>The program for <paramref name="tool"/> (see <see cref="CliLocator.Locate"/>). No process is started.</summary>
    public Task<string> LocateAsync(CliTool tool, string? configuredPath) =>
        Task.FromResult(locator.Locate(tool, configuredPath));

    /// <summary>
    /// <c>--version</c> of <paramref name="executable"/>, from the cache or by
    /// running it once (then cached). Null when it fails.
    /// </summary>
    internal async Task<string?> CachedVersionAsync(CliTool tool, string executable, CancellationToken cancellationToken)
    {
        if (versions.Version(executable) is { } known) return known;
        string? version;
        try
        {
            version = await RunAsync(tool, executable, VersionTimeout, prepare: null,
                _ => ([executable, "--version"], null),
                (result, _) => !result.TimedOut && result.ExitCode == 0 ? result.Stdout.Trim() : null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CliProviderException)
        {
            version = null;
        }
        if (version is not null) versions.Set(version, executable);
        return version;
    }

    public async Task<string> CompleteAsync(string systemMessage, string userMessage,
        AIProviderConfiguration configuration, string? token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemMessage);
        ArgumentNullException.ThrowIfNull(userMessage);
        ArgumentNullException.ThrowIfNull(configuration);
        var preset = configuration.Preset;
        if (preset.Kind.CliTool() is not { } tool)
        {
            throw new CliProviderException(new CliProviderError.NotACliPreset(preset.Name));
        }
        var executable = await LocateAsync(tool, configuration.CliPath(tool)).ConfigureAwait(false);
        var agyVersion = tool == CliTool.Antigravity
            ? await CachedVersionAsync(tool, executable, cancellationToken).ConfigureAwait(false)
            : null;
        if (tool == CliTool.Antigravity)
        {
            // Never run agy without the deny rules in place.
            try
            {
                antigravity.EnsureProject();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AntigravityWriteException)
            {
                throw new CliProviderException(new CliProviderError.LaunchFailed(tool,
                    $"Could not set up the {CliArguments.AntigravityProjectName} project in {antigravity.ProjectsFolder}: {error.Message}"));
            }
        }
        return await RunAsync(tool, executable, Timeout,
            prepare: directory =>
            {
                if (tool != CliTool.Antigravity) return directory;
                // The agent sees an empty folder; the schema and log sit beside it.
                File.WriteAllBytes(Path.Combine(directory, CliArguments.AntigravitySchemaFileName),
                    Encoding.UTF8.GetBytes(CliArguments.NotesReplySchema));
                var workspace = Path.Combine(directory, CliArguments.AntigravityProjectName);
                Directory.CreateDirectory(workspace);
                return workspace;
            },
            directory =>
            {
                IReadOnlyList<string> argv = tool switch
                {
                    CliTool.Copilot => CliArguments.Copilot(executable, userMessage, configuration.Model,
                        configuration.ReasoningEffort),
                    CliTool.ClaudeCode => CliArguments.ClaudeCode(executable, userMessage, systemMessage,
                        configuration.Model, configuration.ReasoningEffort),
                    CliTool.Codex => CliArguments.Codex(executable, userMessage, configuration.Model,
                        configuration.ReasoningEffort, directory,
                        Path.Combine(directory, CliArguments.CodexReplyFileName)),
                    _ => CliArguments.Antigravity(executable,
                        CliArguments.AntigravityPrompt(systemMessage, userMessage), configuration.Model,
                        configuration.ReasoningEffort,
                        Path.Combine(directory, CliArguments.AntigravitySchemaFileName),
                        Path.Combine(directory, CliArguments.AntigravityLogFileName)),
                };
                return CliArguments.WindowsInvocation(tool, argv);
            },
            (result, directory) =>
            {
                // agy keeps every run in its history; delete this one, whatever
                // the outcome. No conversation id: nothing is deleted.
                if (tool == CliTool.Antigravity && AntigravityHousekeeping.ConversationId(result.Stdout) is { } id)
                {
                    antigravity.RemoveConversation(id, agyVersion);
                }
                return Reply(tool, result, directory);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The reply of a finished notes run, or the error it maps to.</summary>
    internal static string Reply(CliTool tool, CliRunResult result, string directory)
    {
        if (result.TimedOut) throw new CliProviderException(new CliProviderError.TimedOut(tool));
        if (result.ExitCode != 0)
        {
            var excerpt = Excerpt(tool, result);
            if (CliProviderError.IndicatesLoggedOut(result.Stderr + "\n" + result.Stdout, tool))
            {
                throw new CliProviderException(new CliProviderError.NotLoggedIn(tool, excerpt));
            }
            throw new CliProviderException(new CliProviderError.Failed(tool, result.ExitCode, excerpt));
        }
        var reply = tool switch
        {
            CliTool.Copilot or CliTool.ClaudeCode => result.Stdout,
            CliTool.Codex => ReadReplyFile(Path.Combine(directory, CliArguments.CodexReplyFileName)),
            _ => AntigravityReply(result),
        };
        if (reply.Trim().Length == 0) throw new CliProviderException(new CliProviderError.EmptyOutput(tool));
        return reply;
    }

    private static string ReadReplyFile(string path)
    {
        try
        {
            return File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The reply inside <c>agy --output-format json</c>: <c>structured_output</c>
    /// (the object the schema enforced, re-encoded with sorted keys) when
    /// present, else <c>response</c>. Output that is not such an envelope is
    /// returned as is; a <c>status</c> other than <c>SUCCESS</c> fails.
    /// </summary>
    private static readonly string[] EnvelopeKeys = ["status", "response", "structured_output"];

    internal static string AntigravityReply(CliRunResult result)
    {
        JsonObject? envelope;
        try
        {
            envelope = JsonNode.Parse(result.Stdout) as JsonObject;
        }
        catch (JsonException)
        {
            envelope = null;
        }
        if (envelope is null || !EnvelopeKeys.Any(envelope.ContainsKey))
        {
            return result.Stdout;
        }
        if (StringValue(envelope["status"]) is { } status && status != "SUCCESS")
        {
            var detail = StringValue(envelope["error"]) is { } error
                ? TextElements.Prefix(error, ChatCompletionsError.ExcerptLength)
                : Excerpt(CliTool.Antigravity, result);
            throw new CliProviderException(new CliProviderError.Failed(CliTool.Antigravity, result.ExitCode, detail));
        }
        if (envelope["structured_output"] is JsonObject or JsonArray)
        {
            return SortedKeys(envelope["structured_output"])?.ToJsonString() ?? string.Empty;
        }
        return StringValue(envelope["response"]) ?? string.Empty;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A copy of <paramref name="node"/> with every object's keys in ordinal order.</summary>
    internal static JsonNode? SortedKeys(JsonNode? node) => node switch
    {
        JsonObject json => new JsonObject(json.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, SortedKeys(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(SortedKeys).ToArray()),
        _ => node?.DeepClone(),
    };

    /// <summary>The program's path, <c>--version</c> output, and login state, for Settings &gt; AI.</summary>
    public async Task<CliInstallation> CheckInstallationAsync(CliTool tool, string? configuredPath,
        CancellationToken cancellationToken = default)
    {
        var executable = await LocateAsync(tool, configuredPath).ConfigureAwait(false);
        var version = await RunAsync(tool, executable, VersionTimeout, prepare: null,
            _ => ([executable, "--version"], null),
            (result, _) =>
            {
                if (result.TimedOut) throw new CliProviderException(new CliProviderError.TimedOut(tool));
                if (result.ExitCode != 0)
                {
                    throw new CliProviderException(new CliProviderError.Failed(tool, result.ExitCode, Excerpt(tool, result)));
                }
                return result.Stdout.Trim();
            },
            cancellationToken).ConfigureAwait(false);
        versions.Set(version, executable);
        var installation = new CliInstallation(tool, executable, version);
        if (tool.LoginStatusArguments() is { } statusArguments)
        {
            var (text, loggedIn) = await RunAsync(tool, executable, tool.LoginStatusTimeout(), prepare: null,
                directory => (tool == CliTool.Antigravity
                    // `agy models` starts agy's backend, which logs to
                    // ~/.gemini/antigravity-cli/log unless told otherwise.
                    ? [executable, "--log-file", Path.Combine(directory, CliArguments.AntigravityLogFileName), .. statusArguments]
                    : (IReadOnlyList<string>)[executable, .. statusArguments], null),
                (result, _) => LoginStatus(tool, result),
                cancellationToken).ConfigureAwait(false);
            installation = installation with { LoginStatus = text, LoggedIn = loggedIn };
        }
        return installation;
    }

    /// <summary>
    /// One line from <c>claude auth status</c> (JSON), <c>codex login status</c>
    /// (plain text on stderr), or <c>agy models</c> (only whether it worked
    /// and how many models it listed). The account e-mail is not shown.
    /// </summary>
    internal static (string Text, bool LoggedIn) LoginStatus(CliTool tool, CliRunResult result)
    {
        var advice = $"Run `{tool.LoginCommand()}` once in Terminal to log in.";
        var notLoggedIn = $"Not logged in. {advice}";
        const string loggedInText = "Logged in";
        if (result.TimedOut) return ("The login status check did not answer.", false);
        switch (tool)
        {
            case CliTool.ClaudeCode:
                {
                    JsonObject? status;
                    try
                    {
                        status = JsonNode.Parse(result.Stdout) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        status = null;
                    }
                    if (status?["loggedIn"] is JsonValue flag && flag.TryGetValue<bool>(out var loggedIn))
                    {
                        if (!loggedIn) return (notLoggedIn, false);
                        var text = loggedInText;
                        if (StringValue(status["authMethod"]) is { Length: > 0 } method) text = $"Logged in via {method}";
                        if (StringValue(status["subscriptionType"]) is { Length: > 0 } plan) text += " " + $"({plan} plan)";
                        return (text, true);
                    }
                    break;
                }
            case CliTool.Antigravity:
                if (result.ExitCode == 0)
                {
                    // One "<id>\t<label>" line per model after "Fetching available models...".
                    var count = Lines(result.Stdout).Count(line => line.Contains('\t', StringComparison.Ordinal));
                    return (count == 1
                        ? "Logged in; 1 model available"
                        : $"Logged in; {count.ToString(CultureInfo.InvariantCulture)} models available", true);
                }
                if (CliProviderError.IndicatesLoggedOut(result.Stdout + "\n" + result.Stderr, tool))
                {
                    return (notLoggedIn, false);
                }
                return ($"`agy models` failed (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}). {advice}", false);
            default:
                break;
        }
        var output = string.Join(" ", new[] { result.Stdout, result.Stderr }
            .Select(text => text.Trim())
            .Where(text => text.Length > 0));
        var line = TextElements.Prefix(output, ChatCompletionsError.ExcerptLength);
        if (result.ExitCode == 0 && !output.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return (line.Length == 0 ? loggedInText : line, true);
        }
        return (line.Length == 0 ? notLoggedIn : $"{line}. {advice}", false);
    }

    /// <summary>
    /// Runs in a fresh temporary folder, removed afterwards: the CLIs are
    /// agents that could use tools, so they are kept away from the user's
    /// files. <paramref name="prepare"/> may add files and returns the
    /// working folder (default: the folder itself). <paramref name="finish"/>
    /// reads anything it needs from the folder before it is removed.
    /// </summary>
    private async Task<T> RunAsync<T>(
        CliTool tool,
        string executable,
        TimeSpan timeout,
        Func<string, string>? prepare,
        Func<string, (IReadOnlyList<string> Argv, string? StandardInput)> makeInvocation,
        Func<CliRunResult, string, T> finish,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(temporaryRoot, tool.TemporaryFolderPrefix() + Guid.NewGuid().ToString("D").ToUpperInvariant());
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CliProviderException(new CliProviderError.LaunchFailed(tool, error.Message));
        }
        try
        {
            string workingDirectory;
            try
            {
                workingDirectory = prepare is null ? directory : prepare(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CliProviderException(new CliProviderError.LaunchFailed(tool, error.Message));
            }
            var (argv, standardInput) = makeInvocation(directory);
            CliRunResult result;
            try
            {
                result = await runner(
                    new CliRunRequest(argv, workingDirectory, Environment(tool, executable, baseEnvironment()), timeout,
                        standardInput),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CliProviderException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CliCommandLineTooLongException error)
            {
                throw new CliProviderException(new CliProviderError.CommandLineTooLong(tool, error.Length));
            }
            catch (Exception error)
            {
                throw new CliProviderException(new CliProviderError.LaunchFailed(tool, error.Message));
            }
            return finish(result, directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
