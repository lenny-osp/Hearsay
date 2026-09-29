using System.Diagnostics;
using Hearsay.App.Features.History;
using Hearsay.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Updates;

/// <summary>
/// The update dialogs as WinUI <see cref="ContentDialog"/>s over the main
/// window (brought forward first, as the Mac activates the app) and the
/// progress window: the <c>NSAlert</c>s of <c>UpdateService.presentAvailable</c>,
/// <c>presentResult</c> (mac/Hearsay/Features/Updates/UpdateService.swift) and
/// <c>UpdateInstaller.presentFailure</c>, <c>offerInstall</c>'s busy alert and
/// <c>showProgressPanel</c> (UpdateInstaller.swift). The failure dialog adds
/// Show in Explorer when the downloaded zip is still on disk (Windows only).
/// </summary>
internal sealed class UpdatePrompts : IUpdatePrompts
{
    private readonly AppShell shell;
    private UpdateProgressWindow? progress;

    public UpdatePrompts(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
    }

    /// <summary>The progress window while it is open (the UI snapshots render it).</summary>
    public UpdateProgressWindow? ProgressWindow => progress;

    public async Task<UpdateOfferChoice> OfferAsync(ReleaseInfo release, string currentVersion, string? problem)
    {
        if (Root() is not { } root) return UpdateOfferChoice.Later;
        var dialog = MakeOffer(root, release, currentVersion, problem);
        return await Alert.PresentAsync(dialog).ConfigureAwait(true) switch
        {
            ContentDialogResult.Primary => UpdateOfferChoice.Install,
            ContentDialogResult.Secondary => UpdateOfferChoice.ViewOnGitHub,
            _ => UpdateOfferChoice.Later,
        };
    }

    public async Task ShowCheckResultAsync(UpdateState result)
    {
        if (Root() is not { } root) return;
        var dialog = MakeCheckResult(root, result, shell.Updates.Version.Version);
        await Alert.PresentAsync(dialog).ConfigureAwait(true);
    }

    public async Task ShowFailureAsync(UpdateFailure failure)
    {
        if (Root() is not { } root) return;
        var dialog = MakeFailure(root, failure);
        switch (await Alert.PresentAsync(dialog).ConfigureAwait(true))
        {
            case ContentDialogResult.Primary when failure.Release is { } release:
                OpenReleasePage(release.HtmlUri);
                break;
            case ContentDialogResult.Secondary when failure.DownloadedFile is { } file:
                if (ExplorerShell.Reveal([file]) is { } error) AppLog.Write($"update: cannot show {file}: {error}");
                break;
        }
    }

    public async Task ShowBlockedAsync(string message)
    {
        // Over the progress window, which is in front.
        var root = progress?.Content?.XamlRoot ?? Root();
        if (root is null) return;
        var dialog = Alert.Make(root, message, "");
        dialog.Content = null;
        dialog.CloseButtonText = Strings.OK;
        dialog.DefaultButton = ContentDialogButton.Close;
        await Alert.PresentAsync(dialog).ConfigureAwait(true);
    }

    public void ShowProgress()
    {
        if (progress is null)
        {
            progress = new UpdateProgressWindow(shell.Updates);
            progress.Closed += (_, _) => progress = null;
        }
        progress.Reveal();
    }

    public void CloseProgress() => progress?.CloseQuietly();

    public void OpenReleasePage(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            AppLog.Write($"update: cannot open {page}: {error.Message}");
        }
    }

    /// <summary>"Hearsay x is available." (the Mac's <c>presentAvailable</c>).</summary>
    public static ContentDialog MakeOffer(XamlRoot root, ReleaseInfo release, string currentVersion, string? problem)
    {
        ArgumentNullException.ThrowIfNull(release);
        var installed = Strings.UpdateYouHaveVersion(currentVersion);
        var dialog = Alert.Make(root, Strings.UpdateAvailable(release.Version), Alert.Message(
            problem is null ? installed : installed + " " + problem + " " + Strings.UpdateDownloadPageOpens));
        if (problem is null)
        {
            dialog.PrimaryButtonText = Strings.UpdateInstallUpdate;
            dialog.SecondaryButtonText = Strings.UpdateViewOnGitHub;
        }
        else
        {
            dialog.PrimaryButtonText = Strings.Download;
        }
        dialog.CloseButtonText = Strings.UpdateLater;
        dialog.DefaultButton = ContentDialogButton.Primary;
        return dialog;
    }

    /// <summary>The answer to a manual check (the Mac's <c>presentResult</c>).</summary>
    public static ContentDialog MakeCheckResult(XamlRoot root, UpdateState result, string currentVersion)
    {
        var dialog = result is UpdateState.Failed failed
            ? Alert.Make(root, Strings.UpdateCouldNotCheck, Alert.Message(failed.Failure.Message))
            : Alert.Make(root, Strings.UpdateUpToDate(currentVersion), "");
        if (result is not UpdateState.Failed) dialog.Content = null;
        dialog.CloseButtonText = Strings.OK;
        dialog.DefaultButton = ContentDialogButton.Close;
        return dialog;
    }

    /// <summary>An install failure with the Core's text (the Mac's <c>presentFailure</c>), plus Show in Explorer.</summary>
    public static ContentDialog MakeFailure(XamlRoot root, UpdateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var dialog = Alert.Make(root, failure.Title, Alert.Message(failure.Message));
        if (failure.Release is not null) dialog.PrimaryButtonText = Strings.UpdateViewOnGitHub;
        if (failure.DownloadedFile is { } file && File.Exists(file)) dialog.SecondaryButtonText = Strings.ShowInExplorer;
        dialog.CloseButtonText = Strings.Cancel;
        dialog.DefaultButton = failure.Release is null ? ContentDialogButton.Close : ContentDialogButton.Primary;
        return dialog;
    }

    /// <summary>The main window, brought forward, for a dialog; null while quitting.</summary>
    private XamlRoot? Root()
    {
        if (shell.IsQuitting) return null;
        shell.ShowMain();
        return shell.MainWindow.RenderRoot.XamlRoot;
    }
}
