using System.Globalization;

namespace Hearsay.Core.Updates;

/// <summary>
/// The update dialogs' texts for the App (the alerts in
/// mac/Hearsay/Features/Updates/UpdateService.swift and UpdateInstaller.swift),
/// in English. Where the Mac has the same text the value is its string
/// catalog key, so shared/localization can translate it; the Windows-only
/// ones are marked and listed in PLAN.md 18.4, "Updates and packaging". The
/// App may move these into its Strings class when it wires the dialogs.
/// </summary>
public static class UpdateTexts
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    // Update check (UpdateService.swift).
    public static string VersionLine(string version, string build) => string.Format(Culture, "Version {0} ({1})", version, build);
    public static string UpToDate(string version) => string.Format(Culture, "You're up to date ({0}).", version);
    public static string Available(string version) => string.Format(Culture, "Hearsay {0} is available.", version);
    public static string YouHaveVersion(string version) => string.Format(Culture, "You have version {0}.", version);
    public const string DownloadPageOpens = "The download page opens in your browser.";
    public const string CouldNotCheck = "Could not check for updates.";
    public const string NoRepository = "This build of Hearsay has no update repository set.";
    public const string CheckForUpdatesCommand = "Check for Updates…";
    public const string InstallUpdate = "Install Update";
    public const string ViewOnGitHub = "View on GitHub";
    public const string Download = "Download";
    public const string Later = "Later";
    public const string SoftwareUpdates = "Software updates";
    public const string AutomaticallyCheck = "Automatically check for updates";
    public const string AutomaticallyCheckCaption = "Checks GitHub once a day for a new release. Nothing else is sent.";
    public const string CheckNow = "Check Now";
    public static string LastChecked(string dateTime) => string.Format(Culture, "Last checked {0}", dateTime);
    public const string NotCheckedYet = "Not checked yet";

    // Install (UpdateInstaller.swift).
    public const string CannotInstallHere = "Hearsay cannot install the update here.";
    public const string CouldNotDownload = "Hearsay could not download the update.";
    public const string CouldNotVerify = "Hearsay could not verify the downloaded update.";
    public const string CouldNotInstall = "Hearsay could not install the update.";
    public const string SoftwareUpdateWindowTitle = "Software Update";
    public static string Downloading(string version) => string.Format(Culture, "Downloading Hearsay {0}…", version);
    public static string Verifying(string version) => string.Format(Culture, "Verifying Hearsay {0}…", version);
    public static string ReadyToInstall(string version) => string.Format(Culture, "Hearsay {0} is ready to install.", version);
    public static string Installing(string version) => string.Format(Culture, "Installing Hearsay {0}…", version);
    public const string WillQuitAndReopen = "Hearsay will quit and open again as the new version.";
    public const string InstallAndRelaunch = "Install and Relaunch";
    public const string FinishRecordingFirst = "Finish the recording first.";
    public static string BytesOf(string received, string total) => string.Format(Culture, "{0} of {1}", received, total);

    // The formatted texts above as their catalog keys (app catalog) and
    // values, for the app to translate.
    public static LocalizedMessage VersionLineMessage(string version, string build) => new("Version %@ (%@)", version, build);
    public static LocalizedMessage UpToDateMessage(string version) => new("You're up to date (%@).", version);
    public static LocalizedMessage AvailableMessage(string version) => new("Hearsay %@ is available.", version);
    public static LocalizedMessage YouHaveVersionMessage(string version) => new("You have version %@.", version);
    public static LocalizedMessage LastCheckedMessage(string dateTime) => new("Last checked %@", dateTime);
    public static LocalizedMessage DownloadingMessage(string version) => new("Downloading Hearsay %@…", version);
    public static LocalizedMessage VerifyingMessage(string version) => new("Verifying Hearsay %@…", version);
    public static LocalizedMessage ReadyToInstallMessage(string version) => new("Hearsay %@ is ready to install.", version);
    public static LocalizedMessage InstallingMessage(string version) => new("Installing Hearsay %@…", version);
    public static LocalizedMessage BytesOfMessage(string received, string total) => new("%@ of %@", received, total);

    /// <summary>Windows only: shown after a relaunch when the helper's log ends in a failure.</summary>
    public const string PreviousInstallFailed = "The last update could not be installed. Hearsay is still the previous version.";
}
