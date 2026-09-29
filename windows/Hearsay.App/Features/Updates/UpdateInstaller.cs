using System.Diagnostics;
using Hearsay.Core.Settings;
using Hearsay.Core.Updates;

namespace Hearsay.App.Features.Updates;

/// <summary>
/// The real <see cref="IUpdateCheckSource"/>: one <see cref="UpdateChecker"/>
/// for <see cref="UpdateConfiguration.Repository"/> (the Mac's
/// <c>UpdateService.fetchOutcome</c> in mac/Hearsay/Features/Updates/UpdateService.swift).
/// </summary>
internal sealed class GitHubUpdateCheckSource : IUpdateCheckSource, IDisposable
{
    private readonly UpdateChecker checker = new(UpdateConfiguration.Repository);

    public Task<UpdateCheckOutcome> CheckAsync(string currentVersion, AppSettings settings, CancellationToken cancellationToken) =>
        checker.CheckAsync(currentVersion, settings, cancellationToken: cancellationToken);

    public void Dispose() => checker.Dispose();
}

/// <summary>
/// The real <see cref="IUpdateInstallSteps"/> on Core's
/// <see cref="UpdateInstall"/> and <see cref="UpdateSwapHelper"/>: the file
/// work of mac/Hearsay/Features/Updates/UpdateInstaller.swift and
/// UpdatePackage.swift for the running install folder (the folder of
/// Hearsay.exe) and an updates root (<see cref="UpdateInstall.DefaultUpdatesRoot"/>,
/// <c>%LOCALAPPDATA%\Hearsay\Updates</c>; a scratch folder in debug runs).
/// </summary>
internal sealed class UpdateInstaller : IUpdateInstallSteps, IDisposable
{
    /// <summary>What the helper's log holds after a successful swap (UpdateSwapHelper's script).</summary>
    internal const string InstalledLine = "installed the new version";

    /// <summary>The log of the last install once the relaunched app has read it.</summary>
    internal const string PreviousLogName = "install-previous.log";

    private readonly HttpClient client = new();

    public UpdateInstaller(string installFolder, string updatesRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(installFolder);
        ArgumentException.ThrowIfNullOrEmpty(updatesRoot);
        InstallFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder));
        UpdatesRoot = updatesRoot;
    }

    /// <summary>The folder of the running Hearsay.exe.</summary>
    public static string RunningInstallFolder =>
        Path.GetDirectoryName(Environment.ProcessPath ?? "") is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;

    public string InstallFolder { get; }

    public string UpdatesRoot { get; }

    public void CheckLocation() => UpdateInstall.CheckInstallLocation(InstallFolder);

    public async Task<PreparedUpdate> PrepareAsync(
        ReleaseInfo release, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
    {
        var reader = FileAppIdentityReader.Shared;
        var running = await Task.Run(
            () => Environment.ProcessPath is { } exe && File.Exists(exe) ? reader.Read(exe) : null, cancellationToken).ConfigureAwait(true);
        var prepared = await UpdateInstall.PrepareAsync(client, release, InstallFolder, UpdatesRoot, running, reader, progress, cancellationToken)
            .ConfigureAwait(true);
        AppLog.Write($"update {release.Version}: {prepared.Summary}");
        return prepared;
    }

    public void Discard(PreparedUpdate prepared) => UpdateInstall.Discard(prepared);

    public void StartSwap(PreparedUpdate prepared)
    {
        using var app = Process.GetCurrentProcess();
        var plan = SwapPlan.ForInstall(prepared, InstallFolder, UpdatesRoot, app);
        // A log from an earlier attempt would be read as this one's.
        File.Delete(plan.LogFile);
        using var helper = UpdateSwapHelper.Start(plan);
        AppLog.Write($"update {prepared.Version}: swap helper {helper.Id} started, log {plan.LogFile}");
    }

    public string? DownloadedFile(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.ZipAsset(release.Version) is not { } zip) return null;
        var path = Path.Combine(UpdatesRoot, release.Version, zip.Name);
        return File.Exists(path) ? path : null;
    }

    public Task<bool?> TakeLastInstallFailedAsync() => TakeLastInstallFailedAsync(UpdatesRoot, TimeSpan.FromSeconds(10));

    /// <summary>
    /// Reads <c>install.log</c> in <paramref name="updatesRoot"/> once the
    /// helper has written its last line (<c>exit &lt;code&gt;</c>; it relaunches
    /// Hearsay just before, so this waits up to <paramref name="wait"/>):
    /// exit 0 worked, any other code failed; a log without an exit line
    /// failed unless it says <see cref="InstalledLine"/>. The log is then
    /// renamed to <see cref="PreviousLogName"/> (kept for troubleshooting).
    /// Null without a log.
    /// </summary>
    internal static async Task<bool?> TakeLastInstallFailedAsync(string updatesRoot, TimeSpan wait)
    {
        var log = SwapPlan.LogFileIn(updatesRoot);
        try
        {
            if (!File.Exists(log)) return null;
            var deadline = DateTime.UtcNow + wait;
            var text = await File.ReadAllTextAsync(log).ConfigureAwait(true);
            while (ExitCode(text) is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(true);
                text = await File.ReadAllTextAsync(log).ConfigureAwait(true);
            }
            File.Move(log, Path.Combine(updatesRoot, PreviousLogName), overwrite: true);
            var failed = ExitCode(text) is { } code ? code != UpdateSwapHelper.ExitSwapped : !text.Contains(InstalledLine, StringComparison.Ordinal);
            AppLog.Write($"update: the last install {(failed ? "failed" : "worked")}; log kept as {PreviousLogName}");
            return failed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"update: cannot read {log}: {error.Message}");
            return null;
        }
    }

    /// <summary>The code of the helper's last "exit &lt;code&gt;" line, or null.</summary>
    internal static int? ExitCode(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var last = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(last, @" exit (\d+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    public void Dispose() => client.Dispose();
}
