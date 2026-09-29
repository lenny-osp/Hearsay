using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Recovery;
using Hearsay.Core.Audio;
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
/// <see cref="RecordingController.ShowSample"/> and
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

        controller.ShowSample(new RecordingSample(new ControllerPhase.Transcribing(0.42), new SessionLanguageTracker(english, TranscriptLanguage.English))
        {
            Elapsed = 1834,
            LiveSegments = cues,
            LivePreviewEnabled = true,
            CanUseLivePreview = true,
            FinishedRecording = null,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(shell.Recording.StateText == Strings.StateTranscribing(42), $"the tray shows the final pass ({shell.Recording.StateText})");
        await tools.Render("33-record-final-pass", window.RenderRoot).ConfigureAwait(true);

        // Auto could not tell: the fallback notice with a button per other language.
        window.ResizeClient(MainWindow.DefaultWidth, 1200);
        var fallback = new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English);
        fallback.Finish(new DetectedLanguage("de", 0.41f));
        controller.ShowSample(new RecordingSample(new ControllerPhase.Finished(srt, wav), fallback)
        {
            Elapsed = 1834,
            LiveSegments = cues,
            Notice = LanguageNotice.From(fallback.Decision),
            FinishedTranscript = srt,
            FinishedRecording = wav,
        });
        await tools.Settle().ConfigureAwait(true);
        tools.Check(controller.LanguageNotice is LanguageNotice.Fallback, "the Auto fallback notice shows");
        await tools.Render("34-record-language-notice", window.RenderRoot).ConfigureAwait(true);

        // The result row alone (a session without live preview), so its buttons show.
        controller.ShowSample(new RecordingSample(new ControllerPhase.Finished(srt, wav), new SessionLanguageTracker(english, TranscriptLanguage.English))
        {
            Elapsed = 1834,
            FinishedTranscript = srt,
            FinishedRecording = wav,
        });
        await tools.Settle().ConfigureAwait(true);
        await tools.Render("35-record-result", window.RenderRoot).ConfigureAwait(true);
        controller.ShowSample(new RecordingSample(ControllerPhase.IdleState, new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English)));
        tools.Check(shell.Recording.Phase == MenuBar.RecordingPhase.Idle && shell.Recording.BusyFiles.Count == 0, "the tray is idle again");

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

    /// <summary>The cues of en-30s.expected.srt as a live preview.</summary>
    private static List<TranscriptSegment> SampleCues()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "DebugSamples", "en-30s.expected.srt");
        return File.Exists(fixture)
            ? [.. Srt.Parse(File.ReadAllText(fixture))]
            : [new TranscriptSegment(0, 2, "Sample")];
    }
}
