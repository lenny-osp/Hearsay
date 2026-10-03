using System.ComponentModel;
using System.Security;
using Hearsay.App.Features.Hotkeys;
using Hearsay.App.Features.Updates;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// Settings > General, in the Mac's order: Interface (interface language,
/// applies at next launch), Startup (launch at login), Meetings (Windows
/// only: record Microsoft Teams meetings automatically, and whether to ask
/// for the language first; see <see cref="Recording.MeetingAutoRecord"/>), Transcription (the
/// live preview picker, Auto mode default language, when finished recordings are transcribed), Shortcuts
/// (three: Start / Stop, Pause / Resume, Stop &amp; Start Next). Mirrors <c>GeneralSettingsView</c>,
/// <c>LaunchAtLoginSection</c> and <c>PreferredLanguagePicker</c> in
/// mac/Hearsay/Features/Settings/SettingsView.swift,
/// <c>InterfaceLanguagePicker</c> in InterfaceLanguageSupport.swift, and
/// <c>HotkeySettingsSection</c> in Features/Hotkeys/HotkeyRecorderView.swift,
/// then Acknowledgements (<c>AcknowledgementsSection</c> in
/// Features/Settings/AcknowledgementsView.swift), whose Show Licenses… opens
/// the licenses page in a help window (<see cref="AppShell.ShowLicenses"/>).
/// Each shortcut row has a <see cref="HotkeyRecorderView"/> (PLAN.md 18.9).
/// Software updates, between Shortcuts and Acknowledgements as on the Mac,
/// is <see cref="SoftwareUpdatesSection"/> (<c>SoftwareUpdatesSection</c> in
/// mac/Hearsay/Features/Updates/UpdateService.swift).
/// </summary>
internal sealed partial class GeneralSettingsView : UserControl
{
    private readonly AppShell shell;
    private readonly AppSettings settings;
    private readonly ComboBox interfaceLanguage;
    private readonly TextBlock pendingLanguage;
    private readonly Grid pendingRow;
    private readonly ToggleSwitch launchAtLogin;
    private readonly TextBlock launchError;
    private readonly ToggleSwitch autoRecordTeams;
    private readonly ToggleSwitch autoRecordAsksLanguage;
    private readonly ComboBox preferredLanguage;
    private readonly HotkeyRecorderView startStopShortcut;
    private readonly HotkeyRecorderView pauseShortcut;
    private readonly HotkeyRecorderView stopStartNextShortcut;
    private readonly ComboBox finalPassTiming;
    private readonly ComboBox livePreviewMode;
    private readonly Button resetShortcuts;
    private readonly TextBlock shortcutError;
    private bool refreshing;

    public GeneralSettingsView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        settings = shell.Settings;
        var page = Page();

        // Interface. Each language is shown in its own language (autonym).
        interfaceLanguage = new ComboBox { MinWidth = 180 };
        foreach (var language in InterfaceLanguages.All) interfaceLanguage.Items.Add(language.Autonym());
        interfaceLanguage.SelectionChanged += (_, _) =>
        {
            if (refreshing || interfaceLanguage.SelectedIndex < 0) return;
            settings.InterfaceLanguage = InterfaceLanguages.All[interfaceLanguage.SelectedIndex];
        };
        // A change waits for a restart (PLAN.md 18.3); Restart Now does it at
        // once, as the Mac's language alert does. Debug runs never relaunch.
        pendingLanguage = Caption("");
        pendingLanguage.TextWrapping = TextWrapping.Wrap;
        var restartNow = new Button { Content = Strings.RestartNow };
        restartNow.Click += (_, _) => shell.Restart();
        pendingRow = Labeled("", restartNow);
        pendingRow.Children.RemoveAt(0);
        pendingLanguage.VerticalAlignment = VerticalAlignment.Center;
        pendingRow.Children.Insert(0, pendingLanguage);
        page.Children.Add(Header(Strings.SectionInterface));
        page.Children.Add(Card(
            Labeled(Strings.InterfaceLanguage, interfaceLanguage),
            Caption(Strings.InterfaceLanguageCaption),
            pendingRow));

        // Startup.
        var (launchRow, launchSwitch) = Toggle(Strings.LaunchAtLogin);
        launchAtLogin = launchSwitch;
        launchAtLogin.Toggled += (_, _) =>
        {
            if (!refreshing) SetLaunchAtLogin(launchAtLogin.IsOn);
        };
        launchError = Warning();
        page.Children.Add(Header(Strings.SectionStartup));
        page.Children.Add(Card(launchRow, launchError));

