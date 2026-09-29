using System.Text;
using Hearsay.Core.Updates;

namespace Hearsay.Tests.Updates;

/// <summary>
/// The <c>HEARSAY_INSTALL_UPDATE</c> debug entry's core (the semantics of
/// mac/Hearsay/Features/Debug/UpdateInstallDebug.swift, which has no Swift
/// test), run end to end against a scratch install folder with fake zips.
/// </summary>
public sealed class UpdateInstallDebugTests : IDisposable
{
    private readonly ScratchFolder scratch = new();

    public void Dispose() => scratch.Dispose();

    private string WriteZip(byte[] data, string name = "Hearsay-0.3.0-win-x64.zip", bool withSums = true)
    {
        var folder = scratch.Path("Downloads");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        if (withSums)
        {
            File.WriteAllText(Path.Combine(folder, ReleaseInfo.ChecksumsFileName), FakeApp.Sums(name, data));
        }
        return path;
    }

    private UpdateInstallDebug.Options Options(string zip, string? target, string? version = null) => new()
    {
        Zip = zip,
        Target = target,
        VersionOverride = version,
        RunningExe = FakeApp.MakeInstall(scratch.Path("Running"), "0.2.0", "running") + "\\Hearsay.exe",
        Reader = new FakeIdentityReader(),
        WorkFolder = scratch.Path("Work"),
    };

    private static async Task<(int Code, string Output)> RunAsync(UpdateInstallDebug.Options options)
    {
        var output = new StringBuilder();
        using var writer = new StringWriter(output);
        var code = await UpdateInstallDebug.RunAsync(options, writer);
        return (code, output.ToString());
    }

    [Fact]
    public async Task InstallsIntoTheScratchTarget()
    {
        var target = FakeApp.MakeInstall(scratch.Path("Target"), "0.2.0", "old");
        var zip = WriteZip(FakeApp.Zip("0.3.0", "new"));

        var (code, output) = await RunAsync(Options(zip, target));

        Assert.Equal(0, code);
        Assert.Equal("new", FakeApp.Marker(target));
        Assert.Contains("hearsay install update: expected version 0.3.0", output, StringComparison.Ordinal);
        Assert.Contains("hearsay install update: install location ok", output, StringComparison.Ordinal);
        Assert.Contains("hearsay install update: checksum ok ", output, StringComparison.Ordinal);
        Assert.Contains("signer check skipped", output, StringComparison.Ordinal);
        Assert.Contains("now version 0.3.0; staged copy gone: True", output, StringComparison.Ordinal);
        Assert.Contains("done (no relaunch in a debug run)", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(scratch.Path("Work")));
        Assert.False(Directory.Exists(scratch.Path(".Hearsay-update-0.3.0")));
    }

    [Fact]
    public async Task WithoutChecksumsTheCheckIsSkipped()
    {
        var target = FakeApp.MakeInstall(scratch.Path("Target"), "0.2.0", "old");
        var zip = WriteZip(FakeApp.Zip("0.4.0", "new", top: null), name: "build.zip", withSums: false);

        var (code, output) = await RunAsync(Options(zip, target, version: "0.4.0"));

        Assert.Equal(0, code);
        Assert.Contains("checksum skipped (no SHA256SUMS.txt next to the zip)", output, StringComparison.Ordinal);
        Assert.Contains("now version 0.4.0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownVersionSkipsTheVersionCheck()
    {
        var target = FakeApp.MakeInstall(scratch.Path("Target"), "0.2.0", "old");
        var zip = WriteZip(FakeApp.Zip("9.9.9", "new"), name: "custom.zip", withSums: false);
        var (code, output) = await RunAsync(Options(zip, target));
        Assert.Equal(0, code);
        Assert.Contains("expected version unknown; version check skipped", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadChecksumStopsBeforeAnyChange()
    {
        var target = FakeApp.MakeInstall(scratch.Path("Target"), "0.2.0", "old");
        var zip = WriteZip(FakeApp.Zip("0.3.0", "new"));
        File.WriteAllText(Path.Combine(scratch.Path("Downloads"), ReleaseInfo.ChecksumsFileName), $"{new string('0', 64)}  Hearsay-0.3.0-win-x64.zip\n");

        var (code, output) = await RunAsync(Options(zip, target));

        Assert.Equal(1, code);
        Assert.Contains("checksum: The checksum of Hearsay-0.3.0-win-x64.zip does not match", output, StringComparison.Ordinal);
        Assert.Equal("old", FakeApp.Marker(target));
    }

    [Fact]
    public async Task WrongVersionStopsBeforeAnyChange()
    {
        var target = FakeApp.MakeInstall(scratch.Path("Target"), "0.2.0", "old");
        var zip = WriteZip(FakeApp.Zip("0.2.9", "new"));
        var (code, output) = await RunAsync(Options(zip, target));
        Assert.Equal(1, code);
        Assert.Contains("The new app is version 0.2.9, not 0.3.0.", output, StringComparison.Ordinal);
        Assert.Equal("old", FakeApp.Marker(target));
        Assert.False(Directory.Exists(scratch.Path(".Hearsay-update-0.3.0")));
    }

    [Fact]
    public async Task SignedRunningBuildRefusesAnUnsignedZip()
    {
        var target = FakeApp.MakeInstall(scratch.Path("Target"), "0.2.0", "old");
        var zip = WriteZip(FakeApp.Zip("0.3.0", "new"));
        var options = Options(zip, target);
        File.WriteAllText(options.RunningExe ?? "", FakeApp.Content("0.2.0", signature: "untrusted:CN=Hearsay Code Signing (self-signed):AA11"));

        var (code, output) = await RunAsync(options);

        Assert.Equal(1, code);
        Assert.Contains("The code signature of the new version is not valid: not signed", output, StringComparison.Ordinal);
        Assert.Equal("old", FakeApp.Marker(target));
    }

    [Fact]
    public async Task RefusesMissingInputsAndTheRunningApp()
    {
        var zip = WriteZip(FakeApp.Zip("0.3.0", "new"));

        var (code, output) = await RunAsync(Options(zip, target: null));
        Assert.Equal(1, code);
        Assert.Contains("HEARSAY_INSTALL_TARGET is not set", output, StringComparison.Ordinal);

        var options = Options(zip, scratch.Path("Running"));
        (code, output) = await RunAsync(options);
        Assert.Equal(1, code);
        Assert.Contains("refusing to replace the running app", output, StringComparison.Ordinal);
        Assert.Equal("running", FakeApp.Marker(scratch.Path("Running")));

        (code, output) = await RunAsync(Options(scratch.Path("Downloads", "missing.zip"), scratch.Path("Target")));
        Assert.Equal(1, code);
        Assert.Contains("no zip file at", output, StringComparison.Ordinal);

        (code, output) = await RunAsync(Options(zip, scratch.Path("NoApp")));
        Assert.Equal(1, code);
        Assert.Contains("install location: This copy of Hearsay is not in its own app folder", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsComeFromTheEnvironmentOnlyWhenSet()
    {
        // The variables are process-wide; this test only reads the unset case
        // so it cannot race the others.
        if (Environment.GetEnvironmentVariable(UpdateInstallDebug.Variable) is null)
        {
            Assert.Null(UpdateInstallDebug.FromEnvironment(null));
        }
        Assert.Equal("HEARSAY_INSTALL_UPDATE", UpdateInstallDebug.Variable);
        Assert.Equal("HEARSAY_INSTALL_TARGET", UpdateInstallDebug.TargetVariable);
        Assert.Equal("HEARSAY_INSTALL_VERSION", UpdateInstallDebug.VersionVariable);
    }
}
