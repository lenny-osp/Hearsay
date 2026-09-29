using System.Globalization;
using System.Text;
using Hearsay.App.Features.History;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Settings;
using Hearsay.Core.History;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Debug only: the W6 steps of <c>HEARSAY_UI_SNAPSHOTS</c>
/// (Features/Debug/UISnapshots.cs calls <see cref="RunAsync"/>), the notes
/// part of mac/Hearsay/Features/Debug/UISnapshots.swift and more: Settings >
/// AI for each of the six presets, the template editor, the confirm sheet
/// (normal, regenerate, and the Windows command-line refusal), "Name this
/// meeting yourself?", the notes flow while generating, the naming sheet with
/// the AI's suggestion, the result, an error, and the Record tab's notes
/// panel after a save (<see cref="NotesPanel"/>).
/// <para>
/// No provider is ever called: the History tab's notes flow gets a stub
/// generator (<see cref="HistoryView.NotesGeneratorOverride"/>) that returns
/// canned notes or throws the pipeline's own "not logged in" error, and the
/// refusal case points the Copilot path at an empty <c>copilot.exe</c> in the
/// scratch folder, which is never started (Send is disabled). The detected
/// CLI placeholders come from <see cref="CliLocator"/>, which starts no
/// process. Tokens are in the <see cref="InMemorySecretStore"/> of the debug
/// run; files are written only to the scratch output folder, and the
/// regenerate case is not saved, so nothing reaches the Recycle Bin.
/// </para>
/// </summary>
internal static class NotesSnapshots
{
    private static readonly string[] SavedSuffixes = [".srt", ".md", "_transcript.md", ".wav"];

    public sealed record Tools(
        Func<string, FrameworkElement, Task> Render,
        Action<bool, string> Check,
        Func<Task> Settle,
        Func<ContentDialog, FrameworkElement> DialogBox);

    public static async Task RunAsync(AppShell shell, string sampleOutput, Tools tools)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(tools);
        var (render, check, settle, dialogBox) = tools;
        var store = shell.AIProviders;
        var window = shell.MainWindow;
        check(store.Secrets is InMemorySecretStore, "the debug run keeps tokens in memory, not Credential Manager");

        // Settings > AI, each preset, with one user template in the list.
        var interview = store.AddTemplate("Customer interview",
            "Summarize the customer's needs, pains and quotes in {output_language}.\n\n- Needs\n- Pains\n- Quotes\n");
        window.ResizeClient(MainWindow.DefaultWidth, 1400);
        shell.Tabs.Tab = MainTab.Settings;
        window.SettingsView.Show(SettingsPane.AI);
        await settle().ConfigureAwait(true);
        var aiView = Descendant<AISettingsView>(window.SettingsView);
        check(aiView is not null, "Settings > AI is shown");
        // The section alone, so its whole height is in the PNG.
        string[] names = ["copilot", "claude-code", "codex", "antigravity", "ollama", "custom"];
        for (var index = 0; index < ProviderPreset.All.Count; index++)
        {
            store.SelectPreset(ProviderPreset.All[index]);
            await settle().ConfigureAwait(true);
            await render($"{40 + index}-settings-ai-{names[index]}", (FrameworkElement?)aiView ?? window.RenderRoot)
                .ConfigureAwait(true);
        }
        if (aiView is not null)
        {
            var editing = aiView.EditTemplateAsync(interview);
            await settle().ConfigureAwait(true);
            if (aiView.PendingTemplateEditor is { } editor)
            {
                await render("46-sheet-template-editor", dialogBox(editor)).ConfigureAwait(true);
                editor.Hide();
            }
            else
            {
                check(false, "the template editor opened");
            }
            await editing.ConfigureAwait(true);
        }