        // Meetings (Windows only): MeetingAutoRecord follows these two settings.
        var (teamsRow, teamsSwitch) = Toggle(Strings.AutoRecordTeamsMeetings);
        autoRecordTeams = teamsSwitch;
        autoRecordTeams.Toggled += (_, _) =>
        {
            if (!refreshing) settings.AutoRecordTeamsMeetings = autoRecordTeams.IsOn;
        };
        var (asksRow, asksSwitch) = Toggle(Strings.AutoRecordAsksLanguage);
        autoRecordAsksLanguage = asksSwitch;
        autoRecordAsksLanguage.Toggled += (_, _) =>
        {
            if (!refreshing) settings.AutoRecordAsksLanguage = autoRecordAsksLanguage.IsOn;
        };
        page.Children.Add(Header(Strings.SectionMeetings));
        page.Children.Add(Card(
            teamsRow,
            Caption(Strings.AutoRecordTeamsMeetingsCaption),
            asksRow,
            Caption(Strings.AutoRecordAsksLanguageCaption)));

        // Transcription: the only control that writes PreferredLanguage.
        preferredLanguage = new ComboBox { MinWidth = 180 };
        foreach (var language in TranscriptLanguages.All) preferredLanguage.Items.Add(language.DisplayName());
        preferredLanguage.SelectionChanged += (_, _) =>
        {
            if (refreshing || preferredLanguage.SelectedIndex < 0) return;
            settings.PreferredLanguage = TranscriptLanguages.All[preferredLanguage.SelectedIndex];
        };
        // When a finished recording gets its final pass (PLAN.md 4.9 item 3).
        finalPassTiming = new ComboBox { MinWidth = 180 };
        foreach (var timing in FinalPassTimingChoices.All) finalPassTiming.Items.Add(FinalPassTimingChoices.Label(timing));
        finalPassTiming.SelectionChanged += (_, _) =>
        {
            if (refreshing || FinalPassTimingChoices.At(finalPassTiming.SelectedIndex) is not { } timing) return;
            settings.FinalPassTiming = timing;
        };
        // The live preview (PLAN.md 4.12, 18.9), first in the card as the Mac's switch is.
        livePreviewMode = new ComboBox { MinWidth = 180 };
        foreach (var mode in LivePreviewChoices.All) livePreviewMode.Items.Add(LivePreviewChoices.Label(mode));
        livePreviewMode.SelectionChanged += (_, _) =>
        {
            if (refreshing || LivePreviewChoices.At(livePreviewMode.SelectedIndex) is not { } mode) return;
            settings.LivePreviewMode = mode;
        };
        page.Children.Add(Header(Strings.SectionTranscription));
        page.Children.Add(Card(
            Labeled(Strings.LivePreview, livePreviewMode),
            Caption(Strings.LivePreviewCaption),
            Labeled(Strings.PreferredLanguage, preferredLanguage),
            Caption(Strings.PreferredLanguageCaption),
            Labeled(Strings.FinalPassTimingLabel, finalPassTiming),
            Caption(Strings.FinalPassTimingCaption)));

        // Shortcuts.
        startStopShortcut = new HotkeyRecorderView(shell.Hotkeys, HotkeyAction.StartStop,
            () => settings.StartStopHotkey, () => Others(HotkeyAction.StartStop), binding => settings.StartStopHotkey = binding);
        pauseShortcut = new HotkeyRecorderView(shell.Hotkeys, HotkeyAction.Pause,
            () => settings.PauseHotkey, () => Others(HotkeyAction.Pause), binding => settings.PauseHotkey = binding);
        stopStartNextShortcut = new HotkeyRecorderView(shell.Hotkeys, HotkeyAction.StopStartNext,
            () => settings.StopStartNextHotkey, () => Others(HotkeyAction.StopStartNext), binding => settings.StopStartNextHotkey = binding);
        resetShortcuts = new Button { Content = Strings.ShortcutsReset };
        resetShortcuts.Click += (_, _) =>
        {
            startStopShortcut.StopListening();
            pauseShortcut.StopListening();
            stopStartNextShortcut.StopListening();
            settings.StartStopHotkey = HotkeyBinding.DefaultStartStop;
            settings.PauseHotkey = HotkeyBinding.DefaultPause;
            settings.StopStartNextHotkey = HotkeyBinding.DefaultStopStartNext;
        };
        var captionRow = Labeled(Strings.ShortcutsCaption, resetShortcuts);
        if (captionRow.Children[0] is TextBlock captionText)
        {
            captionText.Style = (Style)Application.Current.Resources["CaptionStyle"];
        }
        shortcutError = Warning();
        page.Children.Add(Header(Strings.SectionShortcuts));
        page.Children.Add(Card(
            Labeled(Strings.ShortcutStartStop, startStopShortcut),
            Labeled(Strings.ShortcutPause, pauseShortcut),
            Labeled(Strings.ShortcutStopStartNext, stopStartNextShortcut),
            captionRow,
            shortcutError));

        // Software updates.
        Updates = new SoftwareUpdatesSection(shell.Updates, settings);
        page.Children.Add(Header(Strings.SectionSoftwareUpdates));
        page.Children.Add(Updates.Card);

