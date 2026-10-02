using System.Runtime.InteropServices.WindowsRuntime;
using Hearsay.App.Features.History;
using Hearsay.App.Features.MenuBar;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Models;
using Hearsay.App.Features.Notes;
using Hearsay.App.Features.Settings;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// Debug only. When Hearsay is launched with <c>HEARSAY_UI_SNAPSHOTS=&lt;dir&gt;</c>,
/// renders the app's own views (never a screen capture) into PNGs in
/// <c>&lt;dir&gt;</c>: every main-window tab, every tab again at 1400 wide, back
/// at the default width and at 600 (<c>NN-tab-wide|back|narrow.png</c>, to
/// check the page stays centered after resizes), every Settings section, the
/// settings warning banner with sample text, the naming and first-run
/// sheets, History's Rename sheet, its inline rejection and its error alert,
/// History after a rename, and the help page (top, the Meeting notes
/// section and the Windows-only notification area section; a page that
/// still shows Mac passages fails). The Record tab's queue states (64 to 67)
/// and "When I start them" (71 to 76: held rows with Transcribe All, mixed
/// with a released job, the single held meeting, Settings > General with the
/// third row, the held queue and the single held meeting narrow) are stubbed
/// in <see cref="RecordingSnapshots"/>. Then it quits with status 0 (1 when a
/// file could not be written or a check failed).
/// Port of mac/Hearsay/Features/Debug/UISnapshots.swift; the Mac's
/// permission sheets and menu bar panel do not exist on Windows. The update
/// states (Settings card, available and failure dialogs, progress window
/// downloading and ready) are rendered by <see cref="UpdateSnapshots"/>. The tray menu is a native Win32
/// popup menu, which XAML cannot render. A sheet (a <see cref="ContentDialog"/>)
/// is rendered alone, without the window behind it, as the Mac renders its
/// sheets.
/// <para>
/// The Models tab shows a stubbed mixed state (<see cref="ModelsSample"/>:
/// the recommended model installed and in use, Whisper Small downloading at
/// 40 %, the rest not installed); nothing is downloaded. The History tab
/// shows three sample meetings written into the sample output folder (SRT,
/// notes, transcript and WAV; SRT only; SRT and WAV), with the SRT text of
/// shared/fixtures/en-30s.expected.srt (copied next to the exe at build
/// time) and silent WAVs from <see cref="WavWriter"/>. The rename checks run
/// the real <c>OutputWriter.RenameEntry</c> on those samples: once with the
/// notes file held open (the error alert, everything rolled back), then for
/// real (the renamed row stays selected).
/// </para>
/// <para>
/// Runs in the interface language of <c>HEARSAY_UI_LANGUAGE</c> (default the
/// stored choice); the strings and the help page follow it.
/// Settings live in the throwaway folder <see cref="DebugEnvironment"/>
/// makes under the temp folder, the output folder is a sample folder there,
/// and launch at login is in memory, so <c>%APPDATA%\Hearsay</c>, the Run
/// key and <c>Documents\Hearsay</c> are never touched. At the end the tray
/// icon shows for about two seconds and the hotkeys are registered and
/// released once, as a smoke test (<see cref="CheckTrayAndHotkeysAsync"/>).
/// </para>
/// <code>
/// $env:HEARSAY_UI_SNAPSHOTS="$env:TEMP\hearsay-ui"; $env:HEARSAY_UI_LANGUAGE="de"
/// windows\Hearsay.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Hearsay.exe
/// </code>
/// </summary>
internal static class UISnapshots
{
    /// <summary>Returns false (and does nothing) when the variable is not set.</summary>
    public static bool RunIfRequested(AppShell shell)
    {
        if (!DebugEnvironment.Environment.TryGetValue(DebugEnvironment.SnapshotsVariable, out var directory)
            || directory.Length == 0)
        {
            return false;
        }
        _ = RunAndFinishAsync(shell, Path.GetFullPath(directory));
        return true;
    }

