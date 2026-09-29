using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Hearsay.Core.Updates;

namespace Hearsay.Tests.Updates;

/// <summary>
/// The file-level steps of the in-app update install (PLAN.md 4.6, 18.4).
/// Port of <c>UpdateInstallTests</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/UpdateInstallTests.swift, one test
/// per Swift test where Windows has the rule: the DMG asset tests test the
/// zip asset; the translocation test tests "opened straight from the zip";
/// the quarantine test tests the mark of the web; the bundle replacement
/// tests are <see cref="UpdateSwapHelperTests"/>. <c>appOnADiskImageIsRejected</c>
/// has no Windows counterpart (nothing mounts a zip). The Windows-only steps
/// (download, extraction, identity and signer, staging, the whole
/// preparation) follow. Everything runs in scratch folders.
/// </summary>
public sealed class UpdateInstallTests : IDisposable
{
    private readonly ScratchFolder scratch = new();
    private readonly FakeGitHub hub = new();
    private readonly FakeIdentityReader reader = new();

    public void Dispose()
    {
        hub.Dispose();
        scratch.Dispose();
    }

    // Checksums

    [Fact]
    public void ParsesTextAndBinaryModeLines()
    {
        var zipDigest = new string('a', 64);
        var otherDigest = "5891B5B522D5DF086D0FF0B110FBD9D21BB4FC7163AF34D08286A2E846F6BE03";
        var text = $"{zipDigest}  Hearsay-0.3.0-win-x64.zip\n{otherDigest} *Hearsay 0.3.0.dmg\r\n\n";
        var sums = UpdateInstall.ParseChecksums(text);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Hearsay-0.3.0-win-x64.zip"] = zipDigest,
                ["Hearsay 0.3.0.dmg"] = otherDigest.ToLowerInvariant(),
            },
            sums);
    }

    [Fact]
    public void IgnoresMalformedLines()
    {
        var good = new string('0', 64);
        var text = string.Join('\n',
            "not a checksum line",
            "abc123  short.zip",
            $"{new string('g', 64)}  nothex.zip",
            good,
            $"{good}  *",
            $"{good}  Hearsay-0.3.0-win-x64.zip");
        Assert.Equal(new Dictionary<string, string> { ["Hearsay-0.3.0-win-x64.zip"] = good }, UpdateInstall.ParseChecksums(text));
        Assert.Empty(UpdateInstall.ParseChecksums(""));
    }

    [Fact]
    public void Sha256OfAKnownFile()
    {
        var file = scratch.Path("hello.txt");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("hello\n"));
        Assert.Equal("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03", UpdateInstall.Sha256Hex(file));
    }

    [Fact]
    public void Sha256OfAFileLargerThanOneChunk()
    {
        var file = scratch.Path("big.bin");
        // 2.5 MiB of zeros: three reads.
        File.WriteAllBytes(file, new byte[5 << 19]);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Equal(expected, UpdateInstall.Sha256Hex(file));
    }

    [Fact]
    public void Sha256OfAMissingFileThrows()
    {
        Assert.ThrowsAny<IOException>(() => UpdateInstall.Sha256Hex(scratch.Path($"missing-{Guid.NewGuid():N}")));
    }

    // Install location

    [Fact]
    public void WritableAppFolderPasses()
    {
        var app = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        UpdateInstall.CheckInstallLocation(app);
    }

    /// <summary>The Mac's <c>translocatedAppIsRejected</c>: Explorer, 7-Zip and WinRAR run a zip's exe from a temporary copy.</summary>
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\Temp1_Hearsay-0.3.0-win-x64.zip\Hearsay")]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\7zO4C2A1B3E\Hearsay")]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\Rar$EXa12345.6789\Hearsay")]
    public void AppRunFromTheZipIsRejected(string folder)
    {
        var error = Assert.Throws<InstallLocationException>(() => UpdateInstall.CheckInstallLocation(folder));
        Assert.Equal(new InstallLocationProblem.RunningFromArchive(), error.Problem);
    }

    [Fact]
    public void NotAnAppFolderIsRejected()
    {
        // A folder without Hearsay.exe, a missing folder, and a file.
        var plain = scratch.Path("Hearsay");
        Directory.CreateDirectory(plain);
        var file = scratch.Path("File");
        File.WriteAllText(file, "");
        foreach (var folder in new[] { plain, scratch.Path("Missing"), file })
        {
            var error = Assert.Throws<InstallLocationException>(() => UpdateInstall.CheckInstallLocation(folder));
            Assert.Equal(new InstallLocationProblem.NotAnInstallFolder(), error.Problem);
        }
    }

    [Fact]
    public void ReadOnlyParentIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var parent = scratch.Path("ReadOnly");
        var app = FakeApp.MakeInstall(Path.Combine(parent, "Hearsay"), "0.2.0", "old");
        var directory = new DirectoryInfo(parent);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no user SID");
        var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);
        try
        {
            var error = Assert.Throws<InstallLocationException>(() => UpdateInstall.CheckInstallLocation(app));
            Assert.Equal(new InstallLocationProblem.FolderNotWritable(parent), error.Problem);
        }
        finally
        {
            security = directory.GetAccessControl();
            security.RemoveAccessRule(deny);
            directory.SetAccessControl(security);
        }
    }

    [Fact]
    public void ProblemsTellTheUserWhatToDo()
    {
        InstallLocationProblem[] problems =
        [
            new InstallLocationProblem.RunningFromArchive(),
            new InstallLocationProblem.FolderNotWritable(@"C:\Program Files\Hearsay"),
        ];
        foreach (var problem in problems)
        {
            Assert.Contains("a folder you can write to", problem.Description, StringComparison.Ordinal);
        }
        Assert.Contains(@"C:\Program Files\Hearsay", problems[1].Description, StringComparison.Ordinal);
        Assert.Contains("release page", new InstallLocationProblem.NotAnInstallFolder().Description, StringComparison.Ordinal);
    }

    // Asset selection

    private static ReleaseAsset Asset(string name) => new(name, new Uri("https://example.test/" + name), 1);

    private static ReleaseInfo Release(params string[] names) => new()
    {
        Version = "0.3.0",
        TagName = "v0.3.0",
        HtmlUri = new Uri("https://example.test/release"),
        Assets = [.. names.Select(Asset)],
    };

    [Fact]
    public void ExactZipNameWins()
    {
        var info = Release("Hearsay-0.2.0-win-x64.zip", "Hearsay-0.3.0-win-x64.zip", "Hearsay-0.3.0.dmg", "SHA256SUMS.txt");
        Assert.Equal("Hearsay-0.3.0-win-x64.zip", info.ZipAsset("0.3.0")?.Name);
        Assert.Equal("SHA256SUMS.txt", info.ChecksumsAsset?.Name);
    }

    [Fact]
    public void SingleWindowsZipIsTheFallback()
    {
        var info = Release("Hearsay-win-x64.zip", "Hearsay-0.3.0.dmg", "SHA256SUMS.txt");
        Assert.Equal("Hearsay-win-x64.zip", info.ZipAsset("0.3.0")?.Name);
        Assert.Equal("HEARSAY-WIN-X64.ZIP", Release("HEARSAY-WIN-X64.ZIP").ZipAsset("0.3.0")?.Name);
    }

    [Fact]
    public void NoOrSeveralUnnamedZipsGiveNone()
    {
        Assert.Null(Release("notes.txt", "Hearsay-0.3.0.dmg").ZipAsset("0.3.0"));
        Assert.Null(Release("A-win-x64.zip", "B-win-x64.zip").ZipAsset("0.3.0"));
        // Any other zip (a source or macOS archive) is never taken for the Windows build.
        Assert.Null(Release("Hearsay-0.3.0.zip").ZipAsset("0.3.0"));
        Assert.Null(Release().ZipAsset("0.3.0"));
        Assert.Null(Release("sha256sums.txt").ChecksumsAsset);
    }

    // Mark of the web (the Mac's quarantine)

    [Fact]
    public void RemovesTheMarkOfTheWebFromTheWholeTree()
    {
        var app = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.3.0", "new");
        var nested = Path.Combine(app, "runtimes", "native.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(nested) ?? app);
        File.WriteAllText(nested, "native");
        var plain = Path.Combine(app, "Plain.txt");
        File.WriteAllText(plain, "");
        foreach (var file in new[] { Path.Combine(app, UpdateInstall.ExeName), nested })
        {
            File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            Assert.True(File.Exists(file + ":Zone.Identifier"));
        }

        UpdateInstall.RemoveMarkOfTheWeb(app);

        foreach (var file in new[] { Path.Combine(app, UpdateInstall.ExeName), nested, plain })
        {
            Assert.False(File.Exists(file + ":Zone.Identifier"));
            Assert.True(File.Exists(file));
        }
        Assert.Equal("Hearsay|0.3.0|unsigned", File.ReadAllText(Path.Combine(app, UpdateInstall.ExeName)));
    }

    // Windows only: cache folder

    [Fact]
    public void CacheFolderKeepsOnlyThisVersion()
    {
        var root = scratch.Path("Updates");
        Directory.CreateDirectory(Path.Combine(root, "0.2.0"));
        File.WriteAllText(Path.Combine(root, "0.2.0", "old.zip"), "x");
        Directory.CreateDirectory(Path.Combine(root, "0.3.0"));
        File.WriteAllText(Path.Combine(root, "0.3.0", "keep.zip"), "x");
        File.WriteAllText(Path.Combine(root, "install.log"), "log");

        var folder = UpdateInstall.PrepareCacheFolder(root, "0.3.0");

        Assert.Equal(Path.Combine(root, "0.3.0"), folder);
        Assert.Equal(["0.3.0"], Directory.GetDirectories(root).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(folder, "keep.zip")));
        // Files beside the version folders (the helper's log) stay.
        Assert.True(File.Exists(Path.Combine(root, "install.log")));
    }

    [Fact]
    public void VersionComesFromTheZipName()
    {
        Assert.Equal("0.3.0", UpdateInstall.VersionFromZipName("Hearsay-0.3.0-win-x64.zip"));
        Assert.Equal("0.3.0-beta.1", UpdateInstall.VersionFromZipName("Hearsay-0.3.0-beta.1-win-x64.zip"));
        Assert.Null(UpdateInstall.VersionFromZipName("Hearsay--win-x64.zip"));
        Assert.Null(UpdateInstall.VersionFromZipName("Hearsay-0.3.0.zip"));
        Assert.Null(UpdateInstall.VersionFromZipName("Hearsay-0.3.0.dmg"));
    }

    // Windows only: extraction

    private string WriteZip(byte[] data, string name = "Hearsay-0.3.0-win-x64.zip")
    {
        var path = scratch.Path(name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void ExtractFindsTheAppInTheTopFolder()
    {
        var zip = WriteZip(FakeApp.Zip("0.3.0", "new"));
        var app = UpdateInstall.Extract(zip, scratch.Path("extract"));
        Assert.Equal(scratch.Path("extract", "Hearsay"), app);
        Assert.Equal("new", FakeApp.Marker(app));
        Assert.True(File.Exists(Path.Combine(app, "runtimes", "win-x64", "native.txt")));
    }

    [Fact]
    public void ExtractFindsTheAppAtTheRoot()
    {
        var zip = WriteZip(FakeApp.Zip("0.3.0", "new", top: null));
        var destination = scratch.Path("extract");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "stale.txt"), "from an earlier run");
        var app = UpdateInstall.Extract(zip, destination);
        Assert.Equal(destination, app);
        Assert.False(File.Exists(Path.Combine(destination, "stale.txt")));
    }

    [Fact]
    public void ExtractNeedsExactlyOneApp()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            FakeApp.Add(archive, "A/Hearsay.exe", FakeApp.Content("0.3.0"));
            FakeApp.Add(archive, "B/Hearsay.exe", FakeApp.Content("0.3.0"));
            FakeApp.Add(archive, "C/Deeper/Hearsay.exe", FakeApp.Content("0.3.0"));
        }
        var two = Assert.Throws<UpdatePackageException>(() => UpdateInstall.Extract(WriteZip(memory.ToArray(), "two.zip"), scratch.Path("two")));
        Assert.Equal(new UpdatePackageError.AppNotFound(2), two.Error);

        using var empty = new MemoryStream();
        using (var archive = new ZipArchive(empty, ZipArchiveMode.Create, leaveOpen: true))
        {
            FakeApp.Add(archive, "readme.txt", "no app");
        }
        var none = Assert.Throws<UpdatePackageException>(() => UpdateInstall.Extract(WriteZip(empty.ToArray(), "none.zip"), scratch.Path("none")));
        Assert.Equal(new UpdatePackageError.AppNotFound(0), none.Error);
        Assert.Equal("The zip file should contain one Hearsay.exe but contains 0.", none.Error.Description);
    }

    [Fact]
    public void BrokenZipAndPathTraversalFail()
    {
        var broken = Assert.Throws<UpdatePackageException>(() =>
            UpdateInstall.Extract(WriteZip(Encoding.UTF8.GetBytes("not a zip"), "broken.zip"), scratch.Path("broken")));
        Assert.IsType<UpdatePackageError.ExtractFailed>(broken.Error);
        Assert.False(Directory.Exists(scratch.Path("broken")));

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            FakeApp.Add(archive, "Hearsay/Hearsay.exe", FakeApp.Content("0.3.0"));
            FakeApp.Add(archive, "../escaped.txt", "outside");
        }
        var traversal = Assert.Throws<UpdatePackageException>(() =>
            UpdateInstall.Extract(WriteZip(memory.ToArray(), "slip.zip"), scratch.Path("slip")));
        Assert.IsType<UpdatePackageError.ExtractFailed>(traversal.Error);
        Assert.False(File.Exists(scratch.Path("escaped.txt")));
    }

    // Windows only: identity and signer

    private const string SelfSigned = "untrusted:CN=Hearsay Code Signing (self-signed):AA11";

    [Fact]
    public void UnsignedRunningBuildSkipsTheSignerCheck()
    {
        Assert.Contains("signer check skipped, new version unsigned", UpdateInstall.CheckSignature(AuthenticodeInfo.NotSigned, AuthenticodeInfo.NotSigned), StringComparison.Ordinal);
        Assert.Contains("new version signed by CN=Hearsay", UpdateInstall.CheckSignature(null, FakeIdentityReader.Signature(SelfSigned)), StringComparison.Ordinal);
        // Even then a signature that does not verify is refused.
        var error = Assert.Throws<UpdatePackageException>(() => UpdateInstall.CheckSignature(null, FakeIdentityReader.Signature("invalid")));
        Assert.IsType<UpdatePackageError.SignatureInvalid>(error.Error);
    }

    [Fact]
    public void SignedRunningBuildNeedsTheSameCertificate()
    {
        var running = FakeIdentityReader.Signature(SelfSigned);
        Assert.Contains("running build's certificate", UpdateInstall.CheckSignature(running, FakeIdentityReader.Signature("untrusted:CN=Hearsay Code Signing (self-signed):aa11")), StringComparison.Ordinal);

        var other = Assert.Throws<UpdatePackageException>(() =>
            UpdateInstall.CheckSignature(running, FakeIdentityReader.Signature("untrusted:CN=Hearsay Code Signing (self-signed):BB22")));
        Assert.Equal(new UpdatePackageError.SignerMismatch("CN=Hearsay Code Signing (self-signed)", "CN=Hearsay Code Signing (self-signed)"), other.Error);

        foreach (var candidate in new[] { "unsigned", "invalid" })
        {
            var error = Assert.Throws<UpdatePackageException>(() => UpdateInstall.CheckSignature(running, FakeIdentityReader.Signature(candidate)));
            Assert.IsType<UpdatePackageError.SignatureInvalid>(error.Error);
        }
    }

    [Fact]
    public void TrustedSignerMayRenewItsCertificate()
    {
        var running = FakeIdentityReader.Signature("trusted:CN=Chihling Wang:AA11");
        Assert.Contains("trusted certificate", UpdateInstall.CheckSignature(running, FakeIdentityReader.Signature("trusted:CN=Chihling Wang:CC33")), StringComparison.Ordinal);
        // The same subject from an untrusted (self-made) certificate is not enough.
        Assert.Throws<UpdatePackageException>(() => UpdateInstall.CheckSignature(running, FakeIdentityReader.Signature("untrusted:CN=Chihling Wang:CC33")));
        Assert.Throws<UpdatePackageException>(() => UpdateInstall.CheckSignature(running, FakeIdentityReader.Signature("trusted:CN=Someone Else:CC33")));
    }

    [Fact]
    public void VerifyAppChecksProductAndVersion()
    {
        var app = FakeApp.MakeInstall(scratch.Path("New"), "0.3.0+0123abc", "new");
        Assert.EndsWith("; Hearsay 0.3.0", UpdateInstall.VerifyApp(app, "0.3.0", null, reader), StringComparison.Ordinal);
        Assert.EndsWith("; Hearsay 0.3.0", UpdateInstall.VerifyApp(app, null, null, reader), StringComparison.Ordinal);

        var version = Assert.Throws<UpdatePackageException>(() => UpdateInstall.VerifyApp(app, "0.4.0", null, reader));
        Assert.Equal(new UpdatePackageError.WrongVersion("0.3.0", "0.4.0"), version.Error);
        Assert.Equal("The new app is version 0.3.0, not 0.4.0.", version.Error.Description);

        File.WriteAllText(Path.Combine(app, UpdateInstall.ExeName), FakeApp.Content("0.3.0", product: "Notepad"));
        var product = Assert.Throws<UpdatePackageException>(() => UpdateInstall.VerifyApp(app, "0.3.0", null, reader));
        Assert.Equal(new UpdatePackageError.WrongIdentifier("Notepad"), product.Error);

        File.Delete(Path.Combine(app, UpdateInstall.ExeName));
        var missing = Assert.Throws<UpdatePackageException>(() => UpdateInstall.VerifyApp(app, "0.3.0", null, reader));
        Assert.Equal(new UpdatePackageError.AppNotFound(0), missing.Error);
    }

    [Fact]
    public void StageCopiesNextToTheInstallHiddenAndChecked()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        var app = FakeApp.MakeInstall(scratch.Path("extract", "Hearsay"), "0.3.0", "new");
        File.WriteAllText(Path.Combine(app, "Marker.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        // A stale copy from an earlier attempt is replaced.
        FakeApp.MakeInstall(scratch.Path(".Hearsay-update-0.3.0"), "0.1.0", "stale");

        var staged = UpdateInstall.Stage(app, install, "0.3.0", "0.3.0", null, reader);

        Assert.Equal(scratch.Path(".Hearsay-update-0.3.0"), staged);
        Assert.Equal("new", FakeApp.Marker(staged));
        Assert.True(new DirectoryInfo(staged).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(File.Exists(Path.Combine(staged, "Marker.txt") + ":Zone.Identifier"));
        Assert.Equal("old", FakeApp.Marker(install));
    }

    [Fact]
    public void FailedStageLeavesNoCopy()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        var app = FakeApp.MakeInstall(scratch.Path("extract", "Hearsay"), "0.3.0", "new");
        Assert.Throws<UpdatePackageException>(() => UpdateInstall.Stage(app, install, "0.4.0", "0.4.0", null, reader));
        Assert.False(Directory.Exists(scratch.Path(".Hearsay-update-0.4.0")));
    }

    // Windows only: the real identity reader

    private static string RuntimeFile(string name) =>
        Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "", name);

    [Fact]
    public void ReadsASignedFile()
    {
        var identity = FileAppIdentityReader.Shared.Read(RuntimeFile("System.Runtime.dll"));
        Assert.Equal(SignatureStatus.Trusted, identity.Signature.Status);
        Assert.Contains("Microsoft Corporation", identity.Signature.Subject, StringComparison.Ordinal);
        Assert.Equal(64, identity.Signature.Thumbprint?.Length);
        Assert.NotNull(identity.ProductVersion);
    }

    [Fact]
    public void ReadsAnUnsignedFile()
    {
        var identity = FileAppIdentityReader.Shared.Read(typeof(UpdateInstallTests).Assembly.Location);
        Assert.Equal(SignatureStatus.NotSigned, identity.Signature.Status);
        Assert.Equal("Hearsay.Tests", identity.ProductName);
        Assert.Equal(SignatureStatus.NotSigned, FileAppIdentityReader.Shared.Read(FakeApp.MakeInstall(scratch.Path("Fake"), "1", "x") + @"\Hearsay.exe").Signature.Status);
    }

    [Fact]
    public void ATamperedSignedFileIsInvalid()
    {
        var copy = scratch.Path("System.Runtime.dll");
        var bytes = File.ReadAllBytes(RuntimeFile("System.Runtime.dll"));
        // A byte in the middle of the image, well before the signature at the end.
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);
        var identity = FileAppIdentityReader.Shared.Read(copy);
        Assert.Equal(SignatureStatus.Invalid, identity.Signature.Status);
        Assert.StartsWith("0x", identity.Signature.Detail, StringComparison.Ordinal);
        var error = Assert.Throws<UpdatePackageException>(() => UpdateInstall.CheckSignature(null, identity.Signature));
        Assert.IsType<UpdatePackageError.SignatureInvalid>(error.Error);
    }

    // Windows only: download and the whole preparation

    private const string Base = "https://github.test/o/r/releases/download/v0.3.0/";

    private ReleaseInfo ServedRelease(byte[] zip, string? sums = null, string zipName = "Hearsay-0.3.0-win-x64.zip")
    {
        hub.Register(Base + zipName, HttpStatusCode.OK, zip);
        hub.Register(Base + "SHA256SUMS.txt", HttpStatusCode.OK, sums ?? FakeApp.Sums(zipName, zip));
        return new ReleaseInfo
        {
            Version = "0.3.0",
            TagName = "v0.3.0",
            HtmlUri = new Uri("https://github.test/o/r/releases/tag/v0.3.0"),
            Assets =
            [
                new ReleaseAsset(zipName, new Uri(Base + zipName), zip.Length),
                new ReleaseAsset("SHA256SUMS.txt", new Uri(Base + "SHA256SUMS.txt"), 90),
                new ReleaseAsset("Hearsay-0.3.0.dmg", new Uri(Base + "Hearsay-0.3.0.dmg"), 10),
            ],
        };
    }

    [Fact]
    public async Task DownloadReportsProgressAndMovesIntoPlace()
    {
        var data = new byte[700_000];
        Random.Shared.NextBytes(data);
        hub.Register(Base + "big.zip", HttpStatusCode.OK, data);
        var destination = scratch.Path("big.zip");
        var reports = new List<UpdateDownloadProgress>();
        using var client = new HttpClient(hub, disposeHandler: false);

        await UpdateInstall.DownloadAsync(client, new Uri(Base + "big.zip"), destination, new SyncProgress(reports.Add));

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.Equal(new UpdateDownloadProgress(0, data.Length), reports[0]);
        Assert.Equal(new UpdateDownloadProgress(data.Length, data.Length), reports[^1]);
        Assert.True(reports.Count >= 4);
    }

    [Fact]
    public async Task DownloadFailureLeavesNothing()
    {
        using var client = new HttpClient(hub, disposeHandler: false);
        var destination = scratch.Path("missing.zip");
        File.WriteAllText(destination, "an older file");
        var error = await Assert.ThrowsAsync<UpdatePackageException>(() =>
            UpdateInstall.DownloadAsync(client, new Uri(Base + "missing.zip"), destination));
        Assert.Equal(new UpdatePackageError.DownloadFailed("HTTP 404"), error.Error);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));

        hub.RegisterFailure(Base + "offline.zip", new HttpRequestException("No such host is known."));
        var offline = await Assert.ThrowsAsync<UpdatePackageException>(() =>
            UpdateInstall.DownloadAsync(client, new Uri(Base + "offline.zip"), scratch.Path("offline.zip")));
        Assert.Equal("The download failed: No such host is known.", offline.Error.Description);
    }

    [Fact]
    public async Task CancelledDownloadRemovesThePartialFile()
    {
        hub.RegisterStall(Base + "stall.zip");
        using var client = new HttpClient(hub, disposeHandler: false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var destination = scratch.Path("stall.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateInstall.DownloadAsync(client, new Uri(Base + "stall.zip"), destination, null, cancel.Token));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task PrepareDownloadsVerifiesAndStages()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        var root = scratch.Path("Updates");
        Directory.CreateDirectory(Path.Combine(root, "0.2.5"));
        var release = ServedRelease(FakeApp.Zip("0.3.0", "new"));
        using var client = new HttpClient(hub, disposeHandler: false);

        var prepared = await UpdateInstall.PrepareAsync(client, release, install, root, null, reader);

        Assert.Equal("0.3.0", prepared.Version);
        Assert.Equal(scratch.Path(".Hearsay-update-0.3.0"), prepared.StagedFolder);
        Assert.Equal("new", FakeApp.Marker(prepared.StagedFolder));
        Assert.Equal(Path.Combine(root, "0.3.0"), prepared.CacheFolder);
        Assert.True(File.Exists(Path.Combine(prepared.CacheFolder, "Hearsay-0.3.0-win-x64.zip")));
        Assert.False(Directory.Exists(Path.Combine(prepared.CacheFolder, "extract")));
        Assert.False(Directory.Exists(Path.Combine(root, "0.2.5")));
        Assert.StartsWith("sha256 ", prepared.Summary, StringComparison.Ordinal);
        Assert.Equal("old", FakeApp.Marker(install));

        // Later: the staged copy goes, the verified zip stays and is not downloaded again.
        UpdateInstall.Discard(prepared);
        Assert.False(Directory.Exists(prepared.StagedFolder));
        var again = await UpdateInstall.PrepareAsync(client, release, install, root, null, reader);
        Assert.Single(hub.Requests(Base + "Hearsay-0.3.0-win-x64.zip"));
        Assert.Equal(2, hub.Requests(Base + "SHA256SUMS.txt").Count);
        Assert.True(Directory.Exists(again.StagedFolder));
    }

    [Fact]
    public async Task PrepareRefusesAMismatchAndDeletesTheDownload()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        var root = scratch.Path("Updates");
        var release = ServedRelease(FakeApp.Zip("0.3.0", "new"), sums: $"{new string('0', 64)}  Hearsay-0.3.0-win-x64.zip\n");
        using var client = new HttpClient(hub, disposeHandler: false);

        var error = await Assert.ThrowsAsync<UpdatePackageException>(() => UpdateInstall.PrepareAsync(client, release, install, root, null, reader));

        Assert.Equal(new UpdatePackageError.ChecksumMismatch("Hearsay-0.3.0-win-x64.zip"), error.Error);
        Assert.False(Directory.Exists(Path.Combine(root, "0.3.0")));
        Assert.False(Directory.Exists(scratch.Path(".Hearsay-update-0.3.0")));
        Assert.Equal("old", FakeApp.Marker(install));
    }

    [Fact]
    public async Task PrepareRefusesTheWrongVersionOrSigner()
    {
        var install = FakeApp.MakeInstall(scratch.Path("Hearsay"), "0.2.0", "old");
        var root = scratch.Path("Updates");
        using var client = new HttpClient(hub, disposeHandler: false);

        var release = ServedRelease(FakeApp.Zip("0.2.9", "new"));
        var version = await Assert.ThrowsAsync<UpdatePackageException>(() => UpdateInstall.PrepareAsync(client, release, install, root, null, reader));
        Assert.Equal(new UpdatePackageError.WrongVersion("0.2.9", "0.3.0"), version.Error);
        Assert.False(Directory.Exists(Path.Combine(root, "0.3.0")));

        var running = new AppIdentity("Hearsay", "0.2.0", FakeIdentityReader.Signature(SelfSigned));
        release = ServedRelease(FakeApp.Zip("0.3.0", "new"));
        var signer = await Assert.ThrowsAsync<UpdatePackageException>(() => UpdateInstall.PrepareAsync(client, release, install, root, running, reader));
        Assert.IsType<UpdatePackageError.SignatureInvalid>(signer.Error);
        Assert.False(Directory.Exists(scratch.Path(".Hearsay-update-0.3.0")));
    }

    [Fact]
    public async Task PrepareNeedsTheZipAndTheChecksumList()
    {
        using var client = new HttpClient(hub, disposeHandler: false);
        var release = Release("Hearsay-0.3.0.dmg", "SHA256SUMS.txt");
        var error = await Assert.ThrowsAsync<UpdatePackageException>(() =>
            UpdateInstall.PrepareAsync(client, release, scratch.Path("Hearsay"), scratch.Path("Updates"), null, reader));
        Assert.Equal(new UpdatePackageError.NoPackage(), error.Error);
        Assert.Equal("The release has no Windows zip file or checksum list.", error.Error.Description);
        Assert.Throws<UpdatePackageException>(() =>
            UpdateInstall.PrepareAsync(client, Release("Hearsay-0.3.0-win-x64.zip"), scratch.Path("Hearsay"), scratch.Path("Updates"), null, reader).GetAwaiter().GetResult());
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
