using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Recovery;
using Hearsay.App.Features.Settings;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
    /// "When I start them" (PLAN.md 4.11) follows in
    /// <see cref="HeldQueueAsync"/>.
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

        await HeldQueueAsync(shell, tools, cues, idle).ConfigureAwait(true);
    }

    /// <summary>
    /// "When I start them" (PLAN.md 4.11, 18.12), all stubbed: two held jobs (one
    /// on hold with its progress) under Transcribe All, a released running job
    /// with Hold among held ones, the single held job as a row (and at a narrow
    /// window), a held row beside a released one, a held row with the error line
    /// of a failed Move to Recycle Bin…, Settings > General with the third row
    /// selected, and the held queue at a narrow window. The row texts, the
    /// buttons, the tray's line and Transcribe All input, and the quit alert's
    /// line are checked. Snapshots 71 to 76 are 18.12's, 77 to 80 are the
    /// queue-list and Recycle Bin rules of 2026-10-02.
    /// </summary>
    private static async Task HeldQueueAsync(AppShell shell, Tools tools, List<TranscriptSegment> cues, RecordingSample idle)
    {
        var window = shell.MainWindow;
        var controller = shell.RecordingController;
        var queue = shell.Queue;
        var spool = shell.Spool.Root;
        string Pending(string stem) => Path.Combine(spool, stem + ".wav");
        List<string> RowStates() => [.. window.RecordView.QueueRowViews.Select(row => row.StateText)];
        bool Shows(string text) => ShowsText(window.RenderRoot, text);

        // The switch to Manual is applied by the queue before the samples exist.
        shell.Settings.FinalPassTiming = FinalPassTiming.Manual;
        await tools.Settle().ConfigureAwait(true);
        queue.ClearSamples();
        controller.ShowSample(idle);
        window.ResizeClient(MainWindow.DefaultWidth, 1000);
        shell.Tabs.Tab = MainTab.Record;

        // Two held jobs, one of them stopped half way: Transcribe All in the card's header.
        var onHold = queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Suspended, 0.45, live: cues);
        queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(onHold.IsHeld && queue.HeldCount == 2 && queue.AllPendingHeld, "Manual holds both pending jobs");
        tools.Check(RowStates().SequenceEqual([Strings.QueueOnHold(45), Strings.QueueNotTranscribedYet]),
            $"the held rows read on hold and not transcribed yet ({string.Join(" | ", RowStates())})");
        foreach (var row in window.RecordView.QueueRowViews)
        {
            tools.Check(row.ShownButtons.SequenceEqual([Strings.TranscribeButton, Strings.UseLivePreviewInstead, Strings.MoveToRecycleBin]),
                $"a held row offers Transcribe first, the live preview, then Move to Recycle Bin… ({string.Join(", ", row.ShownButtons)})");
        }
        tools.Check(Shows(Strings.TranscribeAll), "Transcribe All shows while jobs are held");
        tools.Check(shell.Recording.QueueLine == Strings.QueueLineHeld(2) && shell.Recording.CanTranscribeAll,
            $"the tray says not transcribed yet and offers Transcribe All ({shell.Recording.QueueLine})");
        tools.Check(Strings.QuitQueueMessage(queue.AllPendingHeld) == Strings.TheyStayInQueue, "the quit alert says the jobs stay in the queue");
        await tools.Render("71-record-queue-held", window.RenderRoot).ConfigureAwait(true);

        // The same at a narrow window: the header and the actions wrap.
        window.ResizeClient(480, 1300);
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("75-record-queue-held-narrow", window.RenderRoot).ConfigureAwait(true);
        window.ResizeClient(MainWindow.DefaultWidth, 1000);

        // A released running job (Hold) among held jobs.
        queue.ClearSamples();
        var running = queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Running, 0.45, live: cues);
        running.IsReleased = true;
        queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting);
        queue.InsertSample(Pending("2026-09-30_11-00-00"), TranscriptionJobState.Waiting);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(RowStates().SequenceEqual([Strings.QueueTranscribing(45), Strings.QueueNotTranscribedYet, Strings.QueueNotTranscribedYet]),
            $"the released job runs while the others are held ({string.Join(" | ", RowStates())})");
        var released = window.RecordView.QueueRowViews[0].ShownButtons;
        tools.Check(released.SequenceEqual([Strings.Hold, Strings.UseLivePreviewInstead]), $"a released row offers Hold ({string.Join(", ", released)})");
        tools.Check(queue.HeldCount == 2 && !queue.AllPendingHeld && shell.Recording.CanTranscribeAll
            && shell.Recording.QueueLine == Strings.QueueLineCount(3), $"two of three are held ({shell.Recording.QueueLine})");
        tools.Check(Strings.QuitQueueMessage(queue.AllPendingHeld) == Strings.HearsayContinuesNextTime, "the quit alert keeps the Mac's line while a job is released");
        await tools.Render("72-record-queue-held-mixed", window.RenderRoot).ConfigureAwait(true);

        // The single held job is a row, not the single meeting (PLAN.md 4.11, 2026-10-02): its name and time show.
        queue.ClearSamples();
        var single = queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Waiting, live: cues);
        window.ResizeClient(MainWindow.DefaultWidth, 1000);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(queue.FeaturedJob is null && QueueRows.ShowsList(queue), "one held job and no session is a queue row under Manual");
        tools.Check(Shows(Strings.TranscriptionQueue) && Shows(single.Title) && Shows(Strings.QueueNotTranscribedYet) && Shows(Strings.TranscribeButton)
            && Shows(Strings.UseLivePreviewInstead) && Shows(Strings.MoveToRecycleBin) && !Shows(Strings.Hold) && !Shows(Strings.Transcribing),
            "the single held job is a row with its title, Transcribe, the live preview and Move to Recycle Bin…");
        tools.Check(window.RecordView.StatusText == Strings.Ready, $"the state text at the top reads Ready, not a held text ({window.RecordView.StatusText})");
        tools.Check(shell.Recording.QueueLine == Strings.QueueLineHeld(1), $"the tray reads not transcribed yet ({shell.Recording.QueueLine})");
        await tools.Render("77-record-held-single-row", window.RenderRoot).ConfigureAwait(true);
        window.ResizeClient(480, 1000);
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("78-record-held-single-row-narrow", window.RenderRoot).ConfigureAwait(true);
        window.ResizeClient(MainWindow.DefaultWidth, 1000);

        // A held row has Move to Recycle Bin…; a released one has Hold and not the trash; a running (or held, busy) one neither.
        queue.ClearSamples();
        queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Suspended, 0.45, live: cues);
        var releasedWaiting = queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting, live: cues);
        releasedWaiting.IsReleased = true;
        await tools.Settle().ConfigureAwait(true);
        var heldButtons = window.RecordView.QueueRowViews[0].ShownButtons;
        var releasedButtons = window.RecordView.QueueRowViews[1].ShownButtons;
        tools.Check(heldButtons.SequenceEqual([Strings.TranscribeButton, Strings.UseLivePreviewInstead, Strings.MoveToRecycleBin]),
            $"the held row offers Move to Recycle Bin… last ({string.Join(", ", heldButtons)})");
        tools.Check(releasedButtons.SequenceEqual([Strings.Hold, Strings.UseLivePreviewInstead]),
            $"the released row offers Hold and no Move to Recycle Bin… ({string.Join(", ", releasedButtons)})");
        await tools.Render("79-record-queue-held-recycle", window.RenderRoot).ConfigureAwait(true);

        // A failed Move to Recycle Bin…: the job stays and its row says why.
        queue.ClearSamples();
        var kept = queue.InsertSample(Pending("2026-09-30_09-00-00"), TranscriptionJobState.Waiting, live: cues,
            trashError: Strings.CouldNotTrashRecording("The process cannot access the file because it is being used by another process."));
        queue.InsertSample(Pending("2026-09-30_10-00-00"), TranscriptionJobState.Waiting);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(Shows(kept.TrashError ?? "?") && window.RecordView.QueueRowViews[0].ShownButtons.Contains(Strings.MoveToRecycleBin),
            "the error line shows on the row, which still offers Move to Recycle Bin…");
        tools.Check(!Shows(Strings.CouldNotTrashRecording("x")), "no error line on the other row");
        await tools.Render("80-record-queue-trash-error", window.RenderRoot).ConfigureAwait(true);
        queue.Release(kept);
        await tools.Settle().ConfigureAwait(true);
        tools.Check(kept.TrashError is null && !Shows(Strings.CouldNotTrashRecording("The process cannot access the file because it is being used by another process.")),
            "Transcribe clears the error line");

        // Settings > General with the third row selected.
        queue.ClearSamples();
        shell.Tabs.Tab = MainTab.Settings;
        window.SettingsView.Show(SettingsPane.General);
        await tools.Settle().ConfigureAwait(true);
        // The window is not taller than the screen: scroll the page so the Transcription card shows whole.
        if (Scroller(window.RenderRoot) is { } scroller)
        {
            scroller.ChangeView(null, 420, null, disableAnimation: true);
            await tools.Settle().ConfigureAwait(true);
        }
        tools.Check(Shows(Strings.TimingManual) && Shows(Strings.FinalPassTimingCaption),
            "Settings > General shows the third row selected and its caption");
        await tools.Render("74-settings-general-manual", window.RenderRoot).ConfigureAwait(true);

        shell.Tabs.Tab = MainTab.Record;
        shell.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        await tools.Settle().ConfigureAwait(true);
        controller.ShowSample(idle);
        // The live preview picker set to Off (PLAN.md 4.12, 18.9), at the default width and at 480 px.
        shell.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        shell.Settings.LivePreviewMode = LivePreviewMode.Off;
        shell.Tabs.Tab = MainTab.Settings;
        window.SettingsView.Show(SettingsPane.General);
        await tools.Settle().ConfigureAwait(true);
        if (Scroller(window.RenderRoot) is { } offScroller)
        {
            offScroller.ChangeView(null, 420, null, disableAnimation: true);
            await tools.Settle().ConfigureAwait(true);
        }
        tools.Check(Shows(Strings.LivePreviewOffRow) && Shows(Strings.LivePreviewCaption),
            "Settings > General shows the live preview picker on Off and its caption");
        await tools.Render("81-settings-general-live-preview-off", window.RenderRoot).ConfigureAwait(true);
        window.ResizeClient(480, 1300);
        await tools.Settle().ConfigureAwait(true);
        if (Scroller(window.RenderRoot) is { } narrowScroller)
        {
            narrowScroller.ChangeView(null, 420, null, disableAnimation: true);
            await tools.Settle().ConfigureAwait(true);
        }
        await tools.Render("82-settings-general-live-preview-narrow", window.RenderRoot).ConfigureAwait(true);
        window.ResizeClient(MainWindow.DefaultWidth, 1000);

        // The Record tab during a recording in Auto with the preview off: only the notice, no box, no "Detecting language…".
        shell.Tabs.Tab = MainTab.Record;
        await tools.Settle().ConfigureAwait(true);
        controller.ShowSample(new RecordingSample(new ControllerPhase.Recording(), new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English))
        {
            Elapsed = 754.2,
            Level = 0.62,
            MicLevel = 0.58,
            SystemLevel = 0.31,
            LiveNotice = Strings.LivePreviewIsOff,
            LivePreviewTurnedOff = true,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(Shows(Strings.LivePreviewIsOff) && !Shows(Strings.DetectingLanguage) && !Shows(Strings.FirstLinesAppear) && controller.LatestLiveLine is null,
            "the Record tab shows only the off notice: no \"Detecting language…\", no placeholder, no live line");
        await tools.Render("83-record-live-preview-off", window.RenderRoot).ConfigureAwait(true);

        // Under Automatic, the speed probe found the computer too slow: the notice says the way out.
        shell.Settings.LivePreviewMode = LivePreviewMode.Automatic;
        controller.ShowSample(new RecordingSample(new ControllerPhase.Recording(), new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English))
        {
            Elapsed = 754.2,
            Level = 0.62,
            MicLevel = 0.58,
            SystemLevel = 0.31,
            LiveNotice = Strings.LivePreviewTooSlow,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(Shows(Strings.LivePreviewTooSlow), "the Record tab shows the too-slow notice with the way out");
        await tools.Render("84-record-live-preview-too-slow", window.RenderRoot).ConfigureAwait(true);
        controller.ShowSample(idle);
        await tools.Settle().ConfigureAwait(true);

        // PLAN.md 4.13: "No microphone (system audio only)" chosen. The switch shows on and
        // locked although the stored setting is off, and there is no Mic meter.
        var chosenBefore = controller.MicrophoneChoice;
        var storedBefore = shell.Settings.CaptureSystemAudio;
        shell.Settings.CaptureSystemAudio = false;
        controller.MicrophoneChoice = MicrophoneChoice.NoMicrophone;
        await tools.Settle().ConfigureAwait(true);
        tools.Check(controller.CapturesSystemAudio && !shell.Settings.CaptureSystemAudio,
            "no microphone: the switch shows on and the stored setting stays off");
        tools.Check(!ShowsMeter(window.RenderRoot, Strings.MicMeter) && !ShowsMeter(window.RenderRoot, Strings.SystemMeter), "no microphone: no meter row before the session");
        await tools.Render("85-record-no-microphone", window.RenderRoot).ConfigureAwait(true);

        controller.ShowSample(new RecordingSample(new ControllerPhase.Recording(), new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English))
        {
            Elapsed = 754.2,
            Level = 0.45,
            SystemLevel = 0.45,
            RecordsMicrophone = false,
            SilenceWarning = Strings.SilenceWarningSystemOnly(6),
            LiveNotice = Strings.LivePreviewIsOff,
            LivePreviewTurnedOff = true,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(ShowsMeter(window.RenderRoot, Strings.SystemMeter) && !ShowsMeter(window.RenderRoot, Strings.MicMeter), "system audio only: the System meter shows, the Mic meter does not");
        await tools.Render("86-record-system-audio-only", window.RenderRoot).ConfigureAwait(true);
        controller.ShowSample(idle);
        controller.MicrophoneChoice = chosenBefore;
        shell.Settings.CaptureSystemAudio = storedBefore;
        await tools.Settle().ConfigureAwait(true);
    }

    /// <summary>The first scroll viewer of the shown page that can scroll vertically.</summary>
    private static ScrollViewer? Scroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: Visibility.Collapsed }) continue;
            if (child is ScrollViewer { ScrollableHeight: > 0 } found) return found;
            if (Scroller(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>A visible text block or button in <paramref name="root"/> reads <paramref name="text"/> (collapsed subtrees are skipped).</summary>
    private static bool ShowsText(DependencyObject root, string text)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: Visibility.Collapsed }) continue;
            if (child is TextBlock block && block.Text == text) return true;
            if (child is ContentControl { Content: string content } && content == text) return true;
            if (ShowsText(child, text)) return true;
        }
        return false;
    }

    /// <summary>A visible level meter named <paramref name="source"/> ("Mic", "System") is in <paramref name="root"/> (collapsed subtrees are skipped).</summary>
    private static bool ShowsMeter(DependencyObject root, string source)
    {
        var name = Strings.SourceLevel(source);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: Visibility.Collapsed }) continue;
            if (child is ProgressBar bar && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(bar) == name) return true;
            if (ShowsMeter(child, source)) return true;
        }
        return false;
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
