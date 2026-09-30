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
    public void TheTimingPickerHasTheTwoRowsInOrderAndMapsBothWays()
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        Assert.Equal([FinalPassTiming.Immediate, FinalPassTiming.WhenIdle], FinalPassTimingChoices.All);
        Assert.Equal("Right away (in the background)", FinalPassTimingChoices.Label(FinalPassTiming.Immediate));
        Assert.Equal("When no recording is running", FinalPassTimingChoices.Label(FinalPassTiming.WhenIdle));
        foreach (var timing in FinalPassTimingChoices.All)
        {
            Assert.Equal(timing, FinalPassTimingChoices.At(FinalPassTimingChoices.IndexOf(timing)));
        }
        Assert.Null(FinalPassTimingChoices.At(-1));
        Assert.Null(FinalPassTimingChoices.At(2));
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
    }

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void TimingTextsComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "core", "Right away (in the background)"), Strings.TimingImmediate);
        Assert.Equal(Translations.Text(language, "core", "When no recording is running"), Strings.TimingWhenIdle);
        Assert.Equal(Translations.Text(language, "app", "Transcribe finished recordings"), Strings.FinalPassTimingLabel);
        Assert.Equal(Translations.Text(language, "app", "Either way, the next recording can start at once."), Strings.FinalPassTimingCaption);
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
