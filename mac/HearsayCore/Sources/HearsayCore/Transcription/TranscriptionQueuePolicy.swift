import Foundation

/// When a queued recording gets its final pass (PLAN.md 4.9 item 3,
/// Settings > General > Transcription, key `finalPassTiming`). The raw
/// values are the stored and shared (Windows, `shared/`) values.
public enum FinalPassTiming: String, CaseIterable, Codable, Sendable {
    /// A job starts as soon as it is first in line and the engine has no
    /// foreground work. The Mac default.
    case immediate
    /// Jobs run only while no session is active (Starting, Recording,
    /// Paused, Stopping). The Windows default.
    case whenIdle
    /// "When I start them" (PLAN.md 4.11): a finished recording waits, held,
    /// until the user releases it (Transcribe, Transcribe All, or Try Again);
    /// a released job then follows `whenIdle`.
    case manual

    /// Label for the Settings picker.
    public var displayName: String {
        switch self {
        case .immediate:
            String(localized: "Right away (in the background)", bundle: .module,
                   comment: "Settings > General > Transcription: final-pass timing that transcribes a finished recording at once, while the next one records")
        case .whenIdle:
            String(localized: "When no recording is running", bundle: .module,
                   comment: "Settings > General > Transcription: final-pass timing that waits until no recording is running")
        case .manual:
            String(localized: "When I start them", bundle: .module,
                   comment: "Settings > General > Transcription: row of the \"Transcribe finished recordings\" picker. Finished recordings wait on the Record tab until the user starts their transcription.")
        }
    }
}

/// The state of one job in the background transcription queue (PLAN.md 4.9).
/// The raw values are the ones in `queue.json` and the shared vectors.
public enum TranscriptionJobState: String, CaseIterable, Codable, Sendable {
    /// In line, not started (or started over after a relaunch).
    case waiting
    /// The final pass is decoding.
    case running
    /// Yielded at a window boundary; holds a checkpoint and resumes first.
    case suspended
    /// Transcript written.
    case done
    /// Transcription failed (PLAN.md 4.1 failure rules apply).
    case failed

    /// Waiting, running, or suspended: the recording is not transcribed yet.
    public var isPending: Bool {
        switch self {
        case .waiting, .running, .suspended: true
        case .done, .failed: false
        }
    }
}

/// The pure queue rules of PLAN.md 4.9 ("Queue rules") and 4.11 (`manual`),
/// shared with Windows through `shared/transcription-queue-tests.json`.
///
/// Scheduling contract (`next`):
/// - The engine may run a queued job when `foregroundWaiting == 0` and, for
///   `whenIdle` and `manual`, no session is active.
/// - At most one job runs. If a job is running, the decision is `suspend`
///   of that job when running is no longer allowed (or, under `manual`,
///   when the job is not released), else `none`.
/// - With no running job and running allowed: `resume` the first suspended
///   job in queue order, else `start` the first waiting job, else `none`;
///   under `manual` only released jobs are candidates.
///   Done and failed jobs are skipped wherever they are.
///
/// Manual (4.11): `Job.released` is read only under `manual`. A pending job
/// that is not released is held (`isHeld`): it is never started or resumed,
/// and a running one is suspended at its next window. Among released jobs
/// queue order decides, so releasing job 2 before job 1 does not let job 2
/// run first.
///
/// Invalid inputs are handled tolerantly and deterministically (no
/// assertion, so both platforms can run the same vectors):
/// - Two or more running jobs: only the first running job in queue order
///   is considered. It gets `suspend` when running is not allowed, else
///   the decision is `none` (nothing new starts while anything runs).
///   Repeated calls suspend the remaining running jobs one at a time.
/// - A running job behind a waiting or suspended job: the running job is
///   not preempted (`none` while running is allowed, `suspend` otherwise);
///   once it is suspended, queue order decides again.
/// - A suspended job behind a waiting job: the suspended job is resumed
///   first (it holds a checkpoint; in a valid queue it is always the head).
public enum TranscriptionQueuePolicy {
    /// What `next` reads of a job: its identifier, its state and, under
    /// `manual` only, whether the user released it.
    public struct Job: Equatable, Hashable, Sendable {
        public var id: String
        public var state: TranscriptionJobState
        public var released: Bool

        public init(id: String, state: TranscriptionJobState, released: Bool = false) {
            self.id = id
            self.state = state
            self.released = released
        }
    }

    /// What the queue should do now.
    public enum Decision: Equatable, Hashable, Sendable {
        /// Start the final pass of this waiting job.
        case start(String)
        /// Continue this suspended job from its checkpoint.
        case resume(String)
        /// The running job must yield at its next window.
        case suspend(String)
        /// Nothing to change.
        case none
    }

    /// Whether a queued job may run now. `manual` follows `whenIdle` here;
    /// whether a particular job is released is asked separately (`isHeld`).
    public static func mayRun(timing: FinalPassTiming, sessionActive: Bool, foregroundWaiting: Int) -> Bool {
        guard foregroundWaiting <= 0 else { return false }
        switch timing {
        case .immediate: return true
        case .whenIdle, .manual: return !sessionActive
        }
    }

