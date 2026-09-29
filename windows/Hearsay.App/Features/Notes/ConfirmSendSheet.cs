using Hearsay.App.Features.History;
using Hearsay.Core.Transcription;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Step 1 of the notes flow (Python <c>confirm_ai_processing</c>): the
/// transcript only leaves the PC after "Send". Port of
/// mac/Hearsay/Features/Notes/ConfirmSendSheet.swift as a
/// <see cref="ContentDialog"/>: provider, model, transcript size and file;
/// the template picker; the notes language picker (this run only) with its
/// captions; what will be sent; the Recycle Bin line when regenerating; the
/// "Always ask before sending" switch; Keep local (N) and Send (Enter, the
/// default). Escape answers nothing (<see cref="NotesFlowViewModel.SheetDismissed"/>).
/// <para>
/// Windows only: when the prompt would not fit on a Windows command line
/// (<see cref="NotesFlowViewModel.PromptTooLong"/>, PLAN.md 18.4 "W6 core")
/// the refusal is shown and Send is disabled; changing the template or
/// language updates it.
/// </para>
/// </summary>
internal sealed partial class ConfirmSendSheet : ContentDialog
{
    private readonly NotesFlowViewModel model;
    private readonly TextBlock notesCaption;
    private readonly TextBlock tooLong;
    private readonly ComboBox language;
    private Answer answer = Answer.None;

    /// <summary>How the sheet was closed.</summary>
    public enum Answer
    {
        None,
        Send,
        KeepLocal,
    }

    public ConfirmSendSheet(XamlRoot root, NotesFlowViewModel model)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        Alert.Prepare(this, root);
        Title = Strings.ConfirmTitle;
        PrimaryButtonText = Strings.ConfirmSend;
        SecondaryButtonText = Strings.ConfirmKeepLocal;
        DefaultButton = ContentDialogButton.Primary;
        var store = model.Store;
        var configuration = store.Configuration;

        var panel = new StackPanel { Spacing = 14, Width = 400 };
        var facts = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddFact(facts, Strings.ConfirmProvider, model.ProviderName);
        AddFact(facts, Strings.ConfirmModel, Strings.CoreText(configuration.ModelDescription));
        AddFact(facts, Strings.ConfirmTranscript, Strings.ConfirmCharacters(model.TranscriptCharacterCount));
        if (model.SrtPath is { } srt) AddFact(facts, Strings.ConfirmFile, Path.GetFileName(srt));
        panel.Children.Add(facts);

        var template = new ComboBox { MinWidth = 200 };
        var templates = store.Templates;
        foreach (var item in templates) template.Items.Add(item.IsBuiltIn ? Strings.CoreText(item.Name) : item.Name);
        template.SelectedIndex = Math.Max(0, templates.ToList().FindIndex(item => item.Id == model.TemplateId));
        template.SelectionChanged += (_, _) =>
        {
            if (template.SelectedIndex >= 0 && template.SelectedIndex < templates.Count)
            {
                model.TemplateId = templates[template.SelectedIndex].Id;
                Update();
            }
        };
        var pickers = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddPicker(pickers, Strings.ConfirmTemplate, template);

        // This run only; the choice is not stored in Settings.
        language = new ComboBox { MinWidth = 200 };
        foreach (var item in TranscriptLanguages.All) language.Items.Add(item.DisplayName());
        language.SelectedIndex = TranscriptLanguages.All.ToList().IndexOf(model.NotesLanguage);
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedIndex >= 0)
            {
                model.NotesLanguage = TranscriptLanguages.All[language.SelectedIndex];
                Update();
            }
        };
        AddPicker(pickers, Strings.ConfirmNotesLanguage, language);
        panel.Children.Add(pickers);
        var languageGroup = new StackPanel { Spacing = 4, Margin = new Thickness(0, -8, 0, 0) };
        languageGroup.Children.Add(Caption(model.TranscriptLanguageCaption));
        notesCaption = Caption("");
        languageGroup.Children.Add(notesCaption);
        panel.Children.Add(languageGroup);

        panel.Children.Add(Wrapped(Strings.ConfirmSendsTo(model.ProviderName)));
        if (model.IsRegenerating) panel.Children.Add(Wrapped(Strings.ConfirmReplacesNotes));

        tooLong = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Brush("SystemFillColorCriticalBrush"),
        };
        panel.Children.Add(tooLong);

        var ask = new CheckBox { Content = Strings.ConfirmAlwaysAsk, IsChecked = configuration.AskBeforeSending };
        ask.Click += (_, _) => store.Configuration = store.Configuration with { AskBeforeSending = ask.IsChecked == true };
        panel.Children.Add(ask);
        Content = panel;

        PrimaryButtonClick += (_, _) => answer = Answer.Send;
        SecondaryButtonClick += (_, _) => answer = Answer.KeepLocal;
        // The Mac's keyboardShortcut("n"): Keep local.
        var keepLocal = new KeyboardAccelerator { Key = VirtualKey.N };
        keepLocal.Invoked += (_, e) =>
        {
            e.Handled = true;
            KeepLocal();
        };
        KeyboardAccelerators.Add(keepLocal);
        Update();
    }

    /// <summary>Shows the sheet and returns the answer (<see cref="Answer.None"/> for Escape).</summary>
    public async Task<Answer> AskAsync()
    {
        await Alert.PresentAsync(this).ConfigureAwait(true);
        return answer;
    }

    /// <summary>Closes the sheet as the Send button does (the UI snapshots).</summary>
    public void Send()
    {
        if (!IsPrimaryButtonEnabled) return;
        answer = Answer.Send;
        Hide();
    }

    /// <summary>Closes the sheet as the Keep local button does.</summary>
    public void KeepLocal()
    {
        answer = Answer.KeepLocal;
        Hide();
    }

    /// <summary>Picks a notes language as the picker does (the UI snapshots).</summary>
    public void ChooseNotesLanguage(TranscriptLanguage choice) =>
        language.SelectedIndex = TranscriptLanguages.All.ToList().IndexOf(choice);

    /// <summary>Whether the Windows command-line refusal is showing.</summary>
    public bool ShowsTooLong => tooLong.Visibility == Visibility.Visible;

    private void Update()
    {
        var caption = model.NotesLanguageCaptionText;
        notesCaption.Text = caption ?? "";
        notesCaption.Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible;
        var refusal = model.PromptTooLong;
        tooLong.Text = refusal ?? "";
        tooLong.Visibility = refusal is null ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = refusal is null;
        DefaultButton = refusal is null ? ContentDialogButton.Primary : ContentDialogButton.None;
    }

    private static void AddFact(Grid grid, string label, string value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = label, Foreground = Brush("TextFillColorSecondaryBrush") };
        Grid.SetRow(name, row);
        grid.Children.Add(name);
        var text = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.Normal };
        ToolTipService.SetToolTip(text, value);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
    }

    private static void AddPicker(Grid grid, string label, FrameworkElement control)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
        grid.Children.Add(control);
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Application.Current.Resources["CaptionStyle"] as Style,
    };

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? throw new InvalidOperationException($"Missing resource {key}.");
}
