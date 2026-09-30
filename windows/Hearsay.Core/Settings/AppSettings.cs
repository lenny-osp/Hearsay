using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Hearsay.Core.Transcription;

namespace Hearsay.Core.Settings;

/// <summary>
/// How Hearsay presents itself (PLAN.md 4.4). The Mac's menu bar item is the
/// Windows notification-area (tray) icon and the Dock icon is the taskbar
/// button; the stored values are the Mac's. The localized labels live in the
/// app (W4/W7), like the other display strings of the core port.
/// Port of <c>WindowMode</c> in mac/HearsayCore/Sources/HearsayCore/Settings/AppSettings.swift.
/// </summary>
public enum WindowMode
{
    MenuBarAndDock,
    MenuBarOnly,
    DockOnly,
}

public static class WindowModes
{
    /// <summary>Every mode in picker order.</summary>
    public static readonly IReadOnlyList<WindowMode> All =
        [WindowMode.MenuBarAndDock, WindowMode.MenuBarOnly, WindowMode.DockOnly];

    /// <summary>The stored value, the Mac's raw value.</summary>
    public static string StorageValue(this WindowMode mode) => mode switch
    {
        WindowMode.MenuBarAndDock => "menuBarAndDock",
        WindowMode.MenuBarOnly => "menuBarOnly",
        WindowMode.DockOnly => "dockOnly",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static WindowMode? FromStorageValue(string? value) => value switch
    {
        "menuBarAndDock" => WindowMode.MenuBarAndDock,
        "menuBarOnly" => WindowMode.MenuBarOnly,
        "dockOnly" => WindowMode.DockOnly,
        _ => null,
    };

    /// <summary>Whether the tray icon is shown in this mode (the Mac's <c>showsMenuBarItem</c>).</summary>
    public static bool ShowsTrayIcon(this WindowMode mode) => mode != WindowMode.DockOnly;

    /// <summary>Whether the app keeps a taskbar button (the Mac's <c>showsDockIcon</c>).</summary>
    public static bool ShowsTaskbarButton(this WindowMode mode) => mode != WindowMode.MenuBarOnly;
}

/// <summary>
/// User preferences backed by <see cref="SettingsFile"/> (<c>settings.json</c>
/// in the folder the app passes, <c>%APPDATA%\Hearsay</c>; tests pass a
/// scratch folder). Every write is persisted immediately, and every change
/// raises <see cref="PropertyChanged"/> so WinUI can bind. Use it from the UI
/// thread, as the Mac's <c>@MainActor</c> class is.
/// Port of mac/HearsayCore/Sources/HearsayCore/Settings/AppSettings.swift:
/// the same keys, defaults and migrations, except that the output folder is
/// a path (<see cref="Key.OutputFolder"/>) instead of a bookmark.
/// <para>
/// A failed write does not throw from a property setter (a binding would
/// crash): the value stays in memory for this run and <see cref="SaveFailed"/>
/// reports the error so the app can tell the user.
/// </para>
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    /// <summary>The keys in settings.json, the Mac's UserDefaults keys.</summary>
    public static class Key
    {
        public const string WindowMode = "windowMode";
        public const string MenuBarShowsStatus = "menuBarShowsStatus";
        /// <summary>
        /// Full path of the user-chosen output folder. Windows only: the Mac
        /// stores a bookmark under "outputFolderBookmark" instead.
        /// </summary>
        public const string OutputFolder = "outputFolder";
        /// <summary>Legacy key ("en" or "zh"), read once to migrate into <see cref="LanguageChoice"/>.</summary>
        public const string DefaultLanguageCode = "defaultLanguageCode";
        public const string LanguageChoice = "languageChoice";
        public const string PreferredLanguage = "preferredLanguage";
        public const string ActiveModelRepo = "activeModelRepo";
        public const string CaptureSystemAudio = "captureSystemAudio";
        public const string StartStopHotkey = "startStopHotkey";
        public const string PauseHotkey = "pauseHotkey";
        public const string StopStartNextHotkey = "stopStartNextHotkey";
        public const string FinalPassTiming = "finalPassTiming";
        public const string KeepRecording = "keepRecording";
        /// <summary>
        /// Legacy "Chinese output" setting ("traditional" or "simplified"),
        /// read only to migrate a stored "zh" into ZH-TW or ZH-CN.
        /// </summary>
        public const string ChineseScript = "chineseScript";
        public const string InterfaceLanguage = "interfaceLanguage";
        public const string AutomaticUpdateChecks = "automaticUpdateChecks";
        public const string LastUpdateCheck = "lastUpdateCheck";
        public const string ScreenAudioGrantedCodeHash = "screenAudioGrantedCodeHash";
        public const string ScreenAudioResetCodeHash = "screenAudioResetCodeHash";
        public const string MicrophoneGrantedCodeHash = "microphoneGrantedCodeHash";
    }

