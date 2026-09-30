using System.ComponentModel;
using Hearsay.App.Features.Models;
using Hearsay.Core.Settings;
using Hearsay.Core.Updates;

namespace Hearsay.App.Features.Updates;

/// <summary>What Settings, the progress window and the tray show about updates.</summary>
internal abstract record UpdateState
{
    private UpdateState()
    {
    }

    /// <summary>No check this session.</summary>
    public sealed record Idle : UpdateState;

    public sealed record Checking : UpdateState;

    public sealed record UpToDate : UpdateState;

    public sealed record Available(ReleaseInfo Release) : UpdateState;

    public sealed record Downloading(ReleaseInfo Release, UpdateDownloadProgress Progress) : UpdateState;

    /// <summary>Checksum, extraction, identity and signer, staging.</summary>
    public sealed record Verifying(ReleaseInfo Release) : UpdateState;

    /// <summary>Staged next to the install folder: Install and Relaunch, or Later.</summary>
    public sealed record Ready(ReleaseInfo Release, PreparedUpdate Prepared) : UpdateState;

    /// <summary>The swap helper runs and Hearsay quits.</summary>
    public sealed record Installing(ReleaseInfo Release) : UpdateState;

    public sealed record Failed(UpdateFailure Failure) : UpdateState;
}

/// <summary>Which step failed: it picks the dialog's title (the Mac's alert titles).</summary>
internal enum UpdateFailureKind
{
    /// <summary>The update check itself ("Could not check for updates.").</summary>
    Check,

    /// <summary>This copy cannot replace itself ("Hearsay cannot install the update here.").</summary>
    Location,

    /// <summary>The release lacks the zip, or a download failed.</summary>
    Download,

    /// <summary>Checksum, zip contents, identity or signer: the download was deleted.</summary>
    Verify,

    /// <summary>Staging or starting the swap.</summary>
    Install,
}

/// <param name="Kind">The step.</param>
/// <param name="Message">The reason, in the interface language.</param>
/// <param name="Release">The release being installed (View on GitHub), or null for a check.</param>
/// <param name="DownloadedFile">The downloaded zip when it is still on disk (Show in Explorer), or null.</param>
internal sealed record UpdateFailure(UpdateFailureKind Kind, string Message, ReleaseInfo? Release = null, string? DownloadedFile = null)
{
    public string Title => Kind switch
    {
        UpdateFailureKind.Check => Strings.UpdateCouldNotCheck,
        UpdateFailureKind.Location => Strings.UpdateCannotInstallHere,
        UpdateFailureKind.Download => Strings.UpdateCouldNotDownload,
        UpdateFailureKind.Verify => Strings.UpdateCouldNotVerify,
        _ => Strings.UpdateCouldNotInstall,
    };
}

/// <summary>The buttons of the "Hearsay x is available." dialog.</summary>
internal enum UpdateOfferChoice
{
    /// <summary>Install Update, or Download (the release page) when this copy cannot install it.</summary>
    Install,
    ViewOnGitHub,
    Later,
}