    private static async Task RunAndFinishAsync(AppShell shell, string directory)
    {
        var status = 1;
        try
        {
            status = await RunAsync(shell, directory).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            Say($"failed: {error}");
        }
        shell.FinishDebugRun(status);
    }

    private static async Task<int> RunAsync(AppShell shell, string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = shell.Settings;
        // A sample output folder beside the throwaway settings.
        var sampleOutput = Path.Combine(shell.SettingsFile.Folder, "output");
        Directory.CreateDirectory(sampleOutput);
        settings.OutputFolder = OutputLocation.MakeStoredPath(sampleOutput);
        settings.LanguageChoice = LanguageChoice.Auto;
        Say($"language {shell.RunningLanguage.Code()}, settings {shell.SettingsFile.FilePath}");

        var window = shell.MainWindow;
        window.ResizeClient(MainWindow.DefaultWidth, 560);
        window.Activate();
        await Settle().ConfigureAwait(true);

        var failed = false;
        void Check(bool good, string what)
        {
            Say($"check {(good ? "ok" : "FAILED")}: {what}");
            if (!good) failed = true;
        }
        async Task Render(string name, FrameworkElement element)
        {
            var path = Path.Combine(directory, $"{name}.png");
            if (await SnapshotAsync(element, path).ConfigureAwait(true))
            {
                Say($"wrote {path}");
            }
            else
            {
                Say($"could not write {path}");
                failed = true;
            }
        }

        // Sample data for the Models and History tabs. The (empty) models
        // folder is created when it lies in the throwaway settings folder, so
        // the header shows "Show in Explorer" beside the long path.
        if (Path.GetFullPath(shell.Models.RootPath).StartsWith(Path.GetFullPath(shell.SettingsFile.Folder), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(shell.Models.RootPath);
        }
        window.ModelsView.ShowSample(SampleModels(shell.Models.Catalog));
        var samples = WriteHistorySamples(sampleOutput);

        (string Name, MainTab Tab)[] tabs =
        [
            ("01-record", MainTab.Record), ("02-file", MainTab.File), ("03-models", MainTab.Models),
            ("04-history", MainTab.History), ("05-settings", MainTab.Settings),
        ];
        foreach (var (name, tab) in tabs)
        {
            shell.Tabs.Tab = tab;
            await Settle().ConfigureAwait(true);
            await Render(name, window.RenderRoot).ConfigureAwait(true);
        }

        // Page centering after resizes: every tab at a wide window, then back
        // at the default width (the tab views are cached, so a stale layout
        // would show here).
        foreach (var (label, width) in new[] { ("wide", 1400), ("back", MainWindow.DefaultWidth), ("narrow", 600) })
        {
            window.ResizeClient(width, 560);
            foreach (var (name, tab) in tabs)
            {
                shell.Tabs.Tab = tab;
                await Settle().ConfigureAwait(true);
                await Render($"{name}-{label}", window.RenderRoot).ConfigureAwait(true);
            }
        }

        // The Mac renders each section at 720 x 1000 so nothing is cut off.
        window.ResizeClient(MainWindow.DefaultWidth, 1000);
        shell.Tabs.Tab = MainTab.Settings;
        (string Name, SettingsPane Pane)[] panes =
        [
            ("06-settings-general", SettingsPane.General), ("07-settings-window", SettingsPane.Window),
            ("08-settings-output", SettingsPane.Output), ("09-settings-ai", SettingsPane.AI),
        ];
        foreach (var (name, pane) in panes)
        {
            window.SettingsView.Show(pane);
            await Settle().ConfigureAwait(true);
            await Render(name, window.RenderRoot).ConfigureAwait(true);
        }

        #region Settings > General > Shortcuts recorder (Features/Hotkeys/HotkeyRecorderView.cs)
        // The Start / Stop recorder listening, with the reason it refused
        // Shift+A. The hotkeys are not started yet, so nothing is suspended
        // or probed.
        window.SettingsView.Show(SettingsPane.General);
        await Settle().ConfigureAwait(true);
        if (Descendant<GeneralSettingsView>(window.RenderRoot) is { } general)
        {
            var recorder = general.ShowRecordingSample();
            await Settle().ConfigureAwait(true);
            Check(recorder.IsListening && recorder.Hint == Strings.ShortcutNeedsModifier,
                $"the recorder listens and refuses Shift+A (\"{recorder.Hint}\")");
            await Render("58-settings-general-shortcut-recording", window.RenderRoot).ConfigureAwait(true);
            general.EndRecordingSample();
            Check(!recorder.IsListening && settings.StartStopHotkey == HotkeyBinding.DefaultStartStop,
                "Escape stops the recorder and keeps the binding");

            // The Stop & Start Next recorder given the Start / Stop chord: refused, with the reason.
            var conflict = general.ShowConflictSample();
            await Settle().ConfigureAwait(true);
            Check(conflict.IsListening && conflict.Hint == Strings.ShortcutSameAsStartStop,
                $"the recorder refuses the Start / Stop chord for Stop & Start Next (\"{conflict.Hint}\")");
            await Render("68-settings-general-shortcut-conflict", window.RenderRoot).ConfigureAwait(true);
            general.EndConflictSample();
            Check(!conflict.IsListening && settings.StopStartNextHotkey == HotkeyBinding.DefaultStopStartNext,
                "Escape stops the recorder and keeps the Stop & Start Next binding");

            // Settings > General at a narrow window, so the new rows show how they wrap.
            window.ResizeClient(480, 1300);
            await Settle().ConfigureAwait(true);
            await Render("69-settings-general-narrow", window.RenderRoot).ConfigureAwait(true);
            window.ResizeClient(MainWindow.DefaultWidth, 1000);
            await Settle().ConfigureAwait(true);

            #region Settings > General > Software updates and the update windows (Features/Debug/UpdateSnapshots.cs)
            // Stubbed states: nothing is checked, downloaded or installed.
            await UpdateSnapshots.RunAsync(shell, general, sampleOutput,
                new RecordingSnapshots.Tools(Render, Check, Settle, DialogBox)).ConfigureAwait(true);
            #endregion
        }
        else
        {
            Check(false, "the General settings section is shown");
        }
        #endregion

        // The whole model list, so the installed and active row shows too.
        shell.Tabs.Tab = MainTab.Models;
        await Settle().ConfigureAwait(true);
        await Render("12-models-full", window.RenderRoot).ConfigureAwait(true);
        shell.Tabs.Tab = MainTab.Settings;

        // Taskbar-only mode: the status toggle is disabled.
        settings.WindowMode = WindowMode.DockOnly;
        window.SettingsView.Show(SettingsPane.Window);
        await Settle().ConfigureAwait(true);
        await Render("10-settings-window-taskbar-only", window.RenderRoot).ConfigureAwait(true);
        settings.WindowMode = WindowMode.MenuBarAndDock;

        // The naming sheet's modes (W6 reuses it) and the first-run sheet.
        window.ResizeClient(MainWindow.DefaultWidth, 560);
        shell.Tabs.Tab = MainTab.Models;
        await Settle().ConfigureAwait(true);
        var root = window.RenderRoot.XamlRoot;
        (string Name, NamingSheet Sheet)[] naming =
        [
            ("13-sheet-naming", new NamingSheet(root, "quarterly-planning")),
            ("14-sheet-naming-manual", new NamingSheet(root, null)),
            ("15-sheet-naming-regenerate", new NamingSheet(root, "genhe-road-trip", "trip-to-genhe", replacesNotes: true)),
        ];
        foreach (var (name, sheet) in naming)
        {
            var asking = sheet.AskAsync();
            await Settle().ConfigureAwait(true);
            await Render(name, DialogBox(sheet)).ConfigureAwait(true);
            sheet.Hide();
            await asking.ConfigureAwait(true);
        }
        var onboarding = window.ModelsView.OfferOnboardingAsync(startsDownload: false);
        await Settle().ConfigureAwait(true);
        if (window.ModelsView.Onboarding is { } offer)
        {
            await Render("16-sheet-onboarding", DialogBox(offer)).ConfigureAwait(true);
            offer.Hide();
        }
        else
        {
            Check(false, "the first-run sheet opened");
        }
        await onboarding.ConfigureAwait(true);
        Check(shell.Models.Catalog.Entries.All(entry => shell.Models.State(entry) is DownloadState.NotInstalledState),
            "no download was started");

        // History > Rename: the sheet, its inline rejection, the error alert, the result.
        shell.Tabs.Tab = MainTab.History;
        await Settle().ConfigureAwait(true);
        var history = window.HistoryView;
        Check(history.Model.Entries.Select(entry => entry.Stem).SequenceEqual(samples),
            $"History lists the samples newest first ({string.Join(", ", history.Model.Entries.Select(entry => entry.Stem))})");
        if (history.Model.Entries.FirstOrDefault(entry => entry.Stem == samples[0]) is { } meeting)
        {
            var renaming = history.RenameAsync(meeting);
            await Settle().ConfigureAwait(true);
            if (history.PendingRename is { } sheet)
            {
                Check(sheet.MeetingName == "history-of-coffee-origins", $"the sheet is prefilled ({sheet.MeetingName})");
                await Render("26-sheet-rename", DialogBox(sheet)).ConfigureAwait(true);
                sheet.MeetingName = "???";
                await Settle().ConfigureAwait(true);
                Check(!sheet.IsPrimaryButtonEnabled, "Rename is disabled for a name with no usable characters");
                await Render("27-sheet-rename-rejected", DialogBox(sheet)).ConfigureAwait(true);
                sheet.Hide();
            }
            else
            {
                Check(false, "the Rename sheet opened");
            }
            await renaming.ConfigureAwait(true);

            // A file held open cannot be moved: the alert, and nothing renamed.
            using (File.Open(meeting.Notes ?? "", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failing = history.RenameAsync(meeting, "coffee-origins-review");
                await Settle().ConfigureAwait(true);
                if (Alert.Current is { } alert)
                {
                    await Render("28-alert-rename-error", DialogBox(alert)).ConfigureAwait(true);
                    alert.Hide();
                }
                else
                {
                    Check(false, "the rename error alert opened");
                }
                await failing.ConfigureAwait(true);
            }
            Check(meeting.Files.All(File.Exists), "a failed rename keeps every file under its old name");

            await history.RenameAsync(meeting, "coffee-origins-review").ConfigureAwait(true);
            const string renamed = "2026-09-28_14-30-00_coffee-origins-review";
            Check(history.Model.Selection == renamed && history.Model.Entries.Any(entry => entry.Stem == renamed
                && entry.Srt is not null && entry.Notes is not null && entry.Transcript is not null && entry.Audio is not null),
                $"the renamed entry has all four files and stays selected ({history.Model.Selection})");
            var notes = await File.ReadAllTextAsync(Path.Combine(sampleOutput, renamed + ".md")).ConfigureAwait(true);
            Check(notes.Contains("**Meeting Name:** coffee-origins-review", StringComparison.Ordinal),
                "the notes' Meeting Name line was rewritten");
            await Settle().ConfigureAwait(true);
            await Render("29-history-renamed", window.RenderRoot).ConfigureAwait(true);
        }
        else
        {
            Check(false, "the sample meeting is listed");
        }

        #region W6 notes (Features/Notes/NotesSnapshots.cs)
        // Settings > AI per preset, the template editor, the confirm and
        // naming sheets, the notes flow while generating, its result and an
        // error; a stub generator, so no provider is ever called.
        await NotesSnapshots.RunAsync(shell, sampleOutput,
            new NotesSnapshots.Tools(Render, Check, Settle, DialogBox)).ConfigureAwait(true);
        #endregion

        #region W5 recording (Features/Debug/RecordingSnapshots.cs)
        // The Record tab in each state, the File tab, the recovery sheet; all
        // stubbed, nothing is recorded or transcribed.
        await RecordingSnapshots.RunAsync(shell, sampleOutput,
            new RecordingSnapshots.Tools(Render, Check, Settle, DialogBox)).ConfigureAwait(true);
        #endregion

        // The settings warning (PLAN.md 18.4), with a sample backup path.
        window.ResizeClient(MainWindow.DefaultWidth, 560);
        shell.Tabs.Tab = MainTab.Record;
        window.ShowSettingsProblem(Strings.SettingsCorrupt(Path.Combine(shell.SettingsFile.Folder, SettingsFile.CorruptFileName)));
        await Settle().ConfigureAwait(true);
        await Render("11-settings-problem", window.RenderRoot).ConfigureAwait(true);

        var help = shell.ShowHelp();
        Say($"help file {help.HelpFile}{(File.Exists(help.HelpFile) ? "" : " (missing)")}");
        foreach (var (name, fragment) in new[] { ("19-help-top", (string?)null), ("20-help-meeting-notes", "meeting-notes"), ("22-help-menu-bar", "menu-bar"), ("70-help-recording", "recording") })
        {
            var path = Path.Combine(directory, $"{name}.png");
            if (await help.CaptureAsync(fragment, path).ConfigureAwait(true))
            {
                Say($"wrote {path}");
            }
            else
            {
                Say($"could not write {path}");
                failed = true;
            }
        }

        // Settings > General > Acknowledgements > Show Licenses…: the licenses page.
        var licenses = shell.ShowLicenses();
        var licensesPng = Path.Combine(directory, "57-licenses.png");
        var licensesText = File.Exists(licenses.HelpFile) ? File.ReadAllText(licenses.HelpFile) : "";
        Check(licensesText.Contains("MIT License", StringComparison.Ordinal) && licensesText.Contains("Whisper.net", StringComparison.Ordinal),
            $"the licenses page {licenses.HelpFile} holds the LICENSE and the notices");
        if (await licenses.CaptureAsync(null, licensesPng).ConfigureAwait(true))
        {
            Say($"wrote {licensesPng}");
        }
        else
        {
            Say($"could not write {licensesPng}");
            failed = true;
        }
        licenses.Close();

        // A hearsay://open link in the help page switches the main window.
        shell.Tabs.Tab = MainTab.Record;
        await help.FollowLinkAsync("hearsay://open/settings-output").ConfigureAwait(true);
        var switched = shell.Tabs.Tab == MainTab.Settings && window.SettingsView.Pane == SettingsPane.Output;
        Say($"help link hearsay://open/settings-output -> tab {shell.Tabs.Tab}, section {window.SettingsView.Pane}");
        if (!switched) failed = true;
        await Settle().ConfigureAwait(true);
        await Render("21-help-link-settings-output", window.RenderRoot).ConfigureAwait(true);

        if (!await CheckTrayAndHotkeysAsync(shell).ConfigureAwait(true)) failed = true;
        return failed ? 1 : 0;
    }

    /// <summary>
    /// A smoke test of the tray icon and the hotkeys, which XAML cannot
    /// render: shows the icon for about two seconds, stubs the recording,
    /// paused, transcribing and idle states and checks the icon and tooltip
    /// of each, then registers and unregisters the global shortcuts
    /// once (a shortcut another app holds is reported, not a failure); while
    /// they are registered, the recorder's trial registration of the Start /
    /// Stop chord must report it taken (1409).
    /// </summary>
    private static async Task<bool> CheckTrayAndHotkeysAsync(AppShell shell)
    {
        var ok = true;
        var tray = shell.Tray;
        var recording = shell.Recording;
        tray.SetVisible(true);
        void Expect(string step, string icon, string? tooltip = null)
        {
            var good = tray.IsVisible && tray.CurrentIconName == icon && (tooltip is null || tray.CurrentTooltip == tooltip);
            ok &= good;
            Say($"tray {step}: icon {tray.CurrentIconName}, tooltip \"{tray.CurrentTooltip}\"{(good ? "" : $" (expected {icon}, \"{tooltip}\")")}");
        }
        Expect("idle", "Hearsay.ico");
        // Stubbed states: the real Start would open the microphone (W5).
        recording.Update(RecordingPhase.Recording, TimeSpan.FromSeconds(754), null);
        await Task.Delay(500).ConfigureAwait(true);
        Expect("recording", "Hearsay-recording.ico");
        recording.Update(RecordingPhase.Paused, TimeSpan.FromSeconds(754), null);
        await Task.Delay(500).ConfigureAwait(true);
        Expect("paused", "Hearsay-paused.ico");
        shell.Settings.MenuBarShowsStatus = false;
        Expect("paused, status off", "Hearsay.ico");
        shell.Settings.MenuBarShowsStatus = true;
        recording.Update(RecordingPhase.Transcribing, TimeSpan.FromSeconds(754), 0.42);
        await Task.Delay(500).ConfigureAwait(true);
        Expect("transcribing", "Hearsay.ico", Strings.TrayTooltip(Strings.StateTranscribing(42)));
        ok &= recording.StateText == Strings.StateTranscribing(42);
        Say($"tray transcribing state line \"{recording.StateText}\"");
        shell.Settings.MenuBarShowsStatus = false;
        Expect("transcribing, status off", "Hearsay.ico", "Hearsay");
        shell.Settings.MenuBarShowsStatus = true;
        recording.Update(RecordingPhase.Idle, TimeSpan.Zero, null);
        await Task.Delay(500).ConfigureAwait(true);
        Expect("stopped", "Hearsay.ico", "Hearsay");
        // The queue line and Stop & Start Next of the menu (a native popup, so only their inputs can be checked).
        recording.Update(RecordingPhase.Recording, TimeSpan.FromSeconds(754), null);
        recording.SetQueue(2, 0.45, false);
        await Task.Delay(300).ConfigureAwait(true);
        var lineOk = recording.QueueLine == Strings.QueueLineTranscribing(45, 2) && recording.CanStopAndStartNext;
        ok &= lineOk;
        Say($"tray recording with a queue: \"{recording.QueueLine}\", Stop & Start Next {(recording.CanStopAndStartNext ? "enabled" : "disabled")}{(lineOk ? "" : " (unexpected)")}");
        // "When I start them": every job held reads "Not transcribed yet", and Transcribe All is enabled.
        recording.SetQueue(2, null, false, allHeld: true, heldCount: 2);
        await Task.Delay(300).ConfigureAwait(true);
        var heldOk = recording.QueueLine == Strings.QueueLineHeld(2) && recording.CanTranscribeAll;
        ok &= heldOk;
        Say($"tray with held jobs: \"{recording.QueueLine}\", Transcribe All {(recording.CanTranscribeAll ? "enabled" : "disabled")}{(heldOk ? "" : " (unexpected)")}");
        recording.Update(RecordingPhase.Idle, TimeSpan.Zero, null);
        recording.SetQueue(0, null, false);
        await Task.Delay(300).ConfigureAwait(true);
        ok &= !recording.CanStopAndStartNext && recording.QueueLine is null && !recording.CanTranscribeAll;
        tray.SetVisible(false);
        ok &= !tray.IsVisible;

        shell.Hotkeys.Start();
        Say($"hotkeys: {shell.Hotkeys.RegistrationError ?? "all three registered"}");
        if (shell.Hotkeys.RegistrationError is null)
        {
            // The recorder's check: a chord Hearsay itself holds reports as
            // taken, which is how another app's or the shell's chord reports.
            var probe = shell.Hotkeys.Probe(shell.Settings.StartStopHotkey);
            var good = probe == Interop.NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED;
            ok &= good;
            Say($"hotkeys: probe of the registered {shell.Settings.StartStopHotkey.DisplayString} returned {probe}{(good ? "" : " (expected 1409)")}");
        }
        shell.Hotkeys.Dispose();
        return ok;
    }

    /// <summary>The first element of type <typeparamref name="T"/> under <paramref name="root"/> in the visual tree.</summary>
    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>The recommended model installed and in use, Whisper Small at 40 %, the rest not installed.</summary>
    private static ModelsSample SampleModels(ModelCatalog catalog)
    {
        var states = new Dictionary<string, DownloadState>(StringComparer.Ordinal);
        long total = 0;
        string? active = null;
        if (catalog.Recommended is { } recommended)
        {
            states[recommended.Id] = DownloadState.Installed;
            active = recommended.Id;
            total += recommended.SizeBytes;
        }
        if (catalog.Entries.FirstOrDefault(entry => entry.Family == "small") is { } small)
        {
            var received = small.SizeBytes * 4 / 10;
            states[small.Id] = new DownloadState.Downloading(new DownloadProgress(received, small.SizeBytes, small.WeightsFile));
            total += received;
        }
        return new ModelsSample(states, active, total);
    }

    /// <summary>
    /// Three meetings in <paramref name="folder"/>, returned newest first:
    /// SRT, notes, transcript and a 30 s WAV; SRT only (no name); SRT and a
    /// 12 s WAV.
    /// </summary>
    private static string[] WriteHistorySamples(string folder)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "DebugSamples", "en-30s.expected.srt");
        var srt = File.Exists(fixture) ? File.ReadAllText(fixture) : "1\n00:00:00,000 --> 00:00:02,000\nSample\n";
        string[] stems = ["2026-09-28_14-30-00_history-of-coffee-origins", "2026-09-27_09-15-00", "2026-09-25_16-45-12_weekly-sync"];
        foreach (var stem in stems) File.WriteAllText(Path.Combine(folder, stem + ".srt"), srt);
        File.WriteAllText(Path.Combine(folder, stems[0] + ".md"),
            "# Meeting Notes\n\n**Meeting Name:** history-of-coffee-origins\n\n## Summary\n\nA sample for the UI snapshots.\n");
        File.WriteAllText(Path.Combine(folder, stems[0] + "_transcript.md"),
            "# Structured Transcript\n\n**Meeting Name:** history-of-coffee-origins\n\n## Transcript\n\nA sample for the UI snapshots.\n");
        WriteSilence(Path.Combine(folder, stems[0] + ".wav"), 30);
        WriteSilence(Path.Combine(folder, stems[2] + ".wav"), 12);
        Say($"history samples in {folder}{(File.Exists(fixture) ? "" : " (fixture SRT missing, placeholder text)")}");
        return stems;
    }

    private static void WriteSilence(string path, int seconds)
    {
        using var writer = new WavWriter(path);
        writer.Append(new float[WavWriter.SampleRate * seconds]);
        writer.Close();
    }

    /// <summary>The dialog's box (its template's <c>BackgroundElement</c>), else the dialog.</summary>
    private static FrameworkElement DialogBox(ContentDialog dialog) =>
        FindNamed(dialog, "BackgroundElement") ?? dialog;

    private static FrameworkElement? FindNamed(DependencyObject parent, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>Lets layout and rendering catch up after a change.</summary>
    private static Task Settle() => Task.Delay(500);

    /// <summary>Renders <paramref name="element"/> with RenderTargetBitmap and writes a PNG.</summary>
    private static async Task<bool> SnapshotAsync(FrameworkElement element, string path)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var pixels = await bitmap.GetPixelsAsync();
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return false;
            var dpi = 96.0 * (element.XamlRoot?.RasterizationScale ?? 1.0);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels.ToArray());
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            Say($"snapshot {path}: {error.Message}");
            return false;
        }
    }

    private static void Say(string message) => AppLog.Write($"ui-snapshots: {message}");
}
