using System.ComponentModel;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Hearsay.App.Features.Transcription;

/// <summary>
/// The Auto / EN / ZH-TW / ZH-CN / DE / ES picker of the Record and File tabs,
/// bound to <see cref="AppSettings.LanguageChoice"/> (PLAN.md section 1,
/// "Languages"). Port of <c>LanguageChoicePicker</c> in
/// mac/Hearsay/Features/Transcription/LanguageViews.swift. WinUI has no
/// segmented control, so the Mac's segmented picker is a row of toggle
/// buttons that behave as one choice.
/// </summary>
internal sealed partial class LanguageChoicePicker : UserControl
{
    private readonly AppSettings settings;
    private readonly List<(LanguageChoice Choice, ToggleButton Button)> buttons = [];
    private bool isDisabled;

    public LanguageChoicePicker(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var choice in LanguageChoice.All)
        {
            var button = new ToggleButton
            {
                Content = choice.IsAuto ? Strings.LanguageAuto : choice.ShortLabel,
                MinWidth = 56,
                Padding = new Thickness(10, 4, 10, 5),
            };
            button.Click += (_, _) =>
            {
                settings.LanguageChoice = choice;
                Refresh();
            };
            buttons.Add((choice, button));
            row.Children.Add(button);
        }
        var labeled = new Grid { ColumnSpacing = 12 };
        labeled.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labeled.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labeled.Children.Add(new TextBlock { Text = Strings.LanguageLabel, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(row, 1);
        labeled.Children.Add(row);
        ToolTipService.SetToolTip(row, Strings.LanguagePickerTooltip);
        Content = labeled;
        Refresh();
        settings.PropertyChanged += OnSettingsChanged;
        Unloaded += (_, _) => settings.PropertyChanged -= OnSettingsChanged;
        Loaded += (_, _) =>
        {
            settings.PropertyChanged -= OnSettingsChanged;
            settings.PropertyChanged += OnSettingsChanged;
            Refresh();
        };
    }

    /// <summary>Locked while a session or file is running (the Mac's <c>isDisabled</c>).</summary>
    public bool IsDisabled
    {
        get => isDisabled;
        set
        {
            if (isDisabled == value) return;
            isDisabled = value;
            Refresh();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.LanguageChoice)) Refresh();
    }

    private void Refresh()
    {
        var current = settings.LanguageChoice;
        foreach (var (choice, button) in buttons)
        {
            button.IsChecked = choice == current;
            button.IsEnabled = !isDisabled;
        }
    }
}

/// <summary>
/// The suggestion or fallback banner with its re-run buttons. The re-run
/// callback re-runs this session only; it never changes the language choice
/// or the preferred language. Port of <c>LanguageNoticeView</c> in
/// mac/Hearsay/Features/Transcription/LanguageViews.swift.
/// </summary>
internal sealed partial class LanguageNoticeView : UserControl
{
    private readonly StackPanel buttonRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<TranscriptLanguage> onRerun;
    private readonly Action? onDismiss;
    private LanguageNotice? shownNotice;
    private bool shownEnabled;

    public LanguageNoticeView(Action<TranscriptLanguage> onRerun, Action? onDismiss)
    {
        ArgumentNullException.ThrowIfNull(onRerun);
        this.onRerun = onRerun;
        this.onDismiss = onDismiss;
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(message, 1);
        line.Children.Add(message);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(line);
        stack.Children.Add(buttonRow);
        Content = stack;
    }

    /// <summary>Shows <paramref name="notice"/>; <paramref name="isEnabled"/> is whether its buttons apply now.</summary>
    public void Show(LanguageNotice notice, bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(notice);
        // Unchanged: keep the buttons (the Record tab refreshes at the meters' rate).
        if (notice == shownNotice && isEnabled == shownEnabled) return;
        shownNotice = notice;
        shownEnabled = isEnabled;
        message.Text = Strings.LanguageNoticeMessage(notice);
        buttonRow.Children.Clear();
        switch (notice)
        {
            case LanguageNotice.Suggestion suggestion:
                var again = SmallButton(Strings.TranscribeAgain, () => onRerun(suggestion.Language), isEnabled);
                ToolTipService.SetToolTip(again, Strings.TranscribeAgainTooltip(suggestion.Language.DisplayName()));
                buttonRow.Children.Add(again);
                if (onDismiss is { } dismiss) buttonRow.Children.Add(SmallButton(Strings.Dismiss, dismiss, isEnabled));
                break;
            default:
                foreach (var language in notice.RerunLanguages)
                {
                    var button = SmallButton(language.DisplayName(), () => onRerun(language), isEnabled);
                    ToolTipService.SetToolTip(button, Strings.TranscribeAgainInTooltip(language.DisplayName()));
                    buttonRow.Children.Add(button);
                }
                break;
        }
    }

    private static Button SmallButton(string text, Action action, bool isEnabled)
    {
        var button = new Button { Content = text, IsEnabled = isEnabled, Padding = new Thickness(10, 3, 10, 4) };
        button.Click += (_, _) => action();
        return button;
    }
}