        // The notes flow on the History tab, with a stub generator.
        var calls = 0;
        var answer = new TaskCompletionSource<NotesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var history = window.HistoryView;
        history.NotesGeneratorOverride = async (_, _, _, _, _, cancellationToken) =>
        {
            calls++;
            if (failure is { } error) throw error;
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        store.SelectPreset(ProviderPreset.ClaudeCodeCli);
        store.Configuration = store.Configuration with { AskBeforeSending = true };
        window.ResizeClient(MainWindow.DefaultWidth, 640);
        shell.Tabs.Tab = MainTab.History;
        history.Model.Rescan();
        await settle().ConfigureAwait(true);

        // Generate: confirm (notes in Deutsch), generating, naming with the suggestion, result.
        const string weeklySync = "2026-09-25_16-45-12_weekly-sync";
        if (Entry(history, weeklySync) is { } weekly && history.GenerateNotes(weekly) is { } flow)
        {
            await settle().ConfigureAwait(true);
            if (Alert.Current is ConfirmSendSheet confirm)
            {
                confirm.ChooseNotesLanguage(TranscriptLanguage.German);
                await settle().ConfigureAwait(true);
                check(!confirm.ShowsTooLong, "a 30 s transcript fits the command line");
                await render("47-sheet-confirm", dialogBox(confirm)).ConfigureAwait(true);
                confirm.Send();
            }
            else
            {
                check(false, "the confirm sheet opened");
            }
            await settle().ConfigureAwait(true);
            check(flow.Phase is NotesPhase.Generating, $"the flow is generating ({flow.Phase})");
            check(!history.Model.CanRename(weekly, new HashSet<string>()), "Rename is off for the entry being generated");
            await render("49-notes-generating", window.RenderRoot).ConfigureAwait(true);
            answer.SetResult(new NotesResponse("Weekly Sync: Planning",
                "# Meeting Notes\n\n## Summary\n\nA sample for the UI snapshots.\n",
                "# Structured Transcript\n\n## Transcript\n\nA sample for the UI snapshots.\n"));
            await settle().ConfigureAwait(true);
            if (Alert.Current is NamingSheet naming)
            {
                check(naming.MeetingName == "weekly-sync-planning", $"the naming sheet shows the suggestion ({naming.MeetingName})");
                await render("50-sheet-naming-suggestion", dialogBox(naming)).ConfigureAwait(true);
                naming.Accept();
            }
            else
            {
                check(false, "the naming sheet opened");
            }
            await settle().ConfigureAwait(true);
            const string saved = "2026-09-25_16-45-12_weekly-sync-planning";
            var files = SavedSuffixes.Select(suffix => Path.Combine(sampleOutput, saved + suffix));
            check(flow.Phase is NotesPhase.Finished && files.All(File.Exists)
                && !File.Exists(Path.Combine(sampleOutput, weeklySync + ".srt")),
                $"the notes were saved as {saved} with the SRT and WAV renamed ({flow.Phase})");
            var notes = File.ReadAllText(Path.Combine(sampleOutput, saved + ".md"));
            check(notes.Contains("**Meeting Name:** weekly-sync-planning", StringComparison.Ordinal), "the notes carry the Meeting Name line");
            check(Entry(history, saved) is { Notes: not null, Transcript: not null }, "History rescanned after the flow ended");
            await render("51-notes-result", window.RenderRoot).ConfigureAwait(true);
            history.Model.DismissNotes();
        }
        else
        {
            check(false, "Generate Notes started for the weekly sync");
        }

        // Error: no confirm sheet, the pipeline's "not logged in" text.
        const string untitled = "2026-09-27_09-15-00";
        failure = new CliProviderException(new CliProviderError.NotLoggedIn(CliTool.ClaudeCode, "Not logged in · Please run /login"));
        store.Configuration = store.Configuration with { AskBeforeSending = false };
        if (Entry(history, untitled) is { } plain && history.GenerateNotes(plain) is { } failing)
        {
            await settle().ConfigureAwait(true);
            check(failing.Phase is NotesPhase.Failed && File.Exists(Path.Combine(sampleOutput, untitled + ".srt")),
                $"a provider error ends the flow and keeps the SRT ({failing.Phase})");
            await render("52-notes-error", window.RenderRoot).ConfigureAwait(true);
            history.Model.DismissNotes();
        }
        else
        {
            check(false, "Generate Notes started for the untitled meeting");
        }
        failure = null;
        store.Configuration = store.Configuration with { AskBeforeSending = true };

        // Regenerate: the confirm sheet's Recycle Bin line; dismissed, nothing changes.
        if (history.Model.Entries.FirstOrDefault(entry => entry.Notes is not null && entry.Stem.EndsWith("coffee-origins-review", StringComparison.Ordinal)) is { } coffee
            && history.GenerateNotes(coffee) is { } regenerating)
        {
            await settle().ConfigureAwait(true);
            if (Alert.Current is ConfirmSendSheet confirm)
            {
                await render("54-sheet-confirm-regenerate", dialogBox(confirm)).ConfigureAwait(true);
                confirm.Hide();
            }
            else
            {
                check(false, "the regenerate confirm sheet opened");
            }
            await settle().ConfigureAwait(true);
            check(regenerating.Phase is NotesPhase.Finished { Message: var message } && message == Strings.NotesNothingSent
                && coffee.Files.All(File.Exists), "dismissing the regenerate sheet changes nothing");
            history.Model.DismissNotes();
        }
        else
        {
            check(false, "Regenerate Notes started for the renamed coffee meeting");
        }

        // The Windows command-line refusal: Copilot with a transcript too long for it.
        var fakeCopilot = Path.Combine(shell.SettingsFile.Folder, "fake-cli", "copilot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeCopilot) ?? shell.SettingsFile.Folder);
        await File.WriteAllBytesAsync(fakeCopilot, []).ConfigureAwait(true);
        store.SelectPreset(ProviderPreset.CopilotCli);
        store.Configuration = store.Configuration.WithCliPath(fakeCopilot, CliTool.Copilot);
        const string longMeeting = "2026-09-24_10-00-00_long-review";
        await File.WriteAllTextAsync(Path.Combine(sampleOutput, longMeeting + ".srt"), LongSrt(40_000)).ConfigureAwait(true);
        history.Model.Rescan();
        await settle().ConfigureAwait(true);
        var callsBefore = calls;
        if (Entry(history, longMeeting) is { } longEntry && history.GenerateNotes(longEntry) is { } refused)
        {
            await settle().ConfigureAwait(true);
            if (Alert.Current is ConfirmSendSheet confirm)
            {
                check(confirm.ShowsTooLong && !confirm.IsPrimaryButtonEnabled, "the refusal shows and Send is disabled");
                await render("48-sheet-confirm-too-long", dialogBox(confirm)).ConfigureAwait(true);
                confirm.KeepLocal();
            }
            else
            {
                check(false, "the confirm sheet opened for the long meeting");
            }
            await settle().ConfigureAwait(true);
            if (Alert.Current is ManualNamingPromptSheet prompt)
            {
                await render("53-sheet-name-it-yourself", dialogBox(prompt)).ConfigureAwait(true);
                prompt.Hide();
            }
            else
            {
                check(false, "\"Name this meeting yourself?\" opened");
            }
            await settle().ConfigureAwait(true);
            check(refused.Phase is NotesPhase.Finished { Message: var skipped } && skipped == Strings.NotesSkipped,
                $"keeping it local keeps the timestamp names ({refused.Phase})");
            history.Model.DismissNotes();
        }
        else
        {
            check(false, "Generate Notes started for the long meeting");
        }
        check(calls == callsBefore, "nothing was sent for the refused transcript");
        history.NotesGeneratorOverride = null;

        // The Record tab's notes panel (NotesPanel), started as "Generate
        // notes…" does, with "Ask before sending" off: straight to naming.
        const string recorded = "2026-09-23_08-00-00";
        File.Copy(Path.Combine(sampleOutput, "2026-09-27_09-15-00.srt"), Path.Combine(sampleOutput, recorded + ".srt"));
        store.SelectPreset(ProviderPreset.ClaudeCodeCli);
        store.Configuration = store.Configuration with { AskBeforeSending = false };
        var recordNotes = window.RecordNotes;
        recordNotes.GeneratorOverride = (_, _, _, _, _, _) => Task.FromResult(new NotesResponse("Morning standup",
            "# Meeting Notes\n\n## Summary\n\nA sample for the UI snapshots.\n",
            "# Structured Transcript\n\n## Transcript\n\nA sample for the UI snapshots.\n"));
        shell.Tabs.Tab = MainTab.Record;
        await settle().ConfigureAwait(true);
        if (recordNotes.Start(Path.Combine(sampleOutput, recorded + ".srt"), TranscriptLanguage.English) is { } recordFlow)
        {
            await settle().ConfigureAwait(true);
            if (Alert.Current is NamingSheet naming)
            {
                naming.Accept();
            }
            else
            {
                check(false, "the Record tab's naming sheet opened");
            }
            await settle().ConfigureAwait(true);
            check(recordFlow.Phase is NotesPhase.Finished
                && File.Exists(Path.Combine(sampleOutput, recorded + "_morning-standup.md")),
                $"the Record tab's notes flow saved {recorded}_morning-standup ({recordFlow.Phase})");
            await render("55-record-notes-result", window.RenderRoot).ConfigureAwait(true);
            recordNotes.Reset();
        }
        else
        {
            check(false, "the Record tab's notes flow started");
        }
        recordNotes.GeneratorOverride = null;
        store.Configuration = store.Configuration with { AskBeforeSending = true };
        await settle().ConfigureAwait(true);
    }

    private static HistoryEntry? Entry(HistoryView history, string stem) =>
        history.Model.Entries.FirstOrDefault(entry => entry.Stem == stem);

    /// <summary>An SRT of numbered one-second cues, at least <paramref name="characters"/> long.</summary>
    private static string LongSrt(int characters)
    {
        var text = new StringBuilder();
        for (var index = 1; text.Length < characters; index++)
        {
            var start = TimeSpan.FromSeconds(index - 1);
            var end = TimeSpan.FromSeconds(index);
            text.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(Stamp(start)).Append(" --> ").Append(Stamp(end)).Append('\n')
                .Append("We reviewed the quarterly numbers and agreed on the next steps.\n\n");
        }
        return text.ToString();
    }

    private static string Stamp(TimeSpan time) =>
        string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);

    private static T? Descendant<T>(DependencyObject parent) where T : class
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
