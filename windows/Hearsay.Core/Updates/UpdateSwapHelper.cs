using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hearsay.Core.Updates;

/// <summary>What the swap helper does; see <see cref="UpdateSwapHelper"/>.</summary>
public sealed record SwapPlan
{
    /// <summary>The folder with the running Hearsay.exe; its path stays the same.</summary>
    public required string InstallFolder { get; init; }

    /// <summary>The verified copy, <c>&lt;parent&gt;\.Hearsay-update-&lt;version&gt;</c> (<see cref="PreparedUpdate.StagedFolder"/>).</summary>
    public required string StagedFolder { get; init; }

    /// <summary>The helper appends one line per step here (UTF-8, LF). Keep it outside <see cref="CleanupFolder"/>.</summary>
    public required string LogFile { get; init; }

    /// <summary>Deleted after a successful swap (the version's cache folder), or null.</summary>
    public string? CleanupFolder { get; init; }

    /// <summary>The process to wait for (the running app), or null to start at once (debug entry, tests).</summary>
    public int? WaitForProcessId { get; init; }

    /// <summary>
    /// That process's start time (UTC), so a reused process id is not waited
    /// for: <see cref="Process.StartTime"/> of the running app.
    /// </summary>
    public DateTime? WaitForProcessStartUtc { get; init; }

    /// <summary>How long the app gets to quit; after that nothing is changed and the staged copy is deleted.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long each rename is retried (a virus scanner or indexer may hold a file briefly).</summary>
    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Start Hearsay.exe from the install folder afterwards (the new one, or the old one after a failure). False in debug runs.</summary>
    public bool Relaunch { get; init; } = true;

    /// <summary>The helper's log beside the version folders: <c>&lt;updates root&gt;\install.log</c>.</summary>
    public static string LogFileIn(string updatesRoot) => Path.Combine(updatesRoot, "install.log");

    /// <summary>
    /// The plan for "Install and Relaunch": wait for <paramref name="app"/>
    /// (normally <c>Process.GetCurrentProcess()</c>), swap, delete the
    /// version's cache folder, relaunch, log to <see cref="LogFileIn"/>.
    /// </summary>
    public static SwapPlan ForInstall(PreparedUpdate prepared, string installFolder, string updatesRoot, Process app)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(app);
        return new SwapPlan
        {
            InstallFolder = installFolder,
            StagedFolder = prepared.StagedFolder,
            LogFile = LogFileIn(updatesRoot),
            CleanupFolder = prepared.CacheFolder,
            WaitForProcessId = app.Id,
            WaitForProcessStartUtc = app.StartTime.ToUniversalTime(),
            Relaunch = true,
        };
    }

    /// <summary>The folder beside the install folder the old version waits in.</summary>
    public string PreviousFolder =>
        Path.Combine(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallFolder))) ?? InstallFolder,
            UpdateInstall.PreviousFolderName);
}

/// <summary>
/// Windows cannot replace a running exe, so the swap the Mac does in-process
/// (<c>UpdateInstall.replaceBundle</c> in
/// mac/HearsayCore/Sources/HearsayCore/Updates/UpdateInstall.swift, then
/// <c>AppDelegate.restart</c>) runs in a small detached Windows PowerShell 5.1
/// helper after the app quits (PLAN.md 18.4, "Updates and packaging"):
/// <list type="number">
/// <item>Waits up to <see cref="SwapPlan.WaitTimeout"/> for the app's process to exit (exit code 3 and the staged copy deleted if it does not).</item>
/// <item>Checks the staged copy still has Hearsay.exe (else 4).</item>
/// <item>Renames the install folder to <c>.Hearsay-previous</c> (retried; on failure nothing changed, the staged copy is deleted, 5).</item>
/// <item>Renames the staged folder to the install folder's name and clears its hidden attribute (retried; on failure the old folder is renamed back, 6).</item>
/// <item>Deletes <c>.Hearsay-previous</c> and the cache folder, relaunches, 0.</item>
/// </list>
/// The script goes in <c>-EncodedCommand</c>, so no script file is written
/// and the execution policy (which applies to script files) is not involved;
/// the helper's working folder is the log's folder, never the install folder.
/// Exit codes and every step are written to <see cref="SwapPlan.LogFile"/>,
/// which the relaunched app can show when the version did not change.
/// </summary>
public static class UpdateSwapHelper
{
    public const int ExitSwapped = 0;
    public const int ExitAppDidNotQuit = 3;
    public const int ExitStagedMissing = 4;
    public const int ExitMoveAsideFailed = 5;
    public const int ExitMoveInFailed = 6;