/// <summary>Asks GitHub once (<see cref="UpdateChecker.CheckAsync"/>); a fake in tests.</summary>
internal interface IUpdateCheckSource
{
    Task<UpdateCheckOutcome> CheckAsync(string currentVersion, AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>The file steps of an install (<see cref="UpdateInstall"/>, <see cref="UpdateSwapHelper"/>); a fake in tests.</summary>
internal interface IUpdateInstallSteps
{
    /// <exception cref="InstallLocationException">This copy cannot replace itself.</exception>
    void CheckLocation();

    /// <summary>Download, verify and stage (<see cref="UpdateInstall.PrepareAsync"/>).</summary>
    Task<PreparedUpdate> PrepareAsync(ReleaseInfo release, IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken);

    /// <summary>Later: the staged copy goes, the verified zip stays.</summary>
    void Discard(PreparedUpdate prepared);

    /// <summary>Starts the helper that swaps the folders once Hearsay has quit, and relaunches.</summary>
    void StartSwap(PreparedUpdate prepared);

    /// <summary>The release's zip in the update cache, when it is there.</summary>
    string? DownloadedFile(ReleaseInfo release);

    /// <summary>
    /// The last install's result from the helper's log, once: true when it
    /// failed, false when it worked, null without a log. The log is then set
    /// aside so it is reported only once.
    /// </summary>
    Task<bool?> TakeLastInstallFailedAsync();
}

/// <summary>The dialogs and the progress window; the app's are WinUI, the tests' record the calls.</summary>
internal interface IUpdatePrompts
{
    /// <summary>"Hearsay x is available." with Install Update, View on GitHub and Later; with <paramref name="problem"/>, Download and Later.</summary>
    Task<UpdateOfferChoice> OfferAsync(ReleaseInfo release, string currentVersion, string? problem);

    /// <summary>The answer to a manual check: up to date, or why it failed.</summary>
    Task ShowCheckResultAsync(UpdateState result);

    /// <summary>An install failed; View on GitHub, Show in Explorer (when the zip is on disk), Cancel.</summary>
    Task ShowFailureAsync(UpdateFailure failure);

    /// <summary>Install and Relaunch while a recording or transcription runs.</summary>
    Task ShowBlockedAsync(string message);

    /// <summary>Shows the progress window (it follows <see cref="UpdateService.State"/>) or brings it forward.</summary>
    void ShowProgress();

    void CloseProgress();

    void OpenReleasePage(Uri page);
}

/// <summary>
/// The app's update check and install, the port of mac/Hearsay/Features/Updates/UpdateService.swift
/// with the flow of UpdateInstaller.swift in the same class (PLAN.md 4.6 and
/// 18.4, "Updates and packaging"). Nothing is downloaded or installed without
/// the user's click, and nothing but the requests themselves is sent.
/// <list type="bullet">
/// <item>Automatic: with "Automatically check for updates" on, 10 s after launch and then hourly, a check runs if the last successful one is at least 24 h old. Errors are ignored; a dialog appears only when a newer version exists.</item>
/// <item>Manual (Settings > General > Check Now): a dialog every time, with the result or the error.</item>
/// <item>Install Update: location check, then download with the progress window (Cancel removes the partial file), verify, stage; "Hearsay x is ready to install." offers Install and Relaunch and Later.</item>
/// <item>Install and Relaunch: refused while a recording, its final pass or a File transcription runs ("Finish the recording first."); otherwise the swap helper starts and Hearsay quits (<see cref="Quit"/>); the helper swaps the folders and relaunches.</item>
/// </list>
/// <see cref="State"/> is the Mac's installer phase when one is set, else the
/// last check's result (a failed automatic check keeps the previous one).
/// Use from the UI thread; <see cref="PropertyChanged"/> is raised there.
/// </summary>
internal sealed class UpdateService : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings settings;
    private readonly IUpdateCheckSource checker;
    private readonly IUpdateInstallSteps installer;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private IUpdatePrompts prompts;
    private UpdateState? checkResult;
    private UpdateState? installPhase;
    private bool checking;
    private CancellationTokenSource? installCancellation;
    private CancellationTokenSource? scheduleCancellation;

    public UpdateService(
        AppSettings settings, AppVersion version, IUpdateCheckSource checker, IUpdateInstallSteps installer,
        IUpdatePrompts prompts, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(prompts);
        this.settings = settings;
        Version = version;
        this.checker = checker;
        this.installer = installer;
        this.prompts = prompts;
        this.clock = clock ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppVersion Version { get; }

    /// <summary>
    /// Why Install and Relaunch must wait ("Finish the recording first."), or
    /// null; the Mac's <c>installBlocker</c>, set by the shell.
    /// </summary>
    public Func<string?> InstallBlocker { get; set; } = () => null;

    /// <summary>Quits Hearsay after the helper has started (the shell's quit flow); the Mac's <c>relaunch</c>.</summary>
    public Action Quit { get; set; } = () => { };

    /// <summary>The dialogs; the shell sets the WinUI ones once the main window exists, the UI snapshots stub them.</summary>
    public IUpdatePrompts Prompts
    {
        get => prompts;
        set => prompts = value ?? throw new ArgumentNullException(nameof(value));
    }

    public UpdateState State
    {
        get
        {
            lock (gate)
            {
                return installPhase ?? (checking ? new UpdateState.Checking() : checkResult ?? new UpdateState.Idle());
            }
        }
    }

    public bool IsChecking
    {
        get
        {
            lock (gate)
            {
                return checking;
            }
        }
    }

    /// <summary>Downloading, verifying, ready or installing: the progress window is up (the Mac's <c>isWorking</c>).</summary>
    public bool IsWorking => State is UpdateState.Downloading or UpdateState.Verifying or UpdateState.Ready or UpdateState.Installing;

    /// <summary>The release Settings offers to install (Install Update), or null.</summary>
    public ReleaseInfo? OfferedRelease => State is UpdateState.Available available ? available.Release : null;

    /// <summary>"Version 0.3.0 (a0942f3)".</summary>
    public string VersionLine => Version.Line;

    /// <summary>"Last checked …" or "Not checked yet".</summary>
    public string LastCheckLine => settings.LastUpdateCheck is { } last
        ? Strings.UpdateLastChecked(FormatDate(last.ToLocalTime()))
        : Strings.UpdateNotCheckedYet;

    /// <summary>One line for Settings describing <see cref="State"/> (the Mac's <c>resultLine</c>), or null.</summary>
    public string? ResultLine => State switch
    {
        UpdateState.Downloading d => Strings.UpdateDownloading(d.Release.Version) + " " + BytesLine(d.Progress),
        UpdateState.Verifying v => Strings.UpdateVerifying(v.Release.Version),
        UpdateState.Ready r => Strings.UpdateReadyToInstall(r.Release.Version),
        UpdateState.Installing i => Strings.UpdateInstalling(i.Release.Version),
        UpdateState.Failed f => f.Failure.Message,
        UpdateState.Available a => Strings.UpdateAvailable(a.Release.Version),
        UpdateState.UpToDate => Strings.UpdateUpToDate(Version.Version),
        _ => null,
    };

    /// <summary>"12.3 MB of 48 MB", or "12.3 MB" when the size is unknown (the Mac's <c>bytesLine</c>).</summary>
    public static string BytesLine(UpdateDownloadProgress progress) =>
        progress.Total is { } total
            ? Strings.UpdateBytesOf(ByteSize.Format(progress.Received), ByteSize.Format(total))
            : ByteSize.Format(progress.Received);

    // Automatic checks

    /// <summary>Whether an automatic check is due now (the setting, and 24 h since the last successful check).</summary>
    public bool IsAutomaticCheckDue =>
        UpdateChecker.IsAutomaticCheckDue(settings.AutomaticUpdateChecks, settings.LastUpdateCheck, clock.GetUtcNow());

    /// <summary>One tick of the automatic schedule.</summary>
    public async Task RunAutomaticCheckIfDueAsync()
    {
        if (IsAutomaticCheckDue) await CheckNowAsync(userInitiated: false).ConfigureAwait(true);
    }

    /// <summary>Starts the automatic schedule: 10 s after launch, then hourly. Call once after launch (not in debug runs).</summary>
    public void StartAutomaticChecks()
    {
        if (scheduleCancellation is not null) return;
        scheduleCancellation = new CancellationTokenSource();
        _ = RunScheduleAsync(scheduleCancellation.Token);
    }

    private async Task RunScheduleAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UpdateChecker.FirstAutomaticCheckDelay, clock, token).ConfigureAwait(true);
            while (!token.IsCancellationRequested)
            {
                await RunAutomaticCheckIfDueAsync().ConfigureAwait(true);
                await Task.Delay(UpdateChecker.AutomaticCheckPollInterval, clock, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Checking

    /// <summary>
    /// Asks GitHub once. A manual check always shows a dialog; an automatic
    /// one only when a newer version exists. While an install is under way,
    /// a newer version brings the progress window forward instead.
    /// </summary>
    public async Task CheckNowAsync(bool userInitiated)
    {
        lock (gate)
        {
            if (checking) return;
            checking = true;
            // The failure message is shown once; a new check clears it.
            if (installPhase is UpdateState.Failed) installPhase = null;
        }
        Changed();
        UpdateState result;
        try
        {
            result = await checker.CheckAsync(Version.Version, settings, CancellationToken.None).ConfigureAwait(true) switch
            {
                UpdateCheckOutcome.Available available => new UpdateState.Available(available.Release),
                UpdateCheckOutcome.UpToDate => new UpdateState.UpToDate(),
                UpdateCheckOutcome.Failed failed => new UpdateState.Failed(new UpdateFailure(
                    UpdateFailureKind.Check, failed.Localized is { } key ? Strings.Localize(key) : Strings.CoreText(failed.Message))),
                _ => new UpdateState.Idle(),
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The checker reports expected failures as an outcome; anything
            // else must still end the spinner and reach the user.
            AppLog.Write($"update check failed: {error}");
            result = new UpdateState.Failed(new UpdateFailure(UpdateFailureKind.Check, Strings.Describe(error)));
        }
        finally
        {
            lock (gate)
            {
                checking = false;
            }
        }
        lock (gate)
        {
            if (userInitiated || result is not UpdateState.Failed) checkResult = result;
        }
        Changed();
        switch (result)
        {
            case UpdateState.Available available:
                if (IsWorking)
                {
                    if (userInitiated) prompts.ShowProgress();
                    return;
                }
                await OfferAsync(available.Release).ConfigureAwait(true);
                break;
            case UpdateState.UpToDate or UpdateState.Failed when userInitiated:
                await prompts.ShowCheckResultAsync(result).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>Install Update / View on GitHub / Later, or Download / Later with the reason this copy cannot install it.</summary>
    private async Task OfferAsync(ReleaseInfo release)
    {
        var problem = InstallProblem(release);
        var choice = await prompts.OfferAsync(release, Version.Version, problem).ConfigureAwait(true);
        switch (choice)
        {
            case UpdateOfferChoice.Install when problem is null:
                await InstallAsync(release).ConfigureAwait(true);
                break;
            case UpdateOfferChoice.Install:
            case UpdateOfferChoice.ViewOnGitHub:
                prompts.OpenReleasePage(release.HtmlUri);
                break;
        }
    }

    /// <summary>Why this copy cannot install <paramref name="release"/> itself, or null when it can.</summary>
    public string? InstallProblem(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.ZipAsset(release.Version) is null || release.ChecksumsAsset is null)
        {
            return Strings.Describe(new UpdatePackageException(new UpdatePackageError.NoPackage()));
        }
        try
        {
            installer.CheckLocation();
            return null;
        }
        catch (InstallLocationException error)
        {
            return Strings.Describe(error);
        }
    }

    // Installing

    /// <summary>
    /// Downloads, verifies and stages <paramref name="release"/> with the
    /// progress window, then offers Install and Relaunch. Does nothing but
    /// bring the window forward while an install runs.
    /// </summary>
    public async Task InstallAsync(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (IsWorking)
        {
            prompts.ShowProgress();
            return;
        }
        try
        {
            installer.CheckLocation();
        }
        catch (InstallLocationException error)
        {
            await FailAsync(new UpdateFailure(UpdateFailureKind.Location, Strings.Describe(error), release)).ConfigureAwait(true);
            return;
        }
        var zip = release.ZipAsset(release.Version);
        if (zip is null || release.ChecksumsAsset is null)
        {
            await FailAsync(new UpdateFailure(UpdateFailureKind.Download,
                Strings.Describe(new UpdatePackageException(new UpdatePackageError.NoPackage())), release)).ConfigureAwait(true);
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (gate)
        {
            installCancellation = cancellation;
            installPhase = new UpdateState.Downloading(release, new UpdateDownloadProgress(0, zip.Size > 0 ? zip.Size : null));
        }
        Changed();
        prompts.ShowProgress();
        // Progress<T> reports on the UI thread it was made on.
        var progress = new Progress<UpdateDownloadProgress>(value => OnProgress(release, value, cancellation.Token));
        try
        {
            var prepared = await installer.PrepareAsync(release, progress, cancellation.Token).ConfigureAwait(true);
            lock (gate)
            {
                installPhase = new UpdateState.Ready(release, prepared);
            }
            Changed();
            // The window turns into "Hearsay x is ready to install."
            prompts.ShowProgress();
        }
        catch (OperationCanceledException)
        {
            // A partial download is already gone; a complete zip stays and is
            // checked again next time.
            AppLog.Write($"update {release.Version}: cancelled");
            SetPhase(null);
            prompts.CloseProgress();
        }
        catch (UpdatePackageException error)
        {
            var kind = error.Error.IsVerificationFailure ? UpdateFailureKind.Verify : UpdateFailureKind.Download;
            await FailAsync(new UpdateFailure(kind, Strings.Describe(error), release, installer.DownloadedFile(release))).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await FailAsync(new UpdateFailure(UpdateFailureKind.Install, Strings.Describe(error), release, installer.DownloadedFile(release)))
                .ConfigureAwait(true);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(installCancellation, cancellation)) installCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void OnProgress(ReleaseInfo release, UpdateDownloadProgress value, CancellationToken token)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested || installPhase is not UpdateState.Downloading current || !ReferenceEquals(current.Release, release))
            {
                return;
            }
            // Core reports once more when the zip is complete; the checks follow.
            installPhase = value.Total is { } total && value.Received >= total
                ? new UpdateState.Verifying(release)
                : new UpdateState.Downloading(release, value);
        }
        Changed();
    }

    /// <summary>Cancel in the progress window: stops the download or verification and removes partial files.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            try
            {
                installCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Later: the staged copy goes, the verified zip stays so the next install skips the download.</summary>
    public void Later()
    {
        if (State is not UpdateState.Ready ready) return;
        installer.Discard(ready.Prepared);
        SetPhase(null);
        prompts.CloseProgress();
    }

    /// <summary>
    /// Install and Relaunch: refused while recording or transcribing (the
    /// staged copy goes, as after Later); otherwise starts the swap helper
    /// and quits.
    /// </summary>
    public async Task InstallAndRelaunchAsync()
    {
        if (State is not UpdateState.Ready ready) return;
        if (InstallBlocker() is { } blocker)
        {
            await prompts.ShowBlockedAsync(blocker).ConfigureAwait(true);
            if (State is UpdateState.Ready still && ReferenceEquals(still.Prepared, ready.Prepared))
            {
                installer.Discard(ready.Prepared);
                SetPhase(null);
                prompts.CloseProgress();
            }
            return;
        }
        SetPhase(new UpdateState.Installing(ready.Release));
        try
        {
            installer.StartSwap(ready.Prepared);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            installer.Discard(ready.Prepared);
            AppLog.Write($"update {ready.Release.Version}: the swap helper did not start: {error.Message}");
            await FailAsync(new UpdateFailure(UpdateFailureKind.Install, Strings.Describe(error), ready.Release,
                installer.DownloadedFile(ready.Release))).ConfigureAwait(true);
            return;
        }
        AppLog.Write($"update {ready.Release.Version}: {ready.Prepared.Summary}; helper started, quitting");
        Quit();
    }

    /// <summary>
    /// At launch: when the helper's log says the last install failed, the
    /// Windows-only notice "The last update could not be installed. …"
    /// (PLAN.md 18.4). Returns the text, or null.
    /// </summary>
    public async Task<string?> TakePreviousInstallFailureAsync() =>
        await installer.TakeLastInstallFailedAsync().ConfigureAwait(true) == true ? Strings.UpdatePreviousInstallFailed : null;

    /// <summary>
    /// UI snapshots only: shows <paramref name="state"/> as if a check or an
    /// install had reached it (a check result for Idle, Up to date and
    /// Available; an install phase otherwise). Nothing is checked or downloaded.
    /// </summary>
    internal void ShowSample(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            if (state is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Available)
            {
                checkResult = state is UpdateState.Idle ? null : state;
                installPhase = null;
            }
            else
            {
                installPhase = state;
            }
        }
        Changed();
    }

    private async Task FailAsync(UpdateFailure failure)
    {
        AppLog.Write($"update {failure.Release?.Version ?? "?"} failed ({failure.Kind}): {failure.Message}");
        SetPhase(new UpdateState.Failed(failure));
        prompts.CloseProgress();
        await prompts.ShowFailureAsync(failure).ConfigureAwait(true);
    }

    private void SetPhase(UpdateState? phase)
    {
        lock (gate)
        {
            installPhase = phase;
        }
        Changed();
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    /// <summary>The Mac's <c>date.formatted(date: .abbreviated, time: .shortened)</c>, as History shows dates.</summary>
    private static string FormatDate(DateTimeOffset date)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        return date.ToString(History.HistoryView.AbbreviatedDatePattern(culture.DateTimeFormat.LongDatePattern), culture)
            + " " + date.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
    }

    public void Dispose()
    {
        scheduleCancellation?.Cancel();
        scheduleCancellation?.Dispose();
        scheduleCancellation = null;
        Cancel();
    }
}
