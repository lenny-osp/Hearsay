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

    /// <summary>
    /// "When I start them" (PLAN.md 4.11, 18.12): a finished recording waits, held,
    /// until the user releases it (Transcribe, Transcribe All, or Try Again); a
    /// released job then follows <see cref="WhenIdle"/>. Windows only until the Mac port.
    /// </summary>
    Manual,
}

public static class FinalPassTimings
{
    /// <summary>The Windows default (PLAN.md 18.10); the Mac's is <see cref="FinalPassTiming.Immediate"/>.</summary>
    public const FinalPassTiming Default = FinalPassTiming.WhenIdle;

    /// <summary>Every value in picker order.</summary>
    public static readonly IReadOnlyList<FinalPassTiming> All = [FinalPassTiming.Immediate, FinalPassTiming.WhenIdle, FinalPassTiming.Manual];

    /// <summary>The stored and shared value: "immediate", "whenIdle" or "manual".</summary>
    public static string StorageValue(this FinalPassTiming timing) => timing switch
    {
        FinalPassTiming.Immediate => "immediate",
        FinalPassTiming.WhenIdle => "whenIdle",
        FinalPassTiming.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(timing), timing, null),
    };

    /// <summary>Parses <see cref="StorageValue"/>; null for anything else.</summary>
    public static FinalPassTiming? FromStorageValue(string? value) => value switch
    {
        "immediate" => FinalPassTiming.Immediate,
        "whenIdle" => FinalPassTiming.WhenIdle,
        "manual" => FinalPassTiming.Manual,
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
/// The pure queue rules of PLAN.md 4.9 ("Queue rules") and 4.11 (Manual),
/// shared with the Mac through <c>shared/transcription-queue-tests.json</c>
/// (the 4.11 rules in its <c>manual</c> section, which only Windows reads
/// until the Mac port).
/// Port of <c>TranscriptionQueuePolicy</c> in TranscriptionQueuePolicy.swift.
/// <para>
/// Scheduling contract (<see cref="Next"/>): the engine may run a queued job
/// when <c>foregroundWaiting</c> is 0 (a negative count counts as 0) and, for
/// <see cref="FinalPassTiming.WhenIdle"/> and <see cref="FinalPassTiming.Manual"/>,
/// no session is active. At most one job runs. If a job is running, the
/// decision is <c>Suspend</c> of that job when running is no longer allowed
/// (or, under Manual, when the job is not released), else <c>None</c>. With no
/// running job and running allowed: <c>Resume</c> the first suspended job in
/// queue order, else <c>Start</c> the first waiting job, else <c>None</c>;
/// under Manual only released jobs are candidates. Done and failed jobs are
/// skipped wherever they are.
/// </para>
/// <para>
/// Manual (4.11): <see cref="Job.Released"/> is read only under Manual. A
/// pending job that is not released is <em>held</em> (<see cref="IsHeld"/>):
/// it is never started or resumed, and a running one is suspended at its next
/// window. Among released jobs queue order decides, so releasing job 2 before
/// job 1 does not let job 2 run first.
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
    /// <summary>
    /// What <see cref="Next"/> reads of a job: its identifier, its state and,
    /// under <see cref="FinalPassTiming.Manual"/> only, whether the user released it.
    /// </summary>
    public readonly record struct Job(string Id, TranscriptionJobState State, bool Released = false);

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

    /// <summary>
    /// Whether a queued job may run now. Manual follows WhenIdle here; whether a
    /// particular job is released is asked separately (<see cref="IsHeld"/>).
    /// </summary>
    public static bool MayRun(FinalPassTiming timing, bool sessionActive, int foregroundWaiting)
    {
        if (foregroundWaiting > 0) return false;
        return timing switch
        {
            FinalPassTiming.Immediate => true,
            FinalPassTiming.WhenIdle or FinalPassTiming.Manual => !sessionActive,
            _ => throw new ArgumentOutOfRangeException(nameof(timing), timing, null),
        };
    }

    /// <summary>
    /// Held (PLAN.md 4.11): the timing is Manual and the job is pending and not
    /// released. Never true under the other timings, whatever the flag says.
    /// </summary>
    public static bool IsHeld(FinalPassTiming timing, Job job) =>
        timing == FinalPassTiming.Manual && job.State.IsPending() && !job.Released;

    /// <summary>The next queue action; see the type's documentation. <paramref name="jobs"/> is in queue order (first in first).</summary>
    public static Decision Next(FinalPassTiming timing, bool sessionActive, int foregroundWaiting, IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var allowed = MayRun(timing, sessionActive, foregroundWaiting);
        if (First(timing, jobs, TranscriptionJobState.Running, skipHeld: false) is { } running)
        {
            return !allowed || IsHeld(timing, running) ? Decision.Suspend(running.Id) : Decision.None;
        }
        if (!allowed) return Decision.None;
        if (First(timing, jobs, TranscriptionJobState.Suspended, skipHeld: true) is { } suspended) return Decision.Resume(suspended.Id);
        if (First(timing, jobs, TranscriptionJobState.Waiting, skipHeld: true) is { } waiting) return Decision.Start(waiting.Id);
        return Decision.None;
    }

    /// <summary>
    /// For the decoder's yield check: true exactly when <see cref="Next"/> would
    /// suspend a running job under these conditions. <paramref name="runningReleased"/>
    /// is the running job's released flag, read only under Manual.
    /// </summary>
    public static bool ShouldYield(FinalPassTiming timing, bool sessionActive, int foregroundWaiting, bool runningReleased = true) =>
        !MayRun(timing, sessionActive, foregroundWaiting) || (timing == FinalPassTiming.Manual && !runningReleased);

    /// <summary>
    /// Whether a job that just finished opens its notes flow (PLAN.md 4.9
    /// item 4): only when no session is active and no notes sheet or other
    /// notes flow is on screen. Otherwise its row offers "Generate Notes...".
    /// </summary>
    public static bool PresentsNotes(bool sessionActive, bool notesOnScreen) => !sessionActive && !notesOnScreen;

    /// <summary>The number of recordings not transcribed yet (waiting, running, or suspended), held ones included. Quit asks for confirmation when it is above 0.</summary>
    public static int QuitNeedsConfirmation(IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Count(job => job.State.IsPending());
    }

    /// <summary>
    /// Whether an update install must refuse: true while any job is waiting,
    /// running, or suspended; done and failed rows do not block. Under Manual
    /// held waiting and suspended jobs do not block (they are in queue.json and
    /// come back held after the relaunch); a released job and a running one
    /// (even one whose Hold has not taken effect yet) still do.
    /// </summary>
    public static bool BlocksUpdateInstall(FinalPassTiming timing, IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Any(job => job.State.IsPending() && (job.State == TranscriptionJobState.Running || !IsHeld(timing, job)));
    }

    /// <summary>The rule of the timing-less shared vectors: any pending job blocks (no job is held under the other timings).</summary>
    public static bool BlocksUpdateInstall(IReadOnlyList<Job> jobs) => BlocksUpdateInstall(FinalPassTiming.Immediate, jobs);

    /// <summary>
    /// Whether every pending job is held, with at least one pending (PLAN.md
    /// 4.11, Quit): false under the other timings and for a queue with nothing
    /// pending. A running job that is not released counts as held.
    /// </summary>
    public static bool AllPendingHeld(FinalPassTiming timing, IReadOnlyList<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (timing != FinalPassTiming.Manual) return false;
        var pending = jobs.Where(job => job.State.IsPending()).ToList();
        return pending.Count > 0 && pending.All(job => !job.Released);
    }

    private static Job? First(FinalPassTiming timing, IReadOnlyList<Job> jobs, TranscriptionJobState state, bool skipHeld)
    {
        foreach (var job in jobs)
        {
            if (job.State != state) continue;
            if (skipHeld && IsHeld(timing, job)) continue;
            return job;
        }
        return null;
    }
}
