namespace Hearsay.Core.Updates;

/// <summary>
/// Debug only: the core of the app's <c>HEARSAY_INSTALL_UPDATE</c> entry
/// point, the port of mac/Hearsay/Features/Debug/UpdateInstallDebug.swift
/// (PLAN.md 4.6, "Debug"; AGENTS.md's debug table). For the App: when the app
/// is started with
/// <code>
/// HEARSAY_INSTALL_UPDATE=&lt;zip&gt;            the release zip, e.g. C:\scratch\Hearsay-0.3.0-win-x64.zip
/// HEARSAY_INSTALL_TARGET=&lt;folder&gt;         a scratch install folder holding a Hearsay.exe
/// HEARSAY_INSTALL_VERSION=&lt;v&gt;  (optional) the expected version; else taken from the zip name
/// </code>
/// call <c>await UpdateInstallDebug.RunAsync(new UpdateInstallDebug.Options { Zip = …, Target = …,
/// VersionOverride = …, RunningExe = Environment.ProcessPath }, Console.Out)</c> before any window
/// opens and exit with the returned status (0, or 1 at the first failure).
/// <para>
/// It runs the install against the target instead of the running app: the
/// location check, the checksum (against a <c>SHA256SUMS.txt</c> next to the
/// zip, skipped when there is none), extraction, the identity and signer
/// check (against the running exe), staging next to the target, and the swap
/// through the real helper script with no process to wait for and no
/// relaunch. Every step is printed as <c>hearsay install update: …</c>.
/// Point it only at a scratch folder, never at the running app or an
/// installed copy; the running app's own folder is refused.
/// </para>
/// </summary>
public static class UpdateInstallDebug
{
    public const string Variable = "HEARSAY_INSTALL_UPDATE";
    public const string TargetVariable = "HEARSAY_INSTALL_TARGET";
    public const string VersionVariable = "HEARSAY_INSTALL_VERSION";

    public sealed record Options
    {
        public required string Zip { get; init; }

        /// <summary>HEARSAY_INSTALL_TARGET; empty or null fails with "not set".</summary>
        public string? Target { get; init; }

        public string? VersionOverride { get; init; }

        /// <summary>The running exe (<see cref="Environment.ProcessPath"/>): its folder is refused as a target, and its signature is what the new one is compared with.</summary>
        public string? RunningExe { get; init; }

        public IAppIdentityReader Reader { get; init; } = FileAppIdentityReader.Shared;

        /// <summary>A scratch work folder for the extraction and the helper log; a new one under %TEMP% when null. Deleted afterwards.</summary>
        public string? WorkFolder { get; init; }

        /// <summary>How long the helper may take.</summary>
        public TimeSpan HelperTimeout { get; init; } = TimeSpan.FromSeconds(120);
    }

