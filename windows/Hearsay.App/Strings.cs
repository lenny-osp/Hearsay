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

    // Settings > AI (mac/Hearsay/Features/Notes/AISettingsTab.swift).
    public const string AISectionProvider = "Provider";
    public const string AIPreset = "Preset:";
    public static string AICliPath(string shortName) => string.Format(Culture, "{0} CLI path:", shortName);
    public static string AICliNotFound(string binaryName) =>
        string.Format(Culture, "not found; enter the path to {0}", binaryName);
    public const string AIModel = "Model:";
    public const string AIReasoningEffort = "Reasoning effort:";
    public const string AICliDefault = "CLI default";
    public static string AICheckCli(string shortName) => string.Format(Culture, "Check {0}", shortName);
    public static string AICliFound(string version, string path) => string.Format(Culture, "{0} at {1}", version, path);
    /// <summary>The Mac's captions with "on this Mac" as "on this PC" (Windows only).</summary>
    public const string AICaptionCopilot =
        "Uses the Copilot CLI installed on this PC and its own login. Run `copilot` once in Terminal to log in. \"auto\" lets Copilot pick the model.";
    public const string AICaptionClaudeCode =
        "Uses Claude Code installed on this PC and your Claude subscription login; requests count against its usage limits. Run `claude` once in Terminal to log in. Model: an alias (sonnet, opus) or a full id. Effort: low, medium, high, xhigh, or max (none and minimal become low).";
    public const string AICaptionCodex =
        "Uses Codex installed on this PC and your ChatGPT login; requests count against your plan's usage limits. Run `codex login` once in Terminal to log in. Effort: none, minimal, low, medium, high, xhigh, or max. An empty model uses Codex's default.";
    public const string AICaptionAntigravity =
        "Uses the Antigravity CLI installed on this PC and the Google account it is logged in with; requests count against that account's limits. Run `agy` once in Terminal to log in; `agy models` lists the model ids. Effort: low, medium, high, or max, only for a model id without its own level (a model ending in -high, -medium, or -low ignores it). Runs use agy's hearsay-notes project, whose deny rules leave the model no tools except web search, and each run's conversation is deleted from agy's history afterwards.";
    /// <summary>Windows only: the Mac's captions have no install line.</summary>
    public static string AIInstallLine(string command) =>
        string.Format(Culture, "To install it, run `{0}` in a terminal.", command);
    public const string AIEndpointUrl = "Endpoint URL:";
    public const string AIOmittedWhenEmpty = "omitted when empty";
    public const string AITemperature = "Temperature:";
    public const string AITokenHeader = "Token header:";
    public const string AIExtraHeaders = "Extra headers:";
    public const string AIExtraHeadersPlaceholder = "Name: value, one per line";
    public const string AISectionToken = "Token";
    public const string AIApiToken = "API token:";
    public const string AITokenPlaceholder = "paste and press Return";
    public const string AITokenStatus = "Status:";
    public const string AITokenSaved = "Saved";
    public const string AITokenNotSet = "Not set";
    public const string AITokenRemove = "Remove";
    public const string AINoTokenNeeded = "This provider needs no token.";
    public static string AITokenSaveFailed(string reason) => string.Format(Culture, "Could not save the token: {0}", reason);
    public static string AITokenRemoveFailed(string reason) => string.Format(Culture, "Could not remove the token: {0}", reason);
    public const string AISectionSending = "Sending";
    public const string AIAskBeforeSending = "Ask before sending a transcript";
    public const string AITestConnection = "Test connection";
    public static string AIConnected(string reply) => string.Format(Culture, "Connected. Reply: {0}", reply);
    public const string AISectionTemplates = "Prompt templates";
    public const string AITemplateBuiltIn = "built-in";
    public const string AITemplateDefault = "Default";
    public const string AITemplateAdd = "Add";
    public const string AITemplateEdit = "Edit";
    public const string AITemplateSetDefault = "Set as Default";
    public const string AINewTemplateName = "New template";
    public const string TemplateEditorTitle = "Prompt template";
    public const string TemplateEditorName = "Name:";
    public const string TemplateEditorInstructions = "Instructions";
    public static string TemplateEditorCaption(string placeholder) => string.Format(Culture,
        "{0} is replaced by the notes language. The filename and JSON rules and the transcript are always appended.",
        placeholder);

    // Confirm sheet (mac/Hearsay/Features/Notes/ConfirmSendSheet.swift).
    public const string ConfirmTitle = "Send transcript for meeting notes?";
    public const string ConfirmProvider = "Provider:";
    public const string ConfirmModel = "Model:";
    public const string ConfirmTranscript = "Transcript:";
    public static string ConfirmCharacters(int count) => string.Format(Culture, "{0:N0} characters", count);
    public const string ConfirmFile = "File:";
    public const string ConfirmTemplate = "Template:";
    public const string ConfirmNotesLanguage = "Notes language:";
    /// <summary>The Mac's "…Nothing leaves this Mac otherwise." (Windows only).</summary>
    public static string ConfirmSendsTo(string provider) =>
        string.Format(Culture, "This sends the whole transcript to {0}. Nothing leaves this PC otherwise.", provider);
    /// <summary>The Mac's "…moved to the Trash…" (Windows only).</summary>
    public const string ConfirmReplacesNotes = "The current notes will be moved to the Recycle Bin when the new ones are saved.";
    public const string ConfirmAlwaysAsk = "Always ask before sending";
    public const string ConfirmKeepLocal = "Keep local";
    public const string ConfirmSend = "Send";

    // Notes flow (mac/Hearsay/Features/Notes/NotesFlowView.swift, NotesFlowViewModel.swift).
    public const string NotesWaiting = "Waiting for your answer…";
    public static string NotesGenerating(string provider, string model) =>
        string.Format(Culture, "Generating meeting notes via {0} ({1})…", provider, model);
    public const string NotesNotSaved = "Meeting notes were not saved";
    public static string NotesSrtKept(string path) => string.Format(Culture, "The SRT is kept at {0}", path);
    public const string NotesDone = "Done";
    public const string ManualNamingTitle = "Name this meeting yourself?";
    public const string ManualNamingText =
        "No meeting notes were generated. You can rename the SRT and its recording, or keep the timestamp names.";
    public static string ManualNamingTranscriptKept(string fileName) => string.Format(Culture, "Transcript kept: {0}", fileName);
    public static string ManualNamingRecordingKept(string fileName) => string.Format(Culture, "Recording kept: {0}", fileName);
    public const string ManualNamingNameIt = "Name It…";
    public const string ManualNamingKeep = "Keep Timestamp Names";
    public const string NotesCancelled = "Meeting-note generation was cancelled; no meeting notes were generated.";
    public const string NotesNoneGenerated = "No meeting notes were generated.";
    public const string NotesNothingSent = "Nothing was sent; the current notes are unchanged.";
    public const string NotesSkipped = "Skipped AI processing; no meeting notes were generated.";
    public const string NotesGenerated = "Meeting notes generated successfully!";
    public const string NotesSkippedRenamed = "Skipped AI processing; renamed the transcript and recording.";
    public const string NotesKeepingTimestampNames = "Keeping the timestamp file names.";
    public const string NotesNoNameRegenerate = "No meeting name selected; the current notes are unchanged.";
    public const string NotesNoNameRetained = "No meeting name selected; the SRT has been retained.";
    public const string NotesSkippedKept = "Skipped AI processing; kept the timestamp file names.";

    // Language picker and banner (mac/Hearsay/Features/Transcription/LanguageViews.swift).
    public const string LanguageLabel = "Language";
    /// <summary>The Auto segment (<c>LanguageChoice.shortLabel</c>); the language labels are never translated.</summary>
    public const string LanguageAuto = "Auto";
    public const string LanguagePickerTooltip =
        "Auto detects English, Chinese, German, or Spanish from the first speech. ZH-TW writes Traditional characters, ZH-CN Simplified.";
    /// <summary>The notice's English text; W7 localizes it by <c>LanguageNotice.MessageKey</c>.</summary>
    public static string LanguageNoticeMessage(Hearsay.Core.Transcription.LanguageNotice notice) =>
        notice?.Message ?? "";
    public const string TranscribeAgain = "Transcribe again";
    public const string Dismiss = "Dismiss";
    public static string TranscribeAgainTooltip(string language) =>
        string.Format(Culture, "Transcribe this recording again in {0}. Your language choice stays as it is.", language);
    public static string TranscribeAgainInTooltip(string language) =>
        string.Format(Culture, "Transcribe this recording again in {0}", language);

    // Record tab (mac/Hearsay/Features/Recording/RecordView.swift).
    public const string Microphone = "Microphone";
    public const string NoInputDevice = "No input device";
    public const string AlsoCaptureSystemAudio = "Also capture system audio";
    public const string InputLevel = "Input level";
    public const string MicMeter = "Mic";
    public const string SystemMeter = "System";
    public static string SourceLevel(string source) => string.Format(Culture, "{0} level", source);
    public const string Ready = "Ready";
    public const string Finalizing = "Finalizing…";
    public const string Saved = "Saved";
    public const string StartingState = "Starting…";
    public const string RecordingState = "Recording";
    public const string PausedState = "Paused";
    public const string Saving = "Saving…";
    public const string LivePreview = "Live preview";
    public const string DetectingLanguage = "Detecting language…";
    public static string ChunksWaiting(int count) => string.Format(Culture, "{0} chunks waiting", count);
    public const string ChunksWaitingTooltip = "The preview lags behind the recording but stays complete.";
    /// <summary>Windows only: the Mac shows a small spinner for the one chunk in progress.</summary>
    public const string TranscribingChunk = "Transcribing…";
    public const string FirstLinesAppear = "The first lines appear after about 10 to 30 s.";
    public const string Transcribing = "Transcribing";
    public const string UseLivePreviewInstead = "Use live preview instead";
    public const string UseLivePreviewTooltip = "Skip the full pass and save the live preview as the transcript";
    public const string SavingLivePreview = "Saving the live preview…";
    public const string OpenModels = "Open Models";
    public const string TryAgain = "Try Again";
    public const string TranscribeThisFile = "Transcribe this file";
    public const string Transcript = "Transcript";
    public const string RecordingLabel = "Recording";
    /// <summary>Windows only, temporary: the hook W6's notes flow sets is not connected.</summary>
    public const string GenerateNotesUnavailable = "Meeting notes are not available in this build yet.";
    /// <summary>Windows only: title of the alert when Explorer or the default app cannot be opened.</summary>
    public const string CouldNotOpen = "Could not open the file";
    public static string SilenceWarning(int seconds) =>
        string.Format(Culture, "Silent for {0}s — check the input device", seconds);
    public static string SystemAudioOff(string reason) => string.Format(Culture, "System audio off: {0}", reason);
    public static string LivePreviewOff(string reason) => string.Format(Culture, "Live preview off: {0}", reason);
    /// <summary>PLAN.md 18.4, "Speed" (Windows only).</summary>
    public const string LivePreviewTooSlow = "Live preview off: this computer is too slow for it";
    public static string LivePreviewMissedChunk(string reason) =>
        string.Format(Culture, "Live preview missed a chunk: {0}", reason);

    // Recording and transcription errors (mac/Hearsay/Features/Recording/RecordingController.swift).
    public const string TheInputDevice = "the input device";
    public static string CouldNotCreateRecording(string message) =>
        string.Format(Culture, "Could not create the recording file: {0}", message);
    public static string WritingRecordingFailed(string message) =>
        string.Format(Culture, "Writing the recording failed: {0}", message);
    public static string ClosingRecordingFailed(string message) =>
        string.Format(Culture, "Closing the recording failed: {0}", message);
    public const string NothingRecorded = "Nothing was recorded, so no file was kept.";
    public const string CancelledBecauseQuit = "Transcription was cancelled because Hearsay quit.";
    public const string LanguageNotDecided = "Transcription failed: the language could not be decided.";
    public static string TranscriptionFailed(string reason) => string.Format(Culture, "Transcription failed: {0}", reason);
    public static string CouldNotTranscribeAgain(string reason) =>
        string.Format(Culture, "Could not transcribe again: {0}", reason);
    public const string CouldNotTranscribeAgainNotKept = "Could not transcribe again: the recording was not kept.";
    public static string CouldNotTranscribeAgainMoved(string fileName) =>
        string.Format(Culture, "Could not transcribe again: {0} was moved or renamed.", fileName);
    public const string RecordingMissing = "The recording is missing.";
    public static string CouldNotWriteTranscript(string message) =>
        string.Format(Culture, "Could not write the transcript: {0}", message);
    public static string CouldNotMoveRecordingKeptAt(string message, string path) =>
        string.Format(Culture, "Could not move the recording to the output folder: {0} It is kept at {1}.", message, path);
    public static string CouldNotMoveRecording(string message) =>
        string.Format(Culture, "Could not move the recording to the output folder: {0}", message);
    public static string LivePreviewSavedAs(string path) => string.Format(Culture, "The live preview was saved as {0}.", path);
    public static string LivePreviewNotSaved(string message) =>
        string.Format(Culture, "The live preview could not be saved: {0}", message);
    public static string RecordingKeptAt(string path) => string.Format(Culture, "The recording is kept at {0}.", path);

    // Quit while recording (mac/Hearsay/AppDelegate.swift).
    public const string StopRecordingAndQuit = "Stop recording and quit?";
    public const string RecordingSavedBeforeQuit = "The recording is saved before Hearsay quits.";
    public const string StopAndQuit = "Stop & Quit";

    // File tab (mac/Hearsay/Features/FileTranscription/FileView.swift, FileViewModel.swift).
    public const string DropFileHere = "Drop an audio or video file here";
    /// <summary>The Mac's "wav, m4a, mp3, aac, aiff, caf, or …" with what Media Foundation reads (Windows only).</summary>
    public const string AcceptedFileTypes = "wav, m4a, mp3, aac, wma, flac, or the audio track of mp4 / mov";
    public const string ChooseFile = "Choose…";
    public static string ReadingFile(string fileName) => string.Format(Culture, "Reading {0}…", fileName);
    public static string DetectingLanguageOf(string fileName) =>
        string.Format(Culture, "Detecting the language of {0}…", fileName);
    public static string TranscribingFile(string fileName) => string.Format(Culture, "Transcribing {0}…", fileName);
    public const string CancelFileTooltip = "Stop at the next 30 s window; nothing is saved";
    public const string Cancelled = "Cancelled";
    public static string CouldNotTranscribeFile(string fileName, string reason) =>
        string.Format(Culture, "Could not transcribe {0}: {1}", fileName, reason);
    /// <summary>Windows only (the Mac's AVAudioFile reports its own error).</summary>
    public static string FileNotFound(string fileName) => string.Format(Culture, "{0} does not exist.", fileName);

    // Unfinished recording (mac/Hearsay/Features/Recovery/UnfinishedRecordingSheet.swift, MainView.swift).
    public const string UnfinishedRecording = "Unfinished recording";
    public static string UnfinishedRecordingMessage(string date, string duration) =>
        string.Format(Culture, "A recording from {0} was not finished ({1}). What do you want to do?", date, duration);
    public static string MoreAfterThisOne(int count) => string.Format(Culture, "{0} more after this one.", count);
    public const string AnUnknownTime = "an unknown time";
    public const string UnknownLength = "unknown length";
    public const string TranscribeButton = "Transcribe";
    public const string Keep = "Keep";
    public static string CouldNotKeepRecording(string message, string path) =>
        string.Format(Culture, "Could not keep the recording: {0} It stays at {1}.", message, path);
    public static string CouldNotDeleteRecording(string message) =>
        string.Format(Culture, "Could not delete the recording: {0}", message);
    public static string CouldNotRepairRecording(string message) =>
        string.Format(Culture, "Could not repair the recording: {0}", message);
    public const string CouldNotTranscribeRecording = "Could not transcribe the recording";
    public static string RecoveryStaysAt(string message, string path) =>
        string.Format(Culture, "{0} It stays at {1}.", message, path);

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
    public static string StateTranscribing(int percent) => string.Format(Culture, "Transcribing… {0}%", percent);
    public const string Start = "Start";
    public const string Stop = "Stop";
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string OpenHearsay = "Open Hearsay";
    public const string QuitHearsay = "Quit Hearsay";
    /// <summary>Tooltip of the tray icon: "Hearsay" plus the state line (Windows only).</summary>
    public static string TrayTooltip(string state) => string.Format(Culture, "Hearsay: {0}", state);
}
