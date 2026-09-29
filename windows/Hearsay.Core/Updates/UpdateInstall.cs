using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Updates;

/// <summary>
/// Why the running copy of Hearsay cannot replace itself. The Windows
/// counterpart of <c>UpdateInstall.InstallLocationProblem</c> in
/// mac/HearsayCore/Sources/HearsayCore/Updates/UpdateInstall.swift
/// (PLAN.md 18.4, "Updates and packaging"): App Translocation becomes "opened
/// straight from the zip", a disk image has no equivalent, and "not an app
/// bundle" becomes "no Hearsay.exe in the folder".
/// </summary>
public abstract record InstallLocationProblem
{
    private InstallLocationProblem()
    {
    }

    /// <summary>Explorer, 7-Zip or WinRAR runs Hearsay from a temporary copy of the zip's contents.</summary>
    public sealed record RunningFromArchive : InstallLocationProblem;

    /// <summary>The running code is not in a folder with Hearsay.exe.</summary>
    public sealed record NotAnInstallFolder : InstallLocationProblem;

    /// <summary>The install folder, or the folder holding it, cannot be written.</summary>
    public sealed record FolderNotWritable(string Folder) : InstallLocationProblem;

    /// <summary>The message shown to the user (Windows keys; the Mac's say "Applications folder").</summary>
    public string Description => this switch
    {
        RunningFromArchive => "Windows is running Hearsay straight from the zip file. Extract the zip to a folder you can write to, open Hearsay from there, then check for updates again.",
        NotAnInstallFolder => "This copy of Hearsay is not in its own app folder, so it cannot update itself. Download the new version from the release page.",
        FolderNotWritable e => string.Format(CultureInfo.CurrentCulture, "Hearsay cannot write to the folder {0}. Move the Hearsay folder to a folder you can write to, open it from there, then check for updates again.", e.Folder),
        _ => throw new InvalidOperationException("Unknown InstallLocationProblem."),
    };

    /// <summary><see cref="Description"/> as its catalog key (Windows keys) and values, for the app to translate.</summary>
    public LocalizedMessage Localized => this switch
    {
        RunningFromArchive => new LocalizedMessage("Windows is running Hearsay straight from the zip file. Extract the zip to a folder you can write to, open Hearsay from there, then check for updates again."),
        NotAnInstallFolder => new LocalizedMessage("This copy of Hearsay is not in its own app folder, so it cannot update itself. Download the new version from the release page."),
        FolderNotWritable e => new LocalizedMessage("Hearsay cannot write to the folder %@. Move the Hearsay folder to a folder you can write to, open it from there, then check for updates again.", e.Folder),
        _ => throw new InvalidOperationException("Unknown InstallLocationProblem."),
    };
}

/// <summary>Thrown by <see cref="UpdateInstall.CheckInstallLocation"/>.</summary>
public sealed class InstallLocationException : Exception, ILocalizedError
{
    public InstallLocationException(InstallLocationProblem problem)
        : base(problem?.Description)
    {
        ArgumentNullException.ThrowIfNull(problem);
        Problem = problem;
    }

    public InstallLocationProblem Problem { get; }

    public ILocalizedMessage LocalizedMessage => Problem.Localized;
}

/// <summary>
/// Why a downloaded update was not installed. Port of <c>UpdatePackageError</c>
/// in mac/Hearsay/Features/Updates/UpdatePackage.swift (the Mac keeps it in the
/// app); the English text is the Swift key where the Mac has the same text,
/// otherwise a Windows key (listed in PLAN.md 18.4).
/// </summary>
public abstract record UpdatePackageError
{
    private UpdatePackageError()
    {
    }

    /// <summary><c>SHA256SUMS.txt</c> has no line for the zip.</summary>
    public sealed record ChecksumMissing(string Name) : UpdatePackageError;

    public sealed record ChecksumMismatch(string Name) : UpdatePackageError;

    /// <summary>The Mac's <c>mountFailed</c>: the zip could not be extracted.</summary>
    public sealed record ExtractFailed(string Detail) : UpdatePackageError;

