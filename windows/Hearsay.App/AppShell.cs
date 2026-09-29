using System.ComponentModel;
using System.Globalization;
using Hearsay.App.Features.Debug;
using Hearsay.App.Features.Help;
using Hearsay.App.Features.Hotkeys;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.MenuBar;
using Hearsay.App.Features.Settings;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Settings;
using Microsoft.UI.Dispatching;

namespace Hearsay.App;

/// <summary>
/// What mac/Hearsay/AppDelegate.swift does on the Mac: owns the one
/// <see cref="SettingsFile"/> and <see cref="AppSettings"/>, the one main
/// window, the one help window, the tray icon and the global hotkeys; applies
/// the window mode; quits; and starts the debug entry points. Also plays the
/// part of the Mac's <c>MainWindowOpener</c> (<see cref="ShowMain"/>).
/// <para>
/// Window modes (PLAN.md 4.4; the stored values are the Mac's):
/// "Notification area and taskbar" (<see cref="WindowMode.MenuBarAndDock"/>)
/// and "Notification area only" (<see cref="WindowMode.MenuBarOnly"/>: no
/// taskbar button and no Alt+Tab entry) close the main window to the tray.
/// "Taskbar only" (<see cref="WindowMode.DockOnly"/>) has no tray icon, and a
/// Windows taskbar button needs an open window, so closing the window quits
/// there (the Mac keeps running in the Dock). A change applies at once.
/// </para>
/// Use from the UI thread.
/// </summary>
internal sealed class AppShell
{
    private readonly App app;
    private readonly DispatcherQueue dispatcher;
    private readonly string? scratchFolder;
    private WindowMode? appliedMode;
    private DispatcherQueueTimer? ticker;

    public AppShell(App app, DispatcherQueue dispatcher)
    {
        this.app = app;
        this.dispatcher = dispatcher;
        IsDebugRun = DebugEnvironment.IsDebugRun;
        var folder = DebugEnvironment.UserSettingsFolder;
        if (IsDebugRun)
        {
            folder = DebugEnvironment.CreateScratchSettingsFolder();
            scratchFolder = folder;
            App.ScratchFolder = folder;
        }
        // WebView2 keeps its profile next to the exe by default, which an
        // installed app cannot write; debug runs use the scratch folder.
        var webViewData = scratchFolder is not null
            ? Path.Combine(scratchFolder, "WebView2")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearsay", "WebView2");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewData);

        // One SettingsFile for the whole app (PLAN.md 18.4, Settings): later
        // stores (AI providers, prompt templates) take this same instance.
        SettingsFile = new SettingsFile(folder);
        Settings = new AppSettings(SettingsFile);
        RunningLanguage = InterfaceLanguages.ResolveAtLaunch(Settings.InterfaceLanguage, DebugEnvironment.Environment);
        // Strings are English until W7 wires .resw; dates and numbers follow
        // the interface language already, as the Mac's \.locale does.
        var culture = RunningLanguage.Culture();
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        LaunchAtLogin = IsDebugRun ? new ScratchLaunchAtLogin() : new RunKeyLaunchAtLogin();
        // Models in %LOCALAPPDATA%\Hearsay\Models; debug runs use the scratch folder.
        Models = new ModelStore(Settings, scratchFolder is not null ? Path.Combine(scratchFolder, "Models") : ModelStore.DefaultRootPath);
        Hotkeys = new HotkeyManager(Settings, OnHotkey);
        Tray = new TrayIcon(Settings, Recording, () => ShowMain(), Quit);
        MainWindow = new MainWindow(this);
    }

    /// <summary>The interface language of this run (the setting, or <c>HEARSAY_UI_LANGUAGE</c>), fixed at launch.</summary>
    public InterfaceLanguage RunningLanguage { get; }

    public bool IsDebugRun { get; }

    public SettingsFile SettingsFile { get; }

    public AppSettings Settings { get; }

    public MainTabSelection Tabs { get; } = new();

    public RecordingStatus Recording { get; } = new();

    /// <summary>The one model store (the Mac's <c>ModelStore</c> environment object).</summary>
    public ModelStore Models { get; }

    public HotkeyManager Hotkeys { get; }

    public ILaunchAtLogin LaunchAtLogin { get; }

    public TrayIcon Tray { get; }

    public MainWindow MainWindow { get; }