    /// <summary>Reads the three variables; null when <see cref="Variable"/> is not set.</summary>
    public static Options? FromEnvironment(string? runningExe)
    {
        var zip = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(zip))
        {
            return null;
        }
        var version = Environment.GetEnvironmentVariable(VersionVariable);
        return new Options
        {
            Zip = zip,
            Target = Environment.GetEnvironmentVariable(TargetVariable),
            VersionOverride = string.IsNullOrEmpty(version) ? null : version,
            RunningExe = runningExe,
        };
    }

    /// <summary>Runs the steps and returns the exit status: 0, or 1 at the first failure.</summary>
    public static async Task<int> RunAsync(Options options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        void Say(string line)
        {
            output.WriteLine("hearsay install update: " + line);
            output.Flush();
        }

        if (string.IsNullOrEmpty(options.Target))
        {
            Say($"{TargetVariable} is not set");
            return 1;
        }
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Target));
        var runningFolder = options.RunningExe is null ? null : Path.GetDirectoryName(Path.GetFullPath(options.RunningExe));
        if (runningFolder is not null && string.Equals(target, Path.TrimEndingDirectorySeparator(runningFolder), StringComparison.OrdinalIgnoreCase))
        {
            Say($"refusing to replace the running app {target}");
            return 1;
        }
        var zip = Path.GetFullPath(options.Zip);
        if (!File.Exists(zip))
        {
            Say($"no zip file at {zip}");
            return 1;
        }
        var expectedVersion = options.VersionOverride ?? UpdateInstall.VersionFromZipName(Path.GetFileName(zip));
        var stagingVersion = expectedVersion ?? "debug";
        var reader = options.Reader;
        Say($"zip {zip}");
        Say($"target {target} (version {TargetVersion(target, reader)})");
        Say($"expected version {expectedVersion ?? "unknown; version check skipped"}");

        try
        {
            UpdateInstall.CheckInstallLocation(target);
            Say("install location ok");
        }
        catch (InstallLocationException error)
        {
            Say($"install location: {error.Message}");
            return 1;
        }

        var checksums = Path.Combine(Path.GetDirectoryName(zip) ?? ".", ReleaseInfo.ChecksumsFileName);
        if (File.Exists(checksums))
        {
            try
            {
                var digest = await Task.Run(() => UpdateInstall.VerifyChecksum(zip, checksums)).ConfigureAwait(false);
                Say($"checksum ok {digest}");
            }
            catch (UpdatePackageException error)
            {
                Say($"checksum: {error.Message}");
                return 1;
            }
        }
        else
        {
            Say($"checksum skipped (no {ReleaseInfo.ChecksumsFileName} next to the zip)");
        }

        var work = options.WorkFolder ?? Path.Combine(Path.GetTempPath(), $"hearsay-update-debug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            AppIdentity? running = null;
            if (options.RunningExe is { } runningExe && File.Exists(runningExe))
            {
                running = reader.Read(runningExe);
            }
            string staged;
            try
            {
                var app = await Task.Run(() => UpdateInstall.Extract(zip, Path.Combine(work, "extract"))).ConfigureAwait(false);
                Say($"extracted, app folder {Path.GetRelativePath(work, app)}");
                var summary = await Task.Run(() => UpdateInstall.VerifyApp(app, expectedVersion, running, reader)).ConfigureAwait(false);
                Say($"identity and signature ok: {summary}");
                staged = await Task.Run(() => UpdateInstall.Stage(app, target, stagingVersion, expectedVersion, running, reader)).ConfigureAwait(false);
                Say($"staged {staged} (mark of the web cleared, checked again)");
            }
            catch (UpdatePackageException error)
            {
                Say($"extract, verify or stage: {error.Message}");
                return 1;
            }

            var log = Path.Combine(work, "swap.log");
            var plan = new SwapPlan
            {
                InstallFolder = target,
                StagedFolder = staged,
                LogFile = log,
                Relaunch = false,
            };
            int exitCode;
            using (var helper = UpdateSwapHelper.Start(plan))
            {
                using var timeout = new CancellationTokenSource(options.HelperTimeout);
                try
                {
                    await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    helper.Kill();
                    Say("the swap helper did not finish in time");
                    return 1;
                }
                exitCode = helper.ExitCode;
            }
            if (File.Exists(log))
            {
                foreach (var line in File.ReadAllLines(log))
                {
                    Say("helper: " + line);
                }
            }
            if (exitCode != UpdateSwapHelper.ExitSwapped)
            {
                Say($"replace: the helper exited with {exitCode}");
                return 1;
            }
            Say($"replaced {target}; now version {TargetVersion(target, reader)}; staged copy gone: {!Directory.Exists(staged)}");
            Say("done (no relaunch in a debug run)");
            return 0;
        }
        finally
        {
            UpdateInstall.TryDeleteFolder(work);
        }
    }

    private static string TargetVersion(string target, IAppIdentityReader reader)
    {
        var exe = Path.Combine(target, UpdateInstall.ExeName);
        if (!File.Exists(exe))
        {
            return "unknown";
        }
        try
        {
            return reader.Read(exe).ProductVersion is { } version ? UpdateChecker.WithoutBuildMetadata(version) : "unknown";
        }
        catch (IOException)
        {
            return "unknown";
        }
    }
}
