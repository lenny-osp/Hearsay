using Hearsay.App.Features.MenuBar;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Tests;

/// <summary>
/// The queue line and Stop &amp; Start Next in the tray menu (PLAN.md 4.9,
/// 18.10 "Tray"), the Windows form of <c>queueLine</c> and
/// <c>stopStartNextRow</c> in mac/Hearsay/Features/MenuBar/MenuBarView.swift.
/// The menu itself is a native popup, which cannot be built without a window;
/// the text and enabled state it shows come from <see cref="RecordingStatus"/>.
/// </summary>
public sealed class TrayQueueTests : IDisposable
{
    private readonly InterfaceLanguageScope english = new(InterfaceLanguage.English);
    private readonly RecordingStatus status = new();

    public void Dispose() => english.Dispose();

    [Fact]
    public void AnEmptyQueueHasNoLine()
    {
        Assert.Null(status.QueueLine);
        status.SetQueue(0, null, false);
        Assert.Null(status.QueueLine);
    }

    [Fact]
    public void WithNoSessionTheStateLineShowsTheRunningJobSoOneJobNeedsNoQueueLine()
    {
        status.Update(RecordingPhase.Transcribing, TimeSpan.Zero, 0.42);
        status.SetQueue(1, 0.42, false);
        Assert.Equal("Transcribing… 42%", status.StateText);
        Assert.Null(status.QueueLine);
        status.SetQueue(3, 0.42, false);
        Assert.Equal("Recordings in queue: 3", status.QueueLine);
    }

    [Fact]
    public void WithNoSessionAndNothingRunningItSaysItIsWaiting()
    {
        status.SetQueue(2, null, false);
        Assert.Equal("Waiting to transcribe · in queue: 2", status.QueueLine);
    }

    [Fact]
    public void WhileRecordingItSaysHowFarTheRunningJobIsOrThatItIsPaused()
    {
        status.Update(RecordingPhase.Recording, TimeSpan.FromSeconds(30), null);
        status.SetQueue(2, 0.454, false);
        Assert.Equal("Transcribing… 45% · in queue: 2", status.QueueLine);
        status.SetQueue(2, 0.454, true);
        Assert.Equal("Transcription paused while recording · in queue: 2", status.QueueLine);
        status.SetQueue(1, null, true);
        Assert.Equal("Transcription paused while recording · in queue: 1", status.QueueLine);
        status.SetQueue(1, null, false);
        Assert.Equal("Waiting to transcribe · in queue: 1", status.QueueLine);
        // Paused and stopping are sessions too.
        status.Update(RecordingPhase.Paused, TimeSpan.FromSeconds(30), null);
        Assert.Equal("Waiting to transcribe · in queue: 1", status.QueueLine);
        status.Update(RecordingPhase.Stopping, TimeSpan.FromSeconds(30), null);
        status.SetQueue(1, 0.1, false);
        Assert.Equal("Transcribing… 10% · in queue: 1", status.QueueLine);
    }

