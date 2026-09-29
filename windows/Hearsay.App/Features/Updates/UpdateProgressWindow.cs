using Hearsay.App.Interop;
using Hearsay.Core.Updates;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Hearsay.App.Features.Updates;

/// <summary>
/// "Downloading Hearsay 0.3.0…" with a progress bar, the byte count and
/// Cancel; then "Hearsay 0.3.0 is ready to install." with Later and Install
/// and Relaunch. Port of <c>UpdateProgressView</c> in
/// mac/Hearsay/Features/Updates/UpdateInstaller.swift (380 wide, 20 padding),
/// plus the Mac's ready alert (<c>offerInstall</c>), which Windows shows in
/// the same window. Also rendered by the UI snapshots.
/// </summary>
internal sealed partial class UpdateProgressView : UserControl
{
    public const int Width380 = 380;

    private readonly TextBlock title;
    private readonly TextBlock message;
    private readonly ProgressBar bar;
    private readonly TextBlock detail;
    private readonly Button cancel;
    private readonly Button later;
    private readonly Button install;

    public UpdateProgressView(Action onCancel, Action onLater, Action onInstall)
    {
        ArgumentNullException.ThrowIfNull(onCancel);
        ArgumentNullException.ThrowIfNull(onLater);
        ArgumentNullException.ThrowIfNull(onInstall);
        title = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        bar = new ProgressBar { Minimum = 0, Maximum = 1 };
        detail = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        cancel = new Button { Content = Strings.Cancel };
        cancel.Click += (_, _) => onCancel();
        later = new Button { Content = Strings.UpdateLater };
        later.Click += (_, _) => onLater();
        install = new Button
        {
            Content = Strings.UpdateInstallAndRelaunch,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        install.Click += (_, _) => onInstall();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(later);
        buttons.Children.Add(install);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(detail);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(20), Width = Width380 };
        stack.Children.Add(title);
        stack.Children.Add(message);
        stack.Children.Add(bar);
        stack.Children.Add(footer);
        Content = new Grid
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"],
            Children = { stack },
        };
        Show(new UpdateState.Idle());
    }

    /// <summary>The window's first line, for the UI snapshots' checks.</summary>
    public string TitleText => title.Text;

    /// <summary>Follows <paramref name="state"/>: the Mac's title, fraction, detail and <c>canCancel</c>.</summary>
    public void Show(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var ready = state is UpdateState.Ready;
        title.Text = state switch
        {
            UpdateState.Downloading d => Strings.UpdateDownloading(d.Release.Version),
            UpdateState.Ready r => Strings.UpdateReadyToInstall(r.Release.Version),
            UpdateState.Installing i => Strings.UpdateInstalling(i.Release.Version),
            UpdateState.Verifying v => Strings.UpdateVerifying(v.Release.Version),
            _ => title.Text,
        };
        message.Text = ready ? Strings.UpdateWillQuitAndReopen : "";
        message.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        bar.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (state is UpdateState.Downloading { Progress: var progress } && progress.Fraction is { } fraction)
        {
            bar.IsIndeterminate = false;
            bar.Value = fraction;
        }
        else
        {
            bar.IsIndeterminate = true;
        }
        detail.Text = state is UpdateState.Downloading downloading ? UpdateService.BytesLine(downloading.Progress) : "";
        cancel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        cancel.IsEnabled = state is UpdateState.Downloading or UpdateState.Verifying;
        later.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        install.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A sample release for the UI snapshots: 0.3.0 with its zip and checksum list.</summary>
    internal static ReleaseInfo SampleRelease(string version) => new()
    {
        Version = version,
        TagName = "v" + version,
        HtmlUri = new Uri($"https://github.com/{UpdateConfiguration.Repository}/releases/tag/v{version}"),
        Assets =
        [
            new ReleaseAsset(ReleaseInfo.ZipAssetName(version),
                new Uri($"https://github.com/{UpdateConfiguration.Repository}/releases/download/v{version}/{ReleaseInfo.ZipAssetName(version)}"),
                48_600_000),
            new ReleaseAsset(ReleaseInfo.ChecksumsFileName,
                new Uri($"https://github.com/{UpdateConfiguration.Repository}/releases/download/v{version}/{ReleaseInfo.ChecksumsFileName}"), 300),
        ],
    };
}

/// <summary>
/// The small "Software Update" window holding <see cref="UpdateProgressView"/>
/// (the Mac's floating, titled <c>NSPanel</c> in <c>UpdateInstaller.showProgressPanel</c>):
/// not resizable, on top, no minimize or maximize; closing it is Cancel
/// while downloading and Later when the update is ready.
/// </summary>
internal sealed partial class UpdateProgressWindow : Window
{
    private readonly UpdateService service;
    private bool closing;

    public UpdateProgressWindow(UpdateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service;
        Title = Strings.UpdateWindowTitle;
        View = new UpdateProgressView(service.Cancel, service.Later, () => _ = service.InstallAndRelaunchAsync());
        Content = View;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hearsay.ico"));
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        var dpi = NativeMethods.GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)(UpdateProgressView.Width380 * scale), (int)(160 * scale)));
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) is { } area)
        {
            var work = area.WorkArea;
            AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 3));
        }
        AppWindow.Closing += OnClosing;
        service.PropertyChanged += OnServiceChanged;
        Closed += (_, _) => service.PropertyChanged -= OnServiceChanged;
        View.Show(service.State);
    }

    public UpdateProgressView View { get; }

    /// <summary>Closes the window without treating it as Cancel or Later.</summary>
    public void CloseQuietly()
    {
        closing = true;
        Close();
    }

    public void Reveal()
    {
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }

    private void OnServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => View.Show(service.State));

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        // The title bar's close button: Cancel or Later; the service closes the window.
        args.Cancel = true;
        switch (service.State)
        {
            case UpdateState.Downloading or UpdateState.Verifying:
                service.Cancel();
                break;
            case UpdateState.Ready:
                service.Later();
                break;
        }
    }
}
