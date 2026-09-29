using System.Diagnostics;
using Hearsay.Core.Notes;
using Hearsay.Tests.Naming;

namespace Hearsay.Tests.Notes;

/// <summary>
/// The real <see cref="CliProcessRunner"/> on Windows. The Swift suite has
/// no runner tests (every CLI test injects a fake); these run small
/// <c>.cmd</c> scripts written into a scratch folder, and never a real CLI.
/// Also the Windows-only pieces: npm shim resolution and command-line quoting.
/// </summary>
public sealed class CliProcessRunnerTests : IDisposable
{
    private readonly TemporaryDirectory scratch = new("CliProcessRunnerTests");

    public void Dispose() => scratch.Dispose();

    private string Script(string name, string body)
    {
        var path = Path.Combine(scratch.Url, name);
        File.WriteAllText(path, "@echo off\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal));
        return path;
    }

    private CliRunRequest Request(IReadOnlyList<string> argv, double timeoutSeconds = 30, string? input = null) =>
        new(argv, scratch.Url, CliClient.CurrentEnvironment(), TimeSpan.FromSeconds(timeoutSeconds), input);

    [Fact]
    public async Task CapturesStdoutStderrAndTheExitCode()
    {
        var script = Script("fake-cli.cmd", "echo out-line\necho err-line 1>&2\nexit /b 3\n");
        var result = await CliProcessRunner.RunAsync(Request([script]), CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out-line", result.Stdout.Trim());
        Assert.Equal("err-line", result.Stderr.Trim());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunsInTheGivenFolderWithExactlyTheGivenEnvironment()
    {
        var script = Script("env.cmd", "cd\necho [%HEARSAY_TEST_VALUE%]\necho [%OPENAI_API_KEY%]\n");
        var environment = CliClient.Environment(CliTool.Codex, script,
            new Dictionary<string, string>(CliClient.CurrentEnvironment(), StringComparer.OrdinalIgnoreCase)
            {
                ["HEARSAY_TEST_VALUE"] = "kept",
                ["OPENAI_API_KEY"] = "must-not-leak",
            });
        var result = await CliProcessRunner.RunAsync(
            new CliRunRequest([script], scratch.Url, environment, TimeSpan.FromSeconds(30)), CancellationToken.None);
        var lines = result.Stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(scratch.Url, lines[0], ignoreCase: true);
        Assert.Equal("[kept]", lines[1]);
        Assert.Equal("[]", lines[2]);
    }

    [Fact]
    public async Task StandardInputReachesTheProgramAndIsClosed()
    {
        var script = Script("stdin.cmd", "findstr \"^\"\n");
        var result = await CliProcessRunner.RunAsync(Request([script], input: "first line\nsecond line\n"), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["first line", "second line"], result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        // No input: stdin is empty and closed, so the program does not wait for it.
        var empty = await CliProcessRunner.RunAsync(Request([script], timeoutSeconds: 20), CancellationToken.None);
        Assert.False(empty.TimedOut);
        Assert.Empty(empty.Stdout.Trim());
    }

    [Fact]
    public async Task TimeoutStopsTheWholeProcessTree()
    {
        var marker = $"hearsay-timeout-{Guid.NewGuid():N}";
        var script = Script("slow.cmd", $"echo started\nping -n 60 127.0.0.1 >nul\necho {marker}\n");
        var clock = Stopwatch.StartNew();
        var result = await CliProcessRunner.RunAsync(Request([script], timeoutSeconds: 1.5), CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        Assert.Contains("started", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsTheProgramAndThrows()
    {
        var script = Script("slow-cancel.cmd", "ping -n 60 127.0.0.1 >nul\n");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliProcessRunner.RunAsync(Request([script]), cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliProcessRunner.RunAsync(Request([script]), already.Token));
    }

    [Fact]
    public async Task ProgramThatCannotStartIsALaunchError()
    {
        var missing = Path.Combine(scratch.Url, "no-such-cli.exe");
        await Assert.ThrowsAsync<CliLaunchException>(() => CliProcessRunner.RunAsync(Request([missing]), CancellationToken.None));
        await Assert.ThrowsAsync<CliLaunchException>(() => CliProcessRunner.RunAsync(Request([]), CancellationToken.None));
    }

    /// <summary>A batch file that is not an npm shim cannot carry a prompt safely: cmd.exe would read <c>&amp;</c> or stop at a newline.</summary>
    [Theory]
    [InlineData("a & calc")]
    [InlineData("first\nsecond")]
    [InlineData("100%")]
    [InlineData("say \"hi\"")]
    public async Task UnsafeArgumentsThroughABatchFileAreRefused(string argument)
    {
        var script = Script("plain.cmd", "echo should not run\n");
        var error = await Assert.ThrowsAsync<CliLaunchException>(() =>
            CliProcessRunner.RunAsync(Request([script, "--prompt", argument]), CancellationToken.None));
        Assert.Contains("not an npm shim", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafeArgumentsThroughABatchFileAreAllowed()
    {
        var script = Script("args.cmd", "echo %1 %2\n");
        var result = await CliProcessRunner.RunAsync(Request([script, "--version", @"C:\Temp\agy.log"]), CancellationToken.None);
        Assert.Equal(@"--version C:\Temp\agy.log", result.Stdout.Trim());
    }

    [Fact]
    public async Task TooLongCommandLineIsRefusedBeforeStarting()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var error = await Assert.ThrowsAsync<CliCommandLineTooLongException>(() =>
            CliProcessRunner.RunAsync(Request([cmd, "/c", "echo", new string('x', 40_000)]), CancellationToken.None));
        Assert.True(error.Length > WindowsCommandLine.MaxLength);
    }

    // MARK: - npm shims

    private const string CmdShimText = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@github\copilot\npm-loader.js" %*
        """;

    [Fact]
    public void ShimTargetIsReadFromTheKnownForms()
    {
        Assert.Equal(@"C:\Users\test\AppData\Roaming\npm\node_modules\@github\copilot\npm-loader.js",
            CmdShim.Target(@"C:\Users\test\AppData\Roaming\npm\copilot.cmd", CmdShimText));
        const string older = "@IF EXIST \"%~dp0\\node.exe\" (\r\n  \"%~dp0\\node.exe\"  \"%~dp0\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n) ELSE (\r\n  node  \"%~dp0\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n)";
        Assert.Equal(@"C:\npm\node_modules\@openai\codex\bin\codex.js", CmdShim.Target(@"C:\npm\codex.cmd", older));
        Assert.Equal(@"C:\npm\node_modules\pkg\bin\tool.exe",
            CmdShim.Target(@"C:\npm\tool.cmd", "@\"%~dp0\\node_modules\\pkg\\bin\\tool.exe\" %*"));
        Assert.Equal(@"C:\tools\lib\cli.mjs", CmdShim.Target(@"C:\tools\bin\x.cmd", "@node \"%~dp0\\..\\lib\\cli.mjs\" %*"));
        Assert.Null(CmdShim.Target(@"C:\npm\plain.cmd", "@echo off\r\necho hi\r\n"));
    }

    [Fact]
    public void ShimResolvesToNodeBesideItThenOnPath()
    {
        var texts = new Dictionary<string, string> { [@"C:\npm\copilot.cmd"] = CmdShimText };
        var script = @"C:\npm\node_modules\@github\copilot\npm-loader.js";
        IReadOnlyList<string> argv = [@"C:\npm\copilot.cmd", "--prompt", "line one\nline & two"];

        var beside = CmdShim.Resolve(argv, string.Empty, texts.GetValueOrDefault,
            path => path is @"C:\npm\node.exe");
        Assert.Equal([@"C:\npm\node.exe", script, "--prompt", "line one\nline & two"], beside);

        var onPath = CmdShim.Resolve(argv, @"C:\npm;C:\Program Files\nodejs", texts.GetValueOrDefault,
            path => path is @"C:\Program Files\nodejs\node.exe");
        Assert.Equal([@"C:\Program Files\nodejs\node.exe", script, "--prompt", "line one\nline & two"], onPath);

        var error = Assert.Throws<CliLaunchException>(() => CmdShim.Resolve(argv, @"C:\npm", texts.GetValueOrDefault, _ => false));
        Assert.Contains("node.exe", error.Message, StringComparison.Ordinal);

        IReadOnlyList<string> program = [@"C:\Tools\copilot.exe", "a & b"];
        Assert.Same(program, CmdShim.Resolve(program, string.Empty));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"C:\no\space\", @"C:\no\space\")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void CommandLineQuotingRoundTrips(string argument, string quoted)
    {
        Assert.Equal(quoted, WindowsCommandLine.Quote(argument));
    }

    [Fact]
    public void WindowsInvocationMovesThePromptToStdinForClaudeCodeAndCodexOnly()
    {
        IReadOnlyList<string> claude = ["claude.exe", "--print", "--", "PROMPT"];
        var (claudeArgv, claudeInput) = CliArguments.WindowsInvocation(CliTool.ClaudeCode, claude);
        Assert.Equal(["claude.exe", "--print"], claudeArgv);
        Assert.Equal("PROMPT", claudeInput);

        IReadOnlyList<string> codex = ["codex.cmd", "exec", "--", "PROMPT"];
        var (codexArgv, codexInput) = CliArguments.WindowsInvocation(CliTool.Codex, codex);
        Assert.Equal(["codex.cmd", "exec", "--", "-"], codexArgv);
        Assert.Equal("PROMPT", codexInput);

        IReadOnlyList<string> copilot = CliArguments.Copilot("copilot.exe", "PROMPT", "auto", "max");
        Assert.Equal((copilot, (string?)null), CliArguments.WindowsInvocation(CliTool.Copilot, copilot));
        IReadOnlyList<string> version = ["claude.exe", "--version"];
        Assert.Equal((version, (string?)null), CliArguments.WindowsInvocation(CliTool.ClaudeCode, version));
    }
}
