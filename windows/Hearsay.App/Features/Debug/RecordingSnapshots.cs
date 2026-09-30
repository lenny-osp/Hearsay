using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Recovery;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// The W5 part of <see cref="UISnapshots"/>: the Record tab idle, recording
/// (meters and live text), with the Auto fallback notice, with the mismatch
/// banner, during the final pass and with the saved result; the File tab
/// idle, transcribing and finished with a suggestion; and the
/// unfinished-recording sheet. Every state is stubbed through
/// <see cref="RecordingController.ShowSample"/>, <see cref="TranscriptionQueue.InsertSample"/>
/// (the final pass and the saved result are a job of the queue) and
/// <see cref="FileViewModel.ShowSample"/>: nothing is recorded or
/// transcribed. The live lines are the cues of
/// shared/fixtures/en-30s.expected.srt (copied next to the exe). Mirrors the
/// Record and recovery-sheet renders in mac/Hearsay/Features/Debug/UISnapshots.swift.
/// </summary>
internal static class RecordingSnapshots
{
    /// <summary>What <see cref="UISnapshots"/> lends: render a PNG, record a check, let layout settle, find a dialog's box.</summary>
    public sealed record Tools(
        Func<string, FrameworkElement, Task> Render,
        Action<bool, string> Check,
        Func<Task> Settle,
        Func<ContentDialog, FrameworkElement> DialogBox);

    public static async Task RunAsync(AppShell shell, string sampleOutput, Tools tools)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(tools);
        var window = shell.MainWindow;
        var controller = shell.RecordingController;
        var transcriptions = shell.Queue;
        var file = shell.FileModel;
        var cues = SampleCues();
        var srt = Path.Combine(sampleOutput, "2026-09-29_10-00-00.srt");
        var wav = Path.Combine(sampleOutput, "2026-09-29_10-00-00.wav");
        var english = LanguageChoice.Fixed(TranscriptLanguage.English);

        window.ResizeClient(MainWindow.DefaultWidth, 1000);
        shell.Tabs.Tab = MainTab.Record;
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("30-record-idle", window.RenderRoot).ConfigureAwait(true);

