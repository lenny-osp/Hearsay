namespace Hearsay.Core.Transcription;

/// <summary>
/// When a queued recording gets its final pass (PLAN.md 4.9 item 3 and 18.10,
/// Settings > General > Transcription, key <c>finalPassTiming</c>). The stored
/// values are the shared ones (<see cref="FinalPassTimings.StorageValue"/>).
/// Port of <c>FinalPassTiming</c> in mac/HearsayCore/Sources/HearsayCore/Transcription/TranscriptionQueuePolicy.swift.
/// The picker labels ("Right away (in the background)", "When no recording is
/// running") are the app's, from shared/localization.
/// </summary>
public enum FinalPassTiming
{
    /// <summary>A job starts as soon as it is first in line and the engine has no foreground work. The Mac default.</summary>
    Immediate,

    /// <summary>Jobs run only while no session is active (Starting, Recording, Paused, Stopping). The Windows default.</summary>
    WhenIdle,
}

public static class FinalPassTimings
{
    /// <summary>The Windows default (PLAN.md 18.10); the Mac's is <see cref="FinalPassTiming.Immediate"/>.</summary>
    public const FinalPassTiming Default = FinalPassTiming.WhenIdle;

    /// <summary>Every value in picker order.</summary>
    public static readonly IReadOnlyList<FinalPassTiming> All = [FinalPassTiming.Immediate, FinalPassTiming.WhenIdle];

    /// <summary>The stored and shared value: "immediate" or "whenIdle".</summary>
    public static string StorageValue(this FinalPassTiming timing) => timing switch
    {
        FinalPassTiming.Immediate => "immediate",
        FinalPassTiming.WhenIdle => "whenIdle",
        _ => throw new ArgumentOutOfRangeException(nameof(timing), timing, null),
    };

    /// <summary>Parses <see cref="StorageValue"/>; null for anything else.</summary>
    public static FinalPassTiming? FromStorageValue(string? value) => value switch
    {
        "immediate" => FinalPassTiming.Immediate,
        "whenIdle" => FinalPassTiming.WhenIdle,
        _ => null,
    };
}

/// <summary>
/// The state of one job in the background transcription queue (PLAN.md 4.9).
/// The stored values are the ones in <c>queue.json</c> and the shared vectors.
/// Port of <c>TranscriptionJobState</c> in TranscriptionQueuePolicy.swift.
/// </summary>
public enum TranscriptionJobState
{
    /// <summary>In line, not started (or started over after a relaunch).</summary>
    Waiting,

    /// <summary>The final pass is decoding.</summary>
    Running,

    /// <summary>Yielded at a segment boundary; holds a checkpoint and resumes first.</summary>
    Suspended,

    /// <summary>Transcript written.</summary>
    Done,

    /// <summary>Transcription failed (PLAN.md 4.1 failure rules apply).</summary>
    Failed,
}

public static class TranscriptionJobStates
{
    /// <summary>Every state, in the Mac's declaration order.</summary>
    public static readonly IReadOnlyList<TranscriptionJobState> All =
    [
        TranscriptionJobState.Waiting, TranscriptionJobState.Running, TranscriptionJobState.Suspended,
        TranscriptionJobState.Done, TranscriptionJobState.Failed,
    ];

    /// <summary>The stored and shared value: "waiting", "running", "suspended", "done", "failed".</summary>
    public static string StorageValue(this TranscriptionJobState state) => state switch
    {
        TranscriptionJobState.Waiting => "waiting",
        TranscriptionJobState.Running => "running",
        TranscriptionJobState.Suspended => "suspended",
        TranscriptionJobState.Done => "done",
        TranscriptionJobState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Parses <see cref="StorageValue"/>; null for anything else.</summary>
    public static TranscriptionJobState? FromStorageValue(string? value) => value switch
    {
        "waiting" => TranscriptionJobState.Waiting,
        "running" => TranscriptionJobState.Running,
        "suspended" => TranscriptionJobState.Suspended,
        "done" => TranscriptionJobState.Done,
        "failed" => TranscriptionJobState.Failed,
        _ => null,
    };

    /// <summary>Waiting, running, or suspended: the recording is not transcribed yet.</summary>
    public static bool IsPending(this TranscriptionJobState state) =>
        state is TranscriptionJobState.Waiting or TranscriptionJobState.Running or TranscriptionJobState.Suspended;
}

/// <summary>
/// The pure queue rules of PLAN.md 4.9 ("Queue rules"), shared with the Mac
/// through <c>shared/transcription-queue-tests.json</c>.
/// Port of <c>TranscriptionQueuePolicy</c> in TranscriptionQueuePolicy.swift.
/// <para>
/// Scheduling contract (<see cref="Next"/>): the engine may run a queued job
/// when <c>foregroundWaiting</c> is 0 (a negative count counts as 0) and, for
/// <see cref="FinalPassTiming.WhenIdle"/>, no session is active. At most one
/// job runs. If a job is running, the decision is <c>Suspend</c> of that job
/// when running is no longer allowed, else <c>None</c>. With no running job
/// and running allowed: <c>Resume</c> the first suspended job in queue order,
/// else <c>Start</c> the first waiting job, else <c>None</c>. Done and failed
/// jobs are skipped wherever they are.
/// </para>
/// <para>
/// Invalid inputs are handled tolerantly and deterministically (no
/// assertion): two or more running jobs consider only the first in queue
/// order; a running job behind a waiting or suspended job is not preempted;
/// a suspended job behind a waiting job is resumed first.
/// </para>
/// </summary>
public static class TranscriptionQueuePolicy
{
    /// <summary>What <see cref="Next"/> reads of a job: its identifier and state.</summary>
    public readonly record struct Job(string Id, TranscriptionJobState State);