    /// <summary>The zip holds no Hearsay.exe, or more than one.</summary>
    public sealed record AppNotFound(int Count) : UpdatePackageError;

    public sealed record SignatureInvalid(string Detail) : UpdatePackageError;

    /// <summary>Windows only: signed, but not by the running build's signer (a null subject is shown as "unknown").</summary>
    public sealed record SignerMismatch(string? Found, string? Expected) : UpdatePackageError;

    /// <summary>The Mac's bundle identifier check, on the ProductName.</summary>
    public sealed record WrongIdentifier(string? Found) : UpdatePackageError;

    public sealed record WrongVersion(string? Found, string Expected) : UpdatePackageError;

    public sealed record DownloadFailed(string Detail) : UpdatePackageError
    {
        /// <summary><see cref="Detail"/> with its catalog key, when Hearsay wrote it.</summary>
        public ILocalizedMessage? LocalizedDetail { get; init; }

        /// <summary>The server answered with <paramref name="code"/> ("HTTP 404").</summary>
        public static DownloadFailed HttpStatus(int code)
        {
            var detail = CommonMessages.HttpStatus(code);
            return new(detail.English) { LocalizedDetail = detail };
        }

        /// <summary>No bytes arrived within <see cref="UpdateInstall.ReadTimeout"/>.</summary>
        public static DownloadFailed TimedOut() =>
            new(CommonMessages.RequestTimedOut.English) { LocalizedDetail = CommonMessages.RequestTimedOut };
    }

    /// <summary>The release lacks the Windows zip or <c>SHA256SUMS.txt</c>.</summary>
    public sealed record NoPackage : UpdatePackageError;

    /// <summary>Whether the download is not trusted (checksum, archive, identity or signature): the cache is deleted.</summary>
    public bool IsVerificationFailure => this is not (DownloadFailed or NoPackage);

    public string Description => this switch
    {
        ChecksumMissing e => string.Format(CultureInfo.CurrentCulture, "The release's checksum list has no entry for {0}.", e.Name),
        ChecksumMismatch e => string.Format(CultureInfo.CurrentCulture, "The checksum of {0} does not match the release's checksum list.", e.Name),
        ExtractFailed e => string.Format(CultureInfo.CurrentCulture, "The zip file could not be extracted: {0}", e.Detail),
        AppNotFound e => string.Format(CultureInfo.CurrentCulture, "The zip file should contain one Hearsay.exe but contains {0}.", e.Count),
        SignatureInvalid e => string.Format(CultureInfo.CurrentCulture, "The code signature of the new version is not valid: {0}", e.Detail),
        SignerMismatch e => string.Format(CultureInfo.CurrentCulture, "The new version is signed by {0}, not by {1} like this copy of Hearsay.", e.Found ?? "unknown", e.Expected ?? "unknown"),
        WrongIdentifier e => string.Format(CultureInfo.CurrentCulture, "The new app has the product name {0}, not Hearsay.", e.Found ?? "none"),
        WrongVersion e => string.Format(CultureInfo.CurrentCulture, "The new app is version {0}, not {1}.", e.Found ?? "unknown", e.Expected),
        DownloadFailed e => string.Format(CultureInfo.CurrentCulture, "The download failed: {0}", e.Detail),
        NoPackage => "The release has no Windows zip file or checksum list.",
        _ => throw new InvalidOperationException("Unknown UpdatePackageError."),
    };

