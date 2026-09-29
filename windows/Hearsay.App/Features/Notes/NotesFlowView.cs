using Hearsay.App.Features.History;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Hosts the notes-flow sheets and shows progress, the resulting files, and
/// errors for a <see cref="NotesFlowViewModel"/>. Port of
/// mac/Hearsay/Features/Notes/NotesFlowView.swift (with its private
/// <c>ManualNamingPromptSheet</c> as <see cref="ManualNamingPromptSheet"/>).
/// Reusable wherever a finished SRT is shown: the History tab's notes panel
/// today, the Record and File tabs' results through the same view model.
/// <para>
/// The sheets are <see cref="ContentDialog"/>s presented through
/// <see cref="Alert.PresentAsync"/> (one at a time per window): the confirm
/// sheet, "Name this meeting yourself?", and the <see cref="NamingSheet"/>
/// (name mode with the AI suggestion, or regenerate mode with the current
/// name). Each is shown when the phase asks for it, once the view has a
/// <see cref="XamlRoot"/>; a sheet closed with Escape resolves the step
/// through <see cref="NotesFlowViewModel.SheetDismissed"/>, as the Mac's
/// dismissal does.
/// </para>
/// <para>
/// Windows differences: the result also has Open Notes and Open Transcript
/// buttons beside Reveal in Explorer (the Mac only reveals the files).
/// </para>
/// </summary>
internal sealed partial class NotesFlowView : UserControl
{
    private readonly NotesFlowViewModel model;
    private readonly StackPanel panel;
    private bool presenting;
    private string? openFailure;

    public NotesFlowView(NotesFlowViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        panel = new StackPanel { Spacing = 6 };
        Content = panel;
        model.Changed += (_, _) =>
        {
            openFailure = null;
            Render();
            _ = PresentSheetsAsync();
        };
        Loaded += (_, _) => _ = PresentSheetsAsync();
        Render();
    }

    public NotesFlowViewModel Model => model;

    /// <summary>
    /// Shows the sheet the current phase asks for, then the next one, until
    /// the flow reaches a phase without a sheet.
    /// </summary>
    private async Task PresentSheetsAsync()
    {
        if (presenting || XamlRoot is not { } root) return;
        presenting = true;
        try
        {
            while (true)
            {
                switch (model.Phase)
                {
                    case NotesPhase.Confirming:
                        var answer = await new ConfirmSendSheet(root, model).AskAsync().ConfigureAwait(true);
                        if (model.Phase is not NotesPhase.Confirming) break;
                        switch (answer)
                        {
                            case ConfirmSendSheet.Answer.Send:
                                model.Send();
                                break;
                            case ConfirmSendSheet.Answer.KeepLocal:
                                model.KeepLocal();
                                break;
                            default:
                                model.SheetDismissed();
                                break;
                        }
                        continue;
                    case NotesPhase.AskingManualNaming:
                        var nameIt = await new ManualNamingPromptSheet(root, model).AskAsync().ConfigureAwait(true);
                        if (model.Phase is not NotesPhase.AskingManualNaming) break;
                        if (nameIt)
                        {
                            model.AcceptManualNaming();
                        }
                        else
                        {
                            model.DeclineManualNaming();
                        }
                        continue;
                    case NotesPhase.Naming naming:
                        var sheet = new NamingSheet(root, naming.Suggestion, model.CurrentMeetingName,
                            replacesNotes: model.IsRegenerating);
                        var name = await sheet.AskAsync().ConfigureAwait(true);
                        if (model.Phase is not NotesPhase.Naming) break;
                        if (name is not null)
                        {
                            model.SaveName(name);
                        }
                        else
                        {
                            model.CancelNaming();
                        }
                        continue;
                }
                break;
            }
        }
        finally
        {
            presenting = false;
        }
    }

