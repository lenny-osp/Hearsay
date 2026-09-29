using Hearsay.App.Features.Updates;
using Hearsay.Core;
using Hearsay.Core.Settings;
using Hearsay.Core.Updates;

namespace Hearsay.App.Tests;

/// <summary>
/// The state machine of <see cref="UpdateService"/> (PLAN.md 4.6 and 18.4,
/// the port of mac/Hearsay/Features/Updates/UpdateService.swift and
/// UpdateInstaller.swift) with a fake checker, fake install steps and fake
/// dialogs: nothing is sent to GitHub, downloaded or swapped. The Mac app
/// has no unit tests for these classes; the cases follow the flow 4.6
/// describes step by step. Settings live in a scratch folder.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly ScratchFolder settingsFolder = new();
    private readonly InterfaceLanguageScope english = new(InterfaceLanguage.English, "en-US");
    private readonly SynchronizationContext? previousContext = SynchronizationContext.Current;
    private readonly AppSettings settings;
    private readonly FakeChecker checker = new();
    private readonly FakeInstaller installer = new();
    private readonly FakePrompts prompts = new();
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly UpdateService service;
    private int quits;

    public UpdateServiceTests()
    {
        // No UI thread: Progress<T> reports on the thread pool.
        SynchronizationContext.SetSynchronizationContext(null);
        settings = new AppSettings(new SettingsFile(settingsFolder.Path));
        service = new UpdateService(settings, new AppVersion("0.2.0", "a0942f3"), checker, installer, prompts, clock)
        {
            Quit = () => quits++,
        };
    }

    public void Dispose()
    {
        service.Dispose();
        SynchronizationContext.SetSynchronizationContext(previousContext);
        english.Dispose();
        settingsFolder.Dispose();
    }

    private static ReleaseInfo Release(string version = "0.3.0", bool withZip = true)
    {
        var assets = new List<ReleaseAsset>
        {
            new(ReleaseInfo.ChecksumsFileName, new Uri("https://example.com/SHA256SUMS.txt"), 200),
            new($"Hearsay-{version}.dmg", new Uri("https://example.com/x.dmg"), 90_000_000),
        };
        if (withZip) assets.Add(new(ReleaseInfo.ZipAssetName(version), new Uri("https://example.com/x.zip"), 48_600_000));
        return new ReleaseInfo
        {
            Version = version,
            TagName = "v" + version,
            HtmlUri = new Uri($"https://github.com/lenny-osp/Hearsay/releases/tag/v{version}"),
            Assets = assets,
        };
    }

    private static PreparedUpdate Prepared(string version = "0.3.0") =>
        new(version, @"C:\scratch\.Hearsay-update-" + version, @"C:\scratch\Updates\" + version, "sha256 x; checked");

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>A check that finds 0.3.0 and the user chooses Install Update; returns the running check.</summary>
    private async Task<Task> StartInstallFromCheckAsync(ReleaseInfo release)
    {
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(release));
        prompts.OfferChoice = UpdateOfferChoice.Install;
        var check = service.CheckNowAsync(userInitiated: true);
        await Eventually(() => installer.PrepareCalls == 1, "the download to start");
        return check;
    }

    // The main path: available -> downloading -> ready -> installing.

    [Fact]
    public async Task AvailableThenDownloadingThenReadyThenInstallAndRelaunch()
    {
        var release = Release();
        Assert.IsType<UpdateState.Idle>(service.State);
        var check = await StartInstallFromCheckAsync(release);

        Assert.Equal([(release.Version, "0.2.0", (string?)null)], prompts.Offers);
        Assert.Equal(1, prompts.ProgressShown);
        var downloading = Assert.IsType<UpdateState.Downloading>(service.State);
        Assert.Equal(new UpdateDownloadProgress(0, 48_600_000), downloading.Progress);
        Assert.True(service.IsWorking);

        installer.Report(new UpdateDownloadProgress(20_400_000, 48_600_000));
        await Eventually(() => service.State is UpdateState.Downloading { Progress.Received: 20_400_000 }, "the progress");
        Assert.Equal("Downloading Hearsay 0.3.0… 20.4 MB of 48.6 MB", service.ResultLine);

        // The last report of a complete download: the checks follow.
        installer.Report(new UpdateDownloadProgress(48_600_000, 48_600_000));
        await Eventually(() => service.State is UpdateState.Verifying, "verifying");
        Assert.Equal("Verifying Hearsay 0.3.0…", service.ResultLine);

        installer.Finish(Prepared());
        await check.WaitAsync(Wait);
        var ready = Assert.IsType<UpdateState.Ready>(service.State);
        Assert.Equal(Prepared(), ready.Prepared);
        Assert.Equal("Hearsay 0.3.0 is ready to install.", service.ResultLine);
        Assert.Equal(2, prompts.ProgressShown);
        Assert.Empty(prompts.Failures);

        await service.InstallAndRelaunchAsync();
        Assert.IsType<UpdateState.Installing>(service.State);
        Assert.Equal([Prepared()], installer.Swaps);
        Assert.Empty(installer.Discards);
        Assert.Equal(1, quits);
        Assert.Equal("Installing Hearsay 0.3.0…", service.ResultLine);
    }

    [Fact]
    public async Task ACheckStoresNothingItselfButShowsTheOutcome()
    {
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.UpToDate());
        await service.CheckNowAsync(userInitiated: true);
        Assert.IsType<UpdateState.UpToDate>(service.State);
        Assert.Equal("You're up to date (0.2.0).", service.ResultLine);
        Assert.IsType<UpdateState.UpToDate>(Assert.Single(prompts.CheckResults));
        Assert.Equal(["0.2.0"], checker.Versions);
        Assert.Empty(prompts.Offers);
    }

    [Fact]
    public async Task InstallAndRelaunchIsRefusedWhileRecording()
    {
        service.InstallBlocker = () => Strings.UpdateFinishRecordingFirst;
        var release = Release();
        var check = await StartInstallFromCheckAsync(release);
        installer.Finish(Prepared());
        await check.WaitAsync(Wait);

        await service.InstallAndRelaunchAsync();
        Assert.Equal(["Finish the recording first."], prompts.Blocked);
        Assert.Empty(installer.Swaps);
        Assert.Equal([Prepared()], installer.Discards);
        Assert.Equal(0, quits);
        // Back to the check's result, as the Mac's installer goes idle.
        Assert.Equal(release, Assert.IsType<UpdateState.Available>(service.State).Release);
        Assert.Equal(1, prompts.ProgressClosed);
    }

    [Fact]
    public async Task LaterDiscardsTheStagedCopyAndKeepsTheOffer()
    {
        var release = Release();
        var check = await StartInstallFromCheckAsync(release);
        installer.Finish(Prepared());
        await check.WaitAsync(Wait);

        service.Later();
        Assert.Equal([Prepared()], installer.Discards);
        Assert.Equal(release, service.OfferedRelease);
        Assert.Equal("Hearsay 0.3.0 is available.", service.ResultLine);
        Assert.Equal(1, prompts.ProgressClosed);
        Assert.Equal(0, quits);
    }

    [Fact]
    public async Task CancelStopsTheDownloadWithoutAFailure()
    {
        var release = Release();
        var check = await StartInstallFromCheckAsync(release);
        service.Cancel();
        await check.WaitAsync(Wait);

        Assert.True(installer.WasCancelled);
        Assert.Equal(release, Assert.IsType<UpdateState.Available>(service.State).Release);
        Assert.Empty(prompts.Failures);
        Assert.Equal(1, prompts.ProgressClosed);
        // A report after the cancel changes nothing.
        installer.Report(new UpdateDownloadProgress(1, 2));
        await Task.Delay(50);
        Assert.IsType<UpdateState.Available>(service.State);
    }

    public static TheoryData<string, string, string> PrepareFailures => new()
    {
        { "checksum", nameof(UpdateFailureKind.Verify), "Hearsay could not verify the downloaded update." },
        { "signer", nameof(UpdateFailureKind.Verify), "Hearsay could not verify the downloaded update." },
        { "download", nameof(UpdateFailureKind.Download), "Hearsay could not download the update." },
        { "io", nameof(UpdateFailureKind.Install), "Hearsay could not install the update." },
    };

    [Theory]
    [MemberData(nameof(PrepareFailures))]
    public async Task APrepareFailureShowsTheCoreTextWithItsTitle(string which, string kindName, string title)
    {
        var kind = Enum.Parse<UpdateFailureKind>(kindName);
        Exception error = which switch
        {
            "checksum" => new UpdatePackageException(new UpdatePackageError.ChecksumMismatch("Hearsay-0.3.0-win-x64.zip")),
            "signer" => new UpdatePackageException(new UpdatePackageError.SignerMismatch("CN=Someone", "CN=Hearsay")),
            "download" => new UpdatePackageException(UpdatePackageError.DownloadFailed.HttpStatus(404)),
            _ => new IOException("The disk is full."),
        };
        installer.DownloadedZip = which == "io" ? @"C:\scratch\Updates\0.3.0\Hearsay-0.3.0-win-x64.zip" : null;
        var release = Release();
        var check = await StartInstallFromCheckAsync(release);
        installer.Fail(error);
        await check.WaitAsync(Wait);

        var failure = Assert.IsType<UpdateState.Failed>(service.State).Failure;
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(title, failure.Title);
        Assert.Equal(error.Message, failure.Message);
        Assert.Equal(release, failure.Release);
        Assert.Equal(installer.DownloadedZip, failure.DownloadedFile);
        Assert.Equal([failure], prompts.Failures);
        Assert.Equal(error.Message, service.ResultLine);
        Assert.Equal(1, prompts.ProgressClosed);
        Assert.False(service.IsWorking);
    }

    [Fact]
    public async Task ALocationProblemOffersTheReleasePageInstead()
    {
        installer.LocationProblem = new InstallLocationProblem.RunningFromArchive();
        var release = Release();
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(release));
        prompts.OfferChoice = UpdateOfferChoice.Install;
        await service.CheckNowAsync(userInitiated: false);

        var problem = new InstallLocationException(new InstallLocationProblem.RunningFromArchive()).Message;
        Assert.Equal([(release.Version, "0.2.0", (string?)problem)], prompts.Offers);
        // Download opens the release page; nothing is downloaded.
        Assert.Equal([release.HtmlUri], prompts.OpenedPages);
        Assert.Equal(0, installer.PrepareCalls);
        Assert.Equal(0, prompts.ProgressShown);
    }

    [Fact]
    public async Task InstallFromSettingsRefusesAnUnwritableFolder()
    {
        installer.LocationProblem = new InstallLocationProblem.FolderNotWritable(@"C:\Program Files\Hearsay");
        await service.InstallAsync(Release());
        var failure = Assert.IsType<UpdateState.Failed>(service.State).Failure;
        Assert.Equal(UpdateFailureKind.Location, failure.Kind);
        Assert.Equal("Hearsay cannot install the update here.", failure.Title);
        Assert.Equal(
            @"Hearsay cannot write to the folder C:\Program Files\Hearsay. Move the Hearsay folder to a folder you can write to, open it from there, then check for updates again.",
            failure.Message);
        Assert.Equal(0, installer.PrepareCalls);
    }

    [Fact]
    public async Task AReleaseWithoutTheZipIsOfferedAsADownload()
    {
        var release = Release(withZip: false);
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(release));
        prompts.OfferChoice = UpdateOfferChoice.Install;
        await service.CheckNowAsync(userInitiated: true);
        Assert.Equal("The release has no Windows zip file or checksum list.", Assert.Single(prompts.Offers).Problem);
        Assert.Equal([release.HtmlUri], prompts.OpenedPages);

        // Install from Settings says the same with the download title.
        await service.InstallAsync(release);
        var failure = Assert.IsType<UpdateState.Failed>(service.State).Failure;
        Assert.Equal(UpdateFailureKind.Download, failure.Kind);
        Assert.Equal("The release has no Windows zip file or checksum list.", failure.Message);
    }

    [Fact]
    public async Task ViewOnGitHubAndLaterDoNotDownload()
    {
        var release = Release();
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(release));
        prompts.OfferChoice = UpdateOfferChoice.ViewOnGitHub;
        await service.CheckNowAsync(userInitiated: true);
        Assert.Equal([release.HtmlUri], prompts.OpenedPages);

        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(release));
        prompts.OfferChoice = UpdateOfferChoice.Later;
        await service.CheckNowAsync(userInitiated: true);
        Assert.Single(prompts.OpenedPages);
        Assert.Equal(0, installer.PrepareCalls);
        Assert.Equal(release, service.OfferedRelease);
    }

    [Fact]
    public async Task ManualFailureShowsADialogAndAutomaticFailureStaysSilent()
    {
        var offline = new UpdateCheckError.Offline();
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.UpToDate());
        await service.CheckNowAsync(userInitiated: false);
        Assert.Empty(prompts.CheckResults);
        Assert.IsType<UpdateState.UpToDate>(service.State);

        // Automatic: silent, and the last result stays.
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Failed(offline.Description, offline.Localized));
        await service.CheckNowAsync(userInitiated: false);
        Assert.Empty(prompts.CheckResults);
        Assert.IsType<UpdateState.UpToDate>(service.State);

        // Manual: the dialog, and Settings shows the reason.
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Failed(offline.Description, offline.Localized));
        await service.CheckNowAsync(userInitiated: true);
        var failed = Assert.IsType<UpdateState.Failed>(Assert.Single(prompts.CheckResults));
        Assert.Equal(UpdateFailureKind.Check, failed.Failure.Kind);
        Assert.Equal("Could not check for updates.", failed.Failure.Title);
        Assert.Equal("Could not reach GitHub. Check your internet connection and try again.", service.ResultLine);
    }

    [Fact]
    public async Task AnAutomaticCheckThatFindsAVersionOffersIt()
    {
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(Release()));
        await service.CheckNowAsync(userInitiated: false);
        Assert.Single(prompts.Offers);
    }

    [Fact]
    public async Task ANewCheckClearsAnInstallFailure()
    {
        installer.LocationProblem = new InstallLocationProblem.NotAnInstallFolder();
        await service.InstallAsync(Release());
        Assert.IsType<UpdateState.Failed>(service.State);
        installer.LocationProblem = null;
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.UpToDate());
        await service.CheckNowAsync(userInitiated: false);
        Assert.IsType<UpdateState.UpToDate>(service.State);
    }

    [Fact]
    public async Task AManualCheckDuringAnInstallBringsTheWindowForward()
    {
        var release = Release();
        var check = await StartInstallFromCheckAsync(release);
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.Available(release));
        await service.CheckNowAsync(userInitiated: true);
        Assert.Single(prompts.Offers);
        Assert.Equal(2, prompts.ProgressShown);
        Assert.IsType<UpdateState.Downloading>(service.State);
        service.Cancel();
        await check.WaitAsync(Wait);
    }

    [Fact]
    public async Task ASwapThatDoesNotStartKeepsTheOldVersionRunning()
    {
        installer.SwapError = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");
        var check = await StartInstallFromCheckAsync(Release());
        installer.Finish(Prepared());
        await check.WaitAsync(Wait);
        await service.InstallAndRelaunchAsync();
        var failure = Assert.IsType<UpdateState.Failed>(service.State).Failure;
        Assert.Equal(UpdateFailureKind.Install, failure.Kind);
        Assert.Equal([Prepared()], installer.Discards);
        Assert.Equal(0, quits);
    }

    // The interval rule (UpdateChecker.IsAutomaticCheckDue, UpdateService.startAutomaticChecks).

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, 23.9, false)]
    [InlineData(true, 24.0, true)]
    [InlineData(true, 30.0, true)]
    [InlineData(true, -1.0, true)]
    [InlineData(false, null, false)]
    [InlineData(false, 48.0, false)]
    public async Task AnAutomaticCheckRunsOnlyWhenDue(bool enabled, double? hoursAgo, bool runs)
    {
        settings.AutomaticUpdateChecks = enabled;
        settings.LastUpdateCheck = hoursAgo is { } hours ? clock.GetUtcNow() - TimeSpan.FromHours(hours) : null;
        checker.Outcomes.Enqueue(new UpdateCheckOutcome.UpToDate());
        Assert.Equal(runs, service.IsAutomaticCheckDue);
        await service.RunAutomaticCheckIfDueAsync();
        Assert.Equal(runs ? 1 : 0, checker.Versions.Count);
    }

    [Fact]
    public void TheScheduleIsTheMacs()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), UpdateChecker.FirstAutomaticCheckDelay);
        Assert.Equal(TimeSpan.FromHours(1), UpdateChecker.AutomaticCheckPollInterval);
        Assert.Equal(TimeSpan.FromHours(24), UpdateChecker.AutomaticInterval);
        Assert.Equal(UpdateChecker.HearsayRepository, UpdateConfiguration.Repository);
    }

    // The version display.

    [Theory]
    [InlineData("0.3.0+a0942f314973add7c054e59bb85b01962e7f365a", "0.3.0", "a0942f3")]
    [InlineData("0.0.0+a0942f3", "0.0.0", "a0942f3")]
    [InlineData("1.2.0-beta.1+abc", "1.2.0-beta.1", "abc")]
    [InlineData("0.3.0", "0.3.0", "0")]
    [InlineData("0.3.0+", "0.3.0", "0")]
    [InlineData("", "0.0.0", "0")]
    [InlineData(null, "0.0.0", "0")]
    public void TheVersionComesFromTheInformationalVersion(string? informational, string version, string build) =>
        Assert.Equal(new AppVersion(version, build), AppVersion.Parse(informational));

    [Fact]
    public void TheRunningExeHasAVersion()
    {
        var current = AppVersion.Current;
        Assert.False(string.IsNullOrEmpty(current.Version));
        Assert.DoesNotContain('+', current.Version);
        Assert.Equal(current.Version, UpdateChecker.WithoutBuildMetadata(
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(AppVersion).Assembly)?.InformationalVersion ?? ""));
    }

    [Fact]
    public void SettingsShowsTheVersionAndTheLastCheck()
    {
        Assert.Equal("Version 0.2.0 (a0942f3)", service.VersionLine);
        Assert.Equal("Not checked yet", service.LastCheckLine);
        Assert.Null(service.ResultLine);
        settings.LastUpdateCheck = new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 28)));
        Assert.Equal("Last checked Sep 28, 2026 2:30 PM", service.LastCheckLine);
    }

    [Fact]
    public void TheVersionLineIsTranslated()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German);
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "app", "Version %@ (%@)", "0.2.0", "a0942f3"), service.VersionLine);
        Assert.Equal(Translations.Text(InterfaceLanguage.German, "app", "Not checked yet"), service.LastCheckLine);
    }

    [Fact]
    public void TheTextsAreCoreUpdateTexts()
    {
        Assert.Equal(UpdateTexts.VersionLine("1", "2"), Strings.UpdateVersionLine("1", "2"));
        Assert.Equal(UpdateTexts.UpToDate("1"), Strings.UpdateUpToDate("1"));
        Assert.Equal(UpdateTexts.Available("1"), Strings.UpdateAvailable("1"));
        Assert.Equal(UpdateTexts.YouHaveVersion("1"), Strings.UpdateYouHaveVersion("1"));
        Assert.Equal(UpdateTexts.DownloadPageOpens, Strings.UpdateDownloadPageOpens);
        Assert.Equal(UpdateTexts.CouldNotCheck, Strings.UpdateCouldNotCheck);
        Assert.Equal(UpdateTexts.InstallUpdate, Strings.UpdateInstallUpdate);
        Assert.Equal(UpdateTexts.ViewOnGitHub, Strings.UpdateViewOnGitHub);
        Assert.Equal(UpdateTexts.Download, Strings.Download);
        Assert.Equal(UpdateTexts.Later, Strings.UpdateLater);
        Assert.Equal(UpdateTexts.SoftwareUpdates, Strings.SectionSoftwareUpdates);
        Assert.Equal(UpdateTexts.AutomaticallyCheck, Strings.UpdateAutomaticallyCheck);
        Assert.Equal(UpdateTexts.AutomaticallyCheckCaption, Strings.UpdateAutomaticallyCheckCaption);
        Assert.Equal(UpdateTexts.CheckNow, Strings.UpdateCheckNow);
        Assert.Equal(UpdateTexts.LastChecked("x"), Strings.UpdateLastChecked("x"));
        Assert.Equal(UpdateTexts.NotCheckedYet, Strings.UpdateNotCheckedYet);
        Assert.Equal(UpdateTexts.CannotInstallHere, Strings.UpdateCannotInstallHere);
        Assert.Equal(UpdateTexts.CouldNotDownload, Strings.UpdateCouldNotDownload);
        Assert.Equal(UpdateTexts.CouldNotVerify, Strings.UpdateCouldNotVerify);
        Assert.Equal(UpdateTexts.CouldNotInstall, Strings.UpdateCouldNotInstall);
        Assert.Equal(UpdateTexts.SoftwareUpdateWindowTitle, Strings.UpdateWindowTitle);
        Assert.Equal(UpdateTexts.Downloading("1"), Strings.UpdateDownloading("1"));
        Assert.Equal(UpdateTexts.Verifying("1"), Strings.UpdateVerifying("1"));
        Assert.Equal(UpdateTexts.ReadyToInstall("1"), Strings.UpdateReadyToInstall("1"));
        Assert.Equal(UpdateTexts.Installing("1"), Strings.UpdateInstalling("1"));
        Assert.Equal(UpdateTexts.WillQuitAndReopen, Strings.UpdateWillQuitAndReopen);
        Assert.Equal(UpdateTexts.InstallAndRelaunch, Strings.UpdateInstallAndRelaunch);
        Assert.Equal(UpdateTexts.FinishRecordingFirst, Strings.UpdateFinishRecordingFirst);
        Assert.Equal(UpdateTexts.BytesOf("a", "b"), Strings.UpdateBytesOf("a", "b"));
        Assert.Equal(UpdateTexts.PreviousInstallFailed, Strings.UpdatePreviousInstallFailed);
    }

    [Fact]
    public void TheBytesLineFollowsTheMac()
    {
        Assert.Equal("20.4 MB of 48.6 MB", UpdateService.BytesLine(new UpdateDownloadProgress(20_400_000, 48_600_000)));
        Assert.Equal("12.3 MB", UpdateService.BytesLine(new UpdateDownloadProgress(12_300_000, null)));
    }

    // The helper's log after a relaunch (Windows only).

    [Theory]
    [InlineData("exit 0", false)]
    [InlineData("exit 6", true)]
    [InlineData("exit 3", true)]
    public async Task TheLastInstallIsReadFromTheHelperLog(string last, bool failed)
    {
        using var root = new ScratchFolder();
        var log = SwapPlan.LogFileIn(root.Path);
        var text = "2026-09-30T10:00:00.000+02:00 start: install C:\\x\\Hearsay\n"
            + (failed ? "2026-09-30T10:00:01.000+02:00 could not move the new version in; restoring the old one\n"
                : "2026-09-30T10:00:01.000+02:00 installed the new version\n")
            + "2026-09-30T10:00:01.500+02:00 relaunched C:\\x\\Hearsay\\Hearsay.exe\n"
            + $"2026-09-30T10:00:01.600+02:00 {last}\n";
        File.WriteAllText(log, text);
        Assert.Equal(failed, await UpdateInstaller.TakeLastInstallFailedAsync(root.Path, TimeSpan.Zero));
        Assert.False(File.Exists(log));
        Assert.Equal(text, File.ReadAllText(Path.Combine(root.Path, UpdateInstaller.PreviousLogName)));
        Assert.Null(await UpdateInstaller.TakeLastInstallFailedAsync(root.Path, TimeSpan.Zero));
    }

    [Fact]
    public async Task ALogWithoutAnExitLineIsJudgedByItsSteps()
    {
        using var root = new ScratchFolder();
        File.WriteAllText(SwapPlan.LogFileIn(root.Path), "t start: x\nt installed the new version\nt relaunched x\n");
        Assert.False(await UpdateInstaller.TakeLastInstallFailedAsync(root.Path, TimeSpan.FromMilliseconds(100)));
        File.WriteAllText(SwapPlan.LogFileIn(root.Path), "t start: x\nt the staged copy is missing; nothing was changed\n");
        Assert.True(await UpdateInstaller.TakeLastInstallFailedAsync(root.Path, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("a\nb exit 4\n", 4)]
    [InlineData("b exit 0", 0)]
    [InlineData("b exit 0\nc relaunched\n", null)]
    [InlineData("", null)]
    public void TheExitLineIsTheLastLine(string log, int? code) => Assert.Equal(code, UpdateInstaller.ExitCode(log));

    [Fact]
    public async Task AFailedPreviousInstallIsReportedOnce()
    {
        installer.LastInstallFailed = true;
        Assert.Equal("The last update could not be installed. Hearsay is still the previous version.",
            await service.TakePreviousInstallFailureAsync());
        installer.LastInstallFailed = false;
        Assert.Null(await service.TakePreviousInstallFailureAsync());
    }

    // Fakes

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeChecker : IUpdateCheckSource
    {
        public Queue<UpdateCheckOutcome> Outcomes { get; } = new();

        public List<string> Versions { get; } = [];

        public Task<UpdateCheckOutcome> CheckAsync(string currentVersion, AppSettings settings, CancellationToken cancellationToken)
        {
            Versions.Add(currentVersion);
            return Task.FromResult(Outcomes.Dequeue());
        }
    }

    private sealed class FakeInstaller : IUpdateInstallSteps
    {
        private TaskCompletionSource<PreparedUpdate> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<UpdateDownloadProgress>? progress;

        public InstallLocationProblem? LocationProblem { get; set; }

        public int PrepareCalls { get; private set; }

        public bool WasCancelled { get; private set; }

        public List<PreparedUpdate> Discards { get; } = [];

        public List<PreparedUpdate> Swaps { get; } = [];

        public Exception? SwapError { get; set; }

        public string? DownloadedZip { get; set; }

        public bool? LastInstallFailed { get; set; }

        public void Report(UpdateDownloadProgress value) => progress?.Report(value);

        public void Finish(PreparedUpdate prepared) => result.TrySetResult(prepared);

        public void Fail(Exception error) => result.TrySetException(error);

        public void CheckLocation()
        {
            if (LocationProblem is { } problem) throw new InstallLocationException(problem);
        }

        public async Task<PreparedUpdate> PrepareAsync(ReleaseInfo release, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            this.progress = progress;
            result = new TaskCompletionSource<PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() =>
            {
                WasCancelled = true;
                result.TrySetCanceled(cancellationToken);
            }))
            {
                return await result.Task.ConfigureAwait(false);
            }
        }

        public void Discard(PreparedUpdate prepared) => Discards.Add(prepared);

        public void StartSwap(PreparedUpdate prepared)
        {
            if (SwapError is { } error) throw error;
            Swaps.Add(prepared);
        }

        public string? DownloadedFile(ReleaseInfo release) => DownloadedZip;

        public Task<bool?> TakeLastInstallFailedAsync()
        {
            var value = LastInstallFailed;
            LastInstallFailed = null;
            return Task.FromResult(value);
        }
    }

    private sealed class FakePrompts : IUpdatePrompts
    {
        public UpdateOfferChoice OfferChoice { get; set; } = UpdateOfferChoice.Later;

        public List<(string Version, string Current, string? Problem)> Offers { get; } = [];

        public List<UpdateState> CheckResults { get; } = [];

        public List<UpdateFailure> Failures { get; } = [];

        public List<string> Blocked { get; } = [];

        public List<Uri> OpenedPages { get; } = [];

        public int ProgressShown { get; private set; }

        public int ProgressClosed { get; private set; }

        public Task<UpdateOfferChoice> OfferAsync(ReleaseInfo release, string currentVersion, string? problem)
        {
            Offers.Add((release.Version, currentVersion, problem));
            return Task.FromResult(OfferChoice);
        }

        public Task ShowCheckResultAsync(UpdateState result)
        {
            CheckResults.Add(result);
            return Task.CompletedTask;
        }

        public Task ShowFailureAsync(UpdateFailure failure)
        {
            Failures.Add(failure);
            return Task.CompletedTask;
        }

        public Task ShowBlockedAsync(string message)
        {
            Blocked.Add(message);
            return Task.CompletedTask;
        }

        public void ShowProgress() => ProgressShown++;

        public void CloseProgress() => ProgressClosed++;

        public void OpenReleasePage(Uri page) => OpenedPages.Add(page);
    }
}