    /// <summary>
    /// <see cref="Description"/> as its catalog key and values, for the app to
    /// translate: the Mac's key (app catalog, UpdatePackage.swift) where the
    /// text is the same, otherwise a Windows key; the inserted "unknown" and
    /// "none" are keys too (<see cref="CommonMessages"/>).
    /// </summary>
    public LocalizedMessage Localized => this switch
    {
        ChecksumMissing e => new LocalizedMessage("The release's checksum list has no entry for %@.", e.Name),
        ChecksumMismatch e => new LocalizedMessage("The checksum of %@ does not match the release's checksum list.", e.Name),
        ExtractFailed e => new LocalizedMessage("The zip file could not be extracted: %@", e.Detail),
        AppNotFound e => new LocalizedMessage("The zip file should contain one Hearsay.exe but contains %lld.", e.Count),
        SignatureInvalid e => new LocalizedMessage("The code signature of the new version is not valid: %@", e.Detail),
        SignerMismatch e => new LocalizedMessage("The new version is signed by %@, not by %@ like this copy of Hearsay.",
            OrUnknown(e.Found), OrUnknown(e.Expected)),
        WrongIdentifier e => new LocalizedMessage("The new app has the product name %@, not Hearsay.",
            (object?)e.Found ?? CommonMessages.None),
        WrongVersion e => new LocalizedMessage("The new app is version %@, not %@.", OrUnknown(e.Found), e.Expected),
        DownloadFailed e => new LocalizedMessage("The download failed: %@", (object?)e.LocalizedDetail ?? e.Detail),
        NoPackage => new LocalizedMessage("The release has no Windows zip file or checksum list."),
        _ => throw new InvalidOperationException("Unknown UpdatePackageError."),
    };

    private static object OrUnknown(string? value) => (object?)value ?? CommonMessages.Unknown;
}

public sealed class UpdatePackageException : Exception, ILocalizedError
{
    public UpdatePackageException(UpdatePackageError error)
        : base(error?.Description)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public UpdatePackageException(UpdatePackageError error, Exception inner)
        : base(error?.Description, inner)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public UpdatePackageError Error { get; }

    public ILocalizedMessage LocalizedMessage => Error.Localized;
}

/// <summary>Bytes received so far and the total when the server announced it.</summary>
public readonly record struct UpdateDownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 and var total ? Math.Min(1, (double)Received / total) : null;
}

/// <summary>A verified update staged next to the install folder, ready for <see cref="UpdateSwapHelper"/>.</summary>
/// <param name="Version">The release version.</param>
/// <param name="StagedFolder"><c>&lt;parent of install&gt;\.Hearsay-update-&lt;version&gt;</c>, hidden.</param>
/// <param name="CacheFolder"><c>%LOCALAPPDATA%\Hearsay\Updates\&lt;version&gt;</c> with the verified zip.</param>
/// <param name="Summary">What was checked, for the log.</param>
public sealed record PreparedUpdate(string Version, string StagedFolder, string CacheFolder, string Summary);

/// <summary>
/// The file-level steps of installing an update on Windows (PLAN.md 4.6 and
/// 18.4, "Updates and packaging"). Port of
/// mac/HearsayCore/Sources/HearsayCore/Updates/UpdateInstall.swift together
/// with the non-UI parts of mac/Hearsay/Features/Updates/UpdatePackage.swift
/// and the download/verify/stage sequence of UpdateInstaller.swift, so the
/// app keeps only the dialogs:
/// <list type="number">
/// <item><see cref="CheckInstallLocation"/> on the running install folder.</item>
/// <item>Asset choice: <see cref="ReleaseInfo.ZipAsset"/> and <c>SHA256SUMS.txt</c>.</item>
/// <item>Download into <c>%LOCALAPPDATA%\Hearsay\Updates\&lt;version&gt;\</c> (other version folders removed first); a zip already there whose checksum matches is not downloaded again.</item>
/// <item>Verify the SHA-256, extract (the Mac mounts the DMG), exactly one Hearsay.exe.</item>
/// <item>Check ProductName, ProductVersion and the Authenticode signer (<see cref="CheckSignature"/>).</item>
/// <item>Stage: copy to <c>&lt;parent of install&gt;\.Hearsay-update-&lt;version&gt;</c> (same volume, hidden), clear the mark of the web, check the copy again.</item>
/// <item>Install: <see cref="UpdateSwapHelper"/> waits for the app to exit, swaps the folders with rollback, and relaunches, because Windows cannot replace a running exe (the Mac swaps in-process with <c>replaceItemAt</c>).</item>
/// </list>
/// </summary>
public static class UpdateInstall
{
    public const string ExeName = "Hearsay.exe";

    /// <summary>The ProductName every Hearsay.exe carries (the App's <c>Product</c> MSBuild property).</summary>
    public const string ProductName = "Hearsay";

