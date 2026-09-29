using System.Diagnostics;
using Hearsay.Core.Updates;

namespace Hearsay.Tests.Updates;

/// <summary>
/// The folder swap after the app quits (PLAN.md 18.4, "Updates and
/// packaging"). The Windows counterpart of <c>replaceBundleSwapsTheContents</c>
/// and <c>failedReplacementLeavesTheOriginal</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/UpdateInstallTests.swift: the real
/// helper script runs in Windows PowerShell 5.1 against scratch folders,
/// never with a relaunch.
/// </summary>
public sealed class UpdateSwapHelperTests : IDisposable
{
    private static readonly TimeSpan HelperLimit = TimeSpan.FromSeconds(60);

    private readonly ScratchFolder scratch = new();

    public void Dispose() => scratch.Dispose();

    private (string Install, string Staged) MakeFolders()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        File.WriteAllText(Path.Combine(install, "Old.txt"), "only in old");
        var staged = FakeApp.MakeInstall(scratch.Path(UpdateInstall.StagedFolderName("0.3.0")), "0.3.0", "new");
        new DirectoryInfo(staged).Attributes |= FileAttributes.Hidden;
        return (install, staged);
    }

    private SwapPlan Plan(string install, string staged) => new()
    {
        InstallFolder = install,
        StagedFolder = staged,
        LogFile = scratch.Path("Logs", "install.log"),
        Relaunch = false,
        RetryWindow = TimeSpan.FromSeconds(1),
    };

    private static async Task<int> RunAsync(SwapPlan plan)
    {
        using var helper = UpdateSwapHelper.Start(plan);
        using var limit = new CancellationTokenSource(HelperLimit);
        await helper.WaitForExitAsync(limit.Token);
        return helper.ExitCode;
    }

    private string Log() => File.ReadAllText(scratch.Path("Logs", "install.log"));

    private string[] Listing() => [.. Directory.GetFileSystemEntries(scratch.Root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    [Fact]
    public async Task SwapReplacesTheFolderContents()
    {
        var (install, staged) = MakeFolders();
        var cache = scratch.Path("Updates", "0.3.0");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "Hearsay-0.3.0-win-x64.zip"), "zip");

        var code = await RunAsync(Plan(install, staged) with { CleanupFolder = cache });

        Assert.Equal(UpdateSwapHelper.ExitSwapped, code);
        Assert.Equal("new", FakeApp.Marker(install));
        Assert.False(File.Exists(Path.Combine(install, "Old.txt")));
        Assert.False(new DirectoryInfo(install).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(Directory.Exists(staged));
        Assert.False(Directory.Exists(cache));
        Assert.Equal(["Hearsay", "Logs", "Updates"], Listing());
        var log = Log();
        Assert.Contains("installed the new version", log, StringComparison.Ordinal);
        Assert.Contains("no relaunch", log, StringComparison.Ordinal);
        Assert.EndsWith("exit 0\n", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingStagedCopyChangesNothing()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        var code = await RunAsync(Plan(install, scratch.Path(UpdateInstall.StagedFolderName("0.3.0"))));
        Assert.Equal(UpdateSwapHelper.ExitStagedMissing, code);
        Assert.Equal("old", FakeApp.Marker(install));
        Assert.Equal(["Hearsay", "Logs"], Listing());
    }

    [Fact]
    public async Task LockedOldVersionChangesNothing()
    {
        var (install, staged) = MakeFolders();
        int code;
        using (new FileStream(Path.Combine(install, "Old.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            code = await RunAsync(Plan(install, staged));
        }
        Assert.Equal(UpdateSwapHelper.ExitMoveAsideFailed, code);
        Assert.Equal("old", FakeApp.Marker(install));
        Assert.False(Directory.Exists(staged));
        Assert.Equal(["Hearsay", "Logs"], Listing());
        Assert.Contains("nothing was changed", Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedMoveInRestoresTheOldVersion()
    {
        var (install, staged) = MakeFolders();
        int code;
        using (new FileStream(Path.Combine(staged, "Marker.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            code = await RunAsync(Plan(install, staged));
        }
        Assert.Equal(UpdateSwapHelper.ExitMoveInFailed, code);
        Assert.Equal("old", FakeApp.Marker(install));
        Assert.True(File.Exists(Path.Combine(install, "Old.txt")));
        Assert.False(Directory.Exists(scratch.Path(UpdateInstall.PreviousFolderName)));
        Assert.Contains("old version restored", Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitsForTheAppToQuit()
    {
        var (install, staged) = MakeFolders();
        // Stands in for the running app: its working folder is the install
        // folder, so the rename cannot succeed until it has exited.
        using var app = StartSleeper(install, seconds: 3);
        var plan = Plan(install, staged) with
        {
            WaitForProcessId = app.Id,
            WaitForProcessStartUtc = app.StartTime.ToUniversalTime(),
        };

        var code = await RunAsync(plan);

        Assert.True(app.HasExited);
        Assert.Equal(UpdateSwapHelper.ExitSwapped, code);
        Assert.Equal("new", FakeApp.Marker(install));
        Assert.Contains("waiting for process", Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppThatDoesNotQuitChangesNothing()
    {
        var (install, staged) = MakeFolders();
        using var app = StartSleeper(install, seconds: 30);
        try
        {
            var plan = Plan(install, staged) with
            {
                WaitForProcessId = app.Id,
                WaitForProcessStartUtc = app.StartTime.ToUniversalTime(),
                WaitTimeout = TimeSpan.FromSeconds(1),
            };
            var code = await RunAsync(plan);
            Assert.Equal(UpdateSwapHelper.ExitAppDidNotQuit, code);
            Assert.Equal("old", FakeApp.Marker(install));
            Assert.False(Directory.Exists(staged));
        }
        finally
        {
            app.Kill();
            await app.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task AReusedProcessIdIsNotWaitedFor()
    {
        var (install, staged) = MakeFolders();
        using var other = StartSleeper(scratch.Root, seconds: 30);
        try
        {
            var plan = Plan(install, staged) with
            {
                WaitForProcessId = other.Id,
                WaitForProcessStartUtc = other.StartTime.ToUniversalTime().AddSeconds(-5),
                WaitTimeout = TimeSpan.FromSeconds(20),
            };
            var watch = Stopwatch.StartNew();
            var code = await RunAsync(plan);
            Assert.Equal(UpdateSwapHelper.ExitSwapped, code);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
            Assert.False(other.HasExited);
        }
        finally
        {
            other.Kill();
            await other.WaitForExitAsync();
        }
    }

    [Fact]
    public void InstallPlanWaitsForThisProcessAndRelaunches()
    {
        var prepared = new PreparedUpdate("0.3.0", scratch.Path(".Hearsay-update-0.3.0"), scratch.Path("Updates", "0.3.0"), "");
        using var current = Process.GetCurrentProcess();
        var plan = SwapPlan.ForInstall(prepared, scratch.Path("Hearsay"), scratch.Path("Updates"), current);
        Assert.Equal(Environment.ProcessId, plan.WaitForProcessId);
        Assert.Equal(current.StartTime.ToUniversalTime(), plan.WaitForProcessStartUtc);
        Assert.True(plan.Relaunch);
        Assert.Equal(scratch.Path("Updates", "install.log"), plan.LogFile);
        Assert.Equal(prepared.CacheFolder, plan.CleanupFolder);
        Assert.Equal(scratch.Path(".Hearsay-previous"), plan.PreviousFolder);
    }

    [Fact]
    public void LiteralsDoubleEveryQuote()
    {
        Assert.Equal("'C:\\it''s'", UpdateSwapHelper.Literal("C:\\it's"));
        Assert.Equal("'a\u2019\u2019b'", UpdateSwapHelper.Literal("a\u2019b"));
        Assert.Equal("'$env:x `n'", UpdateSwapHelper.Literal("$env:x `n"));
        var script = UpdateSwapHelper.Script(Plan(scratch.Path("Hearsay"), scratch.Path(".Hearsay-update-1")));
        Assert.Contains("$install = '", script, StringComparison.Ordinal);
        Assert.Contains("it''s here", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
    }

    private static Process StartSleeper(string workingFolder, int seconds)
    {
        var start = new ProcessStartInfo(UpdateSwapHelper.PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingFolder,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Seconds {seconds}" })
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start");
    }
}
