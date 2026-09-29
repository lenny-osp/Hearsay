using Hearsay.App.Features.History;
using Hearsay.Core.Notes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Edits a user template's name and instructions. Port of the private
/// <c>TemplateEditorSheet</c> in mac/Hearsay/Features/Notes/AISettingsTab.swift
/// as a <see cref="ContentDialog"/>: Save is enabled only while both fields
/// have text; Cancel (Escape) changes nothing. A WinUI <see cref="TextBox"/>
/// stores line breaks as CR, so the stored LF newlines (the prompt files'
/// line ending) become CR while editing and LF again on Save.
/// </summary>
internal sealed partial class TemplateEditorSheet : ContentDialog
{
    private readonly PromptTemplate template;
    private readonly TextBox name;
    private readonly TextBox instructions;
    private bool saved;

    public TemplateEditorSheet(XamlRoot root, PromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(template);
        this.template = template;
        Alert.Prepare(this, root);
        Title = Strings.TemplateEditorTitle;
        PrimaryButtonText = Strings.NamingSaveButton;
        CloseButtonText = Strings.Cancel;
        DefaultButton = ContentDialogButton.Primary;
        // The dialog is wider than ContentDialog's default 548 px maximum.
        Resources["ContentDialogMaxWidth"] = 640.0;

        var panel = new StackPanel { Spacing = 10, Width = 560 };
        name = new TextBox { Text = template.Name, Header = Strings.TemplateEditorName };
        name.TextChanged += (_, _) => Update();
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = Strings.TemplateEditorInstructions });
        instructions = new TextBox
        {
            // Before Text: a single-line TextBox keeps only the first line.
            AcceptsReturn = true,
            Text = template.Instructions.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Height = 240,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(instructions, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(instructions, Strings.TemplateEditorInstructions);
        instructions.TextChanged += (_, _) => Update();
        panel.Children.Add(instructions);
        panel.Children.Add(new TextBlock
        {
            Text = Strings.TemplateEditorCaption(PromptTemplate.OutputLanguagePlaceholder),
            Style = Application.Current.Resources["CaptionStyle"] as Style,
            FontWeight = FontWeights.Normal,
        });
        Content = panel;
        PrimaryButtonClick += (_, _) => saved = true;
        Update();
    }

    /// <summary>The edited template (same id), or null when cancelled.</summary>
    public async Task<PromptTemplate?> AskAsync()
    {
        await Alert.PresentAsync(this).ConfigureAwait(true);
        return saved && IsValid ? Edited : null;
    }

    private PromptTemplate Edited => template with
    {
        Name = name.Text,
        Instructions = instructions.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
    };

    private bool IsValid => name.Text.Trim().Length > 0 && instructions.Text.Trim().Length > 0;

    private void Update() => IsPrimaryButtonEnabled = IsValid;
}
