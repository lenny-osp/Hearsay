using Hearsay.App.Features.History;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Settings;
using Hearsay.App.Features.Updates;
using Hearsay.Core.Updates;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// The update part of <see cref="UISnapshots"/>: Settings > General with a
/// newer version offered (the Software updates card with Install Update),
/// the "Hearsay x is available." dialog, the progress window downloading and
/// ready to install, and the failure dialog with Show in Explorer. Every
/// state is stubbed through <see cref="UpdateService.ShowSample"/>; nothing
/// is checked, downloaded or installed. Mirrors "25-update-progress" in
/// mac/Hearsay/Features/Debug/UISnapshots.swift.
/// </summary>
internal static class UpdateSnapshots
{
    public static async Task RunAsync(AppShell shell, GeneralSettingsView general, string sampleOutput, RecordingSnapshots.Tools tools)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(general);
        ArgumentNullException.ThrowIfNull(tools);
        var window = shell.MainWindow;
        var updates = shell.Updates;
        var prompts = shell.UpdatePrompts;
        var release = UpdateProgressView.SampleRelease("0.3.0");

        // Settings > General > Software updates, a newer version offered.
        shell.Settings.LastUpdateCheck = new DateTimeOffset(2026, 9, 30, 9, 41, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 30)));
        updates.ShowSample(new UpdateState.Available(release));
        await tools.Settle().ConfigureAwait(true);
        general.Updates.Card.StartBringIntoView();
        await tools.Settle().ConfigureAwait(true);
        tools.Check(general.Updates.ResultText == Strings.UpdateAvailable("0.3.0"),
            $"Software updates says a newer version is available (\"{general.Updates.ResultText}\")");
        tools.Check(general.Updates.OffersInstall, "Software updates offers Install Update");
        await tools.Render("59-settings-general-update-available", window.RenderRoot).ConfigureAwait(true);

        // The dialog after a check found it.
        var root = window.RenderRoot.XamlRoot;
        var offer = UpdatePrompts.MakeOffer(root, release, updates.Version.Version, null);
        var offering = Alert.PresentAsync(offer);
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("60-alert-update-available", tools.DialogBox(offer)).ConfigureAwait(true);
        offer.Hide();
        await offering.ConfigureAwait(true);

        // The progress window: downloading, then ready to install.
        updates.ShowSample(new UpdateState.Downloading(release, new UpdateDownloadProgress(20_400_000, 48_600_000)));
        prompts.ShowProgress();
        await tools.Settle().ConfigureAwait(true);
        if (prompts.ProgressWindow is { } progress)
        {
            tools.Check(progress.View.TitleText == Strings.UpdateDownloading("0.3.0"),
                $"the progress window says it is downloading (\"{progress.View.TitleText}\")");
            await tools.Render("61-update-progress-downloading", progress.View).ConfigureAwait(true);
            var staged = Path.Combine(sampleOutput, UpdateInstall.StagedFolderName("0.3.0"));
            updates.ShowSample(new UpdateState.Ready(release, new PreparedUpdate("0.3.0", staged, sampleOutput, "sample")));
            await tools.Settle().ConfigureAwait(true);
            tools.Check(progress.View.TitleText == Strings.UpdateReadyToInstall("0.3.0"),
                $"the progress window says the update is ready (\"{progress.View.TitleText}\")");
            await tools.Render("62-update-ready-to-install", progress.View).ConfigureAwait(true);
        }
        else
        {
            tools.Check(false, "the progress window opened");
        }
        prompts.CloseProgress();
        await tools.Settle().ConfigureAwait(true);
        tools.Check(prompts.ProgressWindow is null, "the progress window closed");

        // A verification failure with the zip still on disk (a sample file).
        var cache = Path.Combine(shell.SettingsFile.Folder, "Updates", "0.3.0");
        Directory.CreateDirectory(cache);
        var zip = Path.Combine(cache, ReleaseInfo.ZipAssetName("0.3.0"));
        await File.WriteAllBytesAsync(zip, []).ConfigureAwait(true);
        var failure = new UpdateFailure(UpdateFailureKind.Verify,
            Strings.Describe(new UpdatePackageException(new UpdatePackageError.ChecksumMismatch(ReleaseInfo.ZipAssetName("0.3.0")))),
            release, zip);
        updates.ShowSample(new UpdateState.Failed(failure));
        shell.Tabs.Tab = MainTab.Settings;
        var dialog = UpdatePrompts.MakeFailure(root, failure);
        var failing = Alert.PresentAsync(dialog);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(dialog.SecondaryButtonText == Strings.ShowInExplorer && dialog.PrimaryButtonText == Strings.UpdateViewOnGitHub,
            "the failure dialog offers View on GitHub and Show in Explorer");
        await tools.Render("63-alert-update-failed", tools.DialogBox(dialog)).ConfigureAwait(true);
        dialog.Hide();
        await failing.ConfigureAwait(true);

        updates.ShowSample(new UpdateState.Idle());
        shell.Settings.LastUpdateCheck = null;
    }
}