    /// <summary>Where the old folder waits during the swap, next to the install folder.</summary>
    public const string PreviousFolderName = ".Hearsay-previous";

    /// <summary><c>%LOCALAPPDATA%\Hearsay\Updates</c>.</summary>
    public static string DefaultUpdatesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearsay", "Updates");

    /// <summary>The install location README.md suggests: <c>%LOCALAPPDATA%\Programs\Hearsay</c>.</summary>
    public static string RecommendedInstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hearsay");

    /// <summary>The hidden name the new version is staged under, next to the running one.</summary>
    public static string StagedFolderName(string version) => $".Hearsay-update-{version}";

    // Checksums

    /// <summary>
    /// Parses <c>sha256sum</c> / <c>shasum -a 256</c> output:
    /// <c>&lt;hex&gt;  &lt;file name&gt;</c> (text mode) or
    /// <c>&lt;hex&gt; *&lt;file name&gt;</c> (binary mode). Returns file name
    /// to lowercase hex. Blank lines and lines without a 64-digit hex digest
    /// and a name are ignored; a later line for the same name wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split(['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim(' ', '\t');
            var space = line.IndexOfAny([' ', '\t']);
            if (space < 0)
            {
                continue;
            }
            var digest = line[..space].ToLowerInvariant();
            if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
            {
                continue;
            }
            var name = line[space..].TrimStart(' ', '\t');
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }
            if (name.Length == 0)
            {
                continue;
            }
            result[name] = digest;
        }
        return result;
    }

    /// <summary>The SHA-256 of the file as lowercase hex, streamed so a large zip is never held in memory.</summary>
    public static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// The zip's SHA-256 equals its entry in the checksum file (the entry is
    /// looked up by the zip's file name). Returns the digest.
    /// </summary>
    /// <exception cref="UpdatePackageException">No entry, or a different digest.</exception>
    public static string VerifyChecksum(string zipPath, string checksumFile)
    {
        var text = File.ReadAllText(checksumFile);
        var name = Path.GetFileName(zipPath);
        if (!ParseChecksums(text).TryGetValue(name, out var expected))
        {
            throw new UpdatePackageException(new UpdatePackageError.ChecksumMissing(name));
        }
        var actual = Sha256Hex(zipPath);
        return actual == expected
            ? actual
            : throw new UpdatePackageException(new UpdatePackageError.ChecksumMismatch(name));
    }

    // Install location

    /// <summary>Explorer's "Temp1_x.zip", 7-Zip's "7zO1A2B", WinRAR's "Rar$EXa…".</summary>
    private static readonly Regex ArchiveTempFolder = new(
        @"^(Temp\d+_.+\.zip|7zO[0-9A-F]+|Rar\$.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Throws the first problem for the running install folder: opened
    /// straight from a zip, no Hearsay.exe in it, or it or its parent cannot
    /// be written (the swap renames the folder, so both need write access).
    /// </summary>
    /// <exception cref="InstallLocationException">The folder cannot be replaced.</exception>
    public static void CheckInstallLocation(string installFolder)
    {
        ArgumentNullException.ThrowIfNull(installFolder);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder));
        if (full.Split(Path.DirectorySeparatorChar).Any(ArchiveTempFolder.IsMatch))
        {
            throw new InstallLocationException(new InstallLocationProblem.RunningFromArchive());
        }
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(full) || !File.Exists(Path.Combine(full, ExeName)))
        {
            throw new InstallLocationException(new InstallLocationProblem.NotAnInstallFolder());
        }
        foreach (var folder in new[] { parent, full })
        {
            if (!IsWritable(folder))
            {
                throw new InstallLocationException(new InstallLocationProblem.FolderNotWritable(folder));
            }
        }
    }

    /// <summary>Creates and deletes a probe file: ACLs decide, not the read-only attribute.</summary>
    internal static bool IsWritable(string folder)
    {
        try
        {
            using var probe = new FileStream(
                Path.Combine(folder, $".hearsay-write-test-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // Cache

    /// <summary>Creates <c>&lt;root&gt;\&lt;version&gt;</c> and removes the other versions' folders. Returns the folder.</summary>
    public static string PrepareCacheFolder(string updatesRoot, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(updatesRoot);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var folder = Path.Combine(updatesRoot, version);
        Directory.CreateDirectory(folder);
        foreach (var other in Directory.EnumerateDirectories(updatesRoot))
        {
            if (!string.Equals(Path.GetFileName(other), version, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFolder(other);
            }
        }
        return folder;
    }

    // Download

    /// <summary>Idle limit between reads (URLSession's default request timeout).</summary>
    internal static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Streams <paramref name="uri"/> into <paramref name="destination"/> (via
    /// <c>&lt;destination&gt;.part</c>), reporting about every 256 KB. A
    /// cancelled or failed transfer removes the partial file. Port of
    /// <c>UpdatePackage.download</c>.
    /// </summary>
    /// <exception cref="UpdatePackageException"><see cref="UpdatePackageError.DownloadFailed"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static async Task DownloadAsync(
        HttpClient client, Uri uri, string destination, IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var partial = destination + ".part";
        TryDeleteFile(partial);
        TryDeleteFile(destination);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hearsay", "1"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdatePackageException(UpdatePackageError.DownloadFailed.HttpStatus((int)response.StatusCode));
            }
            var total = response.Content.Headers.ContentLength is > 0 and var length ? length : (long?)null;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    long received = 0;
                    long reported = 0;
                    progress?.Report(new UpdateDownloadProgress(0, total));
                    while (true)
                    {
                        int read;
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            idle.CancelAfter(ReadTimeout);
                            try
                            {
                                read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new UpdatePackageException(UpdatePackageError.DownloadFailed.TimedOut());
                            }
                        }
                        if (read == 0)
                        {
                            break;
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;
                        if (received - reported >= 1 << 18)
                        {
                            reported = received;
                            progress?.Report(new UpdateDownloadProgress(received, total));
                        }
                    }
                    progress?.Report(new UpdateDownloadProgress(received, total));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination);
        }
        catch (Exception error) when (error is OperationCanceledException or UpdatePackageException)
        {
            TryDeleteFile(partial);
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(partial);
            throw new UpdatePackageException(new UpdatePackageError.DownloadFailed(error.Message), error);
        }
    }

    // Extract

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destination"/>
    /// (emptied first) and returns the app folder: the destination itself
    /// when Hearsay.exe is at the zip's root, else the one top-level folder
    /// holding it. The Mac's <c>UpdatePackage.mount</c> plus "exactly one
    /// .app at the root". Entries that would land outside the destination are
    /// refused by <see cref="ZipFile"/>.
    /// </summary>
    /// <exception cref="UpdatePackageException"><see cref="UpdatePackageError.ExtractFailed"/> or <see cref="UpdatePackageError.AppNotFound"/>.</exception>
    public static string Extract(string zipPath, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        TryDeleteFolder(destination);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: false);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            TryDeleteFolder(destination);
            throw new UpdatePackageException(new UpdatePackageError.ExtractFailed(error.Message), error);
        }
        var candidates = new List<string>();
        if (File.Exists(Path.Combine(destination, ExeName)))
        {
            candidates.Add(destination);
        }
        candidates.AddRange(Directory.EnumerateDirectories(destination).Where(d => File.Exists(Path.Combine(d, ExeName))));
        return candidates.Count == 1
            ? candidates[0]
            : throw new UpdatePackageException(new UpdatePackageError.AppNotFound(candidates.Count));
    }

    // Identity and signature

    /// <summary>
    /// The signer rule (PLAN.md 18.4; the Mac's designated-requirement rule in
    /// <c>UpdatePackage.verifySignature</c>). When the running build is signed,
    /// the new one must carry an intact signature by the same certificate
    /// (SHA-256 thumbprint), or, when both chain to a trusted root, by a
    /// certificate with the same subject (Azure Trusted Signing renews its
    /// short-lived certificates, so the thumbprint changes). When the running
    /// build is unsigned (local and CI builds without the secret), only an
    /// intact signature is required if the new one is signed at all, and the
    /// skipped signer check is part of the returned line.
    /// </summary>
    /// <exception cref="UpdatePackageException"><see cref="UpdatePackageError.SignatureInvalid"/> or <see cref="UpdatePackageError.SignerMismatch"/>.</exception>
    public static string CheckSignature(AuthenticodeInfo? running, AuthenticodeInfo candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Status == SignatureStatus.Invalid)
        {
            throw new UpdatePackageException(new UpdatePackageError.SignatureInvalid(candidate.Detail ?? "invalid"));
        }
        if (running is null || !running.IsSigned)
        {
            var found = candidate.IsSigned ? $"new version signed by {candidate.Subject ?? "unknown"}" : "new version unsigned";
            return $"running build is unsigned; signer check skipped, {found}";
        }
        if (!candidate.IsSigned)
        {
            throw new UpdatePackageException(new UpdatePackageError.SignatureInvalid(candidate.Detail ?? "not signed"));
        }
        if (candidate.Thumbprint is not null
            && string.Equals(candidate.Thumbprint, running.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return $"signed by the running build's certificate {running.Subject} ({running.Thumbprint})";
        }
        if (candidate.Status == SignatureStatus.Trusted && running.Status == SignatureStatus.Trusted
            && candidate.Subject is not null && candidate.Subject == running.Subject)
        {
            return $"signed by {candidate.Subject} through a trusted certificate, as the running build";
        }
        throw new UpdatePackageException(new UpdatePackageError.SignerMismatch(candidate.Subject, running.Subject));
    }

    /// <summary>
    /// Checks the app folder's Hearsay.exe: the signer rule, ProductName
    /// <see cref="ProductName"/>, and, when <paramref name="expectedVersion"/>
    /// is given, a ProductVersion equal to it (build metadata ignored).
    /// Returns a line describing what was checked.
    /// </summary>
    /// <exception cref="UpdatePackageException">A check failed.</exception>
    public static string VerifyApp(string appFolder, string? expectedVersion, AppIdentity? running, IAppIdentityReader reader)
    {
        ArgumentException.ThrowIfNullOrEmpty(appFolder);
        ArgumentNullException.ThrowIfNull(reader);
        var exe = Path.Combine(appFolder, ExeName);
        if (!File.Exists(exe))
        {
            throw new UpdatePackageException(new UpdatePackageError.AppNotFound(0));
        }
        var identity = reader.Read(exe);
        var summary = CheckSignature(running?.Signature, identity.Signature);
        if (identity.ProductName != ProductName)
        {
            throw new UpdatePackageException(new UpdatePackageError.WrongIdentifier(identity.ProductName));
        }
        var version = identity.ProductVersion is null ? null : UpdateChecker.WithoutBuildMetadata(identity.ProductVersion);
        if (expectedVersion is not null && version != expectedVersion)
        {
            throw new UpdatePackageException(new UpdatePackageError.WrongVersion(version, expectedVersion));
        }
        return $"{summary}; {identity.ProductName} {version ?? "unknown"}";
    }

    // Staging

    /// <summary><c>&lt;parent of install&gt;\.Hearsay-update-&lt;version&gt;</c>.</summary>
    public static string StagedFolder(string installFolder, string version)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder)))
            ?? throw new ArgumentException("The install folder has no parent.", nameof(installFolder));
        return Path.Combine(parent, StagedFolderName(version));
    }

    /// <summary>
    /// Copies <paramref name="appFolder"/> next to <paramref name="installFolder"/>
    /// under the hidden staging name (replacing a stale copy), clears the mark
    /// of the web, and checks the copy again. Returns the staged folder.
    /// </summary>
    /// <exception cref="UpdatePackageException">The copy failed its check.</exception>
    public static string Stage(
        string appFolder, string installFolder, string version, string? expectedVersion, AppIdentity? running,
        IAppIdentityReader reader)
    {
        var staged = StagedFolder(installFolder, version);
        TryDeleteFolder(staged);
        try
        {
            CopyFolder(appFolder, staged);
            var info = new DirectoryInfo(staged);
            info.Attributes |= FileAttributes.Hidden;
            RemoveMarkOfTheWeb(staged);
            _ = VerifyApp(staged, expectedVersion, running, reader);
        }
        catch
        {
            TryDeleteFolder(staged);
            throw;
        }
        return staged;
    }

    /// <summary>
    /// Deletes the <c>Zone.Identifier</c> stream (the mark of the web, the
    /// Mac's quarantine attribute) from every file below <paramref name="folder"/>.
    /// Files without it are left alone. HttpClient downloads carry none; a
    /// zip downloaded with a browser for the debug entry may.
    /// </summary>
    public static void RemoveMarkOfTheWeb(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            // File.Delete ignores a stream that does not exist.
            File.Delete(file + ":Zone.Identifier");
        }
    }

    /// <summary>"0.3.0" from "Hearsay-0.3.0-win-x64.zip"; null for any other name.</summary>
    public static string? VersionFromZipName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string prefix = "Hearsay-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(ReleaseInfo.ZipSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        var version = name[prefix.Length..^ReleaseInfo.ZipSuffix.Length];
        return version.Length == 0 ? null : version;
    }

    // The whole preparation

    /// <summary>
    /// Downloads, verifies and stages <paramref name="release"/> for the
    /// install folder (steps 2 to 6 above; the Mac's <c>UpdateInstaller.run</c>
    /// without the dialogs). A verification failure deletes the cache folder
    /// (the download is not trusted); a cancelled or failed download keeps a
    /// complete zip, which is checked again next time.
    /// </summary>
    /// <param name="client">For the two downloads (follows GitHub's redirect to its CDN).</param>
    /// <param name="running">The running exe's identity (<c>reader.Read(Environment.ProcessPath)</c>), or null to skip the signer check as for an unsigned build.</param>
    /// <exception cref="UpdatePackageException">A step failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static async Task<PreparedUpdate> PrepareAsync(
        HttpClient client, ReleaseInfo release, string installFolder, string updatesRoot, AppIdentity? running,
        IAppIdentityReader reader, IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(reader);
        var zipAsset = release.ZipAsset(release.Version);
        var sumsAsset = release.ChecksumsAsset;
        if (zipAsset is null || sumsAsset is null)
        {
            throw new UpdatePackageException(new UpdatePackageError.NoPackage());
        }
        var version = release.Version;
        var folder = PrepareCacheFolder(updatesRoot, version);
        var zip = Path.Combine(folder, zipAsset.Name);
        var sums = Path.Combine(folder, ReleaseInfo.ChecksumsFileName);
        var extracted = Path.Combine(folder, "extract");

        await DownloadAsync(client, sumsAsset.DownloadUri, sums, null, cancellationToken).ConfigureAwait(false);
        var cached = await Task.Run(() => TryVerifyChecksum(zip, sums), cancellationToken).ConfigureAwait(false);
        if (!cached)
        {
            await DownloadAsync(client, zipAsset.DownloadUri, zip, progress, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(
                () =>
                {
                    var digest = VerifyChecksum(zip, sums);
                    cancellationToken.ThrowIfCancellationRequested();
                    var app = Extract(zip, extracted);
                    var summary = VerifyApp(app, version, running, reader);
                    cancellationToken.ThrowIfCancellationRequested();
                    var staged = Stage(app, installFolder, version, version, running, reader);
                    TryDeleteFolder(extracted);
                    return new PreparedUpdate(version, staged, folder, $"sha256 {digest}; {summary}");
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (UpdatePackageException error) when (error.Error.IsVerificationFailure)
        {
            TryDeleteFolder(folder);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFolder(extracted);
            TryDeleteFolder(StagedFolder(installFolder, version));
            throw;
        }
    }

    /// <summary>"Later" (or a blocked install): the staged copy goes, the verified zip stays.</summary>
    public static void Discard(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        TryDeleteFolder(prepared.StagedFolder);
    }

    private static bool TryVerifyChecksum(string zip, string sums)
    {
        if (!File.Exists(zip))
        {
            return false;
        }
        try
        {
            _ = VerifyChecksum(zip, sums);
            return true;
        }
        catch (Exception error) when (error is UpdatePackageException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Helpers

    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    internal static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
