using System.ComponentModel;
using Hearsay.App.Features.History;
using Hearsay.App.Features.Models;
using Hearsay.App.Features.Settings;
using Hearsay.App.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace Hearsay.App.Features.Main;

/// <summary>
/// The one main window (PLAN.md 4, 4.4): Record, File, Models, History,
/// Settings, in the Mac's order. Mirrors mac/Hearsay/Features/Main/MainView.swift
/// and the single <c>Window</c> scene in mac/Hearsay/HearsayApp.swift (there
/// is never a second one; a second Hearsay.exe activates this one).
/// <para>
/// The tabs are a <see cref="NavigationView"/> in Top mode: WinUI's own
/// equivalent of the Mac's <c>TabView</c> for an app's top-level pages (what
/// the Windows 11 apps use), with icons, keyboard navigation, and overflow
/// when the window is narrow. <c>SelectorBar</c> is meant for switching views
/// of one page, so the Settings sections use it (the Mac's segmented picker).
/// The built-in settings item is off because its label would follow the
/// Windows display language, not Hearsay's. Help (F1) sits at the right end,
/// in place of the Mac's Help menu.
/// </para>
/// </summary>
internal sealed partial class MainWindow : Window
{
    /// <summary>The Mac's default window size, in DIPs.</summary>
    public const int DefaultWidth = 720;
    public const int DefaultHeight = 480;
    private const int MinimumWidth = 560;
    private const int MinimumHeight = 360;

    private readonly AppShell shell;
    private readonly Dictionary<MainTab, NavigationViewItem> items;
    private readonly Dictionary<MainTab, UIElement> views;
    private bool updatingSelection;

    public MainWindow(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        InitializeComponent();
        Title = "Hearsay";
        SettingsProblem.Title = Strings.SettingsProblemTitle;

        RecordItem.Content = Strings.TabRecord;
        FileItem.Content = Strings.TabFile;
        ModelsItem.Content = Strings.TabModels;
        HistoryItem.Content = Strings.TabHistory;
        SettingsItem.Content = Strings.TabSettings;
        HelpItem.Content = Strings.HelpButton;
        items = new()
        {
            [MainTab.Record] = RecordItem,
            [MainTab.File] = FileItem,
            [MainTab.Models] = ModelsItem,
            [MainTab.History] = HistoryItem,
            [MainTab.Settings] = SettingsItem,
        };
        // Record and File are placeholders until W5 fills them.
        SettingsView = new SettingsPage(shell);
        ModelsView = new ModelManagerView(shell);
        // Move to Recycle Bin needs OutputWriter's Recycle Bin call made
        // public in Hearsay.Core; until then it is off (null).
        HistoryView = new HistoryView(shell, recycle: Hearsay.Core.Naming.OutputWriter.MoveToRecycleBin);
        views = new()
        {
            [MainTab.Record] = new PlaceholderView(Strings.TabRecord, Strings.PlaceholderRecord),
            [MainTab.File] = new PlaceholderView(Strings.TabFile, Strings.PlaceholderFile),
            [MainTab.Models] = ModelsView,
            [MainTab.History] = HistoryView,
            [MainTab.Settings] = SettingsView,
        };

        var help = new KeyboardAccelerator { Key = VirtualKey.F1 };
        help.Invoked += (_, e) =>
        {
            e.Handled = true;
            shell.ShowHelp();
        };
        Root.KeyboardAccelerators.Add(help);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hearsay.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Scale(MinimumWidth);
            presenter.PreferredMinimumHeight = Scale(MinimumHeight);
        }
        ResizeClient(DefaultWidth, DefaultHeight);
        AppWindow.Closing += OnClosing;

        shell.Tabs.PropertyChanged += OnTabsChanged;
        ShowTab(shell.Tabs.Tab);
    }

    /// <summary>The Settings tab, for the UI snapshots.</summary>
    public SettingsPage SettingsView { get; }

    /// <summary>The Models tab, for the UI snapshots.</summary>
    public ModelManagerView ModelsView { get; }

    /// <summary>The History tab, for the UI snapshots.</summary>
    public HistoryView HistoryView { get; }

    /// <summary>The element the UI snapshots render: the whole window content.</summary>
    public FrameworkElement RenderRoot => Root;

    /// <summary>Visible and not minimized (the Mac's <c>isMainWindowVisible</c>).</summary>
    public bool IsShown =>
        AppWindow.IsVisible && !(AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized });

    /// <summary>Shows the window (restoring it when minimized) and brings it to the front.</summary>
    public void Reveal()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
    }

    /// <summary>Taskbar button and Alt+Tab entry, off in "Notification area only" mode.</summary>
    public void SetShownInTaskbar(bool shown) => AppWindow.IsShownInSwitchers = shown;

    /// <summary>Resizes the client area to <paramref name="width"/> × <paramref name="height"/> DIPs.</summary>
    public void ResizeClient(int width, int height) => AppWindow.ResizeClient(new SizeInt32(Scale(width), Scale(height)));

    /// <summary>A warning above the tabs, until the user closes it.</summary>
    public void ShowSettingsProblem(string message)
    {
        SettingsProblem.Message = message;
        SettingsProblem.IsOpen = true;
        AppLog.Write($"settings problem: {message}");
    }

    public void CloseForQuit()
    {
        shell.Tabs.PropertyChanged -= OnTabsChanged;
        AppWindow.Closing -= OnClosing;
        Close();
    }

    private int Scale(int dips)
    {
        var dpi = NativeMethods.GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        return (int)Math.Round(dips * (dpi == 0 ? 1.0 : dpi / 96.0));
    }

    /// <summary>
    /// Closing hides the window in the tray modes (the Mac never quits on
    /// closing its last window) and quits in taskbar-only mode.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (shell.IsQuitting) return;
        args.Cancel = true;
        if (shell.ClosingMainWindowHides)
        {
            AppWindow.Hide();
        }
        else
        {
            shell.Quit();
        }
    }

    private void OnTabsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainTabSelection.Tab)) ShowTab(shell.Tabs.Tab);
    }

    private void ShowTab(MainTab tab)
    {
        updatingSelection = true;
        Navigation.SelectedItem = items[tab];
        updatingSelection = false;
        TabContent.Content = views[tab];
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (updatingSelection) return;
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<MainTab>(tag, out var tab))
        {
            shell.Tabs.Tab = tab;
        }
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == HelpItem) shell.ShowHelp();
    }
}