    [Fact]
    public void ChangingTheLineRaisesQueueLineOnceAndAnUnchangedLineNothing()
    {
        var raised = new List<string?>();
        status.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        status.SetQueue(2, null, false);
        Assert.Equal([nameof(RecordingStatus.QueueLine)], raised);
        status.SetQueue(2, null, false);
        Assert.Single(raised);
        status.SetQueue(0, null, false);
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void WhenEveryPendingJobIsHeldTheLineSaysNotTranscribedYetWhateverElseRuns()
    {
        status.SetQueue(1, null, false, allHeld: true, heldCount: 1);
        Assert.Equal("Not transcribed yet · in queue: 1", status.QueueLine);
        status.SetQueue(3, null, false, allHeld: true, heldCount: 3);
        Assert.Equal("Not transcribed yet · in queue: 3", status.QueueLine);
        // A session does not change it (held jobs are not paused for it), nor does a stale progress.
        status.Update(RecordingPhase.Recording, TimeSpan.FromSeconds(30), null);
        Assert.Equal("Not transcribed yet · in queue: 3", status.QueueLine);
        status.SetQueue(2, 0.4, true, allHeld: true, heldCount: 2);
        Assert.Equal("Not transcribed yet · in queue: 2", status.QueueLine);
        status.Update(RecordingPhase.Idle, TimeSpan.Zero, null);
        Assert.Equal("Not transcribed yet · in queue: 2", status.QueueLine);
        // Not all held (one released job runs): the existing lines.
        status.SetQueue(3, 0.4, false, allHeld: false, heldCount: 2);
        Assert.Equal("Recordings in queue: 3", status.QueueLine);
        // An empty queue has no line, held flag or not.
        status.SetQueue(0, null, false, allHeld: true, heldCount: 0);
        Assert.Null(status.QueueLine);
    }

    [Fact]
    public void TranscribeAllIsEnabledWhileAJobIsHeldAndRaisedWhenThatChanges()
    {
        Assert.False(status.CanTranscribeAll);
        var raised = new List<string?>();
        status.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        status.SetQueue(2, 0.4, false, allHeld: false, heldCount: 1);
        Assert.True(status.CanTranscribeAll);
        Assert.Contains(nameof(RecordingStatus.CanTranscribeAll), raised);
        // Another held job: neither the line nor the enabled state changed.
        raised.Clear();
        status.SetQueue(2, 0.4, false, allHeld: false, heldCount: 2);
        Assert.Empty(raised);
        status.SetQueue(2, 0.4, false, allHeld: false, heldCount: 0);
        Assert.False(status.CanTranscribeAll);
        Assert.Equal([nameof(RecordingStatus.CanTranscribeAll)], raised);
        Assert.Equal(0, status.QueueHeldCount);
    }

    [Fact]
    public void TheTrayRebuildsItsMenuForEveryInputOfAnItem()
    {
        Assert.True(TrayIcon.RebuildsMenu(nameof(RecordingStatus.Phase)));
        Assert.True(TrayIcon.RebuildsMenu(nameof(RecordingStatus.QueueLine)));
        Assert.True(TrayIcon.RebuildsMenu(nameof(RecordingStatus.CanTranscribeAll)));
        Assert.False(TrayIcon.RebuildsMenu(nameof(RecordingStatus.Elapsed)));
        Assert.False(TrayIcon.RebuildsMenu(nameof(RecordingStatus.BusyFiles)));
        Assert.False(TrayIcon.RebuildsMenu(null));
    }

    [Fact]
    public void TranscribeAllReachesTheQueueOnlyWhileAJobIsHeld()
    {
        var calls = 0;
        status.Connect(() => { }, () => { }, null, () => calls++);
        status.TranscribeAll();
        Assert.Equal(0, calls);
        status.SetQueue(1, null, false, allHeld: true, heldCount: 1);
        status.TranscribeAll();
        Assert.Equal(1, calls);
        status.SetQueue(1, 0.2, false, allHeld: false, heldCount: 0);
        status.TranscribeAll();
        Assert.Equal(1, calls);
    }

    [Fact]
    public Task TheShellInputsComeFromTheQueue() => QueueRig.RunAsync(FinalPassTiming.Manual, rig =>
    {
        // What AppShell.SyncRecordingStatus pushes: pending count, running progress, paused flag, AllPendingHeld, HeldCount.
        var queue = rig.Queue;
        queue.InsertSample(@"C:\spool\a.wav", TranscriptionJobState.Waiting);
        queue.InsertSample(@"C:\spool\b.wav", TranscriptionJobState.Suspended, 0.3);
        void Push() => status.SetQueue(
            queue.PendingCount, queue.ActiveJob?.Progress, queue.IsHeldForSession && queue.HasJobPausedForSession, queue.AllPendingHeld, queue.HeldCount);
        Push();
        Assert.Equal("Not transcribed yet · in queue: 2", status.QueueLine);
        Assert.True(status.CanTranscribeAll);
        queue.ReleaseAll();
        Push();
        Assert.False(status.CanTranscribeAll);
        Assert.NotEqual("Not transcribed yet · in queue: 2", status.QueueLine);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(nameof(RecordingPhase.Idle), false)]
    [InlineData(nameof(RecordingPhase.Starting), false)]
    [InlineData(nameof(RecordingPhase.Recording), true)]
    [InlineData(nameof(RecordingPhase.Paused), true)]
    [InlineData(nameof(RecordingPhase.Stopping), false)]
    [InlineData(nameof(RecordingPhase.Transcribing), false)]
    public void StopAndStartNextIsEnabledWhileRecordingOrPaused(string phaseName, bool enabled)
    {
        status.Update(Enum.Parse<RecordingPhase>(phaseName), TimeSpan.FromSeconds(5), null);
        Assert.Equal(enabled, status.CanStopAndStartNext);
    }

    [Fact]
    public void TheCommandReachesTheController()
    {
        var calls = 0;
        status.Connect(() => { }, () => { }, () => calls++);
        status.StopAndStartNext();
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void QueueLinesComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Format(language, "app", "Recordings in queue: %lld", 3), Strings.QueueLineCount(3));
        Assert.Equal(Translations.Format(language, "app", "Transcription paused while recording · in queue: %lld", 3), Strings.QueueLinePaused(3));
        Assert.Equal(Translations.Format(language, "app", "Waiting to transcribe · in queue: %lld", 3), Strings.QueueLineWaiting(3));
        var transcribing = Strings.QueueLineTranscribing(45, 3);
        Assert.Contains("45", transcribing, StringComparison.Ordinal);
        Assert.Contains("3", transcribing, StringComparison.Ordinal);
        Assert.Equal(Translations.Text(language, "app", "Stop & Start Next"), Strings.StopAndStartNext);
        Assert.Equal(Translations.Format(language, "app", "Not transcribed yet · in queue: %lld", 3), Strings.QueueLineHeld(3));
        Assert.Equal(Translations.Text(language, "app", "Transcribe All"), Strings.TranscribeAll);
    }
}
