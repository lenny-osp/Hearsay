using System.ComponentModel;
using System.Security;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// Settings > General, in the Mac's order: Interface (interface language,
/// applies at next launch), Startup (launch at login), Transcription (Auto
/// mode default language), Shortcuts. Mirrors <c>GeneralSettingsView</c>,
/// <c>LaunchAtLoginSection</c> and <c>PreferredLanguagePicker</c> in
/// mac/Hearsay/Features/Settings/SettingsView.swift,
/// <c>InterfaceLanguagePicker</c> in InterfaceLanguageSupport.swift, and
/// <c>HotkeySettingsSection</c> in Features/Hotkeys/HotkeyRecorderView.swift.
/// <para>
/// Not here yet: the shortcut recorder (the bindings are shown and can be
/// reset; recording new ones is later work), Software updates (W7) and
/// Acknowledgements (W7, with the notices).
/// </para>
/// </summary>
internal sealed partial class GeneralSettingsView : UserControl
{
    private readonly AppShell shell;
    private readonly AppSettings settings;
    private readonly ComboBox interfaceLanguage;
    private readonly TextBlock pendingLanguage;
    private readonly ToggleSwitch launchAtLogin;
    private readonly TextBlock launchError;
    private readonly ComboBox preferredLanguage;
    private readonly TextBlock startStopShortcut;
    private readonly TextBlock pauseShortcut;
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
        pendingLanguage = Caption("");
        page.Children.Add(Header(Strings.SectionInterface));
        page.Children.Add(Card(
            Labeled(Strings.InterfaceLanguage, interfaceLanguage),
            Caption(Strings.InterfaceLanguageCaption),
            pendingLanguage));

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

        // Transcription: the only control that writes PreferredLanguage.
        preferredLanguage = new ComboBox { MinWidth = 180 };
        foreach (var language in TranscriptLanguages.All) preferredLanguage.Items.Add(language.DisplayName());
        preferredLanguage.SelectionChanged += (_, _) =>
        {
            if (refreshing || preferredLanguage.SelectedIndex < 0) return;
            settings.PreferredLanguage = TranscriptLanguages.All[preferredLanguage.SelectedIndex];
        };
        page.Children.Add(Header(Strings.SectionTranscription));
        page.Children.Add(Card(
            Labeled(Strings.PreferredLanguage, preferredLanguage),
            Caption(Strings.PreferredLanguageCaption)));

        // Shortcuts.
        startStopShortcut = new TextBlock { IsTextSelectionEnabled = true };
        pauseShortcut = new TextBlock { IsTextSelectionEnabled = true };
        resetShortcuts = new Button { Content = Strings.ShortcutsReset };
        resetShortcuts.Click += (_, _) =>
        {
            settings.StartStopHotkey = HotkeyBinding.DefaultStartStop;
            settings.PauseHotkey = HotkeyBinding.DefaultPause;
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
            captionRow,
            shortcutError));

        Content = page;
        Refresh();
        settings.PropertyChanged += OnSettingsChanged;
        shell.Hotkeys.PropertyChanged += OnHotkeysChanged;
        // The user can change launch at login elsewhere (Task Manager).
        Loaded += (_, _) => Refresh();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnHotkeysChanged(object? sender, PropertyChangedEventArgs e) =>
        SetWarning(shortcutError, shell.Hotkeys.RegistrationError);

    private void Refresh()
    {
        refreshing = true;
        interfaceLanguage.SelectedIndex = IndexOf(InterfaceLanguages.All, settings.InterfaceLanguage);
        pendingLanguage.Text = InterfaceLanguages.NeedsRestart(shell.RunningLanguage, settings.InterfaceLanguage)
            ? Strings.InterfaceLanguagePending(settings.InterfaceLanguage.Autonym())
            : "";
        pendingLanguage.Visibility = pendingLanguage.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        launchAtLogin.IsOn = shell.LaunchAtLogin.IsEnabled;
        preferredLanguage.SelectedIndex = IndexOf(TranscriptLanguages.All, settings.PreferredLanguage);
        startStopShortcut.Text = settings.StartStopHotkey.DisplayString;
        pauseShortcut.Text = settings.PauseHotkey.DisplayString;
        resetShortcuts.IsEnabled = settings.StartStopHotkey != HotkeyBinding.DefaultStartStop
            || settings.PauseHotkey != HotkeyBinding.DefaultPause;
        SetWarning(shortcutError, shell.Hotkeys.RegistrationError);
        refreshing = false;
    }

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
