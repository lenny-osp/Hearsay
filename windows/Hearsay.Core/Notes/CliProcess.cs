using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Notes;

/// <summary>
/// A command-line program that writes meeting notes with the user's own
/// login: GitHub Copilot CLI, Claude Code, OpenAI Codex, or Google
/// Antigravity. Port of <c>CLITool</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/CLIProcess.swift; the members
/// are in <see cref="CliTools"/>.
/// </summary>
public enum CliTool
{
    Copilot,
    ClaudeCode,
    Codex,
    Antigravity,
}

/// <summary>
/// The Swift <c>CLITool</c> raw values and computed properties, with the
/// Windows install commands and file names (PLAN.md 18.3, Meeting notes).
/// </summary>
public static class CliTools
{
    /// <summary>Every tool, in the Swift <c>allCases</c> order (the preset order).</summary>
    public static IReadOnlyList<CliTool> All { get; } =
        [CliTool.Copilot, CliTool.ClaudeCode, CliTool.Codex, CliTool.Antigravity];

    /// <summary>The stored value ("copilot", "claudeCode", "codex", "antigravity").</summary>
    public static string RawValue(this CliTool tool) => tool switch
    {
        CliTool.Copilot => "copilot",
        CliTool.ClaudeCode => "claudeCode",
        CliTool.Codex => "codex",
        CliTool.Antigravity => "antigravity",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    public static CliTool? FromRawValue(string rawValue) => rawValue switch
    {
        "copilot" => CliTool.Copilot,
        "claudeCode" => CliTool.ClaudeCode,
        "codex" => CliTool.Codex,
        "antigravity" => CliTool.Antigravity,
        _ => null,
    };

    /// <summary>The executable's base name, without an extension.</summary>
    public static string BinaryName(this CliTool tool) => tool switch
    {
        CliTool.Copilot => "copilot",
        CliTool.ClaudeCode => "claude",
        CliTool.Codex => "codex",
        CliTool.Antigravity => "agy",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>
    /// The file names looked for in each folder, in order: the native
    /// program (<c>winget</c>, the Claude Code and Antigravity installers)
    /// before the npm <c>.cmd</c> shim. npm also writes an extensionless
    /// shell script and a <c>.ps1</c>; neither can be started directly.
    /// </summary>
    public static IReadOnlyList<string> FileNames(this CliTool tool) =>
        [tool.BinaryName() + ".exe", tool.BinaryName() + ".cmd"];

    public static string DisplayName(this CliTool tool) => tool switch
    {
        CliTool.Copilot => "GitHub Copilot CLI",
        CliTool.ClaudeCode => "Claude Code CLI",
        CliTool.Codex => "Codex CLI",
        CliTool.Antigravity => "Antigravity CLI",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>For the "Check &lt;name&gt;" button.</summary>
    public static string ShortName(this CliTool tool) => tool switch
    {
        CliTool.Copilot => "Copilot",
        CliTool.ClaudeCode => "Claude Code",
        CliTool.Codex => "Codex",
        CliTool.Antigravity => "Antigravity",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>
    /// The documented Windows install command. Copilot and Codex: npm, as on
    /// the Mac (Copilot also ships as <c>winget install GitHub.Copilot</c>).
    /// Claude Code: the native PowerShell installer
    /// (https://claude.ai/install.ps1, into <c>%USERPROFILE%\.local\bin</c>).
    /// Antigravity: the installer from https://antigravity.google/docs/cli/install
    /// (into <c>%LOCALAPPDATA%\agy\bin</c>).
    /// </summary>
    public static string InstallCommand(this CliTool tool) => tool switch
    {
        CliTool.Copilot => "npm install -g @github/copilot",
        CliTool.ClaudeCode => "irm https://claude.ai/install.ps1 | iex",
        CliTool.Codex => "npm install -g @openai/codex",
        CliTool.Antigravity =>
            "curl -fsSL https://antigravity.google/cli/install.cmd -o install.cmd && install.cmd && del install.cmd",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>What to run once in a terminal to log in.</summary>
    public static string LoginCommand(this CliTool tool) => tool switch
    {
        CliTool.Copilot => "copilot",
        CliTool.ClaudeCode => "claude",
        CliTool.Codex => "codex login",
        CliTool.Antigravity => "agy",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>
    /// Arguments after the binary that report the login state, or null when
    /// the CLI has no such command. Antigravity has none; <c>agy models</c>
    /// only succeeds when logged in, so it stands in.
    /// </summary>
    public static IReadOnlyList<string>? LoginStatusArguments(this CliTool tool) => tool switch
    {
        CliTool.Copilot => null,
        CliTool.ClaudeCode => ["auth", "status"],
        CliTool.Codex => ["login", "status"],
        CliTool.Antigravity => ["models"],
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>
    /// Time limit of the login status command. <c>agy models</c> asks the
    /// server, so it gets longer than a local status check.
    /// </summary>
    public static TimeSpan LoginStatusTimeout(this CliTool tool) =>
        tool == CliTool.Antigravity ? TimeSpan.FromSeconds(20) : CliClient.VersionTimeout;

    /// <summary>Prefix of the temporary working folder of one run.</summary>
    public static string TemporaryFolderPrefix(this CliTool tool) => $"Hearsay-{tool.RawValue()}-";
}

/// <summary>
/// Why a CLI provider call produced no usable reply. Port of
/// <c>CLIProviderError</c> in CLIProcess.swift; the Swift enum cases are
/// records and <see cref="CliProviderException"/> carries one. The Copilot
/// wording follows the Copilot branch of Python <c>generate_meeting_notes</c>.
/// The texts are the English ones (the Swift catalog keys); the app
/// localizes them (W7).
/// </summary>
public abstract record CliProviderError
{
    private CliProviderError()
    {
    }

    /// <summary>No binary at the configured path or any searched location.</summary>
    public sealed record NotInstalled(CliTool Tool, IReadOnlyList<string> Searched) : CliProviderError
    {
        public bool Equals(NotInstalled? other) =>
            other is not null && Tool == other.Tool && Searched.SequenceEqual(other.Searched, StringComparer.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Tool, Searched.Count);
    }

    /// <summary>The binary could not be started.</summary>
    public sealed record LaunchFailed(CliTool Tool, string Detail) : CliProviderError
    {
        /// <summary><see cref="Detail"/> with its catalog key, when Hearsay wrote it (the Antigravity project set-up).</summary>
        public ILocalizedMessage? LocalizedDetail { get; init; }
    }

    /// <summary>
    /// Nonzero exit status. <see cref="Excerpt"/> is at most
    /// <see cref="ChatCompletionsError.ExcerptLength"/> characters (<see cref="CliClient.Excerpt"/>).
    /// </summary>
    public sealed record Failed(CliTool Tool, int ExitCode, string Excerpt) : CliProviderError;

    /// <summary>
    /// Nonzero exit status whose output says the CLI has no valid login
    /// (Claude Code, Codex, Antigravity; Copilot keeps the Python wording).
    /// </summary>
    public sealed record NotLoggedIn(CliTool Tool, string Excerpt) : CliProviderError;

    /// <summary>Still running after <see cref="CliClient.Timeout"/>; it was stopped.</summary>
    public sealed record TimedOut(CliTool Tool) : CliProviderError;

    /// <summary>Exit status 0 but no reply.</summary>
    public sealed record EmptyOutput(CliTool Tool) : CliProviderError;

    /// <summary>The configuration's preset does not run a CLI.</summary>
    public sealed record NotACliPreset(string PresetName) : CliProviderError;

    /// <summary>
    /// Windows only: the prompt goes on the command line (Copilot,
    /// Antigravity) and the command line would be longer than Windows allows
    /// (<see cref="WindowsCommandLine.MaxLength"/>). Nothing was started.
    /// </summary>
    public sealed record CommandLineTooLong(CliTool Tool, int Length) : CliProviderError;

    /// <summary>The message shown to the user (Swift <c>errorDescription</c>).</summary>
    public string Description => this switch
    {
        NotInstalled e =>
            $"{e.Tool.DisplayName()} not found. Install it with `{e.Tool.InstallCommand()}` or set the path in Settings > AI.",
        LaunchFailed { Tool: CliTool.Copilot } e =>
            $"GitHub Copilot CLI call failed. Check the Copilot login status and model configuration: {e.Detail}",
        LaunchFailed e => $"{e.Tool.DisplayName()} could not be started: {e.Detail}",
        Failed e => FailedMessage(e),
        NotLoggedIn e => NotLoggedInMessage(e),
        TimedOut e =>
            $"{e.Tool.DisplayName()} did not answer within {(int)CliClient.Timeout.TotalMinutes} minutes and was stopped.",
        EmptyOutput e => $"{e.Tool.DisplayName()} returned empty output.",
        NotACliPreset e => $"The {e.PresetName} preset does not use a command-line tool.",
        CommandLineTooLong e =>
            $"{e.Tool.DisplayName()} cannot take a transcript this long on Windows: the command line would be "
            + $"{e.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, and Windows allows "
            + $"{WindowsCommandLine.MaxLength.ToString("N0", CultureInfo.InvariantCulture)}. "
            + "Use Claude Code, Codex, or an HTTP provider for long meetings.",
        _ => throw new InvalidOperationException("Unknown CliProviderError."),
    };

    /// <summary>
    /// <see cref="Description"/> as its catalog keys and values, for the app
    /// to translate; null for <see cref="CommandLineTooLong"/>, which has no
    /// key in shared/localization yet (Windows only).
    /// </summary>
    public LocalizedMessage? Localized => this switch
    {
        NotInstalled e => new LocalizedMessage(
            "%@ not found. Install it with `%@` or set the path in Settings > AI.", e.Tool.DisplayName(), e.Tool.InstallCommand()),
        LaunchFailed { Tool: CliTool.Copilot } e => new LocalizedMessage(
            "GitHub Copilot CLI call failed. Check the Copilot login status and model configuration: %@",
            (object?)e.LocalizedDetail ?? e.Detail),
        LaunchFailed e => new LocalizedMessage("%@ could not be started: %@", e.Tool.DisplayName(),
            (object?)e.LocalizedDetail ?? e.Detail),
        Failed e => FailedLocalized(e),
        NotLoggedIn e => NotLoggedInLocalized(e),
        TimedOut e => new LocalizedMessage("%@ did not answer within %lld minutes and was stopped.",
            e.Tool.DisplayName(), (int)CliClient.Timeout.TotalMinutes),
        EmptyOutput e => new LocalizedMessage("%@ returned empty output.", e.Tool.DisplayName()),
        NotACliPreset e => new LocalizedMessage("The %@ preset does not use a command-line tool.", e.PresetName),
        _ => null,
    };

    private static LocalizedMessage FailedLocalized(Failed e)
    {
        var trimmed = e.Excerpt.Trim();
        var message = e.Tool == CliTool.Copilot
            ? new LocalizedMessage(
                "GitHub Copilot CLI call failed (exit code %d). Check the Copilot login status and model configuration.", e.ExitCode)
            : new LocalizedMessage(
                "%@ call failed (exit code %d). Check the model and reasoning effort in Settings > AI.", e.Tool.DisplayName(), e.ExitCode);
        if (trimmed.Length > 0) message = message.Appending("\n", trimmed);
        if (e.Tool == CliTool.Copilot && MentionsLogin(trimmed))
        {
            message = message.Appending("\n", new LocalizedMessage("Run `copilot` once in Terminal to log in."));
        }
        return message;
    }

    private static LocalizedMessage NotLoggedInLocalized(NotLoggedIn e)
    {
        var key = e.Tool switch
        {
            CliTool.ClaudeCode => "%@ is not logged in. Run `%@` once in Terminal to log in with your Claude subscription.",
            CliTool.Codex => "%@ is not logged in. Run `%@` once in Terminal to log in with your ChatGPT account.",
            CliTool.Antigravity => "%@ is not logged in. Run `%@` once in Terminal to log in with your Google account.",
            _ => "%@ is not logged in. Run `%@` once in Terminal to log in.",
        };
        var message = new LocalizedMessage(key, e.Tool.DisplayName(), e.Tool.LoginCommand());
        var trimmed = e.Excerpt.Trim();
        return trimmed.Length > 0 ? message.Appending("\n", trimmed) : message;
    }

    private static string FailedMessage(Failed e)
    {
        var trimmed = e.Excerpt.Trim();
        var message = e.Tool == CliTool.Copilot
            ? $"GitHub Copilot CLI call failed (exit code {e.ExitCode}). Check the Copilot login status and model configuration."
            : $"{e.Tool.DisplayName()} call failed (exit code {e.ExitCode}). Check the model and reasoning effort in Settings > AI.";
        if (trimmed.Length > 0)
        {
            message += "\n" + trimmed;
        }
        if (e.Tool == CliTool.Copilot && MentionsLogin(trimmed))
        {
            message += "\n" + "Run `copilot` once in Terminal to log in.";
        }
        return message;
    }

    private static string NotLoggedInMessage(NotLoggedIn e)
    {
        var name = e.Tool.DisplayName();
        var command = e.Tool.LoginCommand();
        var message = e.Tool switch
        {
            CliTool.ClaudeCode =>
                $"{name} is not logged in. Run `{command}` once in Terminal to log in with your Claude subscription.",
            CliTool.Codex =>
                $"{name} is not logged in. Run `{command}` once in Terminal to log in with your ChatGPT account.",
            CliTool.Antigravity =>
                $"{name} is not logged in. Run `{command}` once in Terminal to log in with your Google account.",
            _ => $"{name} is not logged in. Run `{command}` once in Terminal to log in.",
        };
        var trimmed = e.Excerpt.Trim();
        if (trimmed.Length > 0)
        {
            message += "\n" + trimmed;
        }
        return message;
    }

    /// <summary>Copilot: any hint of a login problem (Python parity).</summary>
    internal static bool MentionsLogin(string text)
    {
        var lowered = text.ToLowerInvariant();
        string[] markers = ["auth", "login", "log in", "logged in", "sign in", "token"];
        return markers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Claude Code, Codex, Antigravity: output that means the login is
    /// missing or no longer valid. Stricter than <see cref="MentionsLogin"/>,
    /// because Codex always prints "tokens used".
    /// </summary>
    internal static bool IndicatesLoggedOut(string text, CliTool tool)
    {
        var lowered = text.ToLowerInvariant();
        string[] markers = tool switch
        {
            CliTool.Copilot => [],
            // "Not logged in · Please run /login", "Invalid API key · Please
            // run /login", "OAuth token has expired", "API Error: 401".
            CliTool.ClaudeCode => ["not logged in", "please run /login", "invalid api key", "oauth token", "api error: 401"],
            // "Not logged in"; with no login every request ends in
            // "unexpected status 401 Unauthorized".
            CliTool.Codex => ["not logged in", "401 unauthorized", "codex login"],
            // Print mode without a login shows "Authentication required.
            // Please visit the URL to log in:", waits 60 s, then "Error:
            // authentication timed out." / "error: authentication failed or
            // timed out". `agy models`: "Please sign in to view available models."
            CliTool.Antigravity =>
            [
                "authentication required", "authentication failed", "authentication timed out",
                "not logged into antigravity", "please sign in",
            ],
            _ => [],
        };
        return markers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }
}

/// <summary>Thrown by <see cref="CliClient"/>; <see cref="Error"/> says what failed.</summary>
public sealed class CliProviderException : Exception, ILocalizedError
{
    public CliProviderException(CliProviderError error)
        : base(error?.Description)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public CliProviderError Error { get; }

    public ILocalizedMessage? LocalizedMessage => Error.Localized;
}

/// <summary>
/// Thrown by a <see cref="CliRunner"/> when the program could not be
/// started; <see cref="CliClient"/> maps it to
/// <see cref="CliProviderError.LaunchFailed"/> for the right tool. Port of
/// <c>CLIProcessRunner.LaunchError</c>.
/// </summary>
public class CliLaunchException : Exception
{
    public CliLaunchException(string detail)
        : base(detail)
    {
    }

    public CliLaunchException(string detail, Exception innerException)
        : base(detail, innerException)
    {
    }
}

/// <summary>
/// Thrown by <see cref="CliProcessRunner"/> before starting anything when the
/// command line is longer than Windows allows.
/// </summary>
public sealed class CliCommandLineTooLongException : CliLaunchException
{
    public CliCommandLineTooLongException(int length)
        : base($"The command line is {length} characters long; Windows allows {WindowsCommandLine.MaxLength}.")
    {
        Length = length;
    }

    public int Length { get; }
}

/// <summary>What one run of a program produced. Port of <c>CLIRunResult</c>.</summary>
/// <param name="TimedOut">True when the run was stopped because it exceeded its timeout.</param>
public sealed record CliRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false);

/// <summary>
/// One program run: <see cref="Argv"/>[0] is the full path of the program,
/// started in <see cref="Directory"/> with exactly <see cref="Environment"/>
/// and stopped after <see cref="Timeout"/>. <see cref="StandardInput"/> is
/// written to the program's stdin (UTF-8, no byte-order mark), which is then
/// closed; null gives it an empty stdin (the Mac's <c>/dev/null</c>).
/// </summary>
public sealed record CliRunRequest(
    IReadOnlyList<string> Argv,
    string Directory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    string? StandardInput = null);

/// <summary>
/// Runs a program (Swift <c>CLIRunner</c>). Injected in tests so no process
/// is started. Throws <see cref="CliLaunchException"/> when the program
/// cannot be started and <see cref="OperationCanceledException"/> when
/// cancelled (after stopping it).
/// </summary>
public delegate Task<CliRunResult> CliRunner(CliRunRequest request, CancellationToken cancellationToken);

/// <summary>Where a CLI was found, what <c>--version</c> says, and its login state. Port of <c>CLIInstallation</c>.</summary>
/// <param name="LoginStatus">One line from the CLI's login status command; null when it has none.</param>
/// <param name="LoggedIn">Null when unknown (no status command).</param>
public sealed record CliInstallation(
    CliTool Tool,
    string Path,
    string Version,
    string? LoginStatus = null,
    bool? LoggedIn = null)
{
    /// <summary>
    /// <see cref="LoginStatus"/> as its catalog key and values, for the app
    /// to translate; null when there is no status or the line is the CLI's own output.
    /// </summary>
    public ILocalizedMessage? LocalizedLoginStatus { get; init; }
}

/// <summary>
/// The per-user folders <see cref="CliLocator"/> searches, from the
/// environment by default; tests point them at scratch paths.
/// </summary>
/// <param name="Home"><c>%USERPROFILE%</c>.</param>
/// <param name="LocalAppData"><c>%LOCALAPPDATA%</c>.</param>
/// <param name="RoamingAppData"><c>%APPDATA%</c>.</param>
/// <param name="ProgramFiles"><c>%ProgramFiles%</c>.</param>
public sealed record CliSearchFolders(string Home, string LocalAppData, string RoamingAppData, string ProgramFiles)
{
    /// <summary>The current user's folders.</summary>
    public static CliSearchFolders Current() => new(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles));

    /// <summary>The default layout under <paramref name="home"/> (<c>AppData\Local</c>, <c>AppData\Roaming</c>).</summary>
    public static CliSearchFolders ForHome(string home, string programFiles = @"C:\Program Files") => new(
        home,
        Path.Combine(home, "AppData", "Local"),
        Path.Combine(home, "AppData", "Roaming"),
        programFiles);
}

/// <summary>
/// Finds a CLI binary (PLAN.md section 7). Port of <c>CLILocator</c> in
/// CLIProcess.swift with Windows locations: the Mac's <c>~/.local/bin</c>,
/// Homebrew, <c>/usr/local/bin</c> and nvm become the installers' folders,
/// the WinGet links folder, npm's global folder and nvm-windows; the Mac's
/// last resort, a login-shell <c>command -v</c>, becomes a search of
/// <c>PATH</c> in this process (a Windows app inherits the user's full
/// <c>PATH</c>, and no process is started).
/// </summary>
public sealed class CliLocator
{
    /// <summary>Extensions a program found on <c>PATH</c> may have, in the order tried.</summary>
    public static readonly IReadOnlyList<string> PathExtensions = [".exe", ".cmd", ".bat"];

    private static readonly HashSet<string> ExecutableExtensions =
        new([".exe", ".cmd", ".bat", ".com"], StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, bool> isExecutable;
    private readonly Func<string, IReadOnlyList<string>> directoryContents;

    /// <param name="folders">The per-user folders; null uses <see cref="CliSearchFolders.Current"/>.</param>
    /// <param name="pathVariable">The <c>PATH</c> to search last; null uses this process's.</param>
    /// <param name="isExecutable">Whether a file exists and can be started; null checks the disk.</param>
    /// <param name="directoryContents">Entry names of a folder, empty when missing; null reads the disk.</param>
    public CliLocator(
        CliSearchFolders? folders = null,
        string? pathVariable = null,
        Func<string, bool>? isExecutable = null,
        Func<string, IReadOnlyList<string>>? directoryContents = null)
    {
        Folders = folders ?? CliSearchFolders.Current();
        PathVariable = pathVariable ?? System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        this.isExecutable = isExecutable ?? IsExecutableFile;
        this.directoryContents = directoryContents ?? ListDirectory;
    }

    public CliSearchFolders Folders { get; }

    public string PathVariable { get; }

    /// <summary>What the "searched" list says for the <c>PATH</c> step.</summary>
    public static string PathLookupDescription(CliTool tool) =>
        "PATH (" + string.Join(", ", PathExtensions.Select(extension => tool.BinaryName() + extension)) + ")";

    /// <summary>
    /// The well-known locations, in order, each folder with
    /// <see cref="CliTools.FileNames"/>: <c>%USERPROFILE%\.local\bin</c> (the
    /// Claude Code installer; the Mac's first folder too), for Antigravity
    /// <c>%LOCALAPPDATA%\agy\bin</c> (its installer), <c>%LOCALAPPDATA%\Microsoft\WinGet\Links</c>
    /// (<c>winget</c> portable packages), <c>%APPDATA%\npm</c> (npm's global
    /// folder), <c>%ProgramFiles%\nodejs</c> (npm prefix set to the Node
    /// folder, and the nvm-windows link), then the nvm-windows version
    /// folders under <c>%LOCALAPPDATA%\nvm</c> and <c>%APPDATA%\nvm</c>, newest first.
    /// </summary>
    public IReadOnlyList<string> KnownCandidates(CliTool tool)
    {
        List<string> folders = [Path.Combine(Folders.Home, ".local", "bin")];
        if (tool == CliTool.Antigravity)
        {
            folders.Add(Path.Combine(Folders.LocalAppData, "agy", "bin"));
        }
        folders.Add(Path.Combine(Folders.LocalAppData, "Microsoft", "WinGet", "Links"));
        folders.Add(Path.Combine(Folders.RoamingAppData, "npm"));
        folders.Add(Path.Combine(Folders.ProgramFiles, "nodejs"));
        foreach (var nvmRoot in new[] { Path.Combine(Folders.LocalAppData, "nvm"), Path.Combine(Folders.RoamingAppData, "nvm") })
        {
            var versions = directoryContents(nvmRoot)
                .Where(name => name.StartsWith('v') && name.Length > 1 && char.IsAsciiDigit(name[1]))
                .ToList();
            versions.Sort((left, right) => IsNewer(left, right) ? -1 : IsNewer(right, left) ? 1 : 0);
            folders.AddRange(versions.Select(version => Path.Combine(nvmRoot, version)));
        }
        return folders.SelectMany(folder => tool.FileNames().Select(name => Path.Combine(folder, name))).ToList();
    }

    /// <summary>The first executable well-known candidate. No process is started.</summary>
    public string? KnownInstallation(CliTool tool) => KnownCandidates(tool).FirstOrDefault(isExecutable);

    /// <summary>
    /// The first tool, in preset order, found in a well-known place or on
    /// <c>PATH</c> (the Mac checks only the well-known places, because its
    /// last step starts a shell; the <c>PATH</c> search here does not).
    /// </summary>
    public CliTool? FirstKnownTool()
    {
        foreach (var tool in CliTools.All)
        {
            if (KnownInstallation(tool) is not null || FindOnPath(tool) is not null) return tool;
        }
        return null;
    }

    /// <summary>
    /// The configured path when set, else a well-known location, else the
    /// first match on <c>PATH</c>. A configured path may be quoted, use
    /// <c>%VARIABLES%</c> or a leading <c>~</c>, and may leave out
    /// <c>.exe</c> or <c>.cmd</c>. Throws <see cref="CliProviderException"/>
    /// (<see cref="CliProviderError.NotInstalled"/>).
    /// </summary>
    public string Locate(CliTool tool, string? configuredPath)
    {
        var configured = (configuredPath ?? string.Empty).Trim().Trim('"').Trim();
        if (configured.Length > 0)
        {
            var expanded = ExpandPath(configured);
            if (isExecutable(expanded)) return expanded;
            if (!ExecutableExtensions.Contains(Path.GetExtension(expanded)))
            {
                foreach (var extension in PathExtensions)
                {
                    if (isExecutable(expanded + extension)) return expanded + extension;
                }
            }
            throw new CliProviderException(new CliProviderError.NotInstalled(tool, [expanded]));
        }
        var candidates = KnownCandidates(tool);
        if (candidates.FirstOrDefault(isExecutable) is { } found) return found;
        if (FindOnPath(tool) is { } onPath) return onPath;
        throw new CliProviderException(
            new CliProviderError.NotInstalled(tool, [.. candidates, PathLookupDescription(tool)]));
    }

    /// <summary>The first <c>&lt;name&gt;.exe</c>, <c>.cmd</c> or <c>.bat</c> in a <c>PATH</c> folder, in <c>PATH</c> order.</summary>
    public string? FindOnPath(CliTool tool) => FindOnPath(tool.BinaryName(), PathVariable, isExecutable);

    internal static string? FindOnPath(string baseName, string pathVariable, Func<string, bool> isExecutable)
    {
        foreach (var entry in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Trim('"');
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
            foreach (var extension in PathExtensions)
            {
                var candidate = Path.Combine(folder, baseName + extension);
                if (isExecutable(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Compares nvm folder names such as <c>v24.16.0</c> numerically.</summary>
    internal static bool IsNewer(string lhs, string rhs)
    {
        static int[] Parts(string name) => name.TrimStart('v').Split('.')
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .ToArray();
        var left = Parts(lhs);
        var right = Parts(rhs);
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var a = index < left.Length ? left[index] : 0;
            var b = index < right.Length ? right[index] : 0;
            if (a != b) return a > b;
        }
        return string.CompareOrdinal(lhs, rhs) > 0;
    }

    private string ExpandPath(string path)
    {
        var expanded = System.Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~") return Folders.Home;
        if (expanded.StartsWith(@"~\", StringComparison.Ordinal) || expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            expanded = Path.Combine(Folders.Home, expanded[2..]);
        }
        return expanded;
    }

    /// <summary>An existing file whose extension Windows starts as a program.</summary>
    public static bool IsExecutableFile(string path) =>
        ExecutableExtensions.Contains(Path.GetExtension(path)) && File.Exists(path);

    private static List<string> ListDirectory(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry)).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Windows command-line quoting (the rules <c>CommandLineToArgvW</c> and the
/// C runtime parse, which .NET's <c>ArgumentList</c> writes) and the length
/// limit of <c>CreateProcess</c>.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary><c>CreateProcess</c> takes at most 32,767 characters, including the terminating null.</summary>
    public const int MaxLength = 32_766;

    /// <summary>One argument quoted so it parses back as itself.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var builder = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }
            backslashes = 0;
            builder.Append(c);
        }
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>The command line <paramref name="argv"/> becomes.</summary>
    public static string Join(IEnumerable<string> argv) => string.Join(' ', argv.Select(Quote));
}

/// <summary>
/// The launch of an npm <c>.cmd</c> shim without <c>cmd.exe</c>. cmd.exe
/// cannot carry arbitrary text safely (a newline ends the command, and
/// <c>%</c>, <c>^</c>, <c>&amp;</c>, <c>|</c> are interpreted), and the
/// prompt is a whole transcript, so the shim's target is started directly:
/// <c>node.exe &lt;script&gt;</c>, or the <c>.exe</c> the shim points at. This
/// replaces the Mac's reliance on <c>#!/usr/bin/env node</c>.
/// </summary>
public static partial class CmdShim
{
    // cmd-shim (npm): "%_prog%"  "%dp0%\node_modules\@github\copilot\npm-loader.js" %*
    // older npm:      "%~dp0\node.exe"  "%~dp0\node_modules\@openai\codex\bin\codex.js" %*
    [GeneratedRegex(@"""%~?dp0%?\\?([^""%]+?\.(?:[cm]?js|exe))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();

    [GeneratedRegex(@"^[\p{L}\p{N} _\-.:\\/=@+,]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CmdSafePattern();

    /// <summary>
    /// The script or program <paramref name="shimText"/> (the contents of
    /// <paramref name="shimPath"/>) runs, as a full path, or null when the
    /// file is not a shim in a known form. <c>node.exe</c> itself is skipped.
    /// </summary>
    public static string? Target(string shimPath, string shimText)
    {
        ArgumentNullException.ThrowIfNull(shimPath);
        ArgumentNullException.ThrowIfNull(shimText);
        var folder = Path.GetDirectoryName(Path.GetFullPath(shimPath)) ?? string.Empty;
        string? target = null;
        foreach (Match match in TargetPattern().Matches(shimText))
        {
            var relative = match.Groups[1].Value.TrimStart('\\');
            if (string.Equals(Path.GetFileName(relative), "node.exe", StringComparison.OrdinalIgnoreCase)) continue;
            target = Path.GetFullPath(Path.Combine(folder, relative));
        }
        return target;
    }

    /// <summary>Whether cmd.exe passes <paramref name="argument"/> through unchanged.</summary>
    public static bool IsSafeForCmd(string argument) => CmdSafePattern().IsMatch(argument);

    /// <summary>
    /// The argv to start for <paramref name="argv"/>: unchanged for a
    /// program; for a <c>.cmd</c> or <c>.bat</c> shim, its target (after
    /// <c>node.exe</c> for a script: the one beside the shim, else the first
    /// on <paramref name="pathVariable"/>). A batch file that is not a shim
    /// runs through cmd.exe only when every argument is safe for it.
    /// Throws <see cref="CliLaunchException"/> otherwise.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> argv, string pathVariable,
        Func<string, string?>? readText = null, Func<string, bool>? isExecutable = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0) throw new CliLaunchException("No executable.");
        var executable = argv[0];
        var extension = Path.GetExtension(executable);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return argv;
        }
        readText ??= ReadSmallFile;
        isExecutable ??= CliLocator.IsExecutableFile;
        var arguments = argv.Skip(1).ToList();
        if (readText(executable) is { } text && Target(executable, text) is { } target)
        {
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return [target, .. arguments];
            var besideShim = Path.Combine(Path.GetDirectoryName(executable) ?? string.Empty, "node.exe");
            var node = isExecutable(besideShim)
                ? besideShim
                : CliLocator.FindOnPath("node", pathVariable, path =>
                    path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && isExecutable(path));
            if (node is null)
            {
                throw new CliLaunchException($"Node.js (node.exe) was not found; {executable} needs it.");
            }
            return [node, target, .. arguments];
        }
        if (arguments.All(IsSafeForCmd)) return argv;
        throw new CliLaunchException(
            $"{executable} is a batch file that is not an npm shim, and the prompt cannot be passed through it safely. "
            + "Set the path to the program itself in Settings > AI.");
    }

    private static string? ReadSmallFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024) return null;
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The real <see cref="CliRunner"/>: a child process with piped stdin,
/// stdout and stderr and no console window, stopped (with every process it
/// started) on timeout or cancellation. Port of <c>CLIProcessRunner</c> in
/// CLIProcess.swift. Windows differences: npm <c>.cmd</c> shims are started
/// through <see cref="CmdShim"/>; a command line over
/// <see cref="WindowsCommandLine.MaxLength"/> throws
/// <see cref="CliCommandLineTooLongException"/> before anything starts; the
/// whole process tree is killed (the Mac sends SIGTERM to the process alone),
/// because an npm shim's node starts the real CLI as a child.
/// </summary>
public static class CliProcessRunner
{
    /// <summary>After exit, how long to wait for the pipes to reach end of file.</summary>
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static CliRunner Run { get; } = RunAsync;

    public static async Task<CliRunResult> RunAsync(CliRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Argv.Count == 0 || string.IsNullOrEmpty(request.Argv[0]))
        {
            throw new CliLaunchException("No executable.");
        }
        var path = request.Environment.FirstOrDefault(
            pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
        var argv = CmdShim.Resolve(request.Argv, path);
        var length = WindowsCommandLine.Join(argv).Length;
        if (length > WindowsCommandLine.MaxLength) throw new CliCommandLineTooLongException(length);

        var info = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in argv.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment.Clear();
        foreach (var (key, value) in request.Environment)
        {
            info.Environment[key] = value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new CliLaunchException($"{argv[0]} did not start.");
        }
        catch (Win32Exception error)
        {
            throw new CliLaunchException(error.Message, error);
        }
        catch (InvalidOperationException error)
        {
            throw new CliLaunchException(error.Message, error);
        }

        var stdout = new PipeCollector(process.StandardOutput);
        var stderr = new PipeCollector(process.StandardError);
        var input = WriteInputAsync(process.StandardInput, request.StandardInput);

        var timedOut = false;
        using (var timeout = new CancellationTokenSource(request.Timeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    await WaitBriefly(process).ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);
                }
                timedOut = true;
                await WaitBriefly(process).ConfigureAwait(false);
            }
        }
        await Task.WhenAny(Task.WhenAll(stdout.Completion, stderr.Completion, input), Task.Delay(DrainGrace, CancellationToken.None))
            .ConfigureAwait(false);
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new CliRunResult(exitCode, stdout.Text, stderr.Text, timedOut);
    }

    private static async Task WriteInputAsync(StreamWriter writer, string? text)
    {
        try
        {
            if (text is not null)
            {
                await writer.WriteAsync(text).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The program exited or closed stdin without reading all of it.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                writer.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static async Task WaitBriefly(Process process)
    {
        using var grace = new CancellationTokenSource(DrainGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Reads a pipe to its end, keeping what arrived so far readable at any time.</summary>
    private sealed class PipeCollector
    {
        private readonly Lock gate = new();
        private readonly StringBuilder text = new();

        public PipeCollector(StreamReader reader)
        {
            Completion = Task.Run(() => ReadAsync(reader));
        }

        public Task Completion { get; }

        public string Text
        {
            get
            {
                lock (gate)
                {
                    return text.ToString();
                }
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    if (count == 0) return;
                    lock (gate)
                    {
                        text.Append(buffer, 0, count);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