    private void Render()
    {
        panel.Children.Clear();
        switch (model.Phase)
        {
            case NotesPhase.Idle:
                Visibility = Visibility.Collapsed;
                return;
            case NotesPhase.Confirming or NotesPhase.AskingManualNaming or NotesPhase.Naming:
                // Behind the sheet while it is up; if the sheet went away
                // without an answer, this still ends the step.
                panel.Children.Add(Bar(Secondary(Strings.NotesWaiting), Strings.Cancel, () =>
                {
                    Alert.Current?.Hide();
                    model.SheetDismissed();
                }));
                break;
            case NotesPhase.Generating:
                var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                progress.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
                progress.Children.Add(new TextBlock
                {
                    Text = Strings.NotesGenerating(model.ProviderName, model.Store.Configuration.Model),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                panel.Children.Add(Bar(progress, Strings.Cancel, model.CancelGeneration));
                break;
            case NotesPhase.Finished finished:
                panel.Children.Add(Headline("", finished.Message, "SystemFillColorSuccessBrush"));
                foreach (var file in finished.Files) panel.Children.Add(PathLine(file));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (finished.Files.FirstOrDefault(IsNotes) is { } notes)
                {
                    buttons.Children.Add(ActionButton(Strings.OpenNotes, () => Open(notes)));
                }
                if (finished.Files.FirstOrDefault(IsTranscript) is { } transcript)
                {
                    buttons.Children.Add(ActionButton(Strings.OpenTranscript, () => Open(transcript)));
                }
                buttons.Children.Add(ActionButton(Strings.RevealInExplorer, () => Reveal(finished.Files)));
                panel.Children.Add(buttons);
                break;
            case NotesPhase.Failed failed:
                panel.Children.Add(Headline("", Strings.NotesNotSaved, "SystemFillColorCriticalBrush"));
                panel.Children.Add(new TextBlock { Text = failed.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                panel.Children.Add(new TextBlock
                {
                    Text = Strings.NotesSrtKept(failed.SrtPath),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = Brush("TextFillColorSecondaryBrush"),
                });
                panel.Children.Add(ActionButton(Strings.RevealInExplorer, () => Reveal([failed.SrtPath])));
                break;
        }
        if (openFailure is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = openFailure,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = Brush("SystemFillColorCriticalBrush"),
            });
        }
        Visibility = Visibility.Visible;
    }

    private static bool IsTranscript(string path) =>
        path.EndsWith("_transcript.md", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotes(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !IsTranscript(path);

    private void Open(string path)
    {
        openFailure = ExplorerShell.Open(path);
        Render();
    }

    private void Reveal(IReadOnlyList<string> paths)
    {
        openFailure = ExplorerShell.Reveal(paths.Where(File.Exists).ToList()) is { } failure
            ? Strings.RevealFailed(failure)
            : null;
        Render();
    }

    private static Grid Bar(UIElement content, string button, Action onClick)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(content);
        var cancel = ActionButton(button, onClick);
        Grid.SetColumn(cancel, 1);
        grid.Children.Add(cancel);
        return grid;
    }

    private static StackPanel Headline(string glyph, string text, string brush)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = Brush(brush) });
        line.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(brush),
        });
        return line;
    }

    /// <summary>A result path in monospace, shortened in the middle (the Mac's <c>.truncationMode(.middle)</c>); the tooltip has it whole.</summary>
    private static TextBlock PathLine(string path)
    {
        var line = new TextBlock
        {
            Text = MiddleTruncated(path, 84),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(line, path);
        return line;
    }

    /// <summary><paramref name="text"/> with its middle replaced by "…" when longer than <paramref name="limit"/> characters.</summary>
    internal static string MiddleTruncated(string text, int limit)
    {
        if (text.Length <= limit || limit < 3) return text;
        var tail = (limit - 1) / 2;
        var head = limit - 1 - tail;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("TextFillColorSecondaryBrush"),
    };

    private static Button ActionButton(string title, Action onClick)
    {
        var button = new Button { Content = title, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }

    internal static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? throw new InvalidOperationException($"Missing resource {key}.");
}

/// <summary>
/// Python <c>confirm_manual_naming</c>: "Name this meeting yourself?" with no
/// as the default (Enter keeps the timestamp names, Y names it). The Mac's
/// private <c>ManualNamingPromptSheet</c> in NotesFlowView.swift.
/// </summary>
internal sealed partial class ManualNamingPromptSheet : ContentDialog
{
    private bool nameIt;

    public ManualNamingPromptSheet(XamlRoot root, NotesFlowViewModel model)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(model);
        Alert.Prepare(this, root);
        Title = Strings.ManualNamingTitle;
        PrimaryButtonText = Strings.ManualNamingKeep;
        SecondaryButtonText = Strings.ManualNamingNameIt;
        DefaultButton = ContentDialogButton.Primary;
        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(new TextBlock { Text = Strings.ManualNamingText, TextWrapping = TextWrapping.Wrap });
        if (model.SrtPath is { } srt) panel.Children.Add(Kept(Strings.ManualNamingTranscriptKept(Path.GetFileName(srt))));
        foreach (var audio in model.RetainedAudio)
        {
            panel.Children.Add(Kept(Strings.ManualNamingRecordingKept(Path.GetFileName(audio))));
        }
        Content = panel;
        SecondaryButtonClick += (_, _) => nameIt = true;
        var yes = new KeyboardAccelerator { Key = VirtualKey.Y };
        yes.Invoked += (_, e) =>
        {
            e.Handled = true;
            nameIt = true;
            Hide();
        };
        KeyboardAccelerators.Add(yes);
    }

    /// <summary>True for "Name It…"; false for "Keep Timestamp Names" or Escape.</summary>
    public async Task<bool> AskAsync()
    {
        await Alert.PresentAsync(this).ConfigureAwait(true);
        return nameIt;
    }

    private static TextBlock Kept(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = NotesFlowView.Brush("TextFillColorSecondaryBrush"),
    };
}
