using System.ComponentModel;
using System.Globalization;
using Hearsay.App.Features.Debug;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Help;
using Hearsay.App.Features.History;
using Hearsay.App.Features.Hotkeys;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.MenuBar;
using Hearsay.App.Features.Notes;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Recovery;
using Hearsay.App.Features.Settings;
using Hearsay.App.Features.Transcription;
using Hearsay.App.Features.Updates;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Core.Updates;
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
        // One provider store on the same SettingsFile, tokens in Credential
        // Manager; debug runs keep tokens in memory and start from the
        // default preset (the Mac's UI snapshots do the same).
        Secrets = IsDebugRun ? new InMemorySecretStore() : new CredentialManagerSecretStore();
        AIProviders = IsDebugRun
            ? new AIProviderStore(SettingsFile, Secrets, installedCli: () => null)
            : new AIProviderStore(SettingsFile, Secrets);
        RunningLanguage = InterfaceLanguages.ResolveAtLaunch(Settings.InterfaceLanguage, DebugEnvironment.Environment);
        // Strings come from the .resw of this language, dates and numbers
        // follow it, as the Mac's AppleLanguages and \.locale do. Fixed for
        // the whole run (PLAN.md 18.3, "Localization").
        Strings.Apply(RunningLanguage);
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
        // W5: the one Whisper engine, recording controller and File flow (the
        // Mac's AppDelegate owns the same three); the spool is
        // %LOCALAPPDATA%\Hearsay\Recording, debug runs use the scratch folder.
        Engine = new TranscriptionEngine();
        Spool = new RecordingSpool(scratchFolder is not null ? Path.Combine(scratchFolder, "Recording") : RecordingSpool.DefaultRoot());
        RecordingController = new RecordingController(Settings, Models, Engine, Spool);
        FileModel = new FileViewModel(Settings, Models, Engine);
        Recording.Connect(RecordingController.ToggleStartStop, RecordingController.TogglePause);
        RecordingController.PropertyChanged += (_, _) => SyncRecordingStatus();
        // PLAN.md 4.6 and 18.4: the update check and install (the Mac's
        // UpdateService); the cache is %LOCALAPPDATA%\Hearsay\Updates, debug
        // runs use the scratch folder and never check or install.
        UpdateChecks = new GitHubUpdateCheckSource();
        UpdateSteps = new UpdateInstaller(UpdateInstaller.RunningInstallFolder,
            scratchFolder is not null ? Path.Combine(scratchFolder, "Updates") : UpdateInstall.DefaultUpdatesRoot);
        UpdatePrompts = new UpdatePrompts(this);
        Updates = new UpdateService(Settings, AppVersion.Current, UpdateChecks, UpdateSteps, UpdatePrompts)
        {
            InstallBlocker = UpdateInstallBlocker,
            Quit = Quit,
        };
        MainWindow = new MainWindow(this);
    }

    /// <summary>GitHub's latest release (disposed on quit).</summary>
    private GitHubUpdateCheckSource UpdateChecks { get; }

    /// <summary>The install steps for this install folder (disposed on quit).</summary>
    private UpdateInstaller UpdateSteps { get; }

    /// <summary>The update check and install (the Mac's <c>UpdateService</c> environment object).</summary>
    public UpdateService Updates { get; }

    /// <summary>The update dialogs and progress window.</summary>
    public UpdatePrompts UpdatePrompts { get; }

    /// <summary>The app's one Whisper engine.</summary>
    public TranscriptionEngine Engine { get; }

    /// <summary>Where recordings are written while they are made (PLAN.md 4.5).</summary>
    public RecordingSpool Spool { get; }

    /// <summary>The one recording session (the Mac's <c>RecordingController</c>).</summary>
    public RecordingController RecordingController { get; }

    /// <summary>The File tab's flow, shared with the recovery sheet and "Transcribe this file".</summary>
    public FileViewModel FileModel { get; }

    /// <summary>The interface language of this run (the setting, or <c>HEARSAY_UI_LANGUAGE</c>), fixed at launch.</summary>
    public InterfaceLanguage RunningLanguage { get; }

    public bool IsDebugRun { get; }

    public SettingsFile SettingsFile { get; }

    public AppSettings Settings { get; }

    /// <summary>Where API tokens live: Credential Manager, or memory in a debug run.</summary>
    public ISecretStore Secrets { get; }

    /// <summary>The one meeting-notes provider store (the Mac's <c>AIProviderStore</c> environment object).</summary>
    public AIProviderStore AIProviders { get; }

    public MainTabSelection Tabs { get; } = new();

    public RecordingStatus Recording { get; } = new();

    /// <summary>The one model store (the Mac's <c>ModelStore</c> environment object).</summary>
    public ModelStore Models { get; }

    public HotkeyManager Hotkeys { get; }

    public ILaunchAtLogin LaunchAtLogin { get; }

    public TrayIcon Tray { get; }

    public MainWindow MainWindow { get; }

    public HelpWindow? HelpWindow { get; private set; }

    /// <summary>The licenses window (Settings > General > Acknowledgements), while open.</summary>
    public HelpWindow? LicensesWindow { get; private set; }

    public bool IsQuitting { get; private set; }

    /// <summary>Whether closing the main window hides it (the tray modes) instead of quitting.</summary>
    public bool ClosingMainWindowHides => !IsQuitting && Settings.WindowMode.ShowsTrayIcon();

    public void Launch()
    {
        AppLog.Write($"settings {SettingsFile.FilePath}, interface language {RunningLanguage.Code()}");
        Settings.SaveFailed += (_, e) => MainWindow.ShowSettingsProblem(Strings.SettingsSaveFailed(e.Error.Message));
        AIProviders.SaveFailed += (_, e) => MainWindow.ShowSettingsProblem(Strings.SettingsSaveFailed(e.Error.Message));
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
        // Debug only (W5): transcribe one file, replay a WAV through the
        // recording pipeline, or record from one device, print, and quit.
        if (FileTranscriptionDebug.RunIfRequested(this) || RecordingReplay.RunIfRequested(this)
            || RecordingDebug.RunIfRequested(this))
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
        RecordingController.Activate();
        MainWindow.Activate();
        // PLAN.md 4.5: unfinished spool recordings, once the window can show a sheet.
        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => _ = OfferRecoveryAsync());
        // PLAN.md 4.6: 10 s after launch, then hourly; and the result of an
        // install that failed after the last quit (Windows only).
        Updates.StartAutomaticChecks();
        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => _ = ReportPreviousInstallAsync());
    }

    /// <summary>"The last update could not be installed. …" when the swap helper's log says so.</summary>
    private async Task ReportPreviousInstallAsync()
    {
        if (await Updates.TakePreviousInstallFailureAsync().ConfigureAwait(true) is not { } message) return;
        if (MainWindow.RenderRoot.XamlRoot is not { } root) return;
        await Alert.ShowAsync(root, Strings.UpdateCouldNotInstall, message).ConfigureAwait(true);
    }

    /// <summary>
    /// Why Install and Relaunch must wait: a recording session, its final
    /// pass, or a Whisper job (File mode) runs; notes and model downloads are
    /// not checked (the Mac's <c>AppDelegate.updateInstallBlocker</c>).
    /// </summary>
    private string? UpdateInstallBlocker() =>
        RecordingController.IsSessionActive || RecordingController.IsTranscribing || Engine.IsBusy || FileModel.IsBusy
            ? Strings.UpdateFinishRecordingFirst
            : null;

    /// <summary>
    /// The recovery sheet for spool recordings a crash left behind (the Mac's
    /// <c>UnfinishedRecordingQueue.checkOnce()</c> in MainView.swift).
    /// Transcribe moves the WAV into the output folder and runs the File flow.
    /// </summary>
    private async Task OfferRecoveryAsync()
    {
        if (UnfinishedRecordingQueue.CheckOnce(Spool) is not { } queue) return;
        if (MainWindow.RenderRoot.XamlRoot is not { } root) return;
        AppLog.Write($"recovery: {queue.Pending.Count} unfinished recording(s)");
        var sheet = new UnfinishedRecordingSheet(root, queue, () => TranscriptOutput.ResolveFolder(Settings), TranscribeRecovered);
        await sheet.RunAsync().ConfigureAwait(true);
    }

    /// <summary>Recovery "Transcribe": the sheet repaired the header; the WAV moves into the output folder (kept) and runs through the File flow.</summary>
    private void TranscribeRecovered(string spoolWav)
    {
        try
        {
            if (RecordingSpool.Finalize(spoolWav, keep: true, TranscriptOutput.ResolveFolder(Settings)) is not { } kept) return;
            Tabs.Tab = MainTab.File;
            FileModel.Transcribe(kept);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (MainWindow.RenderRoot.XamlRoot is { } root)
            {
                _ = Alert.ShowAsync(root, Strings.CouldNotTranscribeRecording, Strings.RecoveryStaysAt(error.Message, spoolWav));
            }
        }
    }

    /// <summary>Mirrors the controller into the tray and hotkeys' <see cref="Recording"/>, and hands "Transcribe this file" to the File tab.</summary>
    private void SyncRecordingStatus()
    {
        var controller = RecordingController;
        var phase = controller.Phase switch
        {
            ControllerPhase.Starting => RecordingPhase.Starting,
            ControllerPhase.Recording => RecordingPhase.Recording,
            ControllerPhase.Paused => RecordingPhase.Paused,
            ControllerPhase.Stopping => RecordingPhase.Stopping,
            ControllerPhase.Transcribing => RecordingPhase.Transcribing,
            _ => RecordingPhase.Idle,
        };
        Recording.Update(phase, TimeSpan.FromSeconds(controller.Elapsed), controller.TranscriptionProgress);
        Recording.SetBusyFiles(controller.BusyFiles);
        if (controller.TranscribeFileRequest is { } file && !FileModel.IsBusy)
        {
            // "Transcribe this file" on the Record tab (the Mac's MainView.takeTranscribeFileRequest).
            controller.TranscribeFileRequest = null;
            Tabs.Tab = MainTab.File;
            FileModel.Transcribe(file);
        }
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

    /// <summary>"Show Licenses…": the licenses page in a help window; opening it again brings it forward.</summary>
    public HelpWindow ShowLicenses()
    {
        if (LicensesWindow is not { } window)
        {
            window = HelpWindow.Licenses(OnHelpLink);
            window.Closed += (_, _) => LicensesWindow = null;
            LicensesWindow = window;
        }
        window.Activate();
        return window;
    }

    /// <summary>
    /// Quits Hearsay (tray menu, or closing the window in taskbar-only mode).
    /// While a recording is active it asks first, then stops and saves it;
    /// while the final pass runs it is cancelled and the live preview saved,
    /// the WAV kept (the Mac's <c>applicationShouldTerminate</c>, PLAN.md 4.4).
    /// </summary>
    public void Quit()
    {
        if (IsQuitting || isStoppingForQuit) return;
        if (RecordingController.IsSessionActive || RecordingController.IsTranscribing)
        {
            _ = QuitAfterRecordingAsync();
            return;
        }
        QuitNow();
    }

    private bool isStoppingForQuit;
    private bool relaunchRequested;

    /// <summary>
    /// Restart Now after an interface-language change (the Mac's language
    /// alert): quits as <see cref="Quit"/> does, asking first while
    /// recording, and <see cref="Program"/> starts Hearsay again once this
    /// instance has let go of its single-instance key. Debug runs never
    /// relaunch.
    /// </summary>
    public void Restart()
    {
        if (IsDebugRun)
        {
            AppLog.Write("restart: skipped in a debug run");
            return;
        }
        relaunchRequested = true;
        Quit();
    }

    private async Task QuitAfterRecordingAsync()
    {
        var controller = RecordingController;
        if (controller.IsSessionActive)
        {
            ShowMain();
            if (MainWindow.RenderRoot.XamlRoot is not { } root) return;
            var dialog = Alert.Make(root, Strings.StopRecordingAndQuit, Alert.Message(Strings.RecordingSavedBeforeQuit));
            dialog.PrimaryButtonText = Strings.StopAndQuit;
            dialog.CloseButtonText = Strings.Cancel;
            if (await Alert.PresentAsync(dialog).ConfigureAwait(true) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                relaunchRequested = false;
                return;
            }
            if (isStoppingForQuit || IsQuitting) return;
            isStoppingForQuit = true;
            await controller.StopAsync().ConfigureAwait(true);
        }
        else
        {
            isStoppingForQuit = true;
        }
        // Stopping starts the final pass; do not wait for it.
        await controller.CancelTranscriptionForQuitAsync().ConfigureAwait(true);
        QuitNow();
    }

    private void QuitNow()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        App.RelaunchRequested = relaunchRequested && !IsDebugRun;
        AppLog.Write(App.RelaunchRequested ? "quit to restart" : "quit");
        ticker?.Stop();
        Settings.PropertyChanged -= OnSettingsChanged;
        Hotkeys.Dispose();
        Updates.Dispose();
        UpdatePrompts.CloseProgress();
        UpdateChecks.Dispose();
        UpdateSteps.Dispose();
        Tray.Dispose();
        Models.Dispose();
        RecordingController.Dispose();
        Engine.Dispose();
        HelpWindow?.Close();
        LicensesWindow?.Close();
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
