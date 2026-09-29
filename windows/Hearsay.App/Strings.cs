using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hearsay.App.Features.Debug;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Hearsay.App;

/// <summary>
/// Every user-facing string of the Windows app, looked up in the
/// <c>.resw</c> files that windows/scripts/import-strings.py generates from
/// shared/localization (PLAN.md 18.3, "Localization"). Each member names its
/// key: <see cref="App(string)"/> for the Mac's app catalog,
/// <see cref="Core(string)"/> for its core catalog, and <see cref="Win(string)"/>
/// for the Windows-only entries (catalog "windows"). The key is the English
/// text, so the four translations of the Mac are reused; placeholders are
/// the Mac's (<c>%@</c>, <c>%lld</c>), the .resw values .NET format strings.
/// The script scans this file for those calls, so every key is a literal.
/// <para>
/// The language is fixed at launch (<see cref="Apply"/>). A missing resource
/// throws in debug runs and falls back to the English key otherwise.
/// Never put the AI prompt, CLI arguments, file names, log lines, product
/// names or language autonyms here (AGENTS.md, Conventions).
/// </para>
/// </summary>
internal static partial class Strings
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static ResourceMap? map;
    private static ResourceContext? context;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>
    /// Loads the app's resources in <paramref name="language"/> for this run
    /// (the Mac writes <c>AppleLanguages</c> at the same point). Call once,
    /// before any window is built.
    /// </summary>
    public static void Apply(InterfaceLanguage language)
    {
        Cache.Clear();
        try
        {
            var manager = new ResourceManager();
            var resources = manager.MainResourceMap.GetSubtree("Resources");
            var languageContext = manager.CreateResourceContext();
            languageContext.QualifierValues["Language"] = language.Code();
            map = resources;
            context = languageContext;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            map = null;
            context = null;
            AppLog.Write($"strings: cannot load the resources, English only: {error.Message}");
            if (ThrowsOnMissing) throw new InvalidOperationException("The app's string resources (Hearsay.pri) could not be loaded.", error);
        }
        try
        {
            // Built-in control text (text box context menus and the like).
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language.Code();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"strings: cannot set the primary language override: {error.Message}");
        }
    }

    /// <summary>Debug builds and debug runs (<c>HEARSAY_*</c>) fail on a missing string instead of showing English.</summary>
    private static bool ThrowsOnMissing =>
#if DEBUG
        true;
#else
        DebugEnvironment.IsDebugRun;
