using System.Globalization;

namespace Hearsay.App;

/// <summary>
/// Every user-facing string of the Windows app, in English, in one place so
/// W7 can move them to <c>.resw</c> files generated from shared/localization
/// (PLAN.md 18.3, "Localization"). Where the Mac has the same text, the
/// English value is the Mac's string-catalog key, so the four translations in
/// shared/localization can be reused; Windows-only strings are marked.
/// Never put the AI prompt, CLI arguments, file names, log lines, product
/// names or language autonyms here (AGENTS.md, Conventions).
/// </summary>
internal static class Strings
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    // Main window tabs (mac/Hearsay/Features/Main/MainView.swift).
    public const string TabRecord = "Record";
    public const string TabFile = "File";
    public const string TabModels = "Models";
    public const string TabHistory = "History";
    public const string TabSettings = "Settings";

    // Placeholders until W5 and W6 (Windows only, temporary).
    public const string PlaceholderRecord = "Recording, the live preview and the final pass. Coming in W5.";
    public const string PlaceholderFile = "Transcribing an audio or video file. Coming in W5.";
    public const string PlaceholderAI = "The meeting-notes provider. Coming in W6.";

    // Shared buttons.
    public const string Cancel = "Cancel";
    public const string OK = "OK";

    // Models tab (mac/Hearsay/Features/Models/ModelManagerView.swift, ModelRowView.swift).
    public const string ModelsStoredIn = "Models are stored in";
    public static string ModelsOnDisk(string size) => string.Format(Culture, "{0} on disk", size);
    /// <summary>The Mac's "Show in Finder" (Windows only).</summary>
    public const string ShowInExplorer = "Show in Explorer";
    public const string Recommended = "Recommended";
    public static string DownloadProgress(string received, string total) =>
        string.Format(Culture, "{0} of {1}", received, total);
    public static string ModelInUse(string size) => string.Format(Culture, "{0}, in use", size);
    public static string ModelInstalled(string size) => string.Format(Culture, "{0}, installed", size);
    public const string Download = "Download";
    public const string Retry = "Retry";
    public const string Use = "Use";
    public const string ActiveModel = "Active model";
    public const string Delete = "Delete";
    public const string CouldNotDeleteModel = "Could not delete the model";
    /// <summary>Windows only: the Mac deletes from the context menu without asking.</summary>
    public static string DeleteModelTitle(string displayName) => string.Format(Culture, "Delete {0}?", displayName);
    /// <summary>Windows only.</summary>
    public const string DeleteModelMessage = "Its files are removed from this PC. You can download it again at any time.";
    /// <summary>Byte counts under 1 KB (ByteCountFormatter's "bytes").</summary>
    public static string Bytes(long count) =>
        count == 1 ? string.Format(Culture, "{0} byte", count) : string.Format(Culture, "{0} bytes", count);

    // First-run model sheet (mac/Hearsay/Features/Models/OnboardingModelSheet.swift).
    public const string OnboardingTitle = "Download a speech model";
    /// <summary>The Mac's text with "on this Mac" as "on this PC" (Windows only).</summary>
    public const string OnboardingText =
        "Hearsay transcribes on this PC with a Whisper model that you download once. "
        + "Nothing is sent anywhere while transcribing. The recommended model gives the "
        + "best balance of accuracy and speed; smaller ones download faster but make "
        + "more mistakes.";
    public const string ChooseAnother = "Choose another…";
    public static string DownloadRecommended(string size) =>
        string.Format(Culture, "Download recommended model ({0})", size);

    // History tab (mac/Hearsay/Features/History/HistoryView.swift, HistoryViewModel.swift).
    public const string NoOutputFolder = "No output folder";
    /// <summary>The Mac's "Reveal in Finder" (Windows only).</summary>
    public const string RevealInExplorer = "Reveal in Explorer";
    public const string Refresh = "Refresh";
    public const string NoMeetingsYet = "No meetings yet";
    public const string NoMeetingsDescription =
        "Recordings, transcripts and meeting notes saved in the output folder appear here.";
    public const string Untitled = "Untitled";
    public const string BadgeNotes = "Notes";
    public const string BadgeTranscript = "Transcript";
    public const string BadgeAudio = "Audio";
    public static string BadgeSaved(string title) => string.Format(Culture, "{0} saved", title);
    public const string OpenNotesTooltip = "Open notes";
    public const string OpenTranscriptTooltip = "Open transcript";
    public const string OpenSrt = "Open SRT";
    public const string OpenNotes = "Open Notes";
    public const string OpenTranscript = "Open Transcript";
    public const string Rename = "Rename…";
    public const string GenerateNotes = "Generate Notes…";
    public const string RegenerateNotes = "Regenerate Notes…";
    /// <summary>Temporary (Windows only): notes arrive in W6.</summary>
    public const string ComingInW6 = "Coming in W6";
    /// <summary>The Mac's "Move to Trash…" (Windows only).</summary>
    public const string MoveToRecycleBin = "Move to Recycle Bin…";
    /// <summary>The Mac's "Move this meeting to the Trash?" (Windows only).</summary>
    public const string MoveToRecycleBinTitle = "Move this meeting to the Recycle Bin?";
    /// <summary>The Mac's "Move to Trash" (Windows only).</summary>
    public const string MoveToRecycleBinButton = "Move to Recycle Bin";
    /// <summary>The Mac's "Some files could not be moved to the Trash:" (Windows only).</summary>
    public const string RecycleFailures = "Some files could not be moved to the Recycle Bin:";
    public const string CouldNotRename = "Could not rename the meeting";
    public static string OutputFolderOpenFailed(string message) =>
        string.Format(Culture, "Could not open the output folder: {0}", message);
    public static string FolderReadFailed(string path, string message) =>
        string.Format(Culture, "Could not read {0}: {1}", path, message);
    public static string NoAppToOpen(string fileName) =>
        string.Format(Culture, "No app is available to open {0}.", fileName);
    /// <summary>Windows only: File Explorer could not be asked to show the files.</summary>
    public static string RevealFailed(string message) =>
        string.Format(Culture, "Could not show the files in File Explorer: {0}", message);

    // Naming sheet (mac/Hearsay/Features/Notes/NamingSheet.swift).
    public const string NamingTitleRename = "Rename meeting";
    public const string NamingTitleNew = "Name this meeting";
    public const string NamingTitleExisting = "Meeting name";
    public const string NamingExplainRename = "The transcript, notes, and recording are renamed to <timestamp>_<name>.";
    public const string NamingExplainCurrent = "This is the meeting's current name. Edit it or press Return to keep it.";
    public const string NamingExplainCurrentOrSuggestion =
        "This is the meeting's current name. Edit it, keep it, or use the AI's suggestion.";
    public const string NamingExplainManual = "The transcript and recording are renamed to <timestamp>_<name>.";
    public const string NamingExplainSuggestion = "The AI suggested this name. Edit it or press Return to accept it.";
    public const string NamingPlaceholder = "Meeting name (in English)";
    public static string NamingUseSuggestion(string suggestion) =>
        string.Format(Culture, "Use suggested name: {0}", suggestion);
    public const string NamingFileName = "File name:";
    public const string NamingRejected = "Enter an English meeting name using letters or digits.";
    /// <summary>The Mac's "Saving moves the current notes to the Trash." (Windows only).</summary>
    public const string NamingReplacesNotes = "Saving moves the current notes to the Recycle Bin.";
    public const string NamingRenameButton = "Rename";
    public const string NamingSaveButton = "Save";

    // Help (mac/Hearsay/Features/Help/HelpView.swift).
    public const string HelpTitle = "Hearsay Help";
    public const string HelpButton = "Help";
    public static string HelpMissing(string fileName) =>
        string.Format(Culture, "({0} is missing from the app bundle.)", fileName);

    // Settings sections (mac/Hearsay/Features/Settings/SettingsView.swift).
    public const string PaneGeneral = "General";
    public const string PaneWindow = "Window";
    public const string PaneOutput = "Output";
    public const string PaneAI = "AI";

    // Settings > General.
    public const string SectionInterface = "Interface";
    public const string InterfaceLanguage = "Interface language";
    public const string InterfaceLanguageCaption = "Menus and windows. A change applies after Hearsay restarts.";
    /// <summary>Windows only until W7 offers "Restart Now" as the Mac's alert does.</summary>
    public static string InterfaceLanguagePending(string autonym) =>
        string.Format(Culture, "Hearsay will use {0} after it restarts.", autonym);
    public const string SectionStartup = "Startup";
    public const string LaunchAtLogin = "Launch Hearsay at login";
    public static string LaunchAtLoginOnFailed(string message) =>
        string.Format(Culture, "Could not turn on launch at login: {0}", message);
    public static string LaunchAtLoginOffFailed(string message) =>
        string.Format(Culture, "Could not turn off launch at login: {0}", message);
    public const string SectionTranscription = "Transcription";
    public const string PreferredLanguage = "Auto mode default language";
    public const string PreferredLanguageCaption =
        "Used when Auto can't tell the language. When Auto hears Chinese, it writes 简体中文 if that is chosen here, otherwise 繁體中文.";
    public const string SectionShortcuts = "Shortcuts";
    public const string ShortcutStartStop = "Start / Stop recording:";
    public const string ShortcutPause = "Pause / Resume:";
    public const string ShortcutsCaption = "Work in any app, even with the window closed.";
    public const string ShortcutsReset = "Reset";
    public static string ShortcutTaken(string shortcut) =>
        string.Format(Culture, "{0} is already used by another app or shortcut.", shortcut);
    public static string ShortcutsUnavailable(int error) =>
        string.Format(Culture, "Global shortcuts are unavailable (error {0}).", error);

    // Settings > Window.
    public const string ShowHearsayIn = "Show Hearsay in:";
    public const string ChangesApplyImmediately = "Changes apply immediately.";
    /// <summary>Windows labels of the Mac's "Menu bar and Dock", "Menu bar only", "Dock only" (Windows only).</summary>
    public const string ModeTrayAndTaskbar = "Notification area and taskbar";
    public const string ModeTrayOnly = "Notification area only";
    public const string ModeTaskbarOnly = "Taskbar only";
    /// <summary>PLAN.md 18.3, "Tray status" (Windows only).</summary>
    public const string ShowTrayStatus = "Show recording status in the notification area";
    /// <summary>Windows only: the notification area cannot show text beside the icon.</summary>
    public const string ShowTrayStatusCaption =
        "While recording, the notification area icon turns red and its tooltip shows the elapsed time. When off, the icon stays the same.";

    // Settings > Output.
    public const string OutputFolder = "Output folder:";
    public const string ChooseFolder = "Choose…";
    public const string UseDefaultFolder = "Use Default";
    public static string OutputFolderCreateFailed(string message) =>
        string.Format(Culture, "Could not create the output folder: {0}", message);
    public static string OutputFolderRememberFailed(string message) =>
        string.Format(Culture, "Could not remember that folder: {0}", message);
    public const string KeepRecording = "Keep the recording (WAV) after a successful transcription";
    public const string KeepRecordingCaption =
        "When off, the WAV is deleted once its SRT is written. A failed transcription always keeps it.";

    // Settings file problems (PLAN.md 18.4, Settings; Windows only).
    public const string SettingsProblemTitle = "Settings";
    public static string SettingsCorrupt(string backupPath) =>
        string.Format(Culture, "Hearsay could not read its settings and started with the defaults. The old file was moved to {0}.", backupPath);
    public static string SettingsSaveFailed(string message) =>
        string.Format(Culture, "Could not save the settings: {0} The change holds until Hearsay quits.", message);

    // Notification-area menu (mac/Hearsay/Features/MenuBar/MenuBarView.swift).
    public const string StateIdle = "Idle";
    public static string StateRecording(string elapsed) => string.Format(Culture, "Recording {0}", elapsed);
    public static string StatePaused(string elapsed) => string.Format(Culture, "Paused {0}", elapsed);
    public const string Start = "Start";
    public const string Stop = "Stop";
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string OpenHearsay = "Open Hearsay";
    public const string QuitHearsay = "Quit Hearsay";
    /// <summary>Tooltip of the tray icon: "Hearsay" plus the state line (Windows only).</summary>
    public static string TrayTooltip(string state) => string.Format(Culture, "Hearsay: {0}", state);
}
