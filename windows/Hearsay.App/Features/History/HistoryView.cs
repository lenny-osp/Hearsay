using System.ComponentModel;
using System.Globalization;
using Hearsay.App.Features.MenuBar;
using Hearsay.App.Features.Notes;
using Hearsay.Core.History;
using Hearsay.Core.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Hearsay.App.Features.History;

/// <summary>
/// The History tab: past meetings in the output folder with their files
/// (PLAN.md 4.8). Mirrors mac/Hearsay/Features/History/HistoryView.swift:
/// a header with the folder, Reveal in Explorer and Refresh (Ctrl+R); the
/// error line; the list, newest first, one row per meeting with name, date
/// and time, duration, file badges and icon buttons, and the same actions
/// in the row's context menu; the Rename sheet and its error alert; the
/// Move to Recycle Bin confirmation.
/// <para>
/// Generate / Regenerate Notes… is shown disabled until the notes flow
/// arrives in W6 (the Mac's notes panel under the list comes with it).
/// </para>
/// </summary>
internal sealed partial class HistoryView : UserControl
{
    private readonly AppSettings settings;
    private readonly RecordingStatus recording;
    private readonly HistoryViewModel model;
    private readonly TextBlock folderText;
    private readonly Button revealFolder;
    private readonly TextBlock errorText;
    private readonly ListView list;
    private readonly FrameworkElement empty;
    private bool rendering;
    private bool opened;

    /// <param name="recycle">Moves a file to the Recycle Bin; null turns Move to Recycle Bin off.</param>
    public HistoryView(AppShell shell, Action<string>? recycle)
    {
        ArgumentNullException.ThrowIfNull(shell);
        settings = shell.Settings;
        recording = shell.Recording;
        model = new HistoryViewModel(recycle);

        var grid = new Grid { RowSpacing = 8, Margin = new Thickness(24, 12, 24, 16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderIcon = new FontIcon { Glyph = "", FontSize = 16, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") };
        header.Children.Add(folderIcon);
        folderText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
            Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
        };
        Grid.SetColumn(folderText, 1);
        header.Children.Add(folderText);
        revealFolder = new Button { Content = Strings.RevealInExplorer };
        revealFolder.Click += (_, _) => model.RevealFolder();
        Grid.SetColumn(revealFolder, 2);
        header.Children.Add(revealFolder);
        var refresh = new Button { Content = Label("", Strings.Refresh) };
        refresh.Click += (_, _) => model.Rescan();
        refresh.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control });
        Grid.SetColumn(refresh, 3);
        header.Children.Add(refresh);
        grid.Children.Add(header);

        errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Resource<Brush>("SystemFillColorCriticalBrush"),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(errorText, 1);
        grid.Children.Add(errorText);

        list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        list.SelectionChanged += OnSelectionChanged;
        Grid.SetRow(list, 2);
        grid.Children.Add(list);

        empty = EmptyState();
        Grid.SetRow(empty, 2);
        grid.Children.Add(empty);

        Content = grid;
        model.Changed += (_, _) => Render();
        settings.PropertyChanged += OnSettingsChanged;
        recording.PropertyChanged += OnRecordingChanged;
        Loaded += (_, _) =>
        {
            opened = true;
            model.Open(settings);
        };
        Render();
    }

    /// <summary>The view model, for the UI snapshots.</summary>
    public HistoryViewModel Model => model;

    /// <summary>The Rename sheet while it is open, for the UI snapshots.</summary>
    public NamingSheet? PendingRename { get; private set; }

    /// <summary>History > Rename…: the naming sheet in rename mode, then the rename.</summary>
    public async Task RenameAsync(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (XamlRoot is not { } root || !HistoryViewModel.CanRename(entry, HistoryViewModel.BusyStems(recording))) return;
        var sheet = new NamingSheet(root, suggestion: null, currentName: entry.MeetingName, renames: true);
        PendingRename = sheet;
        var name = await sheet.AskAsync().ConfigureAwait(true);
        PendingRename = null;
        if (name is not null) await RenameAsync(entry, name).ConfigureAwait(true);
    }

