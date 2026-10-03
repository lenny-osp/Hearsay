using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Settings;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using static Hearsay.App.Tests.QueueRig;

namespace Hearsay.App.Tests;

/// <summary>
/// The Record tab's "Transcription queue" list (PLAN.md 4.9 "UI"): when it
/// shows, each row's state text and which actions each state offers, and the
/// Settings picker of the final-pass timing. The Mac has no test for
/// <c>QueueRow</c> in RecordView.swift; these follow the rules its
/// <c>body</c>, <c>stateText</c> and <c>actions</c> apply. Jobs are the
/// queue's stubbed samples (<see cref="TranscriptionQueue.InsertSample"/>).
/// </summary>
public sealed class QueueRowsTests
{
    private const string Wav = @"C:\spool\2026-09-30_10-00-00.wav";
    private const string Srt = @"C:\out\2026-09-30_10-00-00.srt";

    [Fact]
    public Task TheListShowsForASessionOrTwoJobsButNotForTheSingleMeeting() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        var queue = rig.Queue;
        Assert.False(QueueRows.ShowsList(queue));
        queue.InsertSample(Wav, TranscriptionJobState.Running, 0.5);
        // One job and no session: the featured single-meeting view.
        Assert.NotNull(queue.FeaturedJob);
        Assert.False(QueueRows.ShowsList(queue));
        // One job and a session: the list.
        queue.SetSessionActive(true);
        Assert.Null(queue.FeaturedJob);
        Assert.True(QueueRows.ShowsList(queue));
        queue.SetSessionActive(false);
        Assert.False(QueueRows.ShowsList(queue));
        // Two jobs: the list.
        queue.InsertSample(Wav.Replace("10-00-00", "11-00-00", StringComparison.Ordinal), TranscriptionJobState.Waiting);
        Assert.True(QueueRows.ShowsList(queue));
        // A session and no job: nothing to list.
        queue.ClearSamples();
        queue.SetSessionActive(true);
        Assert.False(QueueRows.ShowsList(queue));
        return Task.CompletedTask;
    });

    [Fact]
    public Task StateTextNamesEachState() => RunAsync(FinalPassTiming.Immediate, rig =>
    {
        var queue = rig.Queue;
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.454);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.5);
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        var failed = queue.InsertSample(Wav, TranscriptionJobState.Failed);
        Assert.Equal("Waiting", QueueRows.StateText(waiting, pausedForSession: false));
        Assert.Equal("Paused while recording", QueueRows.StateText(waiting, pausedForSession: true));
        Assert.Equal("Transcribing 45%", QueueRows.StateText(running, false));
        Assert.Equal("Transcribing 50%", QueueRows.StateText(suspended, false));
        Assert.Equal("Paused while recording", QueueRows.StateText(suspended, true));
        Assert.Equal("Done", QueueRows.StateText(done, false));
        Assert.Equal("Failed", QueueRows.StateText(failed, false));
        // A re-run in another language shows its own progress, whatever the state.
        done.RerunProgress = 0.25;
        done.IsRerunning = true;
        Assert.Equal("Transcribing 25%", QueueRows.StateText(done, false));
        return Task.CompletedTask;
    });

    [Fact]
    public Task WhenIdleQueueSaysPausedOnlyWhileASessionHoldsIt() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        var queue = rig.Queue;
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.3);
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        Assert.False(queue.IsPausedForSession(waiting));
        queue.SetSessionActive(true);
        Assert.True(queue.IsPausedForSession(waiting));
        Assert.True(queue.IsPausedForSession(suspended));
        Assert.False(queue.IsPausedForSession(done));
        Assert.Equal("Paused while recording", QueueRows.StateText(suspended, queue.IsPausedForSession(suspended)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ImmediateNeverPausesForASession() => RunAsync(FinalPassTiming.Immediate, rig =>
    {
        var waiting = rig.Queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        rig.Queue.SetSessionActive(true);
        Assert.False(rig.Queue.IsPausedForSession(waiting));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ActionsByState() => RunAsync(FinalPassTiming.Immediate, rig =>
    {
        var queue = rig.Queue;
        // Waiting and running: the live preview can stand in; no files to show, nothing to dismiss.
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        Assert.Equal(new QueueRowActions(UseLivePreview: true, false, false, false, false, Reveal: false, Dismiss: false), QueueRows.Actions(waiting));
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.4);
        Assert.Equal(QueueRows.Actions(waiting), QueueRows.Actions(running));
        // Saving the live preview: the button is replaced by a note.
        running.IsUsingLivePreview = true;
        Assert.Equal(new QueueRowActions(false, SavingLivePreview: true, false, false, false, false, false), QueueRows.Actions(running));
        // Done: Open Transcript, Reveal, Dismiss; Generate Notes only when the queue offers it.
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        Assert.Equal(new QueueRowActions(false, false, OpenTranscript: true, GenerateNotes: false, false, Reveal: true, Dismiss: true), QueueRows.Actions(done));
        done.OffersNotes = true;
        Assert.True(QueueRows.Actions(done).GenerateNotes);
        // Failed: Try Again, Reveal, Dismiss.
        var failed = queue.InsertSample(Wav, TranscriptionJobState.Failed);
        Assert.Equal(new QueueRowActions(false, false, false, false, TryAgain: true, Reveal: true, Dismiss: true), QueueRows.Actions(failed));
        // A done job being transcribed again cannot be dismissed.
        done.IsRerunning = true;
        Assert.False(QueueRows.Actions(done).Dismiss);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheProgressBarShowsForARunningPassOnly() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        var queue = rig.Queue;
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.4);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.4);
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        Assert.False(QueueRows.ShowsProgress(waiting, false));
        Assert.True(QueueRows.ShowsProgress(running, false));
        Assert.True(QueueRows.ShowsProgress(suspended, false));
        Assert.False(QueueRows.ShowsProgress(suspended, pausedForSession: true));
        Assert.False(QueueRows.ShowsProgress(done, false));
        done.IsRerunning = true;
        Assert.True(QueueRows.ShowsProgress(done, false));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheLanguageBannerShowsOnDoneAndWaitingRows() => RunAsync(FinalPassTiming.Immediate, rig =>
    {
        var queue = rig.Queue;
        var tracker = new SessionLanguageTracker(LanguageChoice.Fixed(TranscriptLanguage.German), TranscriptLanguage.English);
        tracker.Record(new DetectedLanguage("en", 0.9993f), 30 * 16_000);
        var notice = LanguageNotice.From(tracker.Decision);
        Assert.NotNull(notice);
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt, notice: notice);
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting, notice: notice);
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.2, notice: notice);
        var failed = queue.InsertSample(Wav, TranscriptionJobState.Failed, notice: notice);
        var plain = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        Assert.True(QueueRows.ShowsLanguageNotice(done));
        Assert.True(QueueRows.ShowsLanguageNotice(waiting));
        Assert.False(QueueRows.ShowsLanguageNotice(running));
        Assert.False(QueueRows.ShowsLanguageNotice(failed));
        Assert.False(QueueRows.ShowsLanguageNotice(plain));
        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void RowTextsComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "app", "Transcription queue"), Strings.TranscriptionQueue);
        Assert.Equal(Translations.Text(language, "app", "Waiting"), Strings.QueueWaiting);
        Assert.Equal(Translations.Text(language, "app", "Paused while recording"), Strings.QueuePausedWhileRecording);
        Assert.Equal(Translations.Text(language, "app", "queue.state.done"), Strings.QueueDone);
        Assert.Equal(Translations.Text(language, "app", "Failed"), Strings.QueueFailed);
        Assert.Equal(Translations.Format(language, "app", "Transcribing %lld%%", 42), Strings.QueueTranscribing(42));
        Assert.Contains("42", Strings.QueueTranscribing(42), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimingPickerHasTheThreeRowsInOrderAndMapsBothWays()
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        Assert.Equal([FinalPassTiming.Immediate, FinalPassTiming.WhenIdle, FinalPassTiming.Manual], FinalPassTimingChoices.All);
        Assert.Equal("Right away (in the background)", FinalPassTimingChoices.Label(FinalPassTiming.Immediate));
        Assert.Equal("When no recording is running", FinalPassTimingChoices.Label(FinalPassTiming.WhenIdle));
        Assert.Equal("When I start them", FinalPassTimingChoices.Label(FinalPassTiming.Manual));
        foreach (var timing in FinalPassTimingChoices.All)
        {
            Assert.Equal(timing, FinalPassTimingChoices.At(FinalPassTimingChoices.IndexOf(timing)));
        }
        Assert.Equal(2, FinalPassTimingChoices.IndexOf(FinalPassTiming.Manual));
        Assert.Null(FinalPassTimingChoices.At(-1));
        Assert.Null(FinalPassTimingChoices.At(3));
    }

    [Fact]
    public void ThePickerFollowsTheSettingAndWritesIt()
    {
        using var folder = new ScratchFolder();
        var settings = new AppSettings(folder.Path);
        // Windows' default is when idle, the picker's second row.
        Assert.Equal(FinalPassTiming.WhenIdle, settings.FinalPassTiming);
        Assert.Equal(1, FinalPassTimingChoices.IndexOf(settings.FinalPassTiming));
        // Choosing the first row (what the view's SelectionChanged does) stores the shared value.
        settings.FinalPassTiming = FinalPassTimingChoices.At(0) ?? throw new InvalidOperationException("no first row");
        Assert.Equal(FinalPassTiming.Immediate, settings.FinalPassTiming);
        Assert.Equal(FinalPassTiming.Immediate, new AppSettings(folder.Path).FinalPassTiming);
        // The third row stores "manual" and a stored "manual" selects that row (it selected none before WI-2).
        settings.FinalPassTiming = FinalPassTimingChoices.At(2) ?? throw new InvalidOperationException("no third row");
        Assert.Equal(FinalPassTiming.Manual, settings.FinalPassTiming);
        Assert.Equal(2, FinalPassTimingChoices.IndexOf(new AppSettings(folder.Path).FinalPassTiming));
    }

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void TimingTextsComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "core", "Right away (in the background)"), Strings.TimingImmediate);
        Assert.Equal(Translations.Text(language, "core", "When no recording is running"), Strings.TimingWhenIdle);
        Assert.Equal(Translations.Text(language, "app", "Transcribe finished recordings"), Strings.FinalPassTimingLabel);
        Assert.Equal(Translations.Text(language, "core", "When I start them"), Strings.TimingManual);
        Assert.Equal(
            Translations.Text(language, "app", "The next recording can start at once, whichever you choose. With “When I start them”, recordings wait on the Record tab until you click Transcribe or Transcribe All."),
            Strings.FinalPassTimingCaption);
    }

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void TheCaptionQuotesTheTranslatedLabelsExactly(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        var caption = Strings.FinalPassTimingCaption;
        Assert.Contains(Strings.TimingManual, caption, StringComparison.Ordinal);
        Assert.Contains(Strings.TranscribeButton, caption, StringComparison.Ordinal);
        Assert.Contains(Strings.TranscribeAll, caption, StringComparison.Ordinal);
        Assert.Contains(Translations.Text(language, "app", "Record"), caption, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void HeldRowTextsComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "app", "Not transcribed yet"), Strings.QueueNotTranscribedYet);
        Assert.Equal(Translations.Format(language, "app", "On hold · %lld%%", 42), Strings.QueueOnHold(42));
        Assert.Contains("42", Strings.QueueOnHold(42), StringComparison.Ordinal);
        Assert.Equal(Translations.Text(language, "app", "Transcribe All"), Strings.TranscribeAll);
        Assert.Equal(Translations.Text(language, "app", "Hold"), Strings.Hold);
        Assert.Equal(Translations.Text(language, "app", "Stops transcribing for now. Transcribe continues where it stopped."), Strings.HoldTooltip);
        Assert.Equal(Translations.Text(language, "app", "Transcribe"), Strings.TranscribeButton);
        Assert.Equal(Translations.Text(language, "app", "They stay in the queue until you transcribe them."), Strings.TheyStayInQueue);
        Assert.Equal(Translations.Format(language, "app", "Not transcribed yet · in queue: %lld", 3), Strings.QueueLineHeld(3));
        // Each is its own text, not a copy of a neighbour.
        Assert.NotEqual(Strings.QueueNotTranscribedYet, Strings.QueueWaiting);
        Assert.NotEqual(Strings.Hold, Strings.TranscribeAll);
    }

    [Fact]
    public void TheQuitAlertSecondLineIsTheMacLineUnlessEveryPendingJobIsHeld()
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        Assert.Equal("Hearsay continues with them the next time it opens.", Strings.QuitQueueMessage(allPendingHeld: false));
        Assert.Equal("They stay in the queue until you transcribe them.", Strings.QuitQueueMessage(allPendingHeld: true));
    }

    [Fact]
    public Task TheQuitAlertReadsTheQueueAllPendingHeld() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        var queue = rig.Queue;
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        var first = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var second = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.3);
        Assert.True(queue.AllPendingHeld);
        Assert.Equal(2, queue.PendingCount);
        Assert.Equal("They stay in the queue until you transcribe them.", Strings.QuitQueueMessage(queue.AllPendingHeld));
        // One released job: the alert keeps the Mac line.
        queue.Release(first);
        Assert.False(queue.AllPendingHeld);
        Assert.Equal("Hearsay continues with them the next time it opens.", Strings.QuitQueueMessage(queue.AllPendingHeld));
        queue.Hold(first);
        Assert.True(queue.AllPendingHeld);
        Assert.True(second.IsHeld);
        return Task.CompletedTask;
    });

    // "When I start them" rows (PLAN.md 4.11)

    [Fact]
    public Task AJobIsHeldOnlyUnderManualWhenPendingAndNotReleased() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        var queue = rig.Queue;
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.4);
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        var failed = queue.InsertSample(Wav, TranscriptionJobState.Failed);
        Assert.True(QueueRows.IsHeld(waiting, FinalPassTiming.Manual));
        Assert.True(QueueRows.IsHeld(suspended, FinalPassTiming.Manual));
        Assert.False(QueueRows.IsHeld(done, FinalPassTiming.Manual));
        Assert.False(QueueRows.IsHeld(failed, FinalPassTiming.Manual));
        Assert.False(QueueRows.IsHeld(waiting, FinalPassTiming.WhenIdle));
        Assert.False(QueueRows.IsHeld(waiting, FinalPassTiming.Immediate));
        // The row rule is the queue own.
        foreach (var job in queue.Jobs) Assert.Equal(queue.IsHeld(job), QueueRows.IsHeld(job, FinalPassTiming.Manual));
        waiting.IsReleased = true;
        Assert.False(QueueRows.IsHeld(waiting, FinalPassTiming.Manual));
        Assert.Equal(queue.IsHeld(waiting), QueueRows.IsHeld(waiting, FinalPassTiming.Manual));
        return Task.CompletedTask;
    });

    [Fact]
    public Task HeldRowsReadNotTranscribedYetOrOnHoldWithTheirProgress() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        var queue = rig.Queue;
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.454);
        Assert.Equal("Not transcribed yet", QueueRows.StateText(waiting, pausedForSession: false, held: true));
        Assert.Equal("On hold · 45%", QueueRows.StateText(suspended, pausedForSession: false, held: true));
        // A running job that is held (its Hold has not taken effect yet) reads the same.
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.5);
        Assert.Equal("On hold · 50%", QueueRows.StateText(running, false, held: true));
        // Released jobs read as before, including "Paused while recording" for those waiting for a session.
        Assert.Equal("Waiting", QueueRows.StateText(waiting, false));
        Assert.Equal("Paused while recording", QueueRows.StateText(waiting, true));
        Assert.Equal("Paused while recording", QueueRows.StateText(suspended, true));
        Assert.Equal("Transcribing 45%", QueueRows.StateText(suspended, false));
        // A held job is never paused for a session; the queue says so too.
        queue.SetSessionActive(true);
        Assert.False(queue.IsPausedForSession(waiting));
        Assert.False(queue.IsPausedForSession(suspended));
        queue.Release(waiting);
        Assert.True(queue.IsPausedForSession(waiting));
        Assert.Equal("Paused while recording",
            QueueRows.StateText(waiting, queue.IsPausedForSession(waiting), QueueRows.IsHeld(waiting, FinalPassTiming.Manual)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task HeldRowsOfferTranscribeAndReleasedPendingRowsOfferHoldOnlyUnderManual() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        var queue = rig.Queue;
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.4);
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.4);
        running.IsReleased = true;
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        var failed = queue.InsertSample(Wav, TranscriptionJobState.Failed);
        const FinalPassTiming manual = FinalPassTiming.Manual;

        // Held: Transcribe, the live preview stays, no Hold.
        foreach (var held in new[] { waiting, suspended })
        {
            var actions = QueueRows.Actions(held, manual);
            Assert.True(actions.Transcribe);
            Assert.False(actions.Hold);
            Assert.True(actions.UseLivePreview);
            Assert.False(actions.Reveal);
            Assert.False(actions.Dismiss);
        }
        // Released and pending: Hold, no Transcribe.
        var released = QueueRows.Actions(running, manual);
        Assert.True(released.Hold);
        Assert.False(released.Transcribe);
        Assert.True(released.UseLivePreview);
        // Done and failed rows are unchanged.
        Assert.Equal(new QueueRowActions(false, false, OpenTranscript: true, false, false, Reveal: true, Dismiss: true), QueueRows.Actions(done, manual));
        Assert.Equal(new QueueRowActions(false, false, false, false, TryAgain: true, Reveal: true, Dismiss: true), QueueRows.Actions(failed, manual));
        // Hold stops being offered once the job saves its live preview.
        running.IsUsingLivePreview = true;
        Assert.False(QueueRows.Actions(running, manual).Hold);
        Assert.True(QueueRows.Actions(running, manual).SavingLivePreview);
        running.IsUsingLivePreview = false;
        // Under the other timings nothing new shows, whatever the flags say.
        foreach (var timing in new[] { FinalPassTiming.Immediate, FinalPassTiming.WhenIdle })
        {
            foreach (var job in new[] { waiting, suspended, running, done, failed })
            {
                var actions = QueueRows.Actions(job, timing);
                Assert.False(actions.Transcribe);
                Assert.False(actions.Hold);
            }
        }
        // Without a timing the rows are the two-timing rows.
        Assert.False(QueueRows.Actions(waiting).Transcribe);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheButtonsCallTheQueue() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        // What the row Transcribe, Hold and the header Transcribe All do (QueueRowView and RecordView
        // call exactly these): Release, Hold, ReleaseAll; the rows then follow the queue.
        var queue = rig.Queue;
        var first = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var second = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        Assert.Equal(2, queue.HeldCount);
        queue.Release(first);
        Assert.True(QueueRows.Actions(first, FinalPassTiming.Manual).Hold);
        Assert.False(QueueRows.Actions(first, FinalPassTiming.Manual).Transcribe);
        Assert.Equal(1, queue.HeldCount);
        queue.Hold(first);
        Assert.True(QueueRows.Actions(first, FinalPassTiming.Manual).Transcribe);
        queue.ReleaseAll();
        Assert.Equal(0, queue.HeldCount);
        Assert.All(new[] { first, second }, job => Assert.True(QueueRows.Actions(job, FinalPassTiming.Manual).Hold));
        return Task.CompletedTask;
    });

    [Fact]
    public Task AHeldJobAloneIsAQueueRowNotTheSingleMeeting() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        // PLAN.md 4.11 (2026-10-02): under Manual every recording is a row, even the only one, so its name
        // and time show; ActiveJob still skips a held suspended one.
        var queue = rig.Queue;
        var job = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.4);
        Assert.True(job.IsHeld);
        Assert.Null(queue.FeaturedJob);
        Assert.True(QueueRows.ShowsList(queue));
        Assert.Null(queue.ActiveJob);
        Assert.Equal(1, queue.PendingCount);
        Assert.True(QueueRows.IsHeld(job, FinalPassTiming.Manual));
        queue.SetSessionActive(true);
        Assert.Null(queue.FeaturedJob);
        Assert.True(QueueRows.ShowsList(queue));
        queue.SetSessionActive(false);
        // Released, running, done: still a row.
        queue.Release(job);
        Assert.True(QueueRows.ShowsList(queue));
        queue.ClearSamples();
        queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        Assert.True(QueueRows.ShowsList(queue));
        queue.ClearSamples();
        Assert.False(QueueRows.ShowsList(queue), "an empty queue lists nothing");
        // Another timing: the single meeting of 4.9 item 5 again, and the switch re-renders at once.
        queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        Assert.NotNull(queue.FeaturedJob);
        Assert.False(QueueRows.ShowsList(queue));
        return Task.CompletedTask;
    });

    [Fact]
    public Task OnlyAHeldIdleRowOffersMoveToRecycleBin() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        var queue = rig.Queue;
        var waiting = queue.InsertSample(Wav, TranscriptionJobState.Waiting);
        var suspended = queue.InsertSample(Wav, TranscriptionJobState.Suspended, 0.4);
        var running = queue.InsertSample(Wav, TranscriptionJobState.Running, 0.4);
        var done = queue.InsertSample(Wav, TranscriptionJobState.Done, 1, Srt);
        var failed = queue.InsertSample(Wav, TranscriptionJobState.Failed);
        const FinalPassTiming manual = FinalPassTiming.Manual;
        Assert.True(QueueRows.Actions(waiting, manual).Trash);
        Assert.True(QueueRows.Actions(suspended, manual).Trash);
        // A running job, even one that is held because Hold has not taken effect yet, is not idle.
        Assert.True(running.IsHeld);
        Assert.False(QueueRows.Actions(running, manual).Trash);
        Assert.False(QueueRows.Actions(done, manual).Trash);
        Assert.False(QueueRows.Actions(failed, manual).Trash);
        // It follows the queue's own rule for every job.
        foreach (var job in queue.Jobs) Assert.Equal(queue.CanTrash(job), QueueRows.Actions(job, manual).Trash);
        // Released: Hold, not the trash.
        queue.Release(waiting);
        Assert.False(QueueRows.Actions(waiting, manual).Trash);
        Assert.True(QueueRows.Actions(waiting, manual).Hold);
        queue.Hold(waiting);
        Assert.True(QueueRows.Actions(waiting, manual).Trash);
        // Not while it saves its live preview, and never under another timing.
        suspended.IsUsingLivePreview = true;
        Assert.False(QueueRows.Actions(suspended, manual).Trash);
        suspended.IsUsingLivePreview = false;
        Assert.True(QueueRows.Actions(suspended, manual).Trash);
        foreach (var timing in new[] { FinalPassTiming.Immediate, FinalPassTiming.WhenIdle })
        {
            foreach (var job in queue.Jobs) Assert.False(QueueRows.Actions(job, timing).Trash);
        }
        Assert.False(QueueRows.Actions(waiting).Trash);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AFailedTrashShowsItsReasonOnTheRowUntilTranscribeClearsIt() => RunAsync(FinalPassTiming.Manual, rig =>
    {
        var queue = rig.Queue;
        var job = queue.InsertSample(Wav, TranscriptionJobState.Waiting, trashError: "Could not move the recording to the Recycle Bin: in use");
        Assert.Equal("Could not move the recording to the Recycle Bin: in use", job.TrashError);
        Assert.True(QueueRows.Actions(job, FinalPassTiming.Manual).Trash, "the row still offers it for another try");
        queue.Release(job);
        Assert.Null(job.TrashError);
        return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void TrashTextsComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "windows", "Move to Recycle Bin…"), Strings.MoveToRecycleBin);
        Assert.Equal(Translations.Text(language, "windows", "Move to Recycle Bin"), Strings.MoveToRecycleBinButton);
        Assert.Equal(Translations.Text(language, "windows", "Move this recording to the Recycle Bin?"), Strings.MoveRecordingTitle);
        Assert.Equal(Translations.Text(language, "app", "It is not transcribed, and its live preview is discarded."), Strings.MoveRecordingMessage);
        Assert.Equal(
            Translations.Format(language, "windows", "Could not move the recording to the Recycle Bin: %@", "in use"),
            Strings.CouldNotTrashRecording("in use"));
    }

    [Fact]
    public void ResetHasWorkWhenAnyOfTheThreeShortcutsIsChanged()
    {
        using var folder = new ScratchFolder();
        var settings = new AppSettings(folder.Path);
        Assert.False(GeneralSettingsView.ShortcutsDiffer(settings));
        settings.StopStartNextHotkey = new HotkeyBinding(0x4D, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        Assert.True(GeneralSettingsView.ShortcutsDiffer(settings));
        settings.StopStartNextHotkey = HotkeyBinding.DefaultStopStartNext;
        Assert.False(GeneralSettingsView.ShortcutsDiffer(settings));
        settings.PauseHotkey = new HotkeyBinding(0x4D, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        Assert.True(GeneralSettingsView.ShortcutsDiffer(settings));
    }
}