        // Acknowledgements.
        var showLicenses = new Button { Content = Strings.ShowLicenses };
        showLicenses.Click += (_, _) => shell.ShowLicenses();
        page.Children.Add(Header(Strings.SectionAcknowledgements));
        page.Children.Add(Card(Labeled(Strings.AcknowledgementsText, showLicenses)));

        Content = page;
        Refresh();
        settings.PropertyChanged += OnSettingsChanged;
        shell.Hotkeys.PropertyChanged += OnHotkeysChanged;
        // The user can change launch at login elsewhere (Task Manager).
        Loaded += (_, _) => Refresh();
    }

    /// <summary>The Software updates card, for the UI snapshots.</summary>
    internal SoftwareUpdatesSection Updates { get; }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Settings can change on a pool thread (an update check's continuation).
        if (DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(Refresh);
            return;
        }
        Refresh();
    }

    private void OnHotkeysChanged(object? sender, PropertyChangedEventArgs e) =>
        SetWarning(shortcutError, shell.Hotkeys.RegistrationError);

    private void Refresh()
    {
        refreshing = true;
        interfaceLanguage.SelectedIndex = IndexOf(InterfaceLanguages.All, settings.InterfaceLanguage);
        pendingLanguage.Text = InterfaceLanguages.NeedsRestart(shell.RunningLanguage, settings.InterfaceLanguage)
            ? Strings.InterfaceLanguagePending(settings.InterfaceLanguage.Autonym())
            : "";
        pendingRow.Visibility = pendingLanguage.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        launchAtLogin.IsOn = shell.LaunchAtLogin.IsEnabled;
        autoRecordTeams.IsOn = settings.AutoRecordTeamsMeetings;
        autoRecordAsksLanguage.IsOn = settings.AutoRecordAsksLanguage;
        // The question applies only to automatic recordings.
        autoRecordAsksLanguage.IsEnabled = settings.AutoRecordTeamsMeetings;
        preferredLanguage.SelectedIndex = IndexOf(TranscriptLanguages.All, settings.PreferredLanguage);
        startStopShortcut.Refresh();
        pauseShortcut.Refresh();
        stopStartNextShortcut.Refresh();
        finalPassTiming.SelectedIndex = FinalPassTimingChoices.IndexOf(settings.FinalPassTiming);
        livePreviewMode.SelectedIndex = LivePreviewChoices.IndexOf(settings.LivePreviewMode);
        resetShortcuts.IsEnabled = ShortcutsDiffer(settings);
        SetWarning(shortcutError, shell.Hotkeys.RegistrationError);
        refreshing = false;
    }

    /// <summary>The bindings of the two actions other than <paramref name="action"/>, for the recorder's conflict check.</summary>
    private IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> Others(HotkeyAction action) => HotkeyRecorder.OthersOf(settings, action);

    /// <summary>Whether Reset has anything to restore: any of the three shortcuts is not its default.</summary>
    internal static bool ShortcutsDiffer(AppSettings settings) =>
        settings.StartStopHotkey != HotkeyBinding.DefaultStartStop
        || settings.PauseHotkey != HotkeyBinding.DefaultPause
        || settings.StopStartNextHotkey != HotkeyBinding.DefaultStopStartNext;

    /// <summary>
    /// UI snapshots only: the Start / Stop recorder listening, after a
    /// Shift+A press it refused, so the recording state and its inline reason
    /// show. <see cref="EndRecordingSample"/> puts it back.
    /// </summary>
    internal HotkeyRecorderView ShowRecordingSample()
    {
        startStopShortcut.StartListening();
        startStopShortcut.Handle(0x41, HotkeyModifiers.Shift);
        return startStopShortcut;
    }

    /// <summary>
    /// UI snapshots only: the Stop &amp; Start Next recorder listening, after
    /// the Start / Stop chord, so its inline "Already used for ..." reason shows.
    /// <see cref="EndConflictSample"/> puts it back.
    /// </summary>
    internal HotkeyRecorderView ShowConflictSample()
    {
        stopStartNextShortcut.StartListening();
        var start = settings.StartStopHotkey;
        stopStartNextShortcut.Handle(start.VirtualKey, start.Modifiers);
        return stopStartNextShortcut;
    }

    internal void EndConflictSample() => stopStartNextShortcut.Handle(HotkeyRecorder.VkEscape, HotkeyModifiers.None);

    internal void EndRecordingSample() => startStopShortcut.Handle(HotkeyRecorder.VkEscape, HotkeyModifiers.None);

    /// <summary>Only the user's toggle writes the Run key; the switch then reads the state back.</summary>
    private void SetLaunchAtLogin(bool enabled)
    {
        string? failure = null;
        try
        {
            shell.LaunchAtLogin.SetEnabled(enabled);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            failure = enabled ? Strings.LaunchAtLoginOnFailed(error.Message) : Strings.LaunchAtLoginOffFailed(error.Message);
        }
        SetWarning(launchError, failure);
        Refresh();
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], value)) return i;
        }
        return -1;
    }
}
