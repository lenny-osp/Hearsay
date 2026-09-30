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

    /// Label for the Settings picker.
    public var displayName: String {
        switch self {
        case .immediate:
            String(localized: "Right away (in the background)", bundle: .module,
                   comment: "Settings > General > Transcription: final-pass timing that transcribes a finished recording at once, while the next one records")
        case .whenIdle:
            String(localized: "When no recording is running", bundle: .module,
                   comment: "Settings > General > Transcription: final-pass timing that waits until no recording is running")
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

/// The pure queue rules of PLAN.md 4.9 ("Queue rules"), shared with Windows
/// through `shared/transcription-queue-tests.json`.
///
/// Scheduling contract (`next`):
/// - The engine may run a queued job when `foregroundWaiting == 0` and, for
///   `whenIdle`, no session is active.
/// - At most one job runs. If a job is running, the decision is `suspend`
///   of that job when running is no longer allowed, else `none`.
/// - With no running job and running allowed: `resume` the first suspended
///   job in queue order, else `start` the first waiting job, else `none`.
///   Done and failed jobs are skipped wherever they are.
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
    /// What `next` reads of a job: its identifier and state.
    public struct Job: Equatable, Hashable, Sendable {
        public var id: String
        public var state: TranscriptionJobState

        public init(id: String, state: TranscriptionJobState) {
            self.id = id
            self.state = state
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

    /// Whether a queued job may run now.
    public static func mayRun(timing: FinalPassTiming, sessionActive: Bool, foregroundWaiting: Int) -> Bool {
        guard foregroundWaiting <= 0 else { return false }
        switch timing {
        case .immediate: return true
        case .whenIdle: return !sessionActive
        }
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
            return allowed ? .none : .suspend(running.id)
        }
        guard allowed else { return .none }
        if let suspended = jobs.first(where: { $0.state == .suspended }) {
            return .resume(suspended.id)
        }
        if let waiting = jobs.first(where: { $0.state == .waiting }) {
            return .start(waiting.id)
        }
        return .none
    }

    /// For the decoder's yield check: true exactly when `next` would
    /// suspend a running job under these conditions.
    public static func shouldYield(timing: FinalPassTiming, sessionActive: Bool, foregroundWaiting: Int) -> Bool {
        !mayRun(timing: timing, sessionActive: sessionActive, foregroundWaiting: foregroundWaiting)
    }

    /// Whether a job that just finished opens its notes flow (PLAN.md 4.9
    /// item 4): only when no session is active and no notes sheet or other
    /// notes flow is on screen. Otherwise its row offers "Generate Notes…".
    public static func presentsNotes(sessionActive: Bool, notesOnScreen: Bool) -> Bool {
        !sessionActive && !notesOnScreen
    }

    /// The number of recordings not transcribed yet (waiting, running, or
    /// suspended). Quit asks for confirmation when it is above 0.
    public static func quitNeedsConfirmation(jobs: [Job]) -> Int {
        jobs.filter(\.state.isPending).count
    }

    /// Whether an update install must refuse: true while any job is
    /// waiting, running, or suspended. Done and failed rows do not block.
    public static func blocksUpdateInstall(jobs: [Job]) -> Bool {
        jobs.contains(where: \.state.isPending)
    }
}