    /// <summary><c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>, present on every Windows 10 and 11.</summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The PowerShell 5.1 script for <paramref name="plan"/>, with every value as a single-quoted literal.</summary>
    public static string Script(SwapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.InstallFolder));
        var staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.StagedFolder));
        var invariant = CultureInfo.InvariantCulture;
        var header = new StringBuilder()
            .Append("# Hearsay update helper, generated by Hearsay.Core.Updates.UpdateSwapHelper (PLAN.md 18.4).\n")
            .Append("$ErrorActionPreference = 'Stop'\n")
            .Append(invariant, $"$install = {Literal(install)}\n")
            .Append(invariant, $"$staged = {Literal(staged)}\n")
            .Append(invariant, $"$previous = {Literal(plan.PreviousFolder)}\n")
            .Append(invariant, $"$log = {Literal(Path.GetFullPath(plan.LogFile))}\n")
            .Append(invariant, $"$cleanup = {Literal(plan.CleanupFolder is null ? "" : Path.GetFullPath(plan.CleanupFolder))}\n")
            .Append(invariant, $"$waitPid = {plan.WaitForProcessId ?? 0}\n")
            .Append(invariant, $"$waitStartTicks = {plan.WaitForProcessStartUtc?.ToUniversalTime().Ticks ?? 0}\n")
            .Append(invariant, $"$waitMilliseconds = {(long)plan.WaitTimeout.TotalMilliseconds}\n")
            .Append(invariant, $"$retryMilliseconds = {(long)plan.RetryWindow.TotalMilliseconds}\n")
            .Append(invariant, $"$relaunch = {(plan.Relaunch ? "$true" : "$false")}\n")
            .Append(invariant, $"$exe = Join-Path $install {Literal(UpdateInstall.ExeName)}\n");
        return header + Body;
    }

    /// <summary>
    /// Starts the helper for <paramref name="plan"/> without a window and
    /// returns its process (the app quits right after; debug runs and tests
    /// wait for it).
    /// </summary>
    public static Process Start(SwapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var logFolder = Path.GetDirectoryName(Path.GetFullPath(plan.LogFile)) ?? Path.GetTempPath();
        Directory.CreateDirectory(logFolder);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(plan)));
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = logFolder,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", encoded })
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start.");
    }

    /// <summary>A PowerShell single-quoted string: only the quote itself needs doubling (also the typographic ones PowerShell treats as quotes).</summary>
    internal static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            escaped.Append(character);
            if (character is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                escaped.Append(character);
            }
        }
        return escaped.Append('\'').ToString();
    }

    private const string Body = """
        function Say([string]$text) {
          $line = [DateTime]::Now.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz') + ' ' + $text + "`n"
          try { [System.IO.File]::AppendAllText($log, $line) } catch { }
        }
        function Retry([scriptblock]$action) {
          $deadline = [DateTime]::UtcNow.AddMilliseconds($retryMilliseconds)
          while ($true) {
            try { & $action | Out-Null; return $true } catch {
              if ([DateTime]::UtcNow -ge $deadline) { Say ('  gave up: ' + $_.Exception.Message); return $false }
              Start-Sleep -Milliseconds 250
            }
          }
        }
        function RemoveFolder([string]$path) {
          if ($path -ne '' -and (Test-Path -LiteralPath $path)) {
            Retry { Remove-Item -LiteralPath $path -Recurse -Force } | Out-Null
          }
        }
        function Launch {
          if (-not $relaunch) { Say 'no relaunch'; return }
          try {
            Start-Process -FilePath $exe -WorkingDirectory $install | Out-Null
            Say ('relaunched ' + $exe)
          } catch { Say ('relaunch failed: ' + $_.Exception.Message) }
        }
        function Finish([int]$code) { Say ('exit ' + $code); exit $code }

        Say ('start: install ' + $install + ', staged ' + $staged)
        if ($waitPid -gt 0) {
          $process = $null
          try { $process = [System.Diagnostics.Process]::GetProcessById($waitPid) } catch { }
          if ($null -ne $process -and $waitStartTicks -gt 0) {
            try {
              if ($process.StartTime.ToUniversalTime().Ticks -ne $waitStartTicks) { $process = $null }
            } catch { }
          }
          if ($null -ne $process) {
            Say ('waiting for process ' + $waitPid + ' to quit')
            if (-not $process.WaitForExit([int]$waitMilliseconds)) {
              Say 'the app did not quit; nothing was changed'
              RemoveFolder $staged
              Finish 3
            }
          }
          Say 'the app has quit'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $staged 'Hearsay.exe'))) {
          Say 'the staged copy is missing; nothing was changed'
          Launch
          Finish 4
        }
        RemoveFolder $previous
        if (-not (Retry { [System.IO.Directory]::Move($install, $previous) })) {
          Say 'could not move the old version aside; nothing was changed'
          RemoveFolder $staged
          Launch
          Finish 5
        }
        Say ('moved the old version to ' + $previous)
        if (-not (Retry { [System.IO.Directory]::Move($staged, $install) })) {
          Say 'could not move the new version in; restoring the old one'
          if (Retry { [System.IO.Directory]::Move($previous, $install) }) {
            Say 'old version restored'
            RemoveFolder $staged
          } else {
            Say ('ROLLBACK FAILED: the old version is in ' + $previous)
          }
          Launch
          Finish 6
        }
        try {
          $item = Get-Item -LiteralPath $install -Force
          $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::Hidden)
        } catch { Say ('could not clear the hidden attribute: ' + $_.Exception.Message) }
        Say 'installed the new version'
        RemoveFolder $previous
        RemoveFolder $cleanup
        Launch
        Finish 0

        """;
}