    private readonly SettingsFile file;

    private WindowMode windowMode;
    private bool menuBarShowsStatus;
    private string? outputFolder;
    private LanguageChoice languageChoice;
    private TranscriptLanguage preferredLanguage;
    private string? activeModelRepo;
    private bool captureSystemAudio;
    private HotkeyBinding startStopHotkey;
    private HotkeyBinding pauseHotkey;
    private HotkeyBinding stopStartNextHotkey;
    private FinalPassTiming finalPassTiming;
    private bool keepRecording;
    private InterfaceLanguage interfaceLanguage;
    private bool automaticUpdateChecks;
    private DateTimeOffset? lastUpdateCheck;
    private string? screenAudioGrantedCodeHash;
    private string? screenAudioResetCodeHash;
    private string? microphoneGrantedCodeHash;

    /// <summary>Loads <c>settings.json</c> from <paramref name="folder"/>.</summary>
    public AppSettings(string folder)
        : this(new SettingsFile(folder))
    {
    }

    /// <summary>Uses <paramref name="file"/>, which other stores may share.</summary>
    public AppSettings(SettingsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        this.file = file;
        windowMode = WindowModes.FromStorageValue(file.GetString(Key.WindowMode)) ?? WindowMode.MenuBarAndDock;
        menuBarShowsStatus = file.GetBool(Key.MenuBarShowsStatus) ?? true;
        outputFolder = file.GetString(Key.OutputFolder);
        languageChoice = LoadLanguageChoice();
        preferredLanguage = LoadPreferredLanguage();
        activeModelRepo = file.GetString(Key.ActiveModelRepo);
        captureSystemAudio = file.GetBool(Key.CaptureSystemAudio) ?? true;
        startStopHotkey = HotkeyBinding.FromJson(file.Get(Key.StartStopHotkey)) ?? HotkeyBinding.DefaultStartStop;
        pauseHotkey = HotkeyBinding.FromJson(file.Get(Key.PauseHotkey)) ?? HotkeyBinding.DefaultPause;
        stopStartNextHotkey = HotkeyBinding.FromJson(file.Get(Key.StopStartNextHotkey)) ?? HotkeyBinding.DefaultStopStartNext;
        finalPassTiming = FinalPassTimings.FromStorageValue(file.GetString(Key.FinalPassTiming)) ?? FinalPassTimings.Default;
        keepRecording = file.GetBool(Key.KeepRecording) ?? true;
        interfaceLanguage = InterfaceLanguages.FromCode(file.GetString(Key.InterfaceLanguage)) ?? InterfaceLanguage.English;
        automaticUpdateChecks = file.GetBool(Key.AutomaticUpdateChecks) ?? true;
        lastUpdateCheck = ParseDate(file.GetString(Key.LastUpdateCheck));
        screenAudioGrantedCodeHash = file.GetString(Key.ScreenAudioGrantedCodeHash);
        screenAudioResetCodeHash = file.GetString(Key.ScreenAudioResetCodeHash);
        microphoneGrantedCodeHash = file.GetString(Key.MicrophoneGrantedCodeHash);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A change could not be written to settings.json; it holds for this run only.</summary>
    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    /// <summary>The file these settings live in.</summary>
    public SettingsFile File => file;

    public WindowMode WindowMode
    {
        get => windowMode;
        set => Update(ref windowMode, value, () => file.SetString(Key.WindowMode, value.StorageValue()));
    }

    /// <summary>
    /// "Show recording status in the notification area" (PLAN.md 18.3, "Tray
    /// status"; the Mac's "Show recording status in the menu bar"). On (the
    /// default): the tray icon turns red while recording and shows a pause
    /// variant while paused, with the elapsed time in the tooltip. Off: the
    /// icon stays plain in every state.
    /// </summary>
    public bool MenuBarShowsStatus
    {
        get => menuBarShowsStatus;
        set => Update(ref menuBarShowsStatus, value, () => file.SetBool(Key.MenuBarShowsStatus, value));
    }

    /// <summary>
    /// Full path of the user-chosen output folder, or null for the default
    /// (see <see cref="OutputLocation"/>). Store what
    /// <see cref="OutputLocation.MakeStoredPath"/> returns.
    /// </summary>
    public string? OutputFolder
    {
        get => outputFolder;
        set => Update(ref outputFolder, value, () => file.SetString(Key.OutputFolder, value));
    }

    /// <summary>
    /// The Whisper model used for transcription, or null when none is chosen
    /// (the model store, W4). The Mac stores a Hugging Face repo; the key is
    /// kept for the Windows model id.
    /// </summary>
    public string? ActiveModelRepo
    {
        get => activeModelRepo;
        set => Update(ref activeModelRepo, value, () => file.SetString(Key.ActiveModelRepo, value));
    }

    /// <summary>
    /// Transcription language chosen on the Record or File tab: Auto or a
    /// fixed language, stored as "auto" or the language's code. A fresh
    /// install starts at Auto; an install that stored the legacy
    /// <c>defaultLanguageCode</c> keeps that language as a fixed choice. A
    /// stored "zh" becomes ZH-TW or ZH-CN by the legacy <see cref="Key.ChineseScript"/>.
    /// </summary>
    public LanguageChoice LanguageChoice
    {
        get => languageChoice;
        set
        {
            if (Update(ref languageChoice, value, () => file.SetString(Key.LanguageChoice, value.StorageValue)))
            {
                OnPropertyChanged(nameof(DefaultLanguageCode));
            }
        }
    }

    /// <summary>
    /// The language Auto falls back to when detection is not confident
    /// (Settings > General). Default English. Only the user changes it in
    /// Settings; nothing else in the app writes it. A stored "zh" migrates
    /// like <see cref="LanguageChoice"/>.
    /// </summary>
    public TranscriptLanguage PreferredLanguage
    {
        get => preferredLanguage;
        set
        {
            if (Update(ref preferredLanguage, value, () => file.SetString(Key.PreferredLanguage, value.Code())))
            {
                OnPropertyChanged(nameof(DefaultLanguageCode));
            }
        }
    }

    /// <summary>
    /// Deprecated on the Mac: use <see cref="LanguageChoice"/> and
    /// <see cref="PreferredLanguage"/>. Reads the fixed language's code, or
    /// the preferred language's for Auto. Writing a supported code (or the
    /// legacy "zh", as ZH-TW) sets <see cref="LanguageChoice"/> to that fixed
    /// language (never <see cref="PreferredLanguage"/>); other values are ignored.
    /// </summary>
    public string DefaultLanguageCode
    {
        get => (languageChoice.FixedLanguage ?? preferredLanguage).Code();
        set
        {
            if (value is null) return;
            if (TranscriptLanguages.FromStoredValue(value, null) is { } language)
            {
                LanguageChoice = LanguageChoice.Fixed(language);
            }
        }
    }

    /// <summary>"Also capture system audio" on the Record tab (PLAN.md 4.1). Default on.</summary>
    public bool CaptureSystemAudio
    {
        get => captureSystemAudio;
        set => Update(ref captureSystemAudio, value, () => file.SetBool(Key.CaptureSystemAudio, value));
    }

    /// <summary>Global shortcut that toggles Start / Stop (PLAN.md 4.4). Default Ctrl+Alt+Win+R.</summary>
    public HotkeyBinding StartStopHotkey
    {
        get => startStopHotkey;
        set => Update(ref startStopHotkey, value, () => file.Set(Key.StartStopHotkey, value.ToJson()));
    }

    /// <summary>Global shortcut that toggles Pause / Resume (PLAN.md 4.4). Default Ctrl+Alt+Win+P.</summary>
    public HotkeyBinding PauseHotkey
    {
        get => pauseHotkey;
        set => Update(ref pauseHotkey, value, () => file.Set(Key.PauseHotkey, value.ToJson()));
    }

    /// <summary>Global shortcut for Stop &amp; Start Next (PLAN.md 4.9 item 2). Default Ctrl+Alt+Win+N.</summary>
    public HotkeyBinding StopStartNextHotkey
    {
        get => stopStartNextHotkey;
        set => Update(ref stopStartNextHotkey, value, () => file.Set(Key.StopStartNextHotkey, value.ToJson()));
    }

    /// <summary>
    /// When a queued recording gets its final pass (Settings > General >
    /// Transcription, PLAN.md 4.9 item 3). Default
    /// <see cref="FinalPassTiming.WhenIdle"/> on Windows (PLAN.md 18.10; the
    /// Mac's is <see cref="FinalPassTiming.Immediate"/>); stored as the
    /// shared value, and an unknown stored value reads as the default.
    /// </summary>
    public FinalPassTiming FinalPassTiming
    {
        get => finalPassTiming;
        set => Update(ref finalPassTiming, value, () => file.SetString(Key.FinalPassTiming, value.StorageValue()));
    }

    /// <summary>
    /// Keep the recording (WAV) in the output folder after a successful
    /// transcription (PLAN.md section 8). Default on, like the Python tool.
    /// Off deletes it; a failed transcription always keeps it.
    /// </summary>
    public bool KeepRecording
    {
        get => keepRecording;
        set => Update(ref keepRecording, value, () => file.SetBool(Key.KeepRecording, value));
    }

    /// <summary>
    /// The language of Hearsay's own interface (Settings > General). Default
    /// English on a fresh install, whatever the Windows display language. The
    /// app applies it at launch (<see cref="InterfaceLanguages.ResolveAtLaunch"/>);
    /// a change needs a restart.
    /// </summary>
    public InterfaceLanguage InterfaceLanguage
    {
        get => interfaceLanguage;
        set => Update(ref interfaceLanguage, value, () => file.SetString(Key.InterfaceLanguage, value.Code()));
    }

    /// <summary>
    /// "Automatically check for updates" (Settings > General > Software
    /// updates, PLAN.md 4.6). Default on: at most once a day Hearsay asks
    /// GitHub for the latest release.
    /// </summary>
    public bool AutomaticUpdateChecks
    {
        get => automaticUpdateChecks;
        set => Update(ref automaticUpdateChecks, value, () => file.SetBool(Key.AutomaticUpdateChecks, value));
    }

    /// <summary>
    /// When the last update check succeeded, or null when none has. Stored as
    /// an ISO 8601 UTC string with 100 ns precision.
    /// </summary>
    public DateTimeOffset? LastUpdateCheck
    {
        get => lastUpdateCheck;
        set => Update(ref lastUpdateCheck, value, () => file.SetString(
            Key.LastUpdateCheck, value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The code identity of the build that last saw Screen & System Audio
    /// Recording granted, or null. Kept for parity with the Mac's keys;
    /// Windows has no such permission, so the app does not use it.
    /// </summary>
    public string? ScreenAudioGrantedCodeHash
    {
        get => screenAudioGrantedCodeHash;
        set => Update(ref screenAudioGrantedCodeHash, value, () => file.SetString(Key.ScreenAudioGrantedCodeHash, value));
    }

    /// <summary>The Mac's stale-grant reset marker; kept for parity, unused on Windows.</summary>
    public string? ScreenAudioResetCodeHash
    {
        get => screenAudioResetCodeHash;
        set => Update(ref screenAudioResetCodeHash, value, () => file.SetString(Key.ScreenAudioResetCodeHash, value));
    }

    /// <summary>
    /// The code identity of the build that last saw Microphone access
    /// granted, or null. Kept for parity; Windows' microphone privacy switch
    /// is not tied to a build, so the app does not use it.
    /// </summary>
    public string? MicrophoneGrantedCodeHash
    {
        get => microphoneGrantedCodeHash;
        set => Update(ref microphoneGrantedCodeHash, value, () => file.SetString(Key.MicrophoneGrantedCodeHash, value));
    }

    /// <summary>
    /// The stored choice (a legacy "zh" migrated and persisted); otherwise
    /// the legacy code as a fixed language (persisted under the new key);
    /// otherwise Auto.
    /// </summary>
    private LanguageChoice LoadLanguageChoice()
    {
        var script = file.GetString(Key.ChineseScript);
        if (file.GetString(Key.LanguageChoice) is { } stored
            && LanguageChoice.FromStoredValue(stored, script) is { } choice)
        {
            if (choice.StorageValue != stored)
            {
                Persist(() => file.SetString(Key.LanguageChoice, choice.StorageValue));
            }
            return choice;
        }
        if (file.GetString(Key.DefaultLanguageCode) is { } legacy
            && TranscriptLanguages.FromStoredValue(legacy, script) is { } language)
        {
            var migrated = LanguageChoice.Fixed(language);
            Persist(() => file.SetString(Key.LanguageChoice, migrated.StorageValue));
            return migrated;
        }
        return LanguageChoice.Auto;
    }

    /// <summary>The stored preferred language (a legacy "zh" migrated and persisted); otherwise English.</summary>
    private TranscriptLanguage LoadPreferredLanguage()
    {
        if (file.GetString(Key.PreferredLanguage) is not { } stored
            || TranscriptLanguages.FromStoredValue(stored, file.GetString(Key.ChineseScript)) is not { } language)
        {
            return TranscriptLanguage.English;
        }
        if (language.Code() != stored)
        {
            Persist(() => file.SetString(Key.PreferredLanguage, language.Code()));
        }
        return language;
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    /// <summary>Sets the field, writes and notifies when the value changed; returns whether it did.</summary>
    private bool Update<T>(ref T field, T value, Action write, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Persist(write);
        OnPropertyChanged(property);
        return true;
    }

    private void Persist(Action write)
    {
        try
        {
            write();
        }
        catch (IOException error)
        {
            SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(error));
        }
        catch (UnauthorizedAccessException error)
        {
            SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(error));
        }
    }

    private void OnPropertyChanged(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>The error that kept a settings change from reaching settings.json.</summary>
public sealed class SettingsSaveFailedEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}