    /// <summary>What the queue does next.</summary>
    public enum DecisionKind
    {
        /// <summary>Nothing to change.</summary>
        None,

        /// <summary>Start the final pass of the waiting job.</summary>
        Start,

        /// <summary>Continue the suspended job from its checkpoint.</summary>
        Resume,

        /// <summary>The running job must yield at its next segment.</summary>
        Suspend,
    }

    /// <summary>What the queue should do now; <see cref="JobId"/> is null exactly for <see cref="DecisionKind.None"/>.</summary>
    public readonly record struct Decision(DecisionKind Kind, string? JobId)
    {
        public static Decision None { get; } = new(DecisionKind.None, null);

        public static Decision Start(string jobId) => new(DecisionKind.Start, jobId);

        public static Decision Resume(string jobId) => new(DecisionKind.Resume, jobId);

        public static Decision Suspend(string jobId) => new(DecisionKind.Suspend, jobId);
    }

    /// <summary>Whether a queued job may run now.</summary>
    public static bool MayRun(FinalPassTiming timing, bool sessionActive, int foregroundWaiting)
    {
        if (foregroundWaiting > 0) return false;
        return timing switch
        {
            FinalPassTiming.Immediate => true,
            FinalPassTiming.WhenIdle => !sessionActive,
            _ => throw new ArgumentOutOfRangeException(nameof(timing), timing, null),
        };
    }

    /// <summary>The next queue action; see the type's documentation. <paramref name="jobs"/> is in queue order (first in first).</summary>
    public static Decision Next(FinalPassTiming timing, bool sessionActive, int foregroundWaiting, IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var allowed = MayRun(timing, sessionActive, foregroundWaiting);
        if (First(jobs, TranscriptionJobState.Running) is { } running)
        {
            return allowed ? Decision.None : Decision.Suspend(running.Id);
        }
        if (!allowed) return Decision.None;
        if (First(jobs, TranscriptionJobState.Suspended) is { } suspended) return Decision.Resume(suspended.Id);
        if (First(jobs, TranscriptionJobState.Waiting) is { } waiting) return Decision.Start(waiting.Id);
        return Decision.None;
    }

    /// <summary>For the decoder's yield check: true exactly when <see cref="Next"/> would suspend a running job under these conditions.</summary>
    public static bool ShouldYield(FinalPassTiming timing, bool sessionActive, int foregroundWaiting) =>
        !MayRun(timing, sessionActive, foregroundWaiting);

    /// <summary>
    /// Whether a job that just finished opens its notes flow (PLAN.md 4.9
    /// item 4): only when no session is active and no notes sheet or other
    /// notes flow is on screen. Otherwise its row offers "Generate Notes...".
    /// </summary>
    public static bool PresentsNotes(bool sessionActive, bool notesOnScreen) => !sessionActive && !notesOnScreen;

    /// <summary>The number of recordings not transcribed yet (waiting, running, or suspended). Quit asks for confirmation when it is above 0.</summary>
    public static int QuitNeedsConfirmation(IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Count(job => job.State.IsPending());
    }

    /// <summary>Whether an update install must refuse: true while any job is waiting, running, or suspended. Done and failed rows do not block.</summary>
    public static bool BlocksUpdateInstall(IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Any(job => job.State.IsPending());
    }

    private static Job? First(IReadOnlyList<Job> jobs, TranscriptionJobState state)
    {
        foreach (var job in jobs)
        {
            if (job.State == state) return job;
        }
        return null;
    }
}