    /// <summary>Renames <paramref name="entry"/> to <paramref name="name"/>; a failure goes to an alert.</summary>
    public async Task RenameAsync(HistoryEntry entry, string name)
    {
        if (model.Rename(entry, name) is { } failure && XamlRoot is { } root)
        {
            await Alert.ShowAsync(root, Strings.CouldNotRename, failure).ConfigureAwait(true);
        }
    }

    /// <summary>Move to Recycle Bin…: lists the files and asks first.</summary>
    public async Task MoveToRecycleBinAsync(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (XamlRoot is not { } root || !model.CanMoveToRecycleBin) return;
        var names = string.Join("\n", entry.Files.Select(Path.GetFileName));
        if (await Alert.ConfirmAsync(root, Strings.MoveToRecycleBinTitle, names, Strings.MoveToRecycleBinButton).ConfigureAwait(true))
        {
            model.MoveToRecycleBin(entry);
        }
    }

    // Rendering

    private void Render()
    {
        rendering = true;
        folderText.Text = model.FolderPath ?? Strings.NoOutputFolder;
        ToolTipService.SetToolTip(folderText, model.FolderPath);
        revealFolder.IsEnabled = model.FolderPath is not null;
        errorText.Text = model.ErrorMessage ?? "";
        errorText.Visibility = model.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;

        var busy = HistoryViewModel.BusyStems(recording);
        list.Items.Clear();
        var selectedIndex = -1;
        for (var index = 0; index < model.Entries.Count; index++)
        {
            var entry = model.Entries[index];
            list.Items.Add(Row(entry, HistoryViewModel.CanRename(entry, busy)));
            if (entry.Stem == model.Selection) selectedIndex = index;
        }
        list.SelectedIndex = selectedIndex;
        var hasEntries = model.Entries.Count > 0;
        list.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = hasEntries || model.FolderPath is null ? Visibility.Collapsed : Visibility.Visible;
        rendering = false;
        if (selectedIndex >= 0) list.ScrollIntoView(list.Items[selectedIndex]);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering) return;
        var index = list.SelectedIndex;
        model.Selection = index >= 0 && index < model.Entries.Count ? model.Entries[index].Stem : null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (opened && e.PropertyName == nameof(AppSettings.OutputFolder)) model.Open(settings);
    }

    private void OnRecordingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingStatus.BusyFiles)) Render();
    }

    /// <summary>One meeting: name, date and time, duration, file badges, action buttons (the Mac's <c>HistoryRow</c>).</summary>
    private Grid Row(HistoryEntry entry, bool canRename)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock
        {
            Text = entry.MeetingName ?? Strings.Untitled,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Resource<Brush>(entry.MeetingName is null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"),
        });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(Secondary(DateText(entry)));
        if (entry.AudioDuration is { } duration) meta.Children.Add(Secondary(FormatDuration(duration)));
        text.Children.Add(meta);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (entry.Srt is not null) badges.Children.Add(Badge("SRT"));
        if (entry.Notes is not null) badges.Children.Add(Badge(Strings.BadgeNotes));
        if (entry.Transcript is not null) badges.Children.Add(Badge(Strings.BadgeTranscript));
        if (entry.Audio is not null) badges.Children.Add(Badge(Strings.BadgeAudio));
        text.Children.Add(badges);
        row.Children.Add(text);

        var notesTitle = HistoryViewModel.NotesActionTitle(entry);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(IconButton(Strings.OpenNotesTooltip, "", entry.Notes is not null, () => model.OpenFile(entry.Notes)));
        buttons.Children.Add(IconButton(Strings.OpenTranscriptTooltip, "", entry.Transcript is not null, () => model.OpenFile(entry.Transcript)));
        buttons.Children.Add(IconButton(Strings.OpenSrt, "", entry.Srt is not null, () => model.OpenFile(entry.Srt)));
        buttons.Children.Add(IconButton(Strings.RevealInExplorer, "", true, () => model.Reveal(entry)));
        buttons.Children.Add(IconButton(Strings.Rename, "", canRename, () => _ = RenameAsync(entry)));
        // Notes arrive in W6: disabled, and the tooltip says so.
        buttons.Children.Add(IconButton(notesTitle, "", false, () => { }, disabledTooltip: Strings.ComingInW6));
        buttons.Children.Add(IconButton(Strings.MoveToRecycleBin, "", model.CanMoveToRecycleBin, () => _ = MoveToRecycleBinAsync(entry)));
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem(Strings.OpenNotes, entry.Notes is not null, () => model.OpenFile(entry.Notes)));
        menu.Items.Add(MenuItem(Strings.OpenTranscript, entry.Transcript is not null, () => model.OpenFile(entry.Transcript)));
        menu.Items.Add(MenuItem(Strings.OpenSrt, entry.Srt is not null, () => model.OpenFile(entry.Srt)));
        menu.Items.Add(MenuItem(Strings.RevealInExplorer, true, () => model.Reveal(entry)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(Strings.Rename, canRename, () => _ = RenameAsync(entry)));
        menu.Items.Add(MenuItem(notesTitle, false, () => { }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(Strings.MoveToRecycleBin, model.CanMoveToRecycleBin, () => _ = MoveToRecycleBinAsync(entry)));
        row.ContextFlyout = menu;
        row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return row;
    }

    private static FrameworkElement IconButton(string title, string glyph, bool enabled, Action onClick, string? disabledTooltip = null)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            IsEnabled = enabled,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7),
            MinWidth = 0,
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => onClick();
        if (enabled || disabledTooltip is null)
        {
            ToolTipService.SetToolTip(button, title);
            return button;
        }
        // A disabled control shows no tooltip; its wrapper does.
        var wrapper = new Border { Child = button, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(wrapper, $"{title} ({disabledTooltip})");
        return wrapper;
    }

    private static MenuFlyoutItem MenuItem(string title, bool enabled, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Border Badge(string title)
    {
        var accent = Resource<Brush>("AccentTextFillColorPrimaryBrush");
        var badge = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            Child = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = accent },
        };
        AutomationProperties.SetName(badge, Strings.BadgeSaved(title));
        return badge;
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
    };

    private static StackPanel Label(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = text });
        return panel;
    }

    private static StackPanel EmptyState()
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 36,
            Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = Strings.NoMeetingsYet,
            Style = Resource<Style>("SubtitleTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Strings.NoMeetingsDescription,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
        });
        return panel;
    }

    /// <summary>
    /// The Mac's abbreviated date with a short time (<c>Sep 28, 2026 at 2:30 PM</c>
    /// there), in the interface language: the culture's long date without
    /// the weekday and with abbreviated month names, then its short time.
    /// The stem when it has no timestamp.
    /// </summary>
    internal static string DateText(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Timestamp is not { } timestamp) return entry.Stem;
        var culture = CultureInfo.CurrentCulture;
        var pattern = AbbreviatedDatePattern(culture.DateTimeFormat.LongDatePattern);
        return timestamp.ToString(pattern, culture) + " " + timestamp.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
    }

    /// <summary><c>dddd, MMMM d, yyyy</c> becomes <c>MMM d, yyyy</c>.</summary>
    internal static string AbbreviatedDatePattern(string longPattern)
    {
        ArgumentNullException.ThrowIfNull(longPattern);
        var pattern = longPattern;
        var weekday = pattern.IndexOf("dddd", StringComparison.Ordinal);
        if (weekday >= 0)
        {
            var end = weekday + 4;
            while (end < pattern.Length && pattern[end] is ',' or ' ' or '，' or '、') end++;
            pattern = pattern.Remove(weekday, end - weekday).Trim(' ', ',');
        }
        return pattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
    }

    /// <summary><c>H:MM:SS</c> or <c>M:SS</c> (the Mac's <c>HistoryRow.format(duration:)</c>).</summary>
    internal static string FormatDuration(double seconds)
    {
        var total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var rest = total % 60;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, rest)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", minutes, rest);
    }

    private static T Resource<T>(string key) where T : class =>
        Application.Current.Resources[key] as T ?? throw new InvalidOperationException($"Missing resource {key}.");
}
