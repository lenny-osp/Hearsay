using System.ComponentModel;
using System.Windows.Input;
using H.NotifyIcon;
using Hearsay.App.Interop;
using Hearsay.Core.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.MenuBar;

/// <summary>
/// The notification-area (tray) icon and its menu, the Windows counterpart of
/// the Mac's menu bar item (PLAN.md 4.4, 18.3). Mirrors
/// mac/Hearsay/Features/MenuBar/MenuBarView.swift (<c>MenuBarView</c>: state
/// line, Start or Stop, Pause / Resume while recording, Open Hearsay, Quit
/// Hearsay) and <c>MenuBarLabel</c> (the icon: plain when idle, red while
/// recording, paused variant while paused; always plain with "Show recording
/// status" off).
/// <para>
/// Built on H.NotifyIcon.WinUI (the Windows App SDK 2.5 has no
/// notification-icon API). The menu is a native Win32 popup menu
/// (<see cref="ContextMenuMode.PopupMenu"/>) built from a
/// <see cref="MenuFlyout"/> each time it opens, so it looks like every other
/// tray menu. A left click opens the main window, as Windows tray icons do;
/// the Mac's click opens its panel. The menu adds one disabled queue line
/// under the state line and Stop &amp; Start Next (enabled while recording or
/// paused) under Pause / Resume (PLAN.md 4.9, 18.10). The notification area cannot show text
/// beside the icon, so the elapsed time and the final pass's percentage are
/// in the tooltip.
/// </para>
/// Use from the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppSettings settings;
    private readonly RecordingStatus recording;
    private readonly System.Action openMain;
    private readonly System.Action quit;
    private readonly string assetsFolder;
    private TaskbarIcon? icon;
    private string? shownIcon;

    public TrayIcon(AppSettings settings, RecordingStatus recording, System.Action openMain, System.Action quit)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recording);
        this.settings = settings;
        this.recording = recording;
        this.openMain = openMain;
        this.quit = quit;
        assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets");
    }

    public bool IsVisible => icon is not null;

    /// <summary>Shows or removes the icon (window mode, PLAN.md 4.4); switching takes effect at once.</summary>
    public void SetVisible(bool visible)
    {
        if (visible == IsVisible) return;
        if (visible)
        {
            Create();
        }
        else
        {
            Remove();
        }
    }

    /// <summary>The icon file the tray shows now: <c>Hearsay.ico</c>, <c>Hearsay-recording.ico</c> or <c>Hearsay-paused.ico</c>.</summary>
    public string CurrentIconName
    {
        get
        {
            if (!settings.MenuBarShowsStatus) return "Hearsay.ico";
            return recording.Phase switch
            {
                RecordingPhase.Recording => "Hearsay-recording.ico",
                RecordingPhase.Paused => "Hearsay-paused.ico",
                _ => "Hearsay.ico",
            };
        }
    }

    /// <summary>
    /// The tooltip now: "Hearsay", or with status on the state line: the
    /// elapsed time while capturing, the percentage while transcribing
    /// ("Hearsay: Transcribing… 42%", the Mac's menu bar percentage in
    /// <c>MenuBarLabel</c>).
    /// </summary>
    public string CurrentTooltip =>
        settings.MenuBarShowsStatus && (recording.IsCapturing || recording.Phase == RecordingPhase.Transcribing)
            ? Strings.TrayTooltip(recording.StateText)
            : "Hearsay";

    public void Dispose()
    {
        Remove();
    }

    private void Create()
    {
        var created = new TaskbarIcon
        {
            ContextMenuMode = ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(openMain),
            ToolTipText = CurrentTooltip,
            ContextFlyout = BuildMenu(),
        };
        shownIcon = CurrentIconName;
        created.Icon = LoadIcon(shownIcon);
        // Efficiency mode (EcoQoS) would throttle a recording app.
        created.ForceCreate(enablesEfficiencyMode: false);
        icon = created;
        recording.PropertyChanged += OnStateChanged;
        settings.PropertyChanged += OnSettingsChanged;
        AppLog.Write("tray: icon shown");
    }

    private void Remove()
    {
        if (icon is null) return;
        recording.PropertyChanged -= OnStateChanged;
        settings.PropertyChanged -= OnSettingsChanged;
        icon.Dispose();
        icon = null;
        shownIcon = null;
        AppLog.Write("tray: icon removed");
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.MenuBarShowsStatus)) Refresh(menuChanged: false);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) =>
        Refresh(menuChanged: e.PropertyName is nameof(RecordingStatus.Phase) or nameof(RecordingStatus.QueueLine));

    /// <summary>Applies the icon, tooltip and (when the phase changed) menu for the current state.</summary>
    private void Refresh(bool menuChanged)
    {
        if (icon is null) return;
        var name = CurrentIconName;
        if (name != shownIcon)
        {
            icon.Icon = LoadIcon(name);
            shownIcon = name;
        }
        icon.ToolTipText = CurrentTooltip;
        if (menuChanged)
        {
            icon.ContextFlyout = BuildMenu();
        }
        else if (icon.ContextFlyout is MenuFlyout menu && menu.Items.Count > 0 && menu.Items[0] is MenuFlyoutItem state)
        {
            state.Text = recording.StateText;
        }
    }

    /// <summary>The menu in the Mac panel's order; the state line is a disabled item.</summary>
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = recording.StateText, IsEnabled = false });
        // One queue line, as the Mac panel shows (PLAN.md 4.9 "UI").
        if (recording.QueueLine is { } queueLine) menu.Items.Add(new MenuFlyoutItem { Text = queueLine, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        if (recording.IsCapturing)
        {
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = $"{Strings.Stop}\t{settings.StartStopHotkey.DisplayString}",
                Command = new RelayCommand(recording.ToggleStartStop),
            });
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = $"{(recording.Phase == RecordingPhase.Paused ? Strings.Resume : Strings.Pause)}\t{settings.PauseHotkey.DisplayString}",
                Command = new RelayCommand(recording.TogglePause),
            });
        }
        else
        {
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = $"{Strings.Start}\t{settings.StartStopHotkey.DisplayString}",
                Command = new RelayCommand(recording.ToggleStartStop),
            });
        }
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = $"{Strings.StopAndStartNext}	{settings.StopStartNextHotkey.DisplayString}",
            Command = new RelayCommand(recording.StopAndStartNext),
            IsEnabled = recording.CanStopAndStartNext,
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Strings.OpenHearsay, Command = new RelayCommand(openMain) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Strings.QuitHearsay, Command = new RelayCommand(quit) });
        return menu;
    }

    /// <summary>
    /// The icon file at the notification area's small-icon size for the system
    /// DPI. A new instance each time: TaskbarIcon disposes the icon it replaces.
    /// </summary>
    private System.Drawing.Icon LoadIcon(string name)
    {
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetDpiForSystem());
        if (size <= 0) size = 16;
        return new System.Drawing.Icon(Path.Combine(assetsFolder, name), size, size);
    }
}

/// <summary>A command that runs an action; the tray's popup menu invokes item commands.</summary>
internal sealed class RelayCommand(System.Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action();
}