    public HelpWindow? HelpWindow { get; private set; }

    public bool IsQuitting { get; private set; }

    /// <summary>Whether closing the main window hides it (the tray modes) instead of quitting.</summary>
    public bool ClosingMainWindowHides => !IsQuitting && Settings.WindowMode.ShowsTrayIcon();

    public void Launch()
    {
        AppLog.Write($"settings {SettingsFile.FilePath}, interface language {RunningLanguage.Code()}");
        Settings.SaveFailed += (_, e) => MainWindow.ShowSettingsProblem(Strings.SettingsSaveFailed(e.Error.Message));
        if (SettingsFile.CorruptFileBackup is { } backup)
        {
            MainWindow.ShowSettingsProblem(Strings.SettingsCorrupt(backup));
        }

        // Debug only: HEARSAY_UI_SNAPSHOTS=<dir> renders every tab, Settings
        // section and the help page into PNGs and quits (see UISnapshots).
        // Like the Mac, a debug run registers no hotkeys.
        if (UISnapshots.RunIfRequested(this))
        {
            return;
        }

        ticker = dispatcher.CreateTimer();
        ticker.Interval = TimeSpan.FromSeconds(1);
        ticker.Tick += (_, _) => Recording.Tick();
        ticker.Start();
        ApplyWindowMode(Settings.WindowMode);
        Settings.PropertyChanged += OnSettingsChanged;
        Hotkeys.Start();
        MainWindow.Activate();
    }

    /// <summary>Brings the one main window forward, optionally on <paramref name="tab"/> (the Mac's <c>MainWindowOpener.show(tab:)</c>).</summary>
    public void ShowMain(MainTab? tab = null)
    {
        if (IsQuitting) return;
        if (tab is { } selected) Tabs.Tab = selected;
        MainWindow.Reveal();
    }

    /// <summary>The help window; opening it again brings the existing one forward.</summary>
    public HelpWindow ShowHelp()
    {
        if (HelpWindow is not { } window)
        {
            window = new HelpWindow(RunningLanguage, OnHelpLink);
            window.Closed += (_, _) => HelpWindow = null;
            HelpWindow = window;
        }
        window.Activate();
        return window;
    }

    /// <summary>Quits Hearsay (tray menu, or closing the window in taskbar-only mode). Recording arrives in W5, so nothing asks first yet.</summary>
    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        AppLog.Write("quit");
        ticker?.Stop();
        Settings.PropertyChanged -= OnSettingsChanged;
        Hotkeys.Dispose();
        Tray.Dispose();
        Models.Dispose();
        HelpWindow?.Close();
        MainWindow.CloseForQuit();
        app.Exit();
    }

    /// <summary>
    /// The end of a debug run: closes everything and ends the message loop;
    /// <see cref="Program"/> then removes the scratch settings folder and
    /// returns <paramref name="status"/>.
    /// </summary>
    public void FinishDebugRun(int status)
    {
        App.ExitCode = status;
        Quit();
    }

    /// <summary>A <c>hearsay://open/...</c> link in the help page.</summary>
    private void OnHelpLink(HelpDestination destination)
    {
        if (destination.SettingsPane() is { } pane) Tabs.SettingsPane = pane;
        ShowMain(destination.Tab());
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.StartStop:
                Recording.ToggleStartStop();
                break;
            case HotkeyAction.Pause:
                Recording.TogglePause();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.WindowMode)) ApplyWindowMode(Settings.WindowMode);
    }

    /// <summary>
    /// Port of <c>AppDelegate.applyWindowMode</c>: the tray icon, the taskbar
    /// button, and, when switching to or from taskbar only with the window
    /// hidden, showing it so the user is never left without an entry point.
    /// </summary>
    private void ApplyWindowMode(WindowMode mode)
    {
        var previous = appliedMode;
        appliedMode = mode;
        Tray.SetVisible(mode.ShowsTrayIcon());
        MainWindow.SetShownInTaskbar(mode.ShowsTaskbarButton());
        AppLog.Write($"window mode {mode.StorageValue()}");
        if (previous is not { } before || before == mode) return;
        var involvesTaskbarOnly = before == WindowMode.DockOnly || mode == WindowMode.DockOnly;
        if (involvesTaskbarOnly && !MainWindow.IsShown) ShowMain();
    }
}