        // On the CPU runtime, before the first recording: how long the final pass takes (PLAN.md 18.4, "Speed").
        controller.ShowSample(new RecordingSample(ControllerPhase.IdleState, new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English))
        {
            CpuRuntime = true,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(controller.CpuSpeedNotice == Strings.CpuFinalPassNotice, "the CPU final-pass notice shows before the first recording");
        await tools.Render("56-record-cpu-notice", window.RenderRoot).ConfigureAwait(true);

        var recording = new RecordingSample(new ControllerPhase.Recording(), new SessionLanguageTracker(english, TranscriptLanguage.English))
        {
            Elapsed = 754.2,
            Level = 0.62,
            MicLevel = 0.58,
            SystemLevel = 0.31,
            LiveSegments = cues,
            LiveChunksWaiting = 1,
            LivePreviewEnabled = true,
        };
        controller.ShowSample(recording);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(shell.Recording.StateText == Strings.StateRecording("00:12:34"), $"the tray follows the controller ({shell.Recording.StateText})");
        await tools.Render("31-record-recording", window.RenderRoot).ConfigureAwait(true);

        // A fixed language, and detection confident it is another: the banner offers a re-run.
        var mismatch = new SessionLanguageTracker(LanguageChoice.Fixed(TranscriptLanguage.German), TranscriptLanguage.English);
        mismatch.Record(new DetectedLanguage("en", 0.9993f), 30 * 16_000);
        controller.ShowSample(recording with
        {
            Tracker = mismatch,
            Notice = LanguageNotice.From(mismatch.Decision),
            SilenceWarning = Strings.SilenceWarning(6),
            SystemAudioNotice = null,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(controller.LanguageNotice is LanguageNotice.Suggestion { Language: TranscriptLanguage.English }, "the mismatch banner suggests English");
        await tools.Render("32-record-mismatch-banner", window.RenderRoot).ConfigureAwait(true);

        // The final pass of the one recording: a job of the queue (no session), as the Record tab always showed it.
        controller.ShowSample(new RecordingSample(ControllerPhase.IdleState, new SessionLanguageTracker(english, TranscriptLanguage.English))
        {
            Elapsed = 1834,
        });
        transcriptions.InsertSample(wav, TranscriptionJobState.Running, 0.42, live: cues);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(shell.Recording.StateText == Strings.StateTranscribing(42), $"the tray shows the final pass ({shell.Recording.StateText})");
        await tools.Render("33-record-final-pass", window.RenderRoot).ConfigureAwait(true);

        // Auto could not tell: the fallback notice with a button per other language.
        window.ResizeClient(MainWindow.DefaultWidth, 1200);
        var fallback = new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English);
        fallback.Finish(new DetectedLanguage("de", 0.41f));
        transcriptions.ClearSamples();
        var finishedJob = transcriptions.InsertSample(wav, TranscriptionJobState.Done, 1, srt, tracker: fallback, notice: LanguageNotice.From(fallback.Decision), live: cues);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(finishedJob.LanguageNotice is LanguageNotice.Fallback, "the Auto fallback notice shows");
        await tools.Render("34-record-language-notice", window.RenderRoot).ConfigureAwait(true);

        // The result row alone (a session without live preview), so its buttons show.
        transcriptions.ClearSamples();
        transcriptions.InsertSample(wav, TranscriptionJobState.Done, 1, srt);
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("35-record-result", window.RenderRoot).ConfigureAwait(true);
        transcriptions.ClearSamples();
        controller.ShowSample(new RecordingSample(ControllerPhase.IdleState, new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English)));
        tools.Check(shell.Recording.Phase == MenuBar.RecordingPhase.Idle && shell.Recording.BusyFiles.Count == 0, "the tray is idle again");

        await QueueStatesAsync(shell, sampleOutput, tools, cues, recording).ConfigureAwait(true);

        window.ResizeClient(MainWindow.DefaultWidth, 560);
        shell.Tabs.Tab = MainTab.File;
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("36-file-idle", window.RenderRoot).ConfigureAwait(true);
        file.ShowSample(new FilePhase.Transcribing(@"C:\Users\Public\Music\team-sync.m4a", 0.63));
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("37-file-transcribing", window.RenderRoot).ConfigureAwait(true);
        var fileTracker = new SessionLanguageTracker(LanguageChoice.Fixed(TranscriptLanguage.Spanish), TranscriptLanguage.English);
        fileTracker.Finish(new DetectedLanguage("en", 0.97f));
        file.ShowSample(new FilePhase.Finished(srt, @"C:\Users\Public\Music\team-sync.m4a"), fileTracker, LanguageNotice.From(fileTracker.Decision));
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("38-file-finished-suggestion", window.RenderRoot).ConfigureAwait(true);
        file.ShowSample(FilePhase.IdleState);

        // The recovery sheet over a sample spool WAV (42 s), with a second one queued.
        var spoolWav = Path.Combine(shell.Spool.Root, "2026-09-27_09-15-00.wav");
        Directory.CreateDirectory(shell.Spool.Root);
        using (var writer = new WavWriter(spoolWav))
        {
            writer.Append(new float[WavWriter.SampleRate * 42]);
            writer.Close();
        }
        var queue = new UnfinishedRecordingQueue([spoolWav, spoolWav], shell.Spool);
        shell.Tabs.Tab = MainTab.Record;
        await tools.Settle().ConfigureAwait(true);
        if (window.RenderRoot.XamlRoot is { } root)
        {
            var sheet = new UnfinishedRecordingSheet(root, queue, () => sampleOutput, _ => { });
            var showing = sheet.RunAsync();
            await tools.Settle().ConfigureAwait(true);
            await tools.Render("39-sheet-unfinished-recording", tools.DialogBox(sheet)).ConfigureAwait(true);
            sheet.Dismiss();
            await showing.ConfigureAwait(true);
        }
        else
        {
            tools.Check(false, "the recovery sheet has a window to show in");
        }
        tools.Check(File.Exists(spoolWav), "showing the recovery sheet changes nothing on disk");
        File.Delete(spoolWav);
    }

    /// <summary>
    /// The Record tab's "Transcription queue" (PLAN.md 4.9 "UI"): three
    /// recordings with no session (done with notes offered, running, waiting),
    /// a session recording over a queue that whenIdle pauses, a failure with
    /// a language banner, and the same at a narrow window. The rows and the
    /// tray's queue line are checked against the queue's states. Mirrors the
    /// "27-record-queue" render of mac/Hearsay/Features/Debug/UISnapshots.swift.
    /// </summary>
    private static async Task QueueStatesAsync(
        AppShell shell, string sampleOutput, Tools tools, List<TranscriptSegment> cues, RecordingSample recording)
    {
        var window = shell.MainWindow;
        var controller = shell.RecordingController;
        var queue = shell.Queue;
        var spool = shell.Spool.Root;
        var idle = new RecordingSample(ControllerPhase.IdleState, new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English));
        string Pending(string stem) => Path.Combine(spool, stem + ".wav");
        string Output(string stem, string extension) => Path.Combine(sampleOutput, stem + extension);
        List<string> RowStates() => [.. window.RecordView.QueueRowViews.Select(row => row.StateText)];
        shell.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;

