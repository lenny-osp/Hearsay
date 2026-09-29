using System.ComponentModel;
using Hearsay.Core.Settings;
using Microsoft.UI.Xaml.Controls;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// Settings > Window: where Hearsay shows itself (PLAN.md 4.4, 18.3) and
/// whether the tray icon shows the recording status. Mirrors
/// <c>WindowSettingsView</c> in mac/Hearsay/Features/Settings/SettingsView.swift;
/// the Mac's menu bar item is the notification-area icon and the Dock icon
/// the taskbar button. The status toggle is disabled in taskbar-only mode,
/// as the Mac's is in Dock-only mode.
/// </summary>
internal sealed partial class WindowSettingsView : UserControl
{
    private readonly AppSettings settings;
    private readonly RadioButtons modes;
    private readonly ToggleSwitch showStatus;
    private bool refreshing;

    public WindowSettingsView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        settings = shell.Settings;
        var page = Page();

        modes = new RadioButtons { Header = Strings.ShowHearsayIn };
        foreach (var mode in WindowModes.All) modes.Items.Add(Label(mode));
        modes.SelectionChanged += (_, _) =>
        {
            if (refreshing || modes.SelectedIndex < 0) return;
            settings.WindowMode = WindowModes.All[modes.SelectedIndex];
        };

        var (statusRow, statusSwitch) = Toggle(Strings.ShowTrayStatus);
        showStatus = statusSwitch;
        showStatus.Toggled += (_, _) =>
        {
            if (!refreshing) settings.MenuBarShowsStatus = showStatus.IsOn;
        };

        page.Children.Add(Header(Strings.PaneWindow));
        page.Children.Add(Card(modes, Caption(Strings.ChangesApplyImmediately)));
        page.Children.Add(Card(statusRow, Caption(Strings.ShowTrayStatusCaption)));
        Content = page;
        Refresh();
        settings.PropertyChanged += OnSettingsChanged;
    }

    /// <summary>The Windows label of a mode (the Mac's <c>WindowMode.displayName</c>).</summary>
    public static string Label(WindowMode mode) => mode switch
    {
        WindowMode.MenuBarAndDock => Strings.ModeTrayAndTaskbar,
        WindowMode.MenuBarOnly => Strings.ModeTrayOnly,
        WindowMode.DockOnly => Strings.ModeTaskbarOnly,
        _ => mode.ToString(),
    };

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.WindowMode) or nameof(AppSettings.MenuBarShowsStatus)) Refresh();
    }

    private void Refresh()
    {
        refreshing = true;
        modes.SelectedIndex = WindowModes.All.ToList().IndexOf(settings.WindowMode);
        showStatus.IsOn = settings.MenuBarShowsStatus;
        showStatus.IsEnabled = settings.WindowMode.ShowsTrayIcon();
        refreshing = false;
    }
}