    /// Held (PLAN.md 4.11): the timing is `manual` and the job is pending and
    /// not released. Never true under the other timings, whatever the flag says.
    public static func isHeld(timing: FinalPassTiming, job: Job) -> Bool {
        timing == .manual && job.state.isPending && !job.released
    }

    /// The next queue action; see the type's documentation for the rules.
    /// `jobs` is in queue order (first in first).
    public static func next(
        timing: FinalPassTiming,
        sessionActive: Bool,
        foregroundWaiting: Int,
        jobs: [Job]
    ) -> Decision {
        let allowed = mayRun(timing: timing, sessionActive: sessionActive, foregroundWaiting: foregroundWaiting)
        if let running = jobs.first(where: { $0.state == .running }) {
            return !allowed || isHeld(timing: timing, job: running) ? .suspend(running.id) : .none
        }
        guard allowed else { return .none }
        if let suspended = jobs.first(where: { $0.state == .suspended && !isHeld(timing: timing, job: $0) }) {
            return .resume(suspended.id)
        }
        if let waiting = jobs.first(where: { $0.state == .waiting && !isHeld(timing: timing, job: $0) }) {
            return .start(waiting.id)
        }
        return .none
    }

    /// For the decoder's yield check: true exactly when `next` would
    /// suspend a running job under these conditions. `runningReleased` is
    /// the running job's released flag, read only under `manual`.
    public static func shouldYield(
        timing: FinalPassTiming,
        sessionActive: Bool,
        foregroundWaiting: Int,
        runningReleased: Bool = true
    ) -> Bool {
        !mayRun(timing: timing, sessionActive: sessionActive, foregroundWaiting: foregroundWaiting)
            || (timing == .manual && !runningReleased)
    }

    /// Whether a job that just finished opens its notes flow (PLAN.md 4.9
    /// item 4): only when no session is active and no notes sheet or other
    /// notes flow is on screen. Otherwise its row offers "Generate Notes…".
    public static func presentsNotes(sessionActive: Bool, notesOnScreen: Bool) -> Bool {
        !sessionActive && !notesOnScreen
    }

    /// The number of recordings not transcribed yet (waiting, running, or
    /// suspended), held ones included. Quit asks for confirmation when it
    /// is above 0.
    public static func quitNeedsConfirmation(jobs: [Job]) -> Int {
        jobs.filter(\.state.isPending).count
    }

    /// Whether an update install must refuse: true while any job is
    /// waiting, running, or suspended; done and failed rows do not block.
    /// Under `manual` held waiting and suspended jobs do not block (they are
    /// in `queue.json` and come back held after the relaunch); a released
    /// job and a running one (even one whose Hold has not taken effect yet)
    /// still do.
    public static func blocksUpdateInstall(timing: FinalPassTiming, jobs: [Job]) -> Bool {
        jobs.contains { $0.state.isPending && ($0.state == .running || !isHeld(timing: timing, job: $0)) }
    }

    /// The rule of the timing-less shared vectors: any pending job blocks
    /// (no job is held under the other timings).
    public static func blocksUpdateInstall(jobs: [Job]) -> Bool {
        blocksUpdateInstall(timing: .immediate, jobs: jobs)
    }

    /// Whether every pending job is held, with at least one pending (PLAN.md
    /// 4.11, Quit): false under the other timings and for a queue with
    /// nothing pending. A running job that is not released counts as held.
    public static func allPendingHeld(timing: FinalPassTiming, jobs: [Job]) -> Bool {
        guard timing == .manual else { return false }
        let pending = jobs.filter(\.state.isPending)
        return !pending.isEmpty && pending.allSatisfy { !$0.released }
    }

    /// Whether the Record tab shows the queue's only job as the single
    /// meeting (PLAN.md 4.9 item 5): no session is active and the queue holds
    /// exactly one job. Never under `manual` (PLAN.md 4.11, 2026-10-02):
    /// there every recording is a row of the queue list, so its name and time
    /// always show.
    public static func showsSingleMeeting(timing: FinalPassTiming, sessionActive: Bool, jobCount: Int) -> Bool {
        timing != .manual && !sessionActive && jobCount == 1
    }

    /// Whether "Move to Trash…" applies to a job (PLAN.md 4.11, 2026-10-02):
    /// it is held (the timing is `manual`, it is not released) and idle, i.e.
    /// waiting or suspended, and `busy` is false (no step task, not saving
    /// its live preview). A running job, even one whose Hold has not taken
    /// effect yet, cannot be trashed.
    public static func canTrash(timing: FinalPassTiming, job: Job, busy: Bool) -> Bool {
        isHeld(timing: timing, job: job) && (job.state == .waiting || job.state == .suspended) && !busy
    }

    /// The released flag a pending job gets when the timing changes (PLAN.md
    /// 4.11): switching to `manual` releases jobs that already started
    /// (running or suspended) and holds waiting ones; any other switch
    /// returns nil, meaning the flag is left as it is (and ignored by the
    /// other timings).
    public static func releasedAfterTimingChange(
        from previous: FinalPassTiming,
        to timing: FinalPassTiming,
        state: TranscriptionJobState
    ) -> Bool? {
        guard timing == .manual, previous != .manual, state.isPending else { return nil }
        return state == .running || state == .suspended
    }
}
