using Hearsay.App.Features.History;
using Hearsay.Core.Naming;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Port of mac/Hearsay/Features/Notes/NamingSheet.swift (itself the Python
/// tool's <c>select_meeting_name</c>) as a <see cref="ContentDialog"/>: an
/// editable name, normalized by <see cref="FilenameSanitizer"/> exactly like
/// the saved file names, with the resulting file name shown under the field.
/// Input with no usable characters is rejected inline and the default
/// button is disabled. Cancel keeps the timestamp names.
/// <para>
/// Modes, as on the Mac: a new meeting (<c>suggestion</c> from the AI, or
/// none for manual naming); regenerating notes (<c>currentName</c> prefilled,
/// the AI suggestion a one-click alternative, <c>replacesNotes</c> says the
/// old notes go to the Recycle Bin); and History > Rename (<c>renames</c>:
/// prefilled, no suggestion, a Rename button).
/// </para>
/// Use <see cref="AskAsync"/>; it returns the name as typed (the caller
/// sanitizes it again when saving, as the Mac's <c>onSave</c> receives the
/// raw text), or null when cancelled.
/// </summary>
internal sealed partial class NamingSheet : ContentDialog
{
    private readonly string? suggestion;
    private readonly string? currentName;
    private readonly TextBox nameField;
    private readonly Button? useSuggestion;
    private readonly TextBlock fileName;
    private readonly StackPanel fileNameRow;
    private readonly TextBlock rejected;
    private bool accepted;

    public NamingSheet(XamlRoot root, string? suggestion, string? currentName = null, bool replacesNotes = false,
        bool renames = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        this.suggestion = suggestion;
        this.currentName = currentName;
        Alert.Prepare(this, root);
        Title = renames
            ? Strings.NamingTitleRename
            : suggestion is null && currentName is null ? Strings.NamingTitleNew : Strings.NamingTitleExisting;
        PrimaryButtonText = renames ? Strings.NamingRenameButton : Strings.NamingSaveButton;
        CloseButtonText = Strings.Cancel;
        DefaultButton = ContentDialogButton.Primary;

        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = Explanation(renames),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextFillColorSecondaryBrush"),
        });
        nameField = new TextBox
        {
            Text = currentName ?? suggestion ?? "",
            PlaceholderText = Strings.NamingPlaceholder,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameField, Strings.NamingPlaceholder);
        nameField.TextChanged += (_, _) => Update();
        // Return saves when the name is usable, as the Mac's onSubmit.
        nameField.KeyDown += OnFieldKeyDown;
        panel.Children.Add(nameField);

        if (Alternative is { } alternative)
        {
            useSuggestion = new Button { Content = Strings.NamingUseSuggestion(alternative) };
            useSuggestion.Click += (_, _) => nameField.Text = alternative;
            panel.Children.Add(useSuggestion);
        }

        fileName = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        fileNameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileNameRow.Children.Add(new TextBlock { Text = Strings.NamingFileName, FontWeight = FontWeights.SemiBold });
        fileNameRow.Children.Add(fileName);
        panel.Children.Add(fileNameRow);
        rejected = new TextBlock
        {
            Text = Strings.NamingRejected,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("SystemFillColorCriticalBrush"),
        };
        panel.Children.Add(rejected);

        if (replacesNotes)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Strings.NamingReplacesNotes,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextFillColorSecondaryBrush"),
            });
        }
        Content = panel;
        Opened += (_, _) =>
        {
            nameField.Focus(FocusState.Programmatic);
            nameField.SelectAll();
        };
        Update();
    }

    /// <summary>The text in the field.</summary>
    public string MeetingName
    {
        get => nameField.Text;
        set => nameField.Text = value;
    }

    /// <summary>The file-name slug of <see cref="MeetingName"/>, null when it has no usable characters.</summary>
    public string? Slug => FilenameSanitizer.Sanitize(nameField.Text);

    /// <summary>Shows the sheet; the name to save, or null when cancelled.</summary>
    public async Task<string?> AskAsync()
    {
        var result = await Alert.PresentAsync(this).ConfigureAwait(true);
        return (result == ContentDialogResult.Primary || accepted) && Slug is not null ? nameField.Text : null;
    }

    /// <summary>Closes the sheet as its Save (or Rename) button does, when the name is usable (the UI snapshots).</summary>
    public void Accept()
    {
        if (Slug is null) return;
        accepted = true;
        Hide();
    }

    /// <summary>The AI suggestion offered beside a prefilled current name.</summary>
    private string? Alternative
    {
        get
        {
            if (currentName is null || suggestion is null) return null;
            return suggestion == FilenameSanitizer.Sanitize(currentName) ? null : suggestion;
        }
    }

    private string Explanation(bool renames)
    {
        if (renames) return Strings.NamingExplainRename;
        if (currentName is not null)
        {
            return Alternative is null ? Strings.NamingExplainCurrent : Strings.NamingExplainCurrentOrSuggestion;
        }
        return suggestion is null ? Strings.NamingExplainManual : Strings.NamingExplainSuggestion;
    }

    private void Update()
    {
        var slug = Slug;
        IsPrimaryButtonEnabled = slug is not null;
        fileName.Text = slug ?? "";
        fileNameRow.Visibility = slug is null ? Visibility.Collapsed : Visibility.Visible;
        rejected.Visibility = slug is null ? Visibility.Visible : Visibility.Collapsed;
        if (useSuggestion is not null && Alternative is { } alternative) useSuggestion.IsEnabled = slug != alternative;
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The dialog's default button already takes Return; without a usable
        // name it is disabled, so Return does nothing, as on the Mac.
        if (e.Key == VirtualKey.Enter && Slug is null) e.Handled = true;
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? throw new InvalidOperationException($"Missing resource {key}.");
}
