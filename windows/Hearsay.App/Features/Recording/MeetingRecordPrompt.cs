using Hearsay.App.Features.History;
using Hearsay.App.Features.Main;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// The question <see cref="MeetingAutoRecord"/> asks before an automatic
/// recording when "Ask which language to use before each automatic
/// recording" is on (Windows only): the main window comes forward on the
/// Record tab with "Microsoft Teams meeting started" / "Record this
/// meeting?", a language picker (Auto, then each language by its own name,
/// as the Record tab's choices) preselected with the current choice, and
/// Record / Don't record. Cancelling the token (the meeting ended) hides it.
/// Use from the UI thread.
/// </summary>
internal static class MeetingRecordPrompt
{
    /// <summary>The picked choice for Record; null for Don't record, a dialog closed another way, or a cancel.</summary>
    public static async Task<LanguageChoice?> AskAsync(AppShell shell, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (shell.IsQuitting || token.IsCancellationRequested) return null;
        shell.ShowMain(MainTab.Record);
        if (shell.MainWindow.RenderRoot.XamlRoot is not { } root) return null;

        var picker = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        foreach (var choice in LanguageChoice.All)
        {
            picker.Items.Add(choice.FixedLanguage is { } language ? language.DisplayName() : Strings.LanguageAuto);
        }
        var current = shell.Settings.LanguageChoice;
        picker.SelectedIndex = Math.Max(0, IndexOf(current));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = Strings.LanguageLabel, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(picker);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(Alert.Message(Strings.MeetingPromptText));
        content.Children.Add(row);

        var dialog = Alert.Make(root, Strings.MeetingPromptTitle, content);
        dialog.PrimaryButtonText = Strings.MeetingPromptRecord;
        dialog.CloseButtonText = Strings.MeetingPromptDontRecord;
        // The meeting may end while the dialog waits behind another one.
        dialog.Opened += (_, _) =>
        {
            if (token.IsCancellationRequested) dialog.Hide();
        };
        var dispatcher = dialog.DispatcherQueue;
        using var registration = token.Register(() => dispatcher.TryEnqueue(dialog.Hide));
        var result = await Alert.PresentAsync(dialog).ConfigureAwait(true);
        if (token.IsCancellationRequested || result != ContentDialogResult.Primary) return null;
        var index = picker.SelectedIndex;
        return index >= 0 && index < LanguageChoice.All.Count ? LanguageChoice.All[index] : current;
    }

    private static int IndexOf(LanguageChoice choice)
    {
        for (var i = 0; i < LanguageChoice.All.Count; i++)
        {
            if (LanguageChoice.All[i] == choice) return i;
        }
        return -1;
    }
}
