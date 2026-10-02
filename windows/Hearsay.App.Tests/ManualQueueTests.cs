using System.ComponentModel;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Transcription;
using static Hearsay.App.Tests.QueueRig;

namespace Hearsay.App.Tests;

/// <summary>
/// The "When I start them" timing of the queue (PLAN.md 4.11, Windows 18.12):
/// jobs are held until released, released jobs follow whenIdle, Hold suspends
/// a running job and keeps its checkpoint, "Try Again" is released, a restore
/// gives held jobs. The policy itself is covered by the <c>manual</c> vectors
/// in Hearsay.Tests; these cases drive the queue with the fake engine of
/// <see cref="TranscriptionQueueTests"/>.
/// </summary>
public sealed class ManualQueueTests
{
    private static readonly TranscriptSegment LiveCue = new(0, 2, "live preview line");

    // Stop leaves the job held

    [Fact]
    public Task AStoppedRecordingIsHeldAndNothingRuns() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var job = rig.Add();
        await Stays(() => job.State == TranscriptionJobState.Waiting && rig.Engine.Steps.Count == 0, "the held job");
        Assert.False(job.IsReleased);
        Assert.True(job.IsHeld);
        Assert.True(rig.Queue.IsHeld(job));
        Assert.Equal(1, rig.Queue.HeldCount);
        Assert.True(rig.Queue.AllPendingHeld);
        Assert.Equal(1, rig.Queue.PendingCount);
        Assert.False(rig.Queue.BlocksUpdateInstall, "a held job does not block an update");
        Assert.Null(rig.Queue.ActiveJob);
        Assert.False(rig.Queue.IsPausedForSession(job), "a held job waits for the user, not for a session");
        Assert.Equal(["queued 1", "held 1"], rig.Events);
        Assert.Empty(rig.Engine.Steps);
    });

    [Fact]
    public Task AnUndecidedAutoLanguageIsStillSettledWhileTheJobIsHeld() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        // Detection at Stop is foreground work, not the final pass (PLAN.md 4.11).
        rig.Engine.Detection = new DetectionResult("de", 0.97f, 1, []);
        var job = rig.Add(LanguageChoice.Auto, live: [], sink: null);
        await WaitUntil(() => job.Language == TranscriptLanguage.German, "the language");
        Assert.Equal(1, rig.Engine.DetectCalls);
        await Stays(() => job.State == TranscriptionJobState.Waiting && rig.Engine.Steps.Count == 0, "no pass while held");
        Assert.True(job.IsHeld);
    });

    [Fact]
    public Task SeveralStoppedRecordingsAreAllHeldWhetherOrNotASessionRuns() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var first = rig.Add();
        rig.Queue.SetSessionActive(true);
        var second = rig.Add();
        rig.Queue.SetSessionActive(false);
        var third = rig.Add();
        await Stays(() => rig.Engine.Steps.Count == 0, "no pass for held jobs");
        Assert.All(new[] { first, second, third }, job => Assert.True(job.IsHeld));
        Assert.Equal(3, rig.Queue.HeldCount);
        Assert.Equal(3, rig.Queue.PendingCount);
        Assert.True(rig.Queue.AllPendingHeld);
    });

    // Release

    [Fact]
    public Task ReleaseRunsTheJob() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var job = rig.Add();
        var queueNames = new List<string?>();
        var jobNames = new List<string?>();
        var thread = Environment.CurrentManagedThreadId;
        var wrongThread = false;
        rig.Queue.PropertyChanged += (_, e) =>
        {
            queueNames.Add(e.PropertyName);
            wrongThread |= Environment.CurrentManagedThreadId != thread;
        };
        job.PropertyChanged += (_, e) =>
        {
            jobNames.Add(e.PropertyName);
            wrongThread |= Environment.CurrentManagedThreadId != thread;
        };
        var changed = 0;
        rig.Queue.Changed += (_, _) => changed++;

        rig.Queue.Release(job);
        Assert.True(job.IsReleased);
        Assert.False(job.IsHeld);
        Assert.Equal(0, rig.Queue.HeldCount);
        Assert.False(rig.Queue.AllPendingHeld);
        Assert.Contains(nameof(TranscriptionJob.IsReleased), jobNames);
        Assert.Contains(nameof(TranscriptionJob.IsHeld), jobNames);
        Assert.Contains(nameof(TranscriptionQueue.HeldCount), queueNames);
        Assert.Contains(nameof(TranscriptionQueue.AllPendingHeld), queueNames);
        Assert.True(changed > 0);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the released job");
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
        Assert.Equal(["queued 1", "held 1", "released 1", "running 1", "done 1"], rig.Events.Where(e => !e.StartsWith("language", StringComparison.Ordinal)));
        Assert.False(wrongThread, "all notifications come on the UI thread");

        // Nothing to release any more: a done job is left alone.
        rig.Queue.Release(job);
        rig.Queue.Hold(job);
        Assert.False(job.IsHeld);
        Assert.DoesNotContain("held 1", rig.Events.Skip(2));
    });

    [Fact]
    public Task ReleaseAllRunsTheHeldJobsFirstInFirstOut() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var first = rig.Add();
        var second = rig.Add();
        var third = rig.Add();
        await Stays(() => rig.Engine.Steps.Count == 0, "no pass before Transcribe All");
        Assert.Equal(3, rig.Queue.HeldCount);

        rig.Queue.ReleaseAll();
        Assert.Equal(0, rig.Queue.HeldCount);
        Assert.All(new[] { first, second, third }, job => Assert.True(job.IsReleased));
        await WaitUntil(() => first.State == TranscriptionJobState.Running, "the first job");
        Assert.Equal(TranscriptionJobState.Waiting, second.State);
        Assert.Equal(TranscriptionJobState.Waiting, third.State);
        rig.Engine.WindowGate.Release(9);
        await WaitUntil(() => third.State == TranscriptionJobState.Done, "the last job");
        var started = rig.Events.Where(e => e.StartsWith("running", StringComparison.Ordinal)).ToList();
        Assert.Equal(["running 1", "running 2", "running 3"], started);
        Assert.True(rig.Events.IndexOf("done 1") < rig.Events.IndexOf("running 2"));
        Assert.True(rig.Events.IndexOf("done 2") < rig.Events.IndexOf("running 3"));
    });

    [Fact]
    public Task ReleasingTheSecondJobBeforeTheFirstStillRunsTheFirstFirst() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        // A session keeps both from starting, so only the queue order can decide.
        rig.Queue.SetSessionActive(true);
        var first = rig.Add();
        var second = rig.Add();
        rig.Queue.Release(second);
        rig.Queue.Release(first);
        Assert.True(rig.Queue.IsPausedForSession(first));
        Assert.True(rig.Queue.IsPausedForSession(second));
        await Stays(() => rig.Engine.Steps.Count == 0, "no pass during the session");
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => second.State == TranscriptionJobState.Done, "both jobs");
        Assert.True(rig.Events.IndexOf("running 1") < rig.Events.IndexOf("running 2"));
        Assert.True(rig.Events.IndexOf("done 1") < rig.Events.IndexOf("running 2"));
    });

    [Fact]
    public Task AReleasedJobRunsPastAnEarlierHeldOne() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var held = rig.Add();
        var released = rig.Add();
        rig.Queue.Release(released);
        await WaitUntil(() => released.State == TranscriptionJobState.Done, "the released job");
        Assert.Equal(TranscriptionJobState.Waiting, held.State);
        Assert.True(held.IsHeld);
        Assert.Equal(1, rig.Queue.PendingCount);
        Assert.True(rig.Queue.AllPendingHeld);
    });

    // Hold

    [Fact]
    public Task HoldSuspendsARunningJobAtItsNextWindowKeepingTheCheckpointAndReleaseResumesIt() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");

        rig.Queue.Hold(job);
        Assert.False(job.IsReleased);
        Assert.True(job.IsHeld);
        Assert.Equal(TranscriptionJobState.Running, job.State);
        Assert.True(rig.Queue.BlocksUpdateInstall, "a running job blocks an update until it has suspended");
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to suspend");
        Assert.NotNull(job.Checkpoint);
        Assert.Equal(1.0 / 3, job.Progress, 3);
        Assert.Contains("held 1", rig.Events.Skip(rig.Events.IndexOf("running 1")));
        Assert.Contains("suspended 1", rig.Events);
        Assert.True(job.IsHeld);
        Assert.Null(rig.Queue.ActiveJob); // a held suspended job is not being transcribed
        Assert.False(rig.Queue.IsPausedForSession(job));
        Assert.True(rig.Queue.AllPendingHeld);
        Assert.False(rig.Queue.BlocksUpdateInstall);
        await Stays(() => rig.Engine.Steps.Count == 1 && job.State == TranscriptionJobState.Suspended, "no resume while held");

        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 2, "the job to resume");
        Assert.Equal(1.0 / 3, job.Progress, 3);
        Assert.Contains("resumed 1", rig.Events);
        var second = rig.Engine.Steps[1];
        Assert.NotNull(second.ResumeFrom);
        Assert.Equal(30, second.ResumeFrom.ResumeSeconds);
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish");
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
    });

    [Fact]
    public Task AHoldThenReleaseBeforeTheNextWindowLetsTheJobGoOn() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Queue.Hold(job);
        rig.Queue.Release(job);
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish");
        Assert.Single(rig.Engine.Steps);
    });

    [Fact]
    public Task HoldOnAWaitingReleasedJobPutsItBehindTheOthers() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var first = rig.Add();
        var second = rig.Add();
        rig.Queue.ReleaseAll();
        rig.Queue.Hold(first);
        Assert.True(first.IsHeld);
        Assert.False(second.IsHeld);
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => second.State == TranscriptionJobState.Done, "the second job");
        Assert.Equal(TranscriptionJobState.Waiting, first.State);
        Assert.Single(rig.Events, e => e == "running 1" || e == "running 2");
        Assert.DoesNotContain("running 1", rig.Events);
    });

    // Sessions, foreground work

    [Fact]
    public Task ASessionPausesAReleasedJobAndItResumesAfterTheSession() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        var other = rig.Add();
        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Queue.SetSessionActive(true);
        Assert.True(rig.Queue.IsHeldForSession);
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to suspend");
        Assert.True(job.IsReleased);
        Assert.False(job.IsHeld);
        Assert.True(rig.Queue.IsPausedForSession(job), "a released job waits for the session");
        Assert.False(rig.Queue.IsPausedForSession(other), "a held job does not");
        Assert.True(rig.Queue.HasJobPausedForSession);
        Assert.True(rig.Queue.BlocksUpdateInstall);
        Assert.False(rig.Queue.AllPendingHeld);
        await Stays(() => rig.Engine.Steps.Count == 1, "no new step while recording");

        rig.Queue.SetSessionActive(false);
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish after the session");
        Assert.NotNull(rig.Engine.Steps[1].ResumeFrom);
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
        Assert.Equal(TranscriptionJobState.Waiting, other.State);
        Assert.True(other.IsHeld);
    });

    [Fact]
    public Task ForegroundWorkSuspendsAReleasedJobLikeWhenIdle() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Engine.Foreground = 1;
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to yield");
        Assert.False(job.IsHeld);
        rig.Engine.Foreground = 0;
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to resume");
    });

    [Fact]
    public Task AJobHeldWhileItsLanguageWasBeingCheckedIsPutBackBeforeItsPass() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        // The decoder runs a window before it can yield, so the queue asks again before the step.
        rig.Engine.DetectGate = new SemaphoreSlim(0);
        var job = rig.Add(LanguageChoice.Auto);
        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.DetectCalls >= 1, "the language check");
        rig.Queue.Hold(job);
        rig.Engine.DetectGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Waiting, "the job to be put back");
        Assert.Empty(rig.Engine.Steps);
        Assert.True(job.IsHeld);
        Assert.Contains("suspended 1", rig.Events);
    });

    // Timing changes

    [Fact]
    public Task SwitchingToManualReleasesStartedJobsAndHoldsWaitingOnes() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var running = rig.Add();
        await WaitUntil(() => running.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Queue.SetSessionActive(true);
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => running.State == TranscriptionJobState.Suspended, "the job to suspend");
        var waiting = rig.Add();

        rig.Settings.FinalPassTiming = FinalPassTiming.Manual;
        Assert.True(running.IsReleased);
        Assert.False(running.IsHeld);
        Assert.False(waiting.IsReleased);
        Assert.True(waiting.IsHeld);
        Assert.Equal(1, rig.Queue.HeldCount);
        Assert.False(rig.Queue.AllPendingHeld);
        Assert.Equal(["released 1", "held 2"], rig.Events.SkipWhile(e => e != "suspended 1").Skip(1).Where(e => e.StartsWith("released", StringComparison.Ordinal) || e.StartsWith("held", StringComparison.Ordinal)));

        rig.Queue.SetSessionActive(false);
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => running.State == TranscriptionJobState.Done, "the started job to finish");
        await Stays(() => waiting.State == TranscriptionJobState.Waiting && rig.Engine.Steps.Count == 2, "the held job stays");
    });

    [Fact]
    public Task SwitchingToManualWhileAJobRunsKeepsItRunningAndHoldsTheNextOne() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var running = rig.Add();
        var waiting = rig.Add();
        await WaitUntil(() => running.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");

        rig.Settings.FinalPassTiming = FinalPassTiming.Manual;
        Assert.True(running.IsReleased);
        Assert.True(waiting.IsHeld);
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => running.State == TranscriptionJobState.Done, "the running job to finish");
        Assert.Single(rig.Events, e => e == "running 1");
        await Stays(() => waiting.State == TranscriptionJobState.Waiting && rig.Engine.Steps.Count == 1, "the held job stays");
    });

    [Fact]
    public Task SwitchingAwayFromManualRunsTheHeldJobsAtOnce() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var first = rig.Add();
        var second = rig.Add();
        await Stays(() => rig.Engine.Steps.Count == 0, "held");
        Assert.Equal(2, rig.Queue.HeldCount);

        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        Assert.False(first.IsHeld);
        Assert.False(second.IsHeld);
        Assert.Equal(0, rig.Queue.HeldCount);
        Assert.False(rig.Queue.AllPendingHeld);
        await WaitUntil(() => second.State == TranscriptionJobState.Done, "both jobs to run");
        Assert.True(rig.Events.IndexOf("done 1") < rig.Events.IndexOf("running 2"));
    });

    [Fact]
    public Task SwitchingAwayFromManualWithASessionActiveWaitsForTheSession() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var job = rig.Add();
        rig.Queue.SetSessionActive(true);
        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        Assert.True(rig.Queue.IsPausedForSession(job));
        await Stays(() => rig.Engine.Steps.Count == 0, "whenIdle waits for the session");
        rig.Queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job");
    });

    [Fact]
    public Task SwitchingAwayFromManualResumesAHeldSuspendedJob() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var job = rig.Add();
        rig.Queue.Release(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        rig.Queue.Hold(job);
        rig.Engine.WindowGate.Release();
        await WaitUntil(() => job.State == TranscriptionJobState.Suspended, "the job to suspend");

        rig.Settings.FinalPassTiming = FinalPassTiming.Immediate;
        await WaitUntil(() => job.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 2, "the job to resume");
        rig.Engine.WindowGate.Release(2);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the job to finish");
        Assert.Equal(["window 0", "window 1", "window 2"], Texts(job.Srt!));
    });

    [Fact]
    public Task SwitchingToManualHoldsAWaitingJobEvenIfItWasReleasedBefore() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        rig.Queue.SetSessionActive(true);
        var job = rig.Add();
        rig.Settings.FinalPassTiming = FinalPassTiming.Manual;
        Assert.True(job.IsHeld);
        rig.Queue.Release(job);
        Assert.False(job.IsHeld);
        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        rig.Settings.FinalPassTiming = FinalPassTiming.Manual;
        Assert.True(job.IsHeld, "a waiting job becomes held when Manual is chosen, even if it was released before");
        rig.Queue.SetSessionActive(false);
        await Stays(() => rig.Engine.Steps.Count == 0 && job.State == TranscriptionJobState.Waiting, "held");
    });

    // Try Again

    [Fact]
    public Task TryAgainOnAFailedRowQueuesTheJobReleased() => RunAsync(FinalPassTiming.Immediate, async rig =>
    {
        rig.Engine.FailStep = () => new InvalidOperationException("boom");
        var job = rig.Add();
        await WaitUntil(() => job.State == TranscriptionJobState.Failed, "the failure");
        Assert.False(job.IsReleased);
        rig.Settings.FinalPassTiming = FinalPassTiming.Manual;
        Assert.False(job.IsHeld, "a failed row is not pending");

        rig.Engine.FailStep = null;
        rig.Queue.Retry(job);
        Assert.True(job.IsReleased);
        Assert.False(job.IsHeld);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the retry to run");
        Assert.Contains("released 1", rig.Events);
    });

    [Fact]
    public Task TryAgainAfterACaptureFailureQueuesTheKeptWavReleased() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var wav = rig.NewWav();
        var tracker = new SessionLanguageTracker(LanguageChoice.Fixed(TranscriptLanguage.English), TranscriptLanguage.English);
        rig.Queue.EnqueueRetry(
            new TranscriptOutput.PendingRecording(wav, InSpool: true), tracker, null, keepRecording: true, [], transcript: null);
        var job = Assert.Single(rig.Queue.Jobs);
        Assert.True(job.IsReleased);
        Assert.False(job.IsHeld);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the retry to run");
        Assert.DoesNotContain("held 1", rig.Events);
    });

    // Restore, update, quit

    [Fact]
    public Task AfterARelaunchEveryRestoredJobIsHeldAndQueueJsonHasNoReleasedFlag() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        string[] stems = ["2026-09-30_08-00-01", "2026-09-30_08-00-02", "2026-09-30_08-00-03"];
        foreach (var stem in stems) rig.NewWav(stem);
        var stop = new DateTimeOffset(2026, 9, 30, 8, 30, 0, TimeSpan.Zero);
        TranscriptionQueueManifest.Job Entry(string id, string stem, TranscriptionJobState state) =>
            new(id, stem + ".wav", stop, LanguageChoice.Fixed(TranscriptLanguage.English), TranscriptLanguage.English, null, true, state, null);
        var store = new TranscriptionQueueStore(rig.Spool);
        store.Save(new TranscriptionQueueManifest(
        [
            Entry("a", stems[0], TranscriptionJobState.Waiting),
            Entry("b", stems[1], TranscriptionJobState.Running),
            Entry("c", stems[2], TranscriptionJobState.Suspended),
        ]));

        var queue = rig.MakeQueue();
        queue.Restore();
        Assert.Equal(["a", "b", "c"], [.. queue.Jobs.Select(job => job.Id)]);
        Assert.All(queue.Jobs, job =>
        {
            Assert.False(job.IsReleased);
            Assert.True(job.IsHeld);
            Assert.Equal(TranscriptionJobState.Waiting, job.State);
        });
        Assert.Equal(3, queue.HeldCount);
        Assert.True(queue.AllPendingHeld);
        Assert.Equal(3, queue.PendingCount);
        Assert.False(queue.BlocksUpdateInstall);
        Assert.Equal(["queued 1", "held 1", "queued 2", "held 2", "queued 3", "held 3"], rig.Events);
        await Stays(() => rig.Engine.Steps.Count == 0, "the app never starts a pass by itself");
        Assert.DoesNotContain("released", File.ReadAllText(store.ManifestPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("held", File.ReadAllText(store.ManifestPath), StringComparison.OrdinalIgnoreCase);

        queue.Release(queue.Jobs[1]);
        await WaitUntil(() => queue.Jobs[1].State == TranscriptionJobState.Done, "the released job");
        Assert.Equal(TranscriptionJobState.Waiting, queue.Jobs[0].State);
    });

    [Fact]
    public Task TheUpdateBlockerIgnoresHeldJobsButNotReleasedOrRunningOnes() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        Assert.False(rig.Queue.BlocksUpdateInstall);
        rig.Queue.SetSessionActive(true);
        var first = rig.Add();
        var second = rig.Add();
        Assert.False(rig.Queue.BlocksUpdateInstall);
        Assert.Equal(2, rig.Queue.PendingCount);

        rig.Queue.Release(second);
        Assert.True(rig.Queue.BlocksUpdateInstall, "a released job blocks");
        rig.Queue.Hold(second);
        Assert.False(rig.Queue.BlocksUpdateInstall);

        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        Assert.True(rig.Queue.BlocksUpdateInstall, "under the other timings every pending job blocks");
        rig.Settings.FinalPassTiming = FinalPassTiming.Manual;
        Assert.False(rig.Queue.BlocksUpdateInstall);

        rig.Engine.WindowGate = new SemaphoreSlim(0);
        rig.Queue.SetSessionActive(false);
        rig.Queue.Release(first);
        await WaitUntil(() => first.State == TranscriptionJobState.Running, "the running job");
        Assert.True(rig.Queue.BlocksUpdateInstall, "a running job blocks");
        rig.Engine.WindowGate.Release(3);
        await WaitUntil(() => first.State == TranscriptionJobState.Done, "the job to finish");
        Assert.False(rig.Queue.BlocksUpdateInstall, "only the held job is left");
        Assert.Equal(1, rig.Queue.PendingCount);
    });

    [Fact]
    public Task AllPendingHeldIsFalseForAnEmptyQueueAndForTheOtherTimings() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        Assert.False(rig.Queue.AllPendingHeld);
        Assert.Equal(0, rig.Queue.HeldCount);
        var first = rig.Add();
        var second = rig.Add();
        Assert.True(rig.Queue.AllPendingHeld);
        rig.Queue.Release(first);
        // first runs to the end; second is the only pending job and it is held.
        await WaitUntil(() => first.State == TranscriptionJobState.Done, "the released job");
        Assert.True(rig.Queue.AllPendingHeld);
        rig.Queue.SetSessionActive(true);
        rig.Queue.Release(second);
        Assert.False(rig.Queue.AllPendingHeld);
        rig.Queue.Hold(second);
        Assert.True(rig.Queue.AllPendingHeld);
        rig.Settings.FinalPassTiming = FinalPassTiming.WhenIdle;
        Assert.False(rig.Queue.AllPendingHeld);
        Assert.Equal(0, rig.Queue.HeldCount);
    });

    [Fact]
    public Task UseLivePreviewInsteadSavesAHeldJobAtOnceWithoutAPass() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var job = rig.Add(live: [LiveCue]);
        Assert.True(job.CanUseLivePreview);
        rig.Queue.UseLivePreviewInstead(job);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the live preview to be saved");
        Assert.Equal(["live preview line"], Texts(job.Srt!));
        Assert.Empty(rig.Engine.Steps);
        Assert.False(job.IsHeld);
        Assert.Equal(0, rig.Queue.PendingCount);
    });

    [Fact]
    public Task UseLivePreviewInsteadReleasesTheHeldJobSoItIsNotListedAsHeldWhileItSaves() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        // WI-2: the row must not read "Not transcribed yet" (with Transcribe) while the live preview is written.
        var job = rig.Add(live: [LiveCue]);
        Assert.True(job.IsHeld);
        Assert.Equal(1, rig.Queue.HeldCount);
        rig.Queue.UseLivePreviewInstead(job);
        Assert.True(job.IsReleased);
        Assert.False(job.IsHeld);
        Assert.Equal(0, rig.Queue.HeldCount);
        Assert.False(rig.Queue.AllPendingHeld);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the live preview to be saved");
        Assert.Empty(rig.Engine.Steps);
    });

    [Fact]
    public Task QuitStopsAReleasedRunningJobAndKeepsTheHeldOnes() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        rig.Engine.WindowGate = new SemaphoreSlim(0);
        var running = rig.Add();
        var held = rig.Add();
        rig.Queue.Release(running);
        await WaitUntil(() => running.State == TranscriptionJobState.Running && rig.Engine.Steps.Count == 1, "the step to start");
        var quitting = rig.Queue.PrepareForQuitAsync();
        rig.Engine.WindowGate.Release();
        await quitting;
        Assert.Equal(2, rig.Queue.PendingCount);
        Assert.Equal(TranscriptionJobState.Waiting, held.State);
        Assert.Equal(2, new TranscriptionQueueStore(rig.Spool).Load().Manifest.Jobs.Count);
    });
}