        // No session: one finished (Generate Notes offered), one running, one waiting.
        window.ResizeClient(MainWindow.DefaultWidth, 1000);
        shell.Tabs.Tab = MainTab.Record;
        controller.ShowSample(idle);
        queue.ClearSamples();
        var finished = queue.InsertSample(Output("2026-09-25_14-30-00", ".wav"), TranscriptionJobState.Done, 1,
            Output("2026-09-25_14-30-00", ".srt"), offersNotes: true);
        finished.DisplayName = "quarterly-planning";
        queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Running, 0.45, live: cues);
        queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(RowStates().SequenceEqual([Strings.QueueDone, Strings.QueueTranscribing(45), Strings.QueueWaiting]),
            $"the queue rows read done, running, waiting ({string.Join(" | ", RowStates())})");
        var buttons = window.RecordView.QueueRowViews[0].ShownButtons;
        tools.Check(buttons.SequenceEqual([Strings.OpenTranscript, Strings.GenerateNotes, Strings.RevealInExplorer, Strings.Dismiss]),
            $"the finished row offers its actions ({string.Join(", ", buttons)})");
        tools.Check(shell.Recording.QueueLine == Strings.QueueLineCount(2), $"the tray's queue line counts two ({shell.Recording.QueueLine})");
        await tools.Render("64-record-queue", window.RenderRoot).ConfigureAwait(true);

        // A session recording: whenIdle holds the queue, so its rows say so.
        queue.ClearSamples();
        controller.ShowSample(recording);
        var held = queue.InsertSample(Output("2026-09-25_14-30-00", ".wav"), TranscriptionJobState.Done, 1,
            Output("2026-09-25_14-30-00", ".srt"), offersNotes: true);
        held.DisplayName = "quarterly-planning";
        queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Suspended, 0.45, live: cues);
        queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(queue.IsHeldForSession, "whenIdle holds the queue while recording");
        tools.Check(RowStates().SequenceEqual([Strings.QueueDone, Strings.QueuePausedWhileRecording, Strings.QueuePausedWhileRecording]),
            $"the held rows say paused while recording ({string.Join(" | ", RowStates())})");
        tools.Check(shell.Recording.QueueLine == Strings.QueueLinePaused(2), $"the tray's queue line says paused ({shell.Recording.QueueLine})");
        await tools.Render("65-record-queue-recording", window.RenderRoot).ConfigureAwait(true);

        // A failure (Try Again), a finished row with the Auto fallback banner, and a waiting one.
        queue.ClearSamples();
        controller.ShowSample(idle);
        var fallback = new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English);
        fallback.Finish(new DetectedLanguage("de", 0.41f));
        var failed = queue.InsertSample(Output("2026-09-29_10-00-00", ".wav"), TranscriptionJobState.Failed);
        failed.ErrorMessage = Strings.TranscriptionFailed("The model file is missing.");
        failed.NeedsModel = true;
        queue.InsertSample(Output("2026-09-29_15-00-00", ".wav"), TranscriptionJobState.Done, 1, Output("2026-09-29_15-00-00", ".srt"),
            tracker: fallback, notice: LanguageNotice.From(fallback.Decision));
        queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(RowStates().SequenceEqual([Strings.QueueFailed, Strings.QueueDone, Strings.QueueWaiting]),
            $"the queue rows read failed, done, waiting ({string.Join(" | ", RowStates())})");
        tools.Check(window.RecordView.QueueRowViews[0].ShownButtons.Contains(Strings.TryAgain), "the failed row offers Try Again");
        window.ResizeClient(MainWindow.DefaultWidth, 1200);
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("66-record-queue-failed", window.RenderRoot).ConfigureAwait(true);

        // The same at a narrow window: the actions wrap, nothing is cut off.
        window.ResizeClient(480, 1300);
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("67-record-queue-narrow", window.RenderRoot).ConfigureAwait(true);

        queue.ClearSamples();
        controller.ShowSample(idle);
        window.ResizeClient(MainWindow.DefaultWidth, 1000);
        await tools.Settle().ConfigureAwait(true);
    }

    /// <summary>The cues of en-30s.expected.srt as a live preview.</summary>
    private static List<TranscriptSegment> SampleCues()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "DebugSamples", "en-30s.expected.srt");
        return File.Exists(fixture)
            ? [.. Srt.Parse(File.ReadAllText(fixture))]
            : [new TranscriptSegment(0, 2, "Sample")];
    }
}
