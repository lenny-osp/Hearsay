using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using static Hearsay.App.Tests.QueueRig;

namespace Hearsay.App.Tests;

/// <summary>
/// The background transcription queue (PLAN.md 4.9, 18.10; the port of
/// mac/Hearsay/Features/Transcription/TranscriptionQueue.swift) with a fake
/// engine that decodes 30 s windows and yields like whisper.cpp's step. The
/// Mac has no tests for the queue class itself (its rules are the shared
/// vectors of <c>TranscriptionQueuePolicy</c>, run in Hearsay.Tests); these
/// cases follow the behavior 4.9 lists, one by one. Everything runs on a test
/// UI thread; spool, output and settings are scratch folders.
/// </summary>
public sealed class TranscriptionQueueTests
{
    private static readonly TranscriptSegment LiveCue = new(0, 2, "live preview line");

    // Order and timing

    [Fact]
    public Task JobsRunFirstInFirstOutOneAtATime() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var first = rig.Add();
        var second = rig.Add();
        await WaitUntil(() => first.State == TranscriptionJobState.Running, "the first job to run");
        Assert.Equal(TranscriptionJobState.Waiting, second.State);
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => first.State == TranscriptionJobState.Done, "the first job to finish");
        await WaitUntil(() => second.State == TranscriptionJobState.Running || second.State == TranscriptionJobState.Done, "the second job");
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => second.State == TranscriptionJobState.Done, "the second job to finish");
        Assert.True(rig.Events.IndexOf("done 1") < rig.Events.IndexOf("running 2"));
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(first.Srt!));
        Assert.Equal(2, rig.Engine.Steps.Count);
    });

    [Fact]
    public Task WhenIdleHoldsJobsWhileASessionIsActive() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var job = rig.Add();
        await Stays(() => job.State == TranscriptionJobState.Waiting && rig.Engine.Steps.Count == 0, "the held job");
        Assert.True(rig.Queue.IsHeldForSession);
        Assert.True(rig.Queue.IsPausedForSession(job));
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to run after the session");
        Assert.False(rig.Queue.IsHeldForSession);
    });

    [Fact]
    public Task WhenIdleSuspendsARunningJobAtItsNextWindowAndResumesAfterTheSession() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Queue.SetSessionActive(true);
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to suspend");
        Assert.Equal(1.0 / 3, job.Progress, 3);
        Assert.Contains("suspended 1", rig.Events);
        Assert.True(rig.Queue.IsPausedForSession(job));
        await Stays(() => rig.Engine.Steps.Count == 1, "no new step while recording");

        rig.Queue.SetSessionActive(false);
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish after the session");
        var second = rig.Engine.Steps[1];
        Assert.NotNull(second.ResumeFrom);
        Assert.Equal(30, second.ResumeFrom.ResumeSeconds);
        Assert.Contains("resumed 1", rig.Events);
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
    });

    [Fact]
    public Task ImmediateRunsDuringASessionButYieldsToForegroundWork() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        rig.Queue.SetSessionActive(true);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the job to run while recording");
        Assert.False(rig.Queue.IsHeldForSession);

        rig.Engine.Foreground = 1;
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to yield to the live preview");
        await Stays(() => rig.Engine.Steps.Count == 1, "no step while foreground work waits");

        rig.Engine.Foreground = 0;
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to resume");
        Assert.Equal(2, rig.Engine.Steps.Count);
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
    });

    [Fact]
    public Task ATimingChangeWhileAJobRunsTakesEffectAtTheNextWindow() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        rig.Queue.SetSessionActive(true);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step");
        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        Assert.True(rig.Queue.IsHeldForSession);
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to yield");
        rig.Settings.FinalPassTiming = FinalPassTiming.Immediate;
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish");
    });

    [Fact]
    public Task AJobIsPutBackWhenASessionStartsWhileItsLanguageWasBeingChecked() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        // The decoder runs a window before it can yield, so the queue asks again before each step.
        rig.Engine.DetectGate = new SemaphoreSlim(0);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.DetectCalls == 1, "the language check");
        rig.Queue.SetSessionActive(true);
        rig.Engine.DetectGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Waiting, "the job to wait again");
        Assert.Empty(rig.Engine.Steps);
        Assert.Contains("suspended 1", rig.Events);
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to run");
        Assert.Single(rig.Engine.Steps);
    });

    [Fact]
    public Task AnotherActiveModelStartsASuspendedJobOver() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Engine.Foreground = 1;
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to suspend");
        Assert.True(job.Progress > 0);

        rig.Model = rig.Combine("models\\b.bin");
        rig.Engine.Foreground = 0;
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish on the other model");
        var restarted = rig.Engine.Steps[^1];
        Assert.Null(restarted.ResumeFrom);
        Assert.Equal(rig.Model, restarted.ModelPath);
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
    });

    // Failure (PLAN.md 4.1) and Retry

    [Fact]
    public Task AFailureKeepsTheWavAndTheLivePreviewAndRetryWorks() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.FailStep = () => new InvalidOperationException("boom");
        var job = rig.Add(keep: false, live: [LiveCue]);
        var spoolWav = job.Recording.Path;
        await WaitUntil(() => job.State == TranscriptionJobState.Failed, "the failure");
        Assert.Contains("Transcription failed: boom", job.ErrorMessage);
        Assert.False(File.Exists(spoolWav));
        Assert.NotNull(job.Wav);
        Assert.True(File.Exists(job.Wav));
        Assert.Equal(rig.OutputPath, Path.GetDirectoryName(job.Wav));
        Assert.False(job.Recording.InSpool);
        Assert.Equal(Path.ChangeExtension(job.Wav, ".srt"), job.Srt);
        Assert.Equal(["live preview line"], Texts(job.Srt!));
        Assert.Contains(job.Wav, job.ErrorMessage);
        Assert.Contains(job.Srt!, job.ErrorMessage);
        Assert.True(job.CanRetry);
        Assert.Contains("failed 1", rig.Events);
        Assert.True(rig.Queue.BusyStems.Count == 0, "a failed job is not busy");
        Assert.False(File.Exists(Path.Combine(rig.SpoolPath, TranscriptionQueueManifest.FileName)), "a WAV outside the spool is not continued after a relaunch");

        rig.Engine.FailStep = null;
        var wav = job.Wav;
        var srt = job.Srt!;
        rig.Queue.Retry(job);
        Assert.True(job.State is TranscriptionJobState.Waiting or TranscriptionJobState.Running);
        Assert.Null(job.ErrorMessage);
        Assert.Contains(Path.GetFileNameWithoutExtension(wav), rig.Queue.BusyStems);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the retry to finish");
        Assert.Equal(srt, job.Srt);
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(srt));
        Assert.False(File.Exists(wav), "Keep the recording is off, so a successful retry deletes it");
        Assert.Null(job.Wav);
    });

    [Fact]
    public Task AMissingModelFailsTheJobAndKeepsItsWav() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.ModelFailure = new WhisperEngineException(WhisperEngineError.NoActiveModel, null);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Failed, "the failure");
        Assert.True(job.NeedsModel);
        Assert.True(File.Exists(job.Wav));
        Assert.Equal(rig.OutputPath, Path.GetDirectoryName(job.Wav));
        rig.ModelFailure = null;
        rig.Queue.Retry(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the retry");
        Assert.False(job.NeedsModel);
        Assert.True(File.Exists(job.Wav));
    });

    [Fact]
    public Task ASuccessMovesTheWavBesideTheSrtWithAFreeName() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        File.WriteAllText(Path.Combine(rig.OutputPath, "2026-09-30_09-00-00.srt"), "taken");
        var job = rig.Add(stem: "2026-09-30_09-00-00");
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Equal(Path.Combine(rig.OutputPath, "2026-09-30_09-00-00-2.srt"), job.Srt);
        Assert.Equal(Path.Combine(rig.OutputPath, "2026-09-30_09-00-00-2.wav"), job.Wav);
        Assert.True(File.Exists(job.Wav));
        Assert.Equal("taken", File.ReadAllText(Path.Combine(rig.OutputPath, "2026-09-30_09-00-00.srt")));
    });

    // Use live preview instead

    [Fact]
    public Task UseLivePreviewInsteadCancelsThePassAndWritesTheLiveSegments() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add(live: [LiveCue]);
        var next = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the pass");
        Assert.True(job.CanUseLivePreview);
        rig.Queue.UseLivePreviewInstead(job);
        Assert.True(job.IsUsingLivePreview);
        Assert.False(job.CanUseLivePreview);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the live preview to be saved");
        Assert.Equal(["live preview line"], Texts(job.Srt!));
        // The next job is not held up by the cancelled one.
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => next.State == TranscriptionJobState.Done, "the next job");
    });

    [Fact]
    public Task UseLivePreviewInsteadOnAWaitingJobSavesItAtOnce() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var job = rig.Add(live: [LiveCue]);
        rig.Queue.UseLivePreviewInstead(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the live preview to be saved");
        Assert.Equal(["live preview line"], Texts(job.Srt!));
        Assert.Empty(rig.Engine.Steps);
    });

    // Language

    [Fact]
    public Task AnUndecidedAutoLanguageIsSettledRightAfterStopWhateverTheTiming() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        rig.Engine.Detection = new DetectionResult("de", 0.97f, 1, []);
        var sink = new LiveSink(null);
        sink.ChunkQueued();
        var job = rig.Add(LanguageChoice.Auto, live: [], sink: sink);
        Assert.True(job.IsDetectingLanguage);
        await WaitUntil(() => job.Language == TranscriptLanguage.German, "the language");
        Assert.Equal(1, rig.Engine.DetectCalls);
        Assert.Equal(TranscriptLanguage.German, sink.Language);
        Assert.Contains("language 1", rig.Events);
        Assert.Equal(TranscriptionJobState.Waiting, job.State);
        Assert.Empty(rig.Engine.Steps);
    });

    [Fact]
    public Task AnUnsureAutoDetectionFallsBackToThePreferredLanguageWithANotice() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.Detection = new DetectionResult(null, 0, 0, []);
        var job = rig.Add(LanguageChoice.Auto, preferred: TranscriptLanguage.German);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Equal(TranscriptLanguage.German, rig.Engine.Steps[0].Language);
        Assert.IsType<LanguageNotice.Fallback>(job.LanguageNotice);
    });

    [Fact]
    public Task AFailedDetectionFallsBackToThePreferredLanguage() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.DetectFailure = new InvalidOperationException("no detection");
        var job = rig.Add(LanguageChoice.Auto, preferred: TranscriptLanguage.Spanish);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Equal(TranscriptLanguage.Spanish, rig.Engine.Steps[0].Language);
    });

    [Fact]
    public Task APickedLanguageBeforeThePassStartsIsUsedByThePass() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        rig.Engine.Detection = new DetectionResult(null, 0, 0, []);
        var job = rig.Add(LanguageChoice.Auto);
        await WaitUntil(() => job.LanguageNotice is not null, "the fallback notice");
        Assert.True(job.CanChangeLanguage);
        rig.Queue.TranscribeAgain(job, TranscriptLanguage.Spanish);
        Assert.Null(job.LanguageNotice);
        Assert.Equal(TranscriptLanguage.Spanish, job.Language);
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Equal(TranscriptLanguage.Spanish, rig.Engine.Steps[0].Language);
        Assert.Equal(0, rig.Engine.RerunCalls);
    });

    [Fact]
    public Task OnlyTheNewestFinishedJobKeepsItsSamplesForTranscribeAgain() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.Detection = new DetectionResult(null, 0, 0, []);
        var older = rig.Add(LanguageChoice.Auto, keep: false);
        await WaitUntil(() => older.State == TranscriptionJobState.Done, "the first job");
        Assert.True(older.CanChangeLanguage);
        var newer = rig.Add(LanguageChoice.Auto, keep: false);
        await WaitUntil(() => newer.State == TranscriptionJobState.Done, "the second job");
        Assert.False(older.CanChangeLanguage);
        Assert.Equal("Could not transcribe again: the recording was not kept.", older.RerunError);
        Assert.True(newer.CanChangeLanguage);

        rig.NotesOnScreen = true;
        rig.Queue.TranscribeAgain(newer, TranscriptLanguage.German);
        Assert.True(newer.IsRerunning);
        await WaitUntil(() => !newer.IsRerunning, "the re-run");
        Assert.Equal([TranscriptLanguage.German], rig.Engine.RerunLanguages);
        Assert.Equal(["again in de"], Texts(newer.Srt!));
        Assert.Null(newer.RerunError);
        Assert.Null(newer.LanguageNotice);
        Assert.True(newer.OffersNotes, "notes cannot open while another flow is on screen");
    });

    [Fact]
    public Task TranscribeAgainWithAKeptWavReadsItFromTheOutputFolder() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.Detection = new DetectionResult(null, 0, 0, []);
        var job = rig.Add(LanguageChoice.Auto, keep: true);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.NotNull(job.Wav);
        Assert.True(job.CanChangeLanguage);
        rig.Queue.TranscribeAgain(job, TranscriptLanguage.Spanish);
        await WaitUntil(() => !job.IsRerunning && rig.Engine.RerunCalls == 1, "the re-run");
        Assert.Equal(["again in es"], Texts(job.Srt!));
    });

    // Notes (PLAN.md 4.9 item 4)

    [Fact]
    public Task NotesOpenWhenNoSessionAndNoNotesFlowAreActive() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var requested = 0;
        rig.Queue.NotesRequested += (_, _) => requested++;
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Equal(1, requested);
        Assert.False(job.OffersNotes);
        var request = rig.Queue.TakeNotesRequest();
        Assert.NotNull(request);
        Assert.Equal(job.Srt, request.Srt);
        Assert.Equal(TranscriptLanguage.English, request.Language);
        Assert.Equal(job.Id, request.JobId);
        Assert.Null(rig.Queue.TakeNotesRequest());
    });

    [Fact]
    public Task NotesAreOfferedOnTheRowWhileASessionIsActive() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var requested = 0;
        rig.Queue.NotesRequested += (_, _) => requested++;
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.True(job.OffersNotes);
        Assert.Equal(0, requested);
        Assert.Null(rig.Queue.PendingNotesRequest);
        rig.Queue.NotesStarted(job);
        Assert.False(job.OffersNotes);
    });

    [Fact]
    public Task NotesAreOfferedOnTheRowWhileAnotherNotesFlowIsOnScreen() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.NotesOnScreen = true;
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.True(job.OffersNotes);
        Assert.Null(rig.Queue.PendingNotesRequest);
    });

    [Fact]
    public Task ARequestNobodyTookBecomesAnOfferWhenASessionStarts() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.NotNull(rig.Queue.PendingNotesRequest);
        rig.Queue.SetSessionActive(true);
        Assert.Null(rig.Queue.PendingNotesRequest);
        Assert.True(job.OffersNotes);
    });

    [Fact]
    public Task ARequestIsCheckedAgainWhenItIsTaken() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        rig.NotesOnScreen = true;
        Assert.Null(rig.Queue.TakeNotesRequest());
        Assert.True(job.OffersNotes);
    });

    [Fact]
    public Task ANewerRequestReplacesAnUntakenOneWhichTurnsIntoAnOffer() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var first = rig.Add();
        await WaitUntil(() => first.State == TranscriptionJobState.Done, "the first job");
        var second = rig.Add();
        await WaitUntil(() => second.State == TranscriptionJobState.Done, "the second job");
        Assert.True(first.OffersNotes);
        Assert.False(second.OffersNotes);
        Assert.Equal(second.Id, rig.Queue.PendingNotesRequest?.JobId);
    });

    [Fact]
    public Task AStartClearsFinishedRowsWhoseNotesOpenedButKeepsOffersAndFailures() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var opened = rig.Add();
        await WaitUntil(() => opened.State == TranscriptionJobState.Done, "the first job");
        rig.Queue.TakeNotesRequest();
        rig.Queue.SetSessionActive(true);
        var offered = rig.Add();
        await WaitUntil(() => offered.State == TranscriptionJobState.Done, "the second job");
        Assert.True(offered.OffersNotes);
        rig.Engine.FailStep = () => new InvalidOperationException("boom");
        var failed = rig.Add();
        await WaitUntil(() => failed.State == TranscriptionJobState.Failed, "the failure");
        rig.Queue.SetSessionActive(false);

        rig.Queue.DismissFinishedForNewSession();
        Assert.DoesNotContain(opened, rig.Queue.Jobs);
        Assert.Contains(offered, rig.Queue.Jobs);
        Assert.Contains(failed, rig.Queue.Jobs);
        rig.Queue.Dismiss(failed);
        Assert.DoesNotContain(failed, rig.Queue.Jobs);
    });

    [Fact]
    public Task DismissDoesNothingToAJobThatIsNotFinished() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        rig.Queue.SetSessionActive(true);
        var job = rig.Add();
        rig.Queue.Dismiss(job);
        Assert.Contains(job, rig.Queue.Jobs);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheNotesFlowsRenamesAreFollowedByTheJob() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        var old = job.Srt!;
        var renamedSrt = Path.Combine(rig.OutputPath, "Weekly sync.srt");
        var renamedWav = Path.Combine(rig.OutputPath, "Weekly sync.wav");
        rig.Queue.FilesRenamed(old, [renamedSrt, renamedWav]);
        Assert.Equal(renamedSrt, job.Srt);
        Assert.Equal(renamedWav, job.Wav);
        Assert.Equal([renamedSrt, renamedWav], job.Files);
    });

    // Persistence (PLAN.md 4.9 "Persistence and recovery")

    [Fact]
    public Task TheQueueIsSavedWhenItChangesAndTheLiveSegmentsSoonAfter() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var job = rig.Add(LanguageChoice.Fixed(TranscriptLanguage.German), keep: false, live: [LiveCue]);
        var store = new TranscriptionQueueStore(rig.Spool);
        var saved = Assert.Single(store.Load().Manifest.Jobs);
        Assert.Equal(job.Id, saved.Id);
        Assert.Equal(Path.GetFileName(job.Recording.Path), saved.WavFileName);
        Assert.Equal(TranscriptionJobState.Waiting, saved.State);
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.German), saved.LanguageChoice);
        Assert.False(saved.KeepRecording);
        await WaitUntil(() => File.Exists(store.LiveSegmentsPath(job.Id)), "the live segments file");
        Assert.Equal(["live preview line"], [.. store.ReadLiveSegments(job.Id).Select(cue => cue.Text)]);

        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Empty(store.Load().Manifest.Jobs);
        Assert.False(File.Exists(store.LiveSegmentsPath(job.Id)), "the final SRT replaces the live preview");
    });

    [Fact]
    public Task ALaunchQueuesTheSavedJobsAgainAndDropsDoneFailedAndMissingOnes() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        string[] pending = ["2026-09-30_08-00-01", "2026-09-30_08-00-02", "2026-09-30_08-00-03"];
        foreach (var stem in pending) rig.NewWav(stem);
        rig.NewWav("2026-09-30_08-00-04");
        rig.NewWav("2026-09-30_08-00-05");
        var stop = new DateTimeOffset(2026, 9, 30, 8, 30, 0, TimeSpan.Zero);
        TranscriptionQueueManifest.Job Entry(string id, string stem, TranscriptionJobState state, TranscriptLanguage? settled = null) =>
            new(id, stem + ".wav", stop, LanguageChoice.Auto, settled, null, true, state, id == "b" ? "Team sync" : null);
        new TranscriptionQueueStore(rig.Spool).Save(new TranscriptionQueueManifest(
        [
            Entry("a", pending[0], TranscriptionJobState.Waiting),
            Entry("b", pending[1], TranscriptionJobState.Running, TranscriptLanguage.German),
            Entry("c", pending[2], TranscriptionJobState.Suspended),
            Entry("d", "2026-09-30_08-00-04", TranscriptionJobState.Done),
            Entry("e", "2026-09-30_08-00-05", TranscriptionJobState.Failed),
            Entry("f", "2026-09-30_08-00-06", TranscriptionJobState.Waiting),
        ]));
        new TranscriptionQueueStore(rig.Spool).WriteLiveSegments([LiveCue], "b");

        var queue = rig.MakeQueue();
        queue.SetSessionActive(true);
        queue.Restore();
        Assert.Equal(["a", "b", "c"], [.. queue.Jobs.Select(job => job.Id)]);
        Assert.All(queue.Jobs, job => Assert.Equal(TranscriptionJobState.Waiting, job.State));
        var b = queue.Jobs[1];
        Assert.Equal("Team sync", b.DisplayName);
        Assert.Equal("Team sync", b.Title);
        Assert.Equal(TranscriptLanguage.German, b.Language);
        Assert.Equal(["live preview line"], [.. b.LiveSegments.Select(cue => cue.Text)]);
        Assert.True(b.LiveEnabled);
        Assert.Equal(3, queue.PendingCount);
        var saved = new TranscriptionQueueStore(rig.Spool).Load().Manifest.Jobs;
        Assert.Equal(["a", "b", "c"], [.. saved.Select(job => job.Id)]);
        Assert.Contains("queued 1", rig.Events);

        queue.SetSessionActive(false);
        await WaitUntil(() => queue.Jobs.All(job => job.State == TranscriptionJobState.Done), "the restored jobs");
        Assert.Equal(["a", "b", "c"], [.. queue.Jobs.Select(job => job.Id)]);
        Assert.Equal("done 1", rig.Events.First(e => e.StartsWith("done", StringComparison.Ordinal)));
    });

    // Quit and update install

    [Fact]
    public Task QuitCountsAndUpdateInstallBlocksWhileRecordingsAreNotTranscribed() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        Assert.Equal(0, rig.Queue.PendingCount);
        Assert.False(rig.Queue.BlocksUpdateInstall);
        rig.Queue.SetSessionActive(true);
        var first = rig.Add();
        var second = rig.Add();
        Assert.Equal(2, rig.Queue.PendingCount);
        Assert.True(rig.Queue.BlocksUpdateInstall);
        rig.Engine.FailStep = () => new InvalidOperationException("boom");
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => first.State == TranscriptionJobState.Failed && second.State == TranscriptionJobState.Failed, "both to fail");
        Assert.Equal(0, rig.Queue.PendingCount);
        Assert.False(rig.Queue.BlocksUpdateInstall, "done and failed rows do not block");
    });

    [Fact]
    public Task QuitCancelsAStepAtItsNextWindowAndKeepsTheWavAndTheQueue() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        var waiting = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step");
        Assert.True(rig.Queue.HasWorkInFlight);
        await rig.Queue.PrepareForQuitAsync();
        Assert.False(rig.Queue.HasWorkInFlight);
        Assert.True(File.Exists(job.Recording.Path));
        Assert.True(File.Exists(waiting.Recording.Path));
        Assert.Empty(Directory.GetFiles(rig.OutputPath));
        await Stays(() => rig.Engine.Steps.Count == 1, "no step after the quit began");
        var saved = new TranscriptionQueueStore(rig.Spool).Load().Manifest.Jobs;
        Assert.Equal(2, saved.Count);
        Assert.All(saved, entry => Assert.Equal(TranscriptionJobState.Waiting, entry.State));
    });

    [Fact]
    public Task AQuitThatDoesNotHappenPutsTheStoppedJobsBackInLine() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step");
        await rig.Queue.PrepareForQuitAsync();
        rig.Queue.QuitCancelled();
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to run again");
        Assert.Equal(2, rig.Engine.Steps.Count);
    });

    // Rows and busy files

    [Fact]
    public Task BusyStemsAreTheOutputFolderFilesOfPendingJobsAndReRuns() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var inSpool = rig.Add();
        Assert.Empty(rig.Queue.BusyStems);
        Assert.Empty(rig.Queue.BusyFiles);
        rig.Engine.FailStep = () => new InvalidOperationException("boom");
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => inSpool.State == TranscriptionJobState.Failed, "the failure");
        rig.Engine.FailStep = null;
        rig.Queue.SetSessionActive(true);
        rig.Queue.Retry(inSpool);
        var stem = Path.GetFileNameWithoutExtension(inSpool.Wav);
        Assert.Contains(stem!, rig.Queue.BusyStems);
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => inSpool.State == TranscriptionJobState.Done, "the retry");
        Assert.Empty(rig.Queue.BusyStems);
    });

    [Fact]
    public Task TheTitleIsTheMeetingNameElseTheStartTimeOfTheRecording() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        rig.Queue.SetSessionActive(true);
        var job = rig.Add(stem: "2026-09-30_10-15-00");
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 15, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 30, 10, 15, 0)))
            .ToString("g", System.Globalization.CultureInfo.CurrentCulture), job.Title);
        job.DisplayName = "Weekly sync";
        Assert.Equal("Weekly sync", job.Title);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheFeaturedJobIsTheOnlyJobWhileNoSessionIsActive() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        Assert.Null(rig.Queue.FeaturedJob);
        rig.Queue.SetSessionActive(true);
        var first = rig.Add();
        Assert.Null(rig.Queue.FeaturedJob);
        rig.Queue.SetSessionActive(false);
        Assert.Same(first, rig.Queue.FeaturedJob);
        rig.Queue.SetSessionActive(true);
        rig.Add();
        rig.Queue.SetSessionActive(false);
        Assert.Null(rig.Queue.FeaturedJob);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheQueueRaisesItsEventsOnTheUiThreadOnly() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        var ui = SynchronizationContext.Current;
        var offThread = new List<string>();
        void Check(string what)
        {
            if (SynchronizationContext.Current != ui) offThread.Add(what);
        }
        rig.Queue.Changed += (_, _) => Check("Changed");
        rig.Queue.PropertyChanged += (_, e) => Check("queue " + e.PropertyName);
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        job.PropertyChanged += (_, e) => Check("job " + e.PropertyName);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step");
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
        Assert.Empty(offThread);
    });

    // Output helpers

    [Fact]
    public Task KeepAfterFailureLeavesAnExistingTranscriptAlone() => RunAsync(FinalPassTiming.Immediate, rig =>
    {
        var wav = Path.Combine(rig.OutputPath, "2026-09-30_10-00-00.wav");
        File.WriteAllBytes(wav, [0]);
        var srt = Path.Combine(rig.OutputPath, "2026-09-30_10-00-00.srt");
        File.WriteAllText(srt, "existing");
        var kept = TranscriptOutput.KeepAfterFailure(
            "Transcription failed: x", new TranscriptOutput.PendingRecording(wav, InSpool: false), [LiveCue], srt, rig.Settings);
        Assert.Equal(srt, kept.Srt);
        Assert.Equal("existing", File.ReadAllText(srt));
        Assert.Equal($"Transcription failed: x\nThe recording is kept at {wav}.", kept.Message);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SaveTranscriptFailsForAMissingRecording() => RunAsync(FinalPassTiming.Immediate, rig =>
    {
        var result = TranscriptOutput.SaveTranscript(
            [LiveCue], new TranscriptOutput.PendingRecording(Path.Combine(rig.SpoolPath, "gone.wav"), InSpool: true), true, rig.Settings);
        Assert.Null(result.Saved);
        Assert.Equal("The recording is missing.", result.Failure);
        return Task.CompletedTask;
    });
}