#endif

    private static string App(string key) => Lookup("app", key, required: true) ?? key;

    private static string App(string key, params object?[] args) => Format("app", key, args);

    private static string Core(string key) => Lookup("core", key, required: true) ?? key;

    private static string Core(string key, params object?[] args) => Format("core", key, args);

    private static string Win(string key) => Lookup("windows", key, required: true) ?? key;

    private static string Win(string key, params object?[] args) => Format("windows", key, args);

    private static string Format(string catalog, string key, object?[] args) =>
        string.Format(Culture, Lookup(catalog, key, required: true) ?? ToDotNetFormat(key), args);

    /// <summary>
    /// The resource name import-strings.py gives <paramref name="key"/>:
    /// the catalog, "_", and 16 hex digits of the key's SHA-256.
    /// </summary>
    internal static string ResourceName(string catalog, string key) =>
        catalog + "_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    private static string? Lookup(string catalog, string key, bool required)
    {
        var name = ResourceName(catalog, key);
        if (Cache.TryGetValue(name, out var cached)) return cached;
        string? value = null;
        if (map is { } resources && context is { } languageContext)
        {
            try
            {
                value = resources.TryGetValue(name, languageContext)?.ValueAsString;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                AppLog.Write($"strings: lookup of {catalog}:{key} failed: {error.Message}");
            }
        }
        if (value is null)
        {
            if (!required) return null;
            if (ThrowsOnMissing)
            {
                throw new InvalidOperationException(
                    $"No string resource for {catalog}:\"{key}\". Add it to shared/localization and run windows/scripts/import-strings.py.");
            }
            return null;
        }
        Cache[name] = value;
        return value;
    }

    /// <summary>The Mac's printf-style placeholders as a .NET format string (the English fallback), as import-strings.py converts them.</summary>
    private static string ToDotNetFormat(string text)
    {
        var builder = new StringBuilder();
        var counter = 0;
        var last = 0;
        foreach (Match match in FormatToken().Matches(text))
        {
            builder.Append(text.AsSpan(last, match.Index - last).ToString().Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
            if (match.Value == "%%")
            {
                builder.Append('%');
            }
            else if (match.Groups[1].Success)
            {
                builder.Append('{').Append(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1).Append('}');
            }
            else
            {
                builder.Append('{').Append(counter++).Append('}');
            }
            last = match.Index + match.Length;
        }
        builder.Append(text[last..].Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
        return builder.ToString();
    }

    [GeneratedRegex(@"%%|%(?:(\d+)\$)?[-+ #0']*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:hh|h|ll|l|q|z|t|j|L)?[@dDuUxXoOfFeEgGcCsSaAp]")]
    private static partial Regex FormatToken();

    /// <summary>
    /// The translation of a plain English text a Core type returns (a preset
    /// name, "No token"), when the Mac's core catalog has it; otherwise the
    /// text unchanged. Never throws for an unknown text.
    /// </summary>
    public static string CoreText(string english) => Lookup("core", english, required: false) ?? english;

    /// <summary>
    /// The user-facing text of an error (the Swift <c>describe</c>): the
    /// capture and Credential Manager errors by kind (Windows wording where
    /// it differs); then a Core error that carries its catalog key
    /// (<see cref="Hearsay.Core.ILocalizedError"/>), translated through the key and its
    /// arguments; then Core's plain English by exact text
    /// (<see cref="CoreText"/>); otherwise the message as it is.
    /// </summary>
    public static string Describe(Exception error) => error switch
    {
        MicrophoneRecorderException microphone => Describe(microphone),
        SystemAudioRecorderException system => Describe(system),
        SecretStoreException { ErrorCode: not 0 } secret => Win("Credential Manager error: %@",
            new Win32Exception(secret.ErrorCode).Message.TrimEnd().TrimEnd('.')),
        Hearsay.Core.ILocalizedError { LocalizedMessage: { } message } => Localize(message),
        null => throw new ArgumentNullException(nameof(error)),
        _ => CoreText(error.Message),
    };

    /// <summary>
    /// A Core text in the interface language: its key looked up in the app
    /// catalog for the few Core texts the Mac keeps in its app target, in the
    /// Windows catalog for Core's Windows-only texts, otherwise in the core
    /// catalog (then the Windows one); each nested message likewise, the
    /// English key when no translation is found.
    /// </summary>
    public static string Localize(Hearsay.Core.ILocalizedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        object Resolve(object value) => value is Hearsay.Core.ILocalizedMessage nested ? Localize(nested) : value;
        var arguments = message.MessageArguments.Select(Resolve).ToArray();
        var head = CoreMessageInAppCatalog(message.MessageKey, arguments)
            ?? CoreMessageInWindowsCatalog(message.MessageKey, arguments)
            ?? Hearsay.Core.LocalizedMessage.Format(new Hearsay.Core.LocalizedMessage(message.MessageKey, arguments, []),
                key => Lookup("core", key, required: false) ?? Lookup("windows", key, required: false), Culture);
        return head + string.Concat(message.MessageTail.Select(part => part.Separator + Resolve(part.Content)));
    }

    /// <summary>
    /// The Core texts whose Mac key is in the app catalog (WhisperEngine.swift,
    /// UpdatePackage.swift, NotesFlowViewModel.swift live in the Mac's app
    /// target), named here so import-strings.py puts them in the .resw; null
    /// for any other key.
    /// </summary>
    private static string? CoreMessageInAppCatalog(string key, object[] arguments) => key switch
    {
        "No model installed. Choose a model in Models." => App("No model installed. Choose a model in Models."),
        "The model %@ is not fully downloaded. Finish the download in Models." =>
            App("The model %@ is not fully downloaded. Finish the download in Models.", arguments),
        "The release's checksum list has no entry for %@." => App("The release's checksum list has no entry for %@.", arguments),
        "The checksum of %@ does not match the release's checksum list." =>
            App("The checksum of %@ does not match the release's checksum list.", arguments),
        "The code signature of the new version is not valid: %@" =>
            App("The code signature of the new version is not valid: %@", arguments),
        "The new app is version %@, not %@." => App("The new app is version %@, not %@.", arguments),
        "The download failed: %@" => App("The download failed: %@", arguments),
        "Error: SRT file not found or unreadable (%@): %@" => App("Error: SRT file not found or unreadable (%@): %@", arguments),
        _ => null,
    };

    /// <summary>
    /// Core's Windows-only texts (catalog "windows"; PLAN.md 18.4 and the
    /// former 18.9 list), named here so import-strings.py puts them in the
    /// .resw; null for any other key.
    /// </summary>
    private static string? CoreMessageInWindowsCatalog(string key, object[] arguments) => key switch
    {
        // Notes (CliProcess.cs, CliClient.cs).
        "%@ cannot take a transcript this long on Windows: the command line would be %@ characters, and Windows allows %@. Use Claude Code, Codex, or an HTTP provider for long meetings." =>
            Win("%@ cannot take a transcript this long on Windows: the command line would be %@ characters, and Windows allows %@. "
                + "Use Claude Code, Codex, or an HTTP provider for long meetings.", arguments),
        "%@. %@" => Win("%@. %@", arguments),
        // Updates (UpdateInstall.cs, UpdateTexts.cs).
        "Windows is running Hearsay straight from the zip file. Extract the zip to a folder you can write to, open Hearsay from there, then check for updates again." =>
            Win("Windows is running Hearsay straight from the zip file. Extract the zip to a folder you can write to, open Hearsay from there, then check for updates again."),
        "This copy of Hearsay is not in its own app folder, so it cannot update itself. Download the new version from the release page." =>
            Win("This copy of Hearsay is not in its own app folder, so it cannot update itself. Download the new version from the release page."),
        "Hearsay cannot write to the folder %@. Move the Hearsay folder to a folder you can write to, open it from there, then check for updates again." =>
            Win("Hearsay cannot write to the folder %@. Move the Hearsay folder to a folder you can write to, open it from there, then check for updates again.", arguments),
        "The zip file could not be extracted: %@" => Win("The zip file could not be extracted: %@", arguments),
        "The zip file should contain one Hearsay.exe but contains %lld." =>
            Win("The zip file should contain one Hearsay.exe but contains %lld.", arguments),
        "The new version is signed by %@, not by %@ like this copy of Hearsay." =>
            Win("The new version is signed by %@, not by %@ like this copy of Hearsay.", arguments),
        "The new app has the product name %@, not Hearsay." => Win("The new app has the product name %@, not Hearsay.", arguments),
        "The release has no Windows zip file or checksum list." => Win("The release has no Windows zip file or checksum list."),
        "The last update could not be installed. Hearsay is still the previous version." =>
            Win("The last update could not be installed. Hearsay is still the previous version."),
        // Whisper (WhisperModelLocation.cs).
        "Could not load the speech model: %@" => Win("Could not load the speech model: %@", arguments),
        // Values Core inserts into other messages (CommonMessages in LocalizedMessage.cs).
        "The request timed out." => Win("The request timed out."),
        "HTTP %lld" => Win("HTTP %lld", arguments),
        "unknown" => Win("unknown"),
        "none" => Win("none"),
        _ => null,
    };

    private static string Describe(MicrophoneRecorderException error) => error.Kind switch
    {
        MicrophoneRecorderErrorKind.PermissionDenied => Win(
            "Hearsay has no microphone access. Allow it in Settings > Privacy & security > Microphone (\"Let desktop apps access your microphone\")."),
        MicrophoneRecorderErrorKind.NoInputDevice => Core("No input device is available."),
        MicrophoneRecorderErrorKind.DeviceNotFound => Core("%@ is not available for recording. Reconnect it or choose another input.", error.Detail),
        MicrophoneRecorderErrorKind.EngineFailed => Core("Audio capture failed: %@", error.Detail),
        MicrophoneRecorderErrorKind.NoAudio => Core(
            "No audio from %@. Nothing arrived within %lld seconds, so the recording was stopped. Check that the device is connected and not muted, or choose another input.",
            error.Detail, (int)NoAudioWatchdog.Timeout),
        MicrophoneRecorderErrorKind.ConfigurationChanged => Core("The input device changed or was disconnected, so recording stopped."),
        _ => error.Message,
    };

    private static string Describe(SystemAudioRecorderException error) => error.Kind switch
    {
        SystemAudioRecorderErrorKind.NoOutputDevice => Win("No output device is available to capture system audio from."),
        SystemAudioRecorderErrorKind.StartFailed => Core("System audio capture could not start: %@", error.Detail),
        SystemAudioRecorderErrorKind.StreamStopped => Core("System audio capture stopped: %@", error.Detail),
        _ => error.Message,
    };

    // Main window tabs (mac/Hearsay/Features/Main/MainView.swift).
    public static string TabRecord => App("Record");
    public static string TabFile => App("File");
    public static string TabModels => App("Models");
    public static string TabHistory => App("History");
    public static string TabSettings => App("Settings");

    // Shared buttons.
    public static string Cancel => App("Cancel");
    public static string OK => App("OK");

    // Models tab (mac/Hearsay/Features/Models/ModelManagerView.swift, ModelRowView.swift).
    public static string ModelsStoredIn => App("Models are stored in");
    public static string ModelsOnDisk(string size) => App("%@ on disk", size);
    /// <summary>The Mac's "Show in Finder" (Windows only).</summary>
    public static string ShowInExplorer => Win("Show in Explorer");
    public static string Recommended => App("Recommended");
    public static string DownloadProgress(string received, string total) => App("%@ of %@", received, total);
    public static string ModelInUse(string size) => App("%@, in use", size);
    public static string ModelInstalled(string size) => App("%@, installed", size);
    public static string Download => App("Download");
    public static string Retry => App("Retry");
    public static string Use => App("Use");
    public static string ActiveModel => App("Active model");
    public static string Delete => App("Delete");
    public static string CouldNotDeleteModel => App("Could not delete the model");
    /// <summary>Windows only: the Mac deletes from the context menu without asking.</summary>
    public static string DeleteModelTitle(string displayName) => Win("Delete %@?", displayName);
    /// <summary>Windows only.</summary>
    public static string DeleteModelMessage => Win("Its files are removed from this PC. You can download it again at any time.");
    /// <summary>Byte counts under 1 KB (ByteCountFormatter's "bytes"; Windows only).</summary>
    public static string Bytes(long count) => count == 1 ? Win("%@ byte", count) : Win("%@ bytes", count);

    // First-run model sheet (mac/Hearsay/Features/Models/OnboardingModelSheet.swift).
    public static string OnboardingTitle => App("Download a speech model");
    /// <summary>The Mac's text with "on this Mac" as "on this PC" (Windows only).</summary>
    public static string OnboardingText => Win(
        "Hearsay transcribes on this PC with a Whisper model that you download once. "
        + "Nothing is sent anywhere while transcribing. The recommended model gives the "
        + "best balance of accuracy and speed; smaller ones download faster but make "
        + "more mistakes.");
    public static string ChooseAnother => App("Choose another…");
    public static string DownloadRecommended(string size) => App("Download recommended model (%@)", size);

    // History tab (mac/Hearsay/Features/History/HistoryView.swift, HistoryViewModel.swift).
    public static string NoOutputFolder => App("No output folder");
    /// <summary>The Mac's "Reveal in Finder" (Windows only).</summary>
    public static string RevealInExplorer => Win("Reveal in Explorer");
    public static string Refresh => App("Refresh");
    public static string NoMeetingsYet => App("No meetings yet");
    public static string NoMeetingsDescription =>
        App("Recordings, transcripts and meeting notes saved in the output folder appear here.");
    public static string Untitled => App("Untitled");
    public static string BadgeNotes => App("Notes");
    public static string BadgeTranscript => App("Transcript");
    public static string BadgeAudio => App("Audio");
    public static string BadgeSaved(string title) => App("%@ saved", title);
    public static string OpenNotesTooltip => App("Open notes");
    public static string OpenTranscriptTooltip => App("Open transcript");
    public static string OpenSrt => App("Open SRT");
    public static string OpenNotes => App("Open Notes");
    public static string OpenTranscript => App("Open Transcript");
    public static string Rename => App("Rename…");
    public static string GenerateNotes => App("Generate Notes…");
    public static string RegenerateNotes => App("Regenerate Notes…");
    /// <summary>The Mac's "Move to Trash…" (Windows only).</summary>
    public static string MoveToRecycleBin => Win("Move to Recycle Bin…");
    /// <summary>The Mac's "Move this meeting to the Trash?" (Windows only).</summary>
    public static string MoveToRecycleBinTitle => Win("Move this meeting to the Recycle Bin?");
    /// <summary>The Mac's "Move to Trash" (Windows only).</summary>
    public static string MoveToRecycleBinButton => Win("Move to Recycle Bin");
    /// <summary>The Mac's "Some files could not be moved to the Trash:" (Windows only).</summary>
    public static string RecycleFailures => Win("Some files could not be moved to the Recycle Bin:");
    public static string CouldNotRename => App("Could not rename the meeting");
    public static string OutputFolderOpenFailed(string message) => App("Could not open the output folder: %@", message);
    public static string FolderReadFailed(string path, string message) => App("Could not read %@: %@", path, message);
    public static string NoAppToOpen(string fileName) => App("No app is available to open %@.", fileName);
    /// <summary>Windows only: File Explorer could not be asked to show the files.</summary>
    public static string RevealFailed(string message) => Win("Could not show the files in File Explorer: %@", message);

    // Naming sheet (mac/Hearsay/Features/Notes/NamingSheet.swift).
    public static string NamingTitleRename => App("Rename meeting");
    public static string NamingTitleNew => App("Name this meeting");
    public static string NamingTitleExisting => App("Meeting name");
    public static string NamingExplainRename => App("The transcript, notes, and recording are renamed to <timestamp>_<name>.");
    public static string NamingExplainCurrent => App("This is the meeting's current name. Edit it or press Return to keep it.");
    public static string NamingExplainCurrentOrSuggestion =>
        App("This is the meeting's current name. Edit it, keep it, or use the AI's suggestion.");
    public static string NamingExplainManual => App("The transcript and recording are renamed to <timestamp>_<name>.");
    public static string NamingExplainSuggestion => App("The AI suggested this name. Edit it or press Return to accept it.");
    public static string NamingPlaceholder => App("Meeting name (in English)");
    public static string NamingUseSuggestion(string suggestion) => App("Use suggested name: %@", suggestion);
    public static string NamingFileName => App("File name:");
    public static string NamingRejected => App("Enter an English meeting name using letters or digits.");
    /// <summary>The Mac's "Saving moves the current notes to the Trash." (Windows only).</summary>
    public static string NamingReplacesNotes => Win("Saving moves the current notes to the Recycle Bin.");
    public static string NamingRenameButton => App("Rename");
    public static string NamingSaveButton => App("Save");

    // Settings > AI (mac/Hearsay/Features/Notes/AISettingsTab.swift).
    public static string AISectionProvider => App("Provider");
    public static string AIPreset => App("Preset:");
    public static string AICliPath(string shortName) => App("%@ CLI path:", shortName);
    public static string AICliNotFound(string binaryName) => App("not found; enter the path to %@", binaryName);
    public static string AIModel => App("Model:");
    public static string AIReasoningEffort => App("Reasoning effort:");
    public static string AICliDefault => App("CLI default");
    public static string AICheckCli(string shortName) => App("Check %@", shortName);
    public static string AICliFound(string version, string path) => App("%@ at %@", version, path);
    /// <summary>The Mac's captions with "on this Mac" as "on this PC" (Windows only).</summary>
    public static string AICaptionCopilot => Win(
        "Uses the Copilot CLI installed on this PC and its own login. Run `copilot` once in Terminal to log in. \"auto\" lets Copilot pick the model.");
    public static string AICaptionClaudeCode => Win(
        "Uses Claude Code installed on this PC and your Claude subscription login; requests count against its usage limits. Run `claude` once in Terminal to log in. Model: an alias (sonnet, opus) or a full id. Effort: low, medium, high, xhigh, or max (none and minimal become low).");
    public static string AICaptionCodex => Win(
        "Uses Codex installed on this PC and your ChatGPT login; requests count against your plan's usage limits. Run `codex login` once in Terminal to log in. Effort: none, minimal, low, medium, high, xhigh, or max. An empty model uses Codex's default.");
    public static string AICaptionAntigravity => Win(
        "Uses the Antigravity CLI installed on this PC and the Google account it is logged in with; requests count against that account's limits. Run `agy` once in Terminal to log in; `agy models` lists the model ids. Effort: low, medium, high, or max, only for a model id without its own level (a model ending in -high, -medium, or -low ignores it). Runs use agy's hearsay-notes project, whose deny rules leave the model no tools except web search, and each run's conversation is deleted from agy's history afterwards.");
    /// <summary>Windows only: the Mac's captions have no install line.</summary>
    public static string AIInstallLine(string command) => Win("To install it, run `%@` in a terminal.", command);
    public static string AIEndpointUrl => App("Endpoint URL:");
    public static string AIOmittedWhenEmpty => App("omitted when empty");
    public static string AITemperature => App("Temperature:");
    public static string AITokenHeader => App("Token header:");
    public static string AIExtraHeaders => App("Extra headers:");
    public static string AIExtraHeadersPlaceholder => App("Name: value, one per line");
    public static string AISectionToken => App("Token");
    public static string AIApiToken => App("API token:");
    public static string AITokenPlaceholder => App("paste and press Return");
    public static string AITokenStatus => App("Status:");
    public static string AITokenSaved => App("Saved");
    public static string AITokenNotSet => App("Not set");
    public static string AITokenRemove => App("Remove");
    public static string AINoTokenNeeded => App("This provider needs no token.");
    public static string AITokenSaveFailed(string reason) => App("Could not save the token: %@", reason);
    public static string AITokenRemoveFailed(string reason) => App("Could not remove the token: %@", reason);
    public static string AISectionSending => App("Sending");
    public static string AIAskBeforeSending => App("Ask before sending a transcript");
    public static string AITestConnection => App("Test connection");
    public static string AIConnected(string reply) => App("Connected. Reply: %@", reply);
    public static string AISectionTemplates => App("Prompt templates");
    public static string AITemplateBuiltIn => App("built-in");
    public static string AITemplateDefault => App("Default");
    public static string AITemplateAdd => App("Add");
    public static string AITemplateEdit => App("Edit");
    public static string AITemplateSetDefault => App("Set as Default");
    public static string AINewTemplateName => App("New template");
    public static string TemplateEditorTitle => App("Prompt template");
    public static string TemplateEditorName => App("Name:");
    public static string TemplateEditorInstructions => App("Instructions");
    public static string TemplateEditorCaption(string placeholder) =>
        App("%@ is replaced by the notes language. The filename and JSON rules and the transcript are always appended.", placeholder);

    // Confirm sheet (mac/Hearsay/Features/Notes/ConfirmSendSheet.swift).
    public static string ConfirmTitle => App("Send transcript for meeting notes?");
    public static string ConfirmProvider => App("Provider:");
    public static string ConfirmModel => App("Model:");
    public static string ConfirmTranscript => App("Transcript:");
    public static string ConfirmCharacters(int count) => App("%@ characters", count.ToString("N0", Culture));
    public static string ConfirmFile => App("File:");
    public static string ConfirmTemplate => App("Template:");
    public static string ConfirmNotesLanguage => App("Notes language:");
    /// <summary>The Mac's "…Nothing leaves this Mac otherwise." (Windows only).</summary>
    public static string ConfirmSendsTo(string provider) =>
        Win("This sends the whole transcript to %@. Nothing leaves this PC otherwise.", provider);
    /// <summary>The Mac's "…moved to the Trash…" (Windows only).</summary>
    public static string ConfirmReplacesNotes => Win("The current notes will be moved to the Recycle Bin when the new ones are saved.");
    public static string ConfirmAlwaysAsk => App("Always ask before sending");
    public static string ConfirmKeepLocal => App("Keep local");
    public static string ConfirmSend => App("Send");

    // Notes flow (mac/Hearsay/Features/Notes/NotesFlowView.swift, NotesFlowViewModel.swift).
    public static string NotesWaiting => App("Waiting for your answer…");
    public static string NotesGenerating(string provider, string model) => App("Generating meeting notes via %@ (%@)…", provider, model);
    public static string NotesNotSaved => App("Meeting notes were not saved");
    public static string NotesSrtKept(string path) => App("The SRT is kept at %@", path);
    public static string NotesDone => App("Done");
    public static string ManualNamingTitle => App("Name this meeting yourself?");
    public static string ManualNamingText =>
        App("No meeting notes were generated. You can rename the SRT and its recording, or keep the timestamp names.");
    public static string ManualNamingTranscriptKept(string fileName) => App("Transcript kept: %@", fileName);
    public static string ManualNamingRecordingKept(string fileName) => App("Recording kept: %@", fileName);
    public static string ManualNamingNameIt => App("Name It…");
    public static string ManualNamingKeep => App("Keep Timestamp Names");
    public static string NotesCancelled => App("Meeting-note generation was cancelled; no meeting notes were generated.");
    public static string NotesNoneGenerated => App("No meeting notes were generated.");
    public static string NotesNothingSent => App("Nothing was sent; the current notes are unchanged.");
    public static string NotesSkipped => App("Skipped AI processing; no meeting notes were generated.");
    public static string NotesGenerated => App("Meeting notes generated successfully!");
    public static string NotesSkippedRenamed => App("Skipped AI processing; renamed the transcript and recording.");
    public static string NotesKeepingTimestampNames => App("Keeping the timestamp file names.");
    public static string NotesNoNameRegenerate => App("No meeting name selected; the current notes are unchanged.");
    public static string NotesNoNameRetained => App("No meeting name selected; the SRT has been retained.");
    public static string NotesSkippedKept => App("Skipped AI processing; kept the timestamp file names.");

    // Language picker and banner (mac/Hearsay/Features/Transcription/LanguageViews.swift).
    public static string LanguageLabel => App("Language");
    /// <summary>The Auto segment (<c>LanguageChoice.shortLabel</c>); the language labels are never translated.</summary>
    public static string LanguageAuto => Core("Auto");
    public static string LanguagePickerTooltip =>
        App("Auto detects English, Chinese, German, or Spanish from the first speech. ZH-TW writes Traditional characters, ZH-CN Simplified.");
    /// <summary>The notice by its <see cref="LanguageNotice.MessageKey"/> (the Mac's core catalog); every placeholder is the language name.</summary>
    public static string LanguageNoticeMessage(LanguageNotice notice) => notice switch
    {
        LanguageNotice.Suggestion => Core("This sounds like %@. Transcribe again in %@?", notice.MessageArgument, notice.MessageArgument),
        LanguageNotice.Fallback => Core("Couldn't tell the language, so this was transcribed in %@ (your Auto mode default language).",
            notice.MessageArgument),
        null => "",
        _ => notice.Message,
    };
    /// <summary>
    /// The confirm sheet's "Transcript language: …" (<see cref="NotesLanguageCaption.TranscriptLine"/>),
    /// with Core's English <paramref name="note"/> translated by the key it was built from.
    /// </summary>
    public static string TranscriptLanguageLine(TranscriptLanguage language, string? note)
    {
        var name = language.DisplayName();
        string? Built(string key) => note == key.Replace("%@", name, StringComparison.Ordinal) ? name : null;
        var localizedNote = note is null ? name
            : Built(StoredTranscriptLanguage.DetectedNoteKey) is { } detected ? Core("%@ (detected from the text)", detected)
            : Built(StoredTranscriptLanguage.ChoiceNoteKey) is { } choice
                ? Core("%@ (your language choice; this transcript's language was not recorded)", choice)
            : Built(StoredTranscriptLanguage.AutoNoteKey) is { } preferred
                ? Core("%@ (your Auto mode default language; this transcript's language was not recorded)", preferred)
            : note;
        return Core("Transcript language: %@", localizedNote);
    }
    /// <summary>"Notes will be written in …." when the notes language differs (<see cref="NotesLanguageCaption.NotesLine"/>), else null.</summary>
    public static string? NotesLanguageLine(TranscriptLanguage transcript, TranscriptLanguage notes) =>
        notes == transcript ? null : Core("Notes will be written in %@.", notes.DisplayName());
    public static string TranscribeAgain => App("Transcribe again");
    public static string Dismiss => App("Dismiss");
    public static string TranscribeAgainTooltip(string language) =>
        App("Transcribe this recording again in %@. Your language choice stays as it is.", language);
    public static string TranscribeAgainInTooltip(string language) => App("Transcribe this recording again in %@", language);

    // Record tab (mac/Hearsay/Features/Recording/RecordView.swift).
    public static string Microphone => App("Microphone");
    public static string NoInputDevice => App("No input device");
    public static string AlsoCaptureSystemAudio => App("Also capture system audio");
    public static string InputLevel => App("Input level");
    public static string MicMeter => App("Mic");
    public static string SystemMeter => App("System");
    public static string SourceLevel(string source) => App("%@ level", source);
    public static string Ready => App("Ready");
    public static string Finalizing => App("Finalizing…");
    public static string Saved => App("Saved");
    public static string StartingState => App("Starting…");
    public static string RecordingState => App("Recording");
    public static string PausedState => App("Paused");
    public static string Saving => App("Saving…");
    public static string LivePreview => App("Live preview");
    public static string DetectingLanguage => App("Detecting language…");
    public static string ChunksWaiting(int count) => App("%lld chunks waiting", count);
    public static string ChunksWaitingTooltip => App("The preview lags behind the recording but stays complete.");
    /// <summary>Windows only: the Mac shows a small spinner for the one chunk in progress.</summary>
    public static string TranscribingChunk => Win("Transcribing…");
    public static string FirstLinesAppear => App("The first lines appear after about 10 to 30 s.");
    public static string Transcribing => App("Transcribing");
    public static string UseLivePreviewInstead => App("Use live preview instead");
    public static string UseLivePreviewTooltip => App("Skip the full pass and save the live preview as the transcript");
    public static string SavingLivePreview => App("Saving the live preview…");
    public static string OpenModels => App("Open Models");
    public static string TryAgain => App("Try Again");
    public static string TranscribeThisFile => App("Transcribe this file");
    public static string Transcript => App("Transcript");
    public static string RecordingLabel => App("Recording");
    /// <summary>Windows only: the tooltip of Generate Notes when no notes flow is connected.</summary>
    public static string GenerateNotesUnavailable => Win("Meeting notes are not available in this build yet.");
    /// <summary>Windows only: title of the alert when Explorer or the default app cannot be opened.</summary>
    public static string CouldNotOpen => Win("Could not open the file");
    public static string SilenceWarning(int seconds) => App("Silent for %llds — check the input device", seconds);
    public static string SystemAudioOff(string reason) => App("System audio off: %@", reason);
    public static string LivePreviewOff(string reason) => App("Live preview off: %@", reason);
    /// <summary>PLAN.md 18.4, "Speed" (Windows only).</summary>
    public static string LivePreviewTooSlow => Win("Live preview off: this computer is too slow for it");
    public static string LivePreviewMissedChunk(string reason) => App("Live preview missed a chunk: %@", reason);

    // Recording and transcription errors (mac/Hearsay/Features/Recording/RecordingController.swift).
    public static string TheInputDevice => App("the input device");
    public static string CouldNotCreateRecording(string message) => App("Could not create the recording file: %@", message);
    public static string WritingRecordingFailed(string message) => App("Writing the recording failed: %@", message);
    public static string ClosingRecordingFailed(string message) => App("Closing the recording failed: %@", message);
    public static string NothingRecorded => App("Nothing was recorded, so no file was kept.");
    public static string CancelledBecauseQuit => App("Transcription was cancelled because Hearsay quit.");
    public static string LanguageNotDecided => App("Transcription failed: the language could not be decided.");
    public static string TranscriptionFailed(string reason) => App("Transcription failed: %@", reason);
    public static string CouldNotTranscribeAgain(string reason) => App("Could not transcribe again: %@", reason);
    public static string CouldNotTranscribeAgainNotKept => App("Could not transcribe again: the recording was not kept.");
    public static string CouldNotTranscribeAgainMoved(string fileName) => App("Could not transcribe again: %@ was moved or renamed.", fileName);
    public static string RecordingMissing => App("The recording is missing.");
    public static string CouldNotWriteTranscript(string message) => App("Could not write the transcript: %@", message);
    public static string CouldNotMoveRecordingKeptAt(string message, string path) =>
        App("Could not move the recording to the output folder: %@ It is kept at %@.", message, path);
    public static string CouldNotMoveRecording(string message) => App("Could not move the recording to the output folder: %@", message);
    public static string LivePreviewSavedAs(string path) => App("The live preview was saved as %@.", path);
    public static string LivePreviewNotSaved(string message) => App("The live preview could not be saved: %@", message);
    public static string RecordingKeptAt(string path) => App("The recording is kept at %@.", path);

    // Quit while recording (mac/Hearsay/AppDelegate.swift).
    public static string StopRecordingAndQuit => App("Stop recording and quit?");
    public static string RecordingSavedBeforeQuit => App("The recording is saved before Hearsay quits.");
    public static string StopAndQuit => App("Stop & Quit");

    // File tab (mac/Hearsay/Features/FileTranscription/FileView.swift, FileViewModel.swift).
    public static string DropFileHere => App("Drop an audio or video file here");
    /// <summary>The Mac's "wav, m4a, mp3, aac, aiff, caf, or …" with what Media Foundation reads (Windows only).</summary>
    public static string AcceptedFileTypes => Win("wav, m4a, mp3, aac, wma, flac, or the audio track of mp4 / mov");
    public static string ChooseFile => App("Choose…");
    public static string ReadingFile(string fileName) => App("Reading %@…", fileName);
    public static string DetectingLanguageOf(string fileName) => App("Detecting the language of %@…", fileName);
    public static string TranscribingFile(string fileName) => App("Transcribing %@…", fileName);
    public static string CancelFileTooltip => App("Stop at the next 30 s window; nothing is saved");
    public static string Cancelled => App("Cancelled");
    public static string CouldNotTranscribeFile(string fileName, string reason) => App("Could not transcribe %@: %@", fileName, reason);
    /// <summary>Windows only (the Mac's AVAudioFile reports its own error).</summary>
    public static string FileNotFound(string fileName) => Win("%@ does not exist.", fileName);
    /// <summary>Windows only: a file decoded to a format the loader cannot use (technical detail).</summary>
    public static string UnexpectedDecodedFormat(string encoding, int bits, int channels) =>
        Win("Media Foundation returned %@, %@ bits, %@ channels.", encoding, bits, channels);

    // Unfinished recording (mac/Hearsay/Features/Recovery/UnfinishedRecordingSheet.swift, MainView.swift).
    public static string UnfinishedRecording => App("Unfinished recording");
    public static string UnfinishedRecordingMessage(string date, string duration) =>
        App("A recording from %@ was not finished (%@). What do you want to do?", date, duration);
    public static string MoreAfterThisOne(int count) => App("%lld more after this one.", count);
    public static string AnUnknownTime => App("an unknown time");
    public static string UnknownLength => App("unknown length");
    public static string TranscribeButton => App("Transcribe");
    public static string Keep => App("Keep");
    public static string CouldNotKeepRecording(string message, string path) =>
        App("Could not keep the recording: %@ It stays at %@.", message, path);
    public static string CouldNotDeleteRecording(string message) => App("Could not delete the recording: %@", message);
    public static string CouldNotRepairRecording(string message) => App("Could not repair the recording: %@", message);
    public static string CouldNotTranscribeRecording => App("Could not transcribe the recording");
    public static string RecoveryStaysAt(string message, string path) => App("%@ It stays at %@.", message, path);

    // Help (mac/Hearsay/Features/Help/HelpView.swift).
    public static string HelpTitle => App("Hearsay Help");
    /// <summary>Windows only: the Help item of the tab bar (the Mac has a Help menu).</summary>
    public static string HelpButton => Win("Help");
    public static string HelpMissing(string fileName) => App("(%@ is missing from the app bundle.)", fileName);

    // Settings sections (mac/Hearsay/Features/Settings/SettingsView.swift).
    public static string PaneGeneral => App("General");
    public static string PaneWindow => App("Window");
    public static string PaneOutput => App("Output");
    public static string PaneAI => App("AI");

    // Settings > General.
    public static string SectionInterface => App("Interface");
    public static string InterfaceLanguage => App("Interface language");
    public static string InterfaceLanguageCaption => App("Menus and windows. A change applies after Hearsay restarts.");
    /// <summary>Windows only: shown next to Restart Now (the Mac asks in an alert).</summary>
    public static string InterfaceLanguagePending(string autonym) => Win("Hearsay will use %@ after it restarts.", autonym);
    /// <summary>The Mac's language-change alert button.</summary>
    public static string RestartNow => App("Restart Now");
    public static string SectionStartup => App("Startup");
    public static string LaunchAtLogin => App("Launch Hearsay at login");
    public static string LaunchAtLoginOnFailed(string message) => App("Could not turn on launch at login: %@", message);
    public static string LaunchAtLoginOffFailed(string message) => App("Could not turn off launch at login: %@", message);
    public static string SectionTranscription => App("Transcription");
    public static string PreferredLanguage => App("Auto mode default language");
    public static string PreferredLanguageCaption =>
        App("Used when Auto can't tell the language. When Auto hears Chinese, it writes 简体中文 if that is chosen here, otherwise 繁體中文.");
    public static string SectionShortcuts => App("Shortcuts");
    public static string ShortcutStartStop => App("Start / Stop recording:");
    public static string ShortcutPause => App("Pause / Resume:");
    public static string ShortcutsCaption => App("Work in any app, even with the window closed.");
    public static string ShortcutsReset => App("Reset");
    public static string ShortcutTaken(string shortcut) => App("%@ is already used by another app or shortcut.", shortcut);
    public static string ShortcutsUnavailable(int error) => App("Global shortcuts are unavailable (error %d).", error);

    // Settings > Window.
    public static string ShowHearsayIn => App("Show Hearsay in:");
    public static string ChangesApplyImmediately => App("Changes apply immediately.");
    /// <summary>Windows labels of the Mac's "Menu bar and Dock", "Menu bar only", "Dock only" (Windows only).</summary>
    public static string ModeTrayAndTaskbar => Win("Notification area and taskbar");
    public static string ModeTrayOnly => Win("Notification area only");
    public static string ModeTaskbarOnly => Win("Taskbar only");
    /// <summary>PLAN.md 18.3, "Tray status" (Windows only).</summary>
    public static string ShowTrayStatus => Win("Show recording status in the notification area");
    /// <summary>Windows only: the notification area cannot show text beside the icon.</summary>
    public static string ShowTrayStatusCaption => Win(
        "While recording, the notification area icon turns red and its tooltip shows the elapsed time. When off, the icon stays the same.");

    // Settings > Output.
    public static string OutputFolder => App("Output folder:");
    public static string ChooseFolder => App("Choose…");
    public static string UseDefaultFolder => App("Use Default");
    public static string OutputFolderCreateFailed(string message) => App("Could not create the output folder: %@", message);
    public static string OutputFolderRememberFailed(string message) => App("Could not remember that folder: %@", message);
    public static string KeepRecording => App("Keep the recording (WAV) after a successful transcription");
    public static string KeepRecordingCaption =>
        App("When off, the WAV is deleted once its SRT is written. A failed transcription always keeps it.");

    // Settings file problems (PLAN.md 18.4, Settings; Windows only).
    public static string SettingsProblemTitle => App("Settings");
    public static string SettingsCorrupt(string backupPath) =>
        Win("Hearsay could not read its settings and started with the defaults. The old file was moved to %@.", backupPath);
    public static string SettingsSaveFailed(string message) =>
        Win("Could not save the settings: %@ The change holds until Hearsay quits.", message);

    // Notification-area menu (mac/Hearsay/Features/MenuBar/MenuBarView.swift).
    public static string StateIdle => App("Idle");
    public static string StateRecording(string elapsed) => App("Recording %@", elapsed);
    public static string StatePaused(string elapsed) => App("Paused %@", elapsed);
    public static string StateTranscribing(int percent) => App("Transcribing… %lld%%", percent);
    public static string Start => App("Start");
    public static string Stop => App("Stop");
    public static string Pause => App("Pause");
    public static string Resume => App("Resume");
    public static string OpenHearsay => App("Open Hearsay");
    public static string QuitHearsay => App("Quit Hearsay");
    /// <summary>Tooltip of the tray icon: "Hearsay" plus the state line (Windows only).</summary>
    public static string TrayTooltip(string state) => Win("Hearsay: %@", state);

    #region Polish (PLAN.md 18.9): app-side keys; appended here only, the rest of the file is edited elsewhere

    // Settings > General > Acknowledgements (mac/Hearsay/Features/Settings/AcknowledgementsView.swift).
    public static string SectionAcknowledgements => App("Acknowledgements");
    public static string AcknowledgementsText => App("Hearsay is MIT licensed and includes open-source software.");
    public static string ShowLicenses => App("Show Licenses…");

    /// <summary>Record tab, before the first recording on whisper.cpp's CPU runtime (PLAN.md 18.4, "Speed"; Windows only).</summary>
    public static string CpuFinalPassNotice =>
        Win("This computer transcribes on its processor (CPU): after you stop, the transcript takes about 1.5 to 3.5 times as long as the recording.");

    #endregion

    #region Hotkeys (PLAN.md 18.9, "Shortcut recorder"): appended here only

    // Shortcut names (Features/Hotkeys/HotkeyDisplay.cs): the modifiers as
    // Windows names them in its menus in each language (Windows only).
    public static string ModifierCtrl => Win("Ctrl");
    public static string ModifierAlt => Win("Alt");
    public static string ModifierShift => Win("Shift");
    public static string ModifierWin => Win("Win");
    public static string KeySpace => Core("Space");
    public static string KeyNumber(uint keyCode) => Core("Key %u", keyCode);

    // The recorder (mac/Hearsay/Features/Hotkeys/HotkeyRecorderView.swift).
    public static string RecorderListening => App("Type shortcut…");
    public static string RecorderTooltip => App("Click, then press a new shortcut.");
    public static string RecorderAccessibilityName => App("Shortcut");
    /// <summary>The Mac's "Press a combination with ⌃, ⌥, or ⌘. Escape cancels." with Windows' keys (Windows only).</summary>
    public static string RecorderListeningTooltip => Win("Press a combination with Ctrl, Alt, or Win. Esc cancels; Backspace restores the default.");
    /// <summary>The Mac's "Include ⌃, ⌥, or ⌘." with Windows' keys (Windows only).</summary>
    public static string ShortcutNeedsModifier => Win("Include Ctrl, Alt, or Win.");
    public static string ShortcutSameAsStartStop => Win("Already used for Start / Stop recording.");
    public static string ShortcutSameAsPause => Win("Already used for Pause / Resume.");
    /// <summary>A trial <c>RegisterHotKey</c> failed with ERROR_HOTKEY_ALREADY_REGISTERED (Windows only).</summary>
    public static string ShortcutUsedElsewhere(string shortcut) => Win("%@ is already used by another app or Windows.", shortcut);
    public static string ShortcutRejected(int error) => Win("Windows does not accept this shortcut (error %d).", error);

    #endregion
}
