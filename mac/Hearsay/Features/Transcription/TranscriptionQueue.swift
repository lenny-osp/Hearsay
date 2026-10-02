import AppKit
import Foundation
import HearsayCore
import HearsayWhisper
import Observation
import os

/// What a recording session hands to the queue when it ends normally
/// (PLAN.md 4.9 item 1).
struct RecordingHandover {
    /// The closed spool WAV.
    var recording: URL
    var stopTime: Date
    var tracker: SessionLanguageTracker
    var chineseScript: ChineseScript?
    var keepRecording: Bool
    var liveSegments: [CoreSegment]
    /// The session transcribed live chunks.
    var liveEnabled: Bool
    /// The session's live sink with the tail still in flight, or nil.
    var liveSink: LiveSink?
    var languageNotice: LanguageNotice?
    var liveNotice: String?
}

/// One recording in the background transcription queue (PLAN.md 4.9): what
/// the session owned after Stop, plus the final pass's progress and result.
@MainActor
@Observable
final class TranscriptionJob: Identifiable {
    let id: String
    /// Where the WAV is now: in the spool until the job is done, or in the
    /// output folder after a failure (a retry reads it there).
    fileprivate(set) var recording: TranscriptOutput.PendingRecording
    let stopTime: Date
    /// The meeting name, when one was given; the row shows the time otherwise.
    var displayName: String?
    fileprivate(set) var tracker: SessionLanguageTracker
    fileprivate(set) var chineseScript: ChineseScript?
    let keepRecording: Bool
    fileprivate(set) var liveSegments: [CoreSegment]
    fileprivate(set) var liveEnabled: Bool
    fileprivate(set) var liveNotice: String?
    fileprivate(set) var state: TranscriptionJobState {
        didSet { if state != oldValue { onChange?() } }
    }
    /// Manual timing (PLAN.md 4.11): the user released this job, so it may
    /// run (when no session is active and no foreground work waits). In
    /// memory only: `queue.json` does not store it, so every job restored at
    /// launch is unreleased. Read only while the timing is `manual`.
    fileprivate(set) var isReleased = false {
        didSet { if isReleased != oldValue { onChange?() } }
    }
    /// Final-pass progress, 0...1.
    fileprivate(set) var progress: Double = 0
    fileprivate(set) var languageNotice: LanguageNotice?
    fileprivate(set) var errorMessage: String?
    /// The failure was the missing model; the view links to Models.
    fileprivate(set) var needsModel = false
    /// The transcript: the final SRT, or the live preview saved after a
    /// failure.
    fileprivate(set) var srt: URL?
    /// The kept recording after the job ended (nil when not kept).
    fileprivate(set) var wav: URL?
    /// "Use live preview instead" was chosen; the live segments become the SRT.
    fileprivate(set) var isUsingLivePreview = false
    /// A "Transcribe again" pass over the finished recording is running.
    fileprivate(set) var isRerunning = false
    fileprivate(set) var rerunProgress: Double = 0
    fileprivate(set) var rerunError: String?
    /// The job finished while notes could not open; its row offers them.
    fileprivate(set) var offersNotes = false
    /// "Move to Trash…" failed (PLAN.md 4.11): the job stayed, and its row
    /// says why.
    fileprivate(set) var trashError: String?

    /// Told when `state` or `isReleased` changes (the queue recomputes its
    /// decoder hold flag).
    @ObservationIgnored fileprivate var onChange: (@MainActor () -> Void)?
    /// The session's live sink while its tail is still being transcribed.
    @ObservationIgnored fileprivate var liveSink: LiveSink?
    @ObservationIgnored fileprivate var checkpoint: TranscriptionCheckpoint?
    /// The model the checkpoint was decoded with; a resume with another
    /// model starts over.
    @ObservationIgnored fileprivate var checkpointLocation: WhisperModelLocation?
    /// The samples, read from the WAV when the job first runs and kept
    /// while it is suspended.
    @ObservationIgnored fileprivate var samples: [Float]?
    /// Samples of a finished recording whose WAV was not kept, held while a
    /// language notice offers a re-run.
    @ObservationIgnored fileprivate var rerunSamples: [Float]?
    @ObservationIgnored fileprivate var stepTask: Task<Void, Never>?
    /// Settles an undecided Auto language right after Stop (see
    /// `TranscriptionQueue.settleEarly`).
    @ObservationIgnored fileprivate var settleTask: Task<Void, Never>?
    @ObservationIgnored fileprivate var finishTask: Task<Void, Never>?
    @ObservationIgnored fileprivate var rerunTask: Task<Void, Never>?
    @ObservationIgnored fileprivate var liveSegmentsDirty = false
    @ObservationIgnored fileprivate var rerunCancelledByQuit = false

    fileprivate init(
        id: String, recording: TranscriptOutput.PendingRecording, stopTime: Date,
        tracker: SessionLanguageTracker, chineseScript: ChineseScript?, keepRecording: Bool,
        liveSegments: [CoreSegment], liveEnabled: Bool, state: TranscriptionJobState = .waiting
    ) {
        self.id = id
        self.recording = recording
        self.stopTime = stopTime
        self.tracker = tracker
        self.chineseScript = chineseScript
        self.keepRecording = keepRecording
        self.liveSegments = liveSegments
        self.liveEnabled = liveEnabled
        self.state = state
    }

    /// The language of the transcript (nil while Auto is undecided).
    var language: TranscriptLanguage? { tracker.language }

    /// Not transcribed yet: waiting, running, or suspended.
    var isPending: Bool { state.isPending }

    /// Auto has not decided the language yet.
    var isDetectingLanguage: Bool { isPending && tracker.isUndecided }

    /// Live chunks of the ended session still to be transcribed for the
    /// live preview (0 once the job ended).
    var liveChunksWaiting: Int { isPending ? liveSink?.waiting ?? 0 : 0 }

    /// Live chunks of the ended session are still in flight, even after the
    /// job ended (the debug replay waits for them).
    var hasLiveTail: Bool { liveSink?.hasPendingChunks ?? false }

    /// "Use live preview instead" applies.
    var canUseLivePreview: Bool {
        isPending && liveEnabled && !isUsingLivePreview && recording.inSpool
    }

    /// The language notice's buttons apply: before the pass starts (it
    /// will use the picked language), or after it finished while the audio
    /// is still available.
    var canChangeLanguage: Bool {
        switch state {
        case .waiting: return !isUsingLivePreview
        case .done: return srt != nil && (wav != nil || rerunSamples != nil) && !isRerunning
        case .running, .suspended, .failed: return false
        }
    }

    var canRetry: Bool { state == .failed }

    var canDismiss: Bool { (state == .done || state == .failed) && !isRerunning }

    /// Progress to show: the final pass while pending, a re-run while it runs.
    var displayProgress: Double? {
        if isRerunning { return rerunProgress }
        return isPending ? progress : nil
    }

    /// The files to reveal in Finder.
    var files: [URL] {
        var urls = [srt, wav].compactMap { $0 }
        if urls.isEmpty { urls = [recording.url] }
        return urls
    }

    /// The name shown in the queue: the meeting name, else when the
    /// recording started (from its timestamp name), else when it stopped.
    var title: String {
        if let displayName, !displayName.isEmpty { return displayName }
        let stem = recording.url.deletingPathExtension().lastPathComponent
        let date = HistoryIndex.timestampDate(stem: stem) ?? stopTime
        return date.formatted(Date.FormatStyle(date: .abbreviated, time: .shortened)
            .locale(InterfaceLanguageLaunch.applied.locale))
    }
}

/// A Sendable snapshot of "whenIdle or manual with a session active, or
/// manual with a running job that is not released", read by the decoder's
/// yield check between windows (PLAN.md 4.9, 4.11).
private final class HoldFlag: Sendable {
    private let value = OSAllocatedUnfairLock(initialState: false)

    var isHeld: Bool { value.withLock { $0 } }

    func set(_ held: Bool) { value.withLock { $0 = held } }
}

/// The background transcription queue (PLAN.md 4.9): recordings whose
/// session ended wait here for their final pass, first in first out, one at
/// a time, on the one loaded model. Owned by `AppDelegate` next to
/// `RecordingController`.
///
/// The driver re-evaluates `TranscriptionQueuePolicy.next` whenever a job is
/// added, a step returns, the session-active flag or the timing changes,
/// and every 250 ms while a job waits or is suspended (the engine's
/// foreground counter is not observable). A step runs through
/// `WhisperEngine.transcribeStep`, which suspends at the next 30 s window
/// while foreground work waits or `holdWhile` is true (whenIdle or manual
/// with a session active; manual with a running job that is not released,
/// i.e. a Hold).
///
/// Manual timing (PLAN.md 4.11): every job has an in-memory `isReleased`
/// flag, read only while the timing is `manual`. A pending, unreleased job is
/// held (`isHeld(_:)`); the driver never starts or resumes it. `release`,
/// `releaseAll` and `hold` change the flag; Try Again and "Use live preview
/// instead" release the job; a switch to manual releases started jobs and
/// holds waiting ones.
///
/// Completion and failure follow PLAN.md 4.1 (`TranscriptOutput.saveTranscript`
/// and `keepAfterFailure`). The queue is saved to `<spool>/queue.json` on every
/// change and each job's live segments to `<spool>/<id>.live.srt` at most
/// once per second; `restore()` queues the saved jobs again at launch.
@MainActor
@Observable
final class TranscriptionQueue {
    /// A queue event, for the debug replay.
    enum Event {
        case queued(id: String, recording: URL)
        case running(id: String)
        case suspended(id: String, progress: Double)
        case resumed(id: String)
        case language(id: String, decision: LanguageDecision)
        case done(id: String, srt: URL)
        case failed(id: String, message: String)
        /// Manual timing: the job is held (queued unreleased, put on hold, or
        /// held by a switch to manual).
        case held(id: String)
        /// Manual timing: the job was released (Transcribe, Transcribe All,
        /// Try Again, or a switch to manual for a job that had started).
        case released(id: String)
        /// Manual timing: "Move to Trash…" on a held row moved the job's WAV
        /// to the Trash (`trashedAs`, where it landed, when the system says)
        /// and removed the job.
        case trashed(id: String, recording: URL, trashedAs: URL?)
    }

    /// A finished SRT waiting for the Record tab's notes flow.
    struct NotesRequest: Equatable {
        var jobID: String
        var srt: URL
        var language: TranscriptLanguage
    }

    static let pollInterval: Duration = .milliseconds(250)
    private static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "queue")

    /// The jobs in queue order, done and failed ones included until dismissed.
    private(set) var jobs: [TranscriptionJob] = []
    /// See `takeNotesRequest()`.
    private(set) var notesRequest: NotesRequest?
    /// A session is active (Starting, Recording, Paused, Stopping); set by
    /// `RecordingController`.
    private(set) var isSessionActive = false
    /// whenIdle or manual, and a session is active: released jobs do not run.
    private(set) var isHeldForSession = false

    /// Debug only: sees every queue event (see `RecordingReplay`).
    @ObservationIgnored var eventObserver: (@MainActor (Event) -> Void)?
    /// Whether a notes sheet or another notes flow is on screen.
    @ObservationIgnored var notesOnScreen: @MainActor () -> Bool = { NotesFlowViewModel.isAnyRunning }

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let modelStore: ModelStore
    @ObservationIgnored private let engine: WhisperEngine
    @ObservationIgnored private let spool: RecordingSpool
    @ObservationIgnored private let store: TranscriptionQueueStore
    @ObservationIgnored private let modelLocation: @MainActor (ModelStore) throws -> WhisperModelLocation
    /// false: the queue only holds jobs (UI snapshots).
    @ObservationIgnored private let drives: Bool
    @ObservationIgnored private let hold = HoldFlag()
    @ObservationIgnored private var pollTask: Task<Void, Never>?
    @ObservationIgnored private var liveWriteTask: Task<Void, Never>?
    @ObservationIgnored private var isQuitting = false
    @ObservationIgnored private var lastTiming: FinalPassTiming

    init(
        settings: AppSettings,
        modelStore: ModelStore,
        engine: WhisperEngine,
        spool: RecordingSpool = RecordingSpool(),
        modelLocation: @escaping @MainActor (ModelStore) throws -> WhisperModelLocation = {
            try WhisperModelLocation.active(in: $0)
        },
        drives: Bool = true
    ) {
        self.settings = settings
        self.modelStore = modelStore
        self.engine = engine
        self.spool = spool
        self.store = TranscriptionQueueStore(spool: spool)
        self.modelLocation = modelLocation
        self.drives = drives
        self.lastTiming = settings.finalPassTiming
        updateHold()
        observeTiming()
    }

    // MARK: - State

    /// The jobs as the policy reads them. A job saving its live preview is
    /// left out once its step returned, so the next job can start; while its
    /// step runs it counts as running and released (the user asked for that
    /// save, so it is never held).
    private var policyJobs: [TranscriptionQueuePolicy.Job] {
        jobs.compactMap { job in
            if job.isUsingLivePreview, job.isPending {
                return job.stepTask == nil ? nil : .init(id: job.id, state: .running, released: true)
            }
            return .init(id: job.id, state: job.state, released: job.isReleased)
        }
    }

    private var stateJobs: [TranscriptionQueuePolicy.Job] {
        jobs.map { .init(id: $0.id, state: $0.state, released: $0.isReleased) }
    }

    /// Recordings not transcribed yet, held ones included; Quit asks when above 0.
    var pendingCount: Int { TranscriptionQueuePolicy.quitNeedsConfirmation(jobs: stateJobs) }

    /// An update install must wait. Held jobs (manual) do not block: they
    /// are in `queue.json` and come back held after the relaunch.
    var blocksUpdateInstall: Bool {
        TranscriptionQueuePolicy.blocksUpdateInstall(timing: settings.finalPassTiming, jobs: stateJobs)
    }

    /// The job is held: the timing is manual and it is pending and not
    /// released (PLAN.md 4.11).
    func isHeld(_ job: TranscriptionJob) -> Bool {
        TranscriptionQueuePolicy.isHeld(
            timing: settings.finalPassTiming, job: .init(id: job.id, state: job.state, released: job.isReleased))
    }

    /// Hold applies: the timing is manual and the job is pending, released,
    /// and not saving its live preview.
    func canHold(_ job: TranscriptionJob) -> Bool {
        settings.finalPassTiming == .manual && job.isPending && job.isReleased && !job.isUsingLivePreview
    }

    /// How many jobs are held; "Transcribe All" shows while this is above 0.
    var heldCount: Int { jobs.filter { isHeld($0) }.count }

    /// Every pending job is held, and there is at least one: the quit alert
    /// and the menu bar line say so. False unless the timing is manual.
    var allPendingHeld: Bool {
        TranscriptionQueuePolicy.allPendingHeld(timing: settings.finalPassTiming, jobs: stateJobs)
    }

    /// The job being transcribed or suspended, for the menu bar. A suspended
    /// job that is held (manual, put on hold) is not active: it waits for
    /// the user.
    var activeJob: TranscriptionJob? {
        jobs.first { $0.state == .running || ($0.state == .suspended && !isHeld($0)) }
    }

    /// With no session active and exactly one job, the Record tab shows it
    /// as the single meeting it always showed (PLAN.md 4.9 item 5). Never
    /// under manual (PLAN.md 4.11, 2026-10-02): every recording is a row of
    /// the queue list, so its name and time show. Reads the timing, so a
    /// view re-renders when it changes.
    var featuredJob: TranscriptionJob? {
        guard TranscriptionQueuePolicy.showsSingleMeeting(
            timing: settings.finalPassTiming, sessionActive: isSessionActive, jobCount: jobs.count)
        else { return nil }
        return jobs.first
    }

    /// "Move to Trash…" applies (PLAN.md 4.11): the job is held and idle,
    /// i.e. waiting or suspended, with no step running and not saving its
    /// live preview.
    func canTrash(_ job: TranscriptionJob) -> Bool {
        TranscriptionQueuePolicy.canTrash(
            timing: settings.finalPassTiming,
            job: .init(id: job.id, state: job.state, released: job.isReleased),
            busy: job.stepTask != nil || job.finishTask != nil || job.isUsingLivePreview)
    }

    /// Output-folder stems the queue is working on: History does not rename
    /// them (a retry reads its WAV there, a re-run rewrites its SRT).
    var busyStems: Set<String> {
        var stems = Set<String>()
        for job in jobs where job.isPending || job.isRerunning {
            var urls = [job.srt, job.wav].compactMap { $0 }
            if !job.recording.inSpool { urls.append(job.recording.url) }
            for url in urls { stems.insert(url.deletingPathExtension().lastPathComponent) }
        }
        return stems
    }

    /// A job waits or is suspended because a session runs in whenIdle or
    /// manual; its row says "Paused while recording". A held job (manual,
    /// not released) is not paused for the session: it waits for the user.
    func isPausedForSession(_ job: TranscriptionJob) -> Bool {
        isHeldForSession && (job.state == .suspended || job.state == .waiting)
            && !job.isUsingLivePreview && !isHeld(job)
    }

    /// The finished SRT waiting for the notes flow, once. The request is
    /// checked again when it is taken (the Record tab may appear only later,
    /// during a session or while another notes flow runs); when the notes
    /// may not open now, its job's row offers "Generate Notes…" instead.
    func takeNotesRequest() -> NotesRequest? {
        guard let request = notesRequest else { return nil }
        notesRequest = nil
        guard TranscriptionQueuePolicy.presentsNotes(sessionActive: isSessionActive, notesOnScreen: notesOnScreen())
        else {
            offerNotesOnRow(request)
            return nil
        }
        return request
    }

    /// A pending notes request that will not open: its row offers it.
    private func offerNotesOnRow(_ request: NotesRequest) {
        jobs.first { $0.id == request.jobID }?.offersNotes = true
    }

    // MARK: - Session and timing

    func setSessionActive(_ active: Bool) {
        guard active != isSessionActive else { return }
        isSessionActive = active
        // A notes request nobody took before the session started must not
        // open during it.
        if active, let request = notesRequest {
            notesRequest = nil
            offerNotesOnRow(request)
        }
        updateHold()
        evaluate()
    }

    /// Recomputes the flag the decoder's yield check reads (whenIdle or
    /// manual with a session active; manual with a running job that is not
    /// released, i.e. a Hold) and `isHeldForSession`.
    private func updateHold() {
        let timing = settings.finalPassTiming
        let sessionHold = (timing == .whenIdle || timing == .manual) && isSessionActive
        let holdRunning = timing == .manual
            && jobs.contains { $0.state == .running && !$0.isReleased && !$0.isUsingLivePreview }
        hold.set(sessionHold || holdRunning)
        if sessionHold != isHeldForSession { isHeldForSession = sessionHold }
    }

    /// Wires a new job's state and released changes to the hold flag.
    private func watch(_ job: TranscriptionJob) {
        job.onChange = { [weak self] in self?.updateHold() }
    }

    private func observeTiming() {
        withObservationTracking {
            _ = settings.finalPassTiming
        } onChange: { [weak self] in
            // onChange fires before the new value is stored.
            Task { @MainActor in
                guard let self else { return }
                self.applyTimingChange()
                self.updateHold()
                self.evaluate()
                self.observeTiming()
            }
        }
    }

    /// PLAN.md 4.11: switching to manual releases the jobs that already
    /// started (running or suspended) and holds the waiting ones; switching
    /// away ignores the flags, and `evaluate` applies the other timing's
    /// rules at once.
    private func applyTimingChange() {
        let timing = settings.finalPassTiming
        let previous = lastTiming
        lastTiming = timing
        for job in jobs {
            guard let value = TranscriptionQueuePolicy.releasedAfterTimingChange(
                from: previous, to: timing, state: job.state) else { continue }
            let changes = job.isReleased != value
            setReleased(job, value)
            // A waiting job that was never released becomes held without a
            // flag change: announce it too.
            if !value, !changes { eventObserver?(.held(id: job.id)) }
        }
    }

    /// Sets the released flag. Under manual a change is announced to the
    /// observer (the debug replay) as a held or released event; under the
    /// other timings the flag has no visible meaning and nothing is announced.
    private func setReleased(_ job: TranscriptionJob, _ value: Bool) {
        guard job.isReleased != value else { return }
        job.isReleased = value
        guard settings.finalPassTiming == .manual else { return }
        eventObserver?(value ? .released(id: job.id) : .held(id: job.id))
        Self.logger.info("\(value ? "released" : "held", privacy: .public) \(job.id, privacy: .public)")
    }

    // MARK: - Adding jobs

    /// A session ended normally: it becomes a waiting job.
    func enqueue(_ handover: RecordingHandover) {
        let job = TranscriptionJob(
            id: UUID().uuidString,
            recording: .init(url: handover.recording, inSpool: true),
            stopTime: handover.stopTime,
            tracker: handover.tracker,
            chineseScript: handover.chineseScript,
            keepRecording: handover.keepRecording,
            liveSegments: handover.liveSegments,
            liveEnabled: handover.liveEnabled
        )
        job.languageNotice = handover.languageNotice
        job.liveNotice = handover.liveNotice
        adopt(handover.liveSink, for: job)
        add(job)
        settleEarly(job)
    }

    /// Auto was still undecided at Stop: detect over the whole recording
    /// now, as foreground work like the detection during the session and
    /// whatever the timing, so the live tail (which waits for the language)
    /// finishes and the row shows the language. The samples are read for
    /// this and dropped again; the pass reads them when it runs.
    private func settleEarly(_ job: TranscriptionJob) {
        guard drives, job.tracker.language == nil else { return }
        job.settleTask = Task { [weak self] in
            defer { job.settleTask = nil }
            guard let self, !Task.isCancelled, !self.isQuitting,
                  let location = try? self.modelLocation(self.modelStore) else { return }
            let url = job.recording.url
            guard let samples = try? await Task.detached(priority: .userInitiated, operation: {
                try AudioFileLoader.loadMono16k(url: url)
            }).value, job.isPending, !Task.isCancelled else { return }
            await self.settleLanguage(job, samples: samples, location: location)
        }
    }

    /// "Try Again" after a capture failure: the kept WAV (normally in the
    /// output folder, next to the saved live preview) becomes a job.
    func enqueueRetry(
        recording: TranscriptOutput.PendingRecording, tracker: SessionLanguageTracker,
        chineseScript: ChineseScript?, keepRecording: Bool, liveSegments: [CoreSegment], transcript: URL?
    ) {
        let job = TranscriptionJob(
            id: UUID().uuidString, recording: recording, stopTime: Date(), tracker: tracker,
            chineseScript: chineseScript, keepRecording: keepRecording,
            liveSegments: liveSegments, liveEnabled: false
        )
        job.srt = transcript
        // The user asked for this pass (PLAN.md 4.11): not held under manual.
        job.isReleased = true
        add(job)
    }

    private func add(_ job: TranscriptionJob) {
        jobs.append(job)
        watch(job)
        updateHold()
        eventObserver?(.queued(id: job.id, recording: job.recording.url))
        if isHeld(job) { eventObserver?(.held(id: job.id)) }
        Self.logger.info("queued \(job.id, privacy: .public) \(job.recording.url.lastPathComponent, privacy: .public)")
        if !job.liveSegments.isEmpty { markLiveSegmentsChanged(job) }
        persist()
        evaluate()
    }

    /// The ended session's live tail now goes to `job`.
    private func adopt(_ sink: LiveSink?, for job: TranscriptionJob) {
        guard let sink else { return }
        job.liveSink = sink
        sink.onResult = { [weak self, weak job] _, outcome in
            guard let self, let job, job.isPending else { return }
            switch outcome {
            case .success(let cues):
                job.liveSegments.append(contentsOf: cues)
                self.markLiveSegmentsChanged(job)
            case .failure(let error):
                job.liveNotice = String(localized: "Live preview missed a chunk: \(RecordingController.describe(error))",
                                        comment: "Record tab notice. %@ is the reason.")
            }
        }
        if let language = job.tracker.language { sink.setLanguage(language) }
    }

    /// Queues the jobs saved in `queue.json` again (PLAN.md 4.9). Jobs that
    /// were done or failed are removed; running and suspended ones start
    /// over.
    func restore() {
        let result = store.load()
        if let error = result.error {
            Self.logger.error("\(error.description, privacy: .public)")
        }
        for dropped in result.dropped {
            Self.logger.notice(
                "dropped job \(dropped.id ?? "?", privacy: .public) (\(dropped.wavFileName ?? "?", privacy: .public)): \(String(describing: dropped.reason), privacy: .public)")
            if let id = dropped.id { store.removeLiveSegments(jobID: id) }
        }
        for entry in result.manifest.jobs {
            guard entry.state.isPending else {
                store.removeLiveSegments(jobID: entry.id)
                continue
            }
            var tracker = SessionLanguageTracker(choice: entry.languageChoice, preferred: settings.preferredLanguage)
            if let settled = entry.settledLanguage { tracker.choose(settled) }
            let live = store.readLiveSegments(jobID: entry.id)
            let job = TranscriptionJob(
                id: entry.id,
                recording: .init(url: spool.root.appendingPathComponent(entry.wavFileName), inSpool: true),
                stopTime: entry.stopTime, tracker: tracker,
                chineseScript: entry.chineseScript ?? tracker.language?.chineseScript,
                keepRecording: entry.keepRecording, liveSegments: live, liveEnabled: !live.isEmpty
            )
            job.displayName = entry.displayName
            // Not released: with manual the app never starts a pass by itself
            // after a relaunch (PLAN.md 4.11).
            jobs.append(job)
            watch(job)
            Self.logger.info("restored \(entry.id, privacy: .public) \(entry.wavFileName, privacy: .public)")
            eventObserver?(.queued(id: job.id, recording: job.recording.url))
            if isHeld(job) { eventObserver?(.held(id: job.id)) }
        }
        updateHold()
        persist()
        evaluate()
    }

    /// Inserts a job as it is, without running it (UI snapshots only).
    func insertSample(
        recording: URL, state: TranscriptionJobState, progress: Double = 0, srt: URL? = nil,
        language: TranscriptLanguage = .english, offersNotes: Bool = false, released: Bool = false,
        liveSegments: [CoreSegment] = [], trashError: String? = nil
    ) {
        var tracker = SessionLanguageTracker(choice: .fixed(language), preferred: language)
        tracker.choose(language)
        let job = TranscriptionJob(
            id: UUID().uuidString, recording: .init(url: recording, inSpool: state.isPending),
            stopTime: Date(), tracker: tracker, chineseScript: language.chineseScript,
            keepRecording: true, liveSegments: liveSegments, liveEnabled: state.isPending, state: state
        )
        job.progress = progress
        job.srt = srt
        job.wav = state == .done ? recording : nil
        job.offersNotes = offersNotes
        job.isReleased = released
        job.trashError = trashError
        jobs.append(job)
        watch(job)
    }

    // MARK: - Driver

    /// Starts or resumes the next job when the policy says so; keeps polling
    /// while a job waits for foreground work or the session to end.
    func evaluate() {
        guard drives, !isQuitting else { return }
        let decision = TranscriptionQueuePolicy.next(
            timing: settings.finalPassTiming, sessionActive: isSessionActive,
            foregroundWaiting: engine.foregroundWaiting, jobs: policyJobs
        )
        switch decision {
        case .start(let id):
            if let job = jobs.first(where: { $0.id == id }) { run(job, resuming: false) }
        case .resume(let id):
            if let job = jobs.first(where: { $0.id == id }) { run(job, resuming: true) }
        case .suspend, .none:
            // A running step suspends itself at its next window.
            break
        }
        schedulePoll()
    }

    private func schedulePoll() {
        let running = policyJobs.contains { $0.state == .running }
        // Held jobs (manual) wait for the user, not for foreground work or
        // the session: no polling for them.
        let timing = settings.finalPassTiming
        let queued = policyJobs.contains {
            ($0.state == .waiting || $0.state == .suspended)
                && !TranscriptionQueuePolicy.isHeld(timing: timing, job: $0)
        }
        guard !running, queued, pollTask == nil else { return }
        pollTask = Task { [weak self] in
            try? await Task.sleep(for: Self.pollInterval)
            guard let self else { return }
            self.pollTask = nil
            self.evaluate()
        }
    }

    private func run(_ job: TranscriptionJob, resuming: Bool) {
        job.state = .running
        eventObserver?(resuming ? .resumed(id: job.id) : .running(id: job.id))
        persist()
        job.stepTask = Task { [weak self] in
            await self?.performStep(job)
            job.stepTask = nil
            self?.evaluate()
        }
    }

    /// One step of a job: read the samples and settle the language when
    /// needed, then decode until the end or the next suspension.
    private func performStep(_ job: TranscriptionJob) async {
        await job.settleTask?.value
        guard isStillRunning(job), !Task.isCancelled else { return }
        let location: WhisperModelLocation
        do {
            location = try modelLocation(modelStore)
        } catch {
            await fail(job, RecordingController.describe(error), missingModel: true)
            return
        }
        guard await loadSamples(job), let samples = job.samples, isStillRunning(job), !Task.isCancelled
        else { return }
        await settleLanguage(job, samples: samples, location: location)
        guard isStillRunning(job) else { return }
        guard let language = job.tracker.language else {
            await fail(job, String(localized: "Transcription failed: the language could not be decided.",
                                   comment: "Transcription error"))
            return
        }
        // The decoder runs at least one window before it checks for a
        // yield; do not start one when the job may not run any more (a
        // session started, or foreground work arrived, while the samples
        // were read or the language detected), or the user put it on hold
        // (manual).
        guard !TranscriptionQueuePolicy.shouldYield(
            timing: settings.finalPassTiming, sessionActive: isSessionActive,
            foregroundWaiting: engine.foregroundWaiting, runningReleased: job.isReleased
        ) else {
            job.state = job.checkpoint == nil ? .waiting : .suspended
            eventObserver?(.suspended(id: job.id, progress: job.progress))
            persist()
            return
        }
        if job.checkpoint != nil, job.checkpointLocation != location {
            // Another model is active now: its checkpoint does not apply.
            job.checkpoint = nil
            job.checkpointLocation = nil
            job.progress = 0
        }
        let hold = hold
        do {
            let step = try await engine.transcribeStep(
                samples: samples, location: location,
                options: TranscriptionOptions.app(language: language),
                resumingFrom: job.checkpoint,
                progress: { [weak job] value in
                    Task { @MainActor in
                        guard let job, job.state == .running else { return }
                        job.progress = min(max(value, 0), 1)
                    }
                },
                holdWhile: { hold.isHeld }
            )
            switch step {
            case .finished(let transcription):
                // A finished pass is written even while quitting (only the
                // file writes; the live tail is not awaited then).
                guard job.state == .running, !job.isUsingLivePreview else { return }
                await complete(job, with: transcription.cues(script: job.chineseScript))
            case .suspended(let checkpoint):
                guard isStillRunning(job) else { return }
                job.checkpoint = checkpoint
                job.checkpointLocation = location
                job.progress = checkpoint.fractionDone
                job.state = .suspended
                eventObserver?(.suspended(id: job.id, progress: checkpoint.fractionDone))
                persist()
            }
        } catch {
            // Cancelled by "Use live preview instead" or by quitting; whoever
            // cancelled writes the files (or the next launch starts over).
            guard !error.isTranscriptionCancelled else { return }
            guard isStillRunning(job) else { return }
            await fail(job, String(localized: "Transcription failed: \(RecordingController.describe(error))",
                                   comment: "Transcription error. %@ is the reason."))
        }
    }

    private func isStillRunning(_ job: TranscriptionJob) -> Bool {
        job.state == .running && !job.isUsingLivePreview && !isQuitting
    }

    /// Reads the job's WAV unless its samples are already held. Fails the
    /// job and returns false when the file cannot be read.
    private func loadSamples(_ job: TranscriptionJob) async -> Bool {
        guard job.samples == nil else { return true }
        let url = job.recording.url
        do {
            let samples = try await Task.detached(priority: .userInitiated) {
                try AudioFileLoader.loadMono16k(url: url)
            }.value
            job.samples = samples
            return true
        } catch {
            guard job.isPending, !isQuitting else { return false }
            await fail(job, String(localized: "Transcription failed: \(RecordingController.describe(error))",
                                   comment: "Transcription error. %@ is the reason."))
            return false
        }
    }

    /// Settles the job's language before its pass when the session did not:
    /// one detection over the whole recording, locked with the Auto rules
    /// (the preferred language when detection is unsure or fails).
    private func settleLanguage(_ job: TranscriptionJob, samples: [Float], location: WhisperModelLocation) async {
        guard !job.tracker.isSettled else { return }
        let result: DetectionResult?
        do {
            result = try await engine.detectLanguage(samples: samples, location: location)
        } catch {
            // A cancelled detection (quit, "Use live preview instead")
            // settles nothing; only a real failure falls back.
            if error.isTranscriptionCancelled || Task.isCancelled || isQuitting { return }
            result = nil
        }
        guard !Task.isCancelled, !isQuitting, !job.tracker.isSettled, job.isPending else { return }
        let decision = job.tracker.finish(detection: result?.decisionInput)
        job.languageNotice = LanguageNotice(decision: decision)
        languageChanged(job)
        eventObserver?(.language(id: job.id, decision: decision))
    }

    private func languageChanged(_ job: TranscriptionJob) {
        if let language = job.tracker.language {
            job.chineseScript = language.chineseScript
            job.liveSink?.setLanguage(language)
        }
        persist()
    }

    // MARK: - Completion

    private func complete(_ job: TranscriptionJob, with cues: [CoreSegment]) async {
        switch TranscriptOutput.saveTranscript(
            cues, recording: job.recording, keepRecording: job.keepRecording, settings: settings
        ) {
        case .success(let saved):
            job.srt = saved.srt
            job.wav = saved.wav
            if let notice = saved.notice { job.liveNotice = notice }
            if let wav = saved.wav {
                job.recording = .init(url: wav, inSpool: wav != job.recording.url ? false : job.recording.inSpool)
            }
            // A re-run needs the audio; hold it only when the WAV is gone.
            job.rerunSamples = job.languageNotice != nil && saved.wav == nil ? job.samples : nil
            if job.rerunSamples != nil { dropOlderRerunSamples(keeping: job) }
            job.samples = nil
            job.checkpoint = nil
            job.needsModel = false
            job.errorMessage = nil
            job.progress = 1
            job.state = .done
            // The final SRT replaces the live preview: drop the rest of it
            // (a chunk already in the engine still finishes).
            if let sink = job.liveSink {
                sink.close()
                Task { [weak job] in
                    await sink.drain()
                    if job?.liveSink === sink { job?.liveSink = nil }
                }
            }
            store.removeLiveSegments(jobID: job.id)
            job.liveSegmentsDirty = false
            eventObserver?(.done(id: job.id, srt: saved.srt))
            Self.logger.info("done \(job.id, privacy: .public) \(saved.srt.lastPathComponent, privacy: .public)")
            persist()
            offerNotes(job, srt: saved.srt)
        case .failure(let failure):
            await fail(job, failure.message)
        }
    }

    /// At most one finished job holds its samples for a re-run (an hour of
    /// audio is about 230 MB): the newest. The older ones lose the re-run,
    /// and their rows say why.
    private func dropOlderRerunSamples(keeping newest: TranscriptionJob) {
        for job in jobs where job !== newest && job.rerunSamples != nil {
            job.rerunSamples = nil
            if job.languageNotice != nil, !job.isRerunning {
                job.rerunError = String(localized: "Could not transcribe again: the recording was not kept.",
                                        comment: "Error")
            }
        }
    }

    /// PLAN.md 4.1 failure rules: the WAV is kept (in the output folder),
    /// the live preview is saved as its SRT (after the rest of the live
    /// tail), and the row offers Retry.
    private func fail(_ job: TranscriptionJob, _ message: String, missingModel: Bool = false) async {
        if !isQuitting { await finishLiveTail(job) }
        guard job.isPending else { return }
        let kept = TranscriptOutput.keepAfterFailure(
            message, recording: job.recording, liveSegments: job.liveSegments,
            existingTranscript: job.srt, settings: settings, spool: spool
        )
        if let recording = kept.recording { job.recording = recording }
        job.wav = kept.recording?.url
        job.srt = kept.srt
        job.errorMessage = kept.message
        job.needsModel = missingModel
        job.samples = nil
        job.checkpoint = nil
        job.state = .failed
        store.removeLiveSegments(jobID: job.id)
        job.liveSegmentsDirty = false
        eventObserver?(.failed(id: job.id, message: kept.message))
        Self.logger.error("failed \(job.id, privacy: .public): \(message, privacy: .public)")
        persist()
    }

    /// Waits for the live chunks still coming for `job`; drops them when
    /// the language is still open (they would wait for it forever).
    private func finishLiveTail(_ job: TranscriptionJob) async {
        guard let sink = job.liveSink else { return }
        if job.tracker.language == nil { sink.close() }
        await sink.drain()
        job.liveSink = nil
    }

    /// PLAN.md 4.9 item 4: the notes flow opens only when no session is
    /// active and no notes flow is on screen; otherwise the row offers it.
    private func offerNotes(_ job: TranscriptionJob, srt: URL) {
        guard let language = job.tracker.language else { return }
        if TranscriptionQueuePolicy.presentsNotes(sessionActive: isSessionActive, notesOnScreen: notesOnScreen()) {
            // A request nobody took yet stays available on its row.
            if let pending = notesRequest, pending.jobID != job.id { offerNotesOnRow(pending) }
            job.offersNotes = false
            notesRequest = NotesRequest(jobID: job.id, srt: srt, language: language)
        } else {
            job.offersNotes = true
        }
    }

    // MARK: - Actions

    /// "Use live preview instead": stops the job's pass (at the next 30 s
    /// window) and writes its live segments as the SRT, after the rest of
    /// the live tail.
    func useLivePreviewInstead(_ job: TranscriptionJob) {
        guard job.canUseLivePreview else { return }
        // The user asked for this save: a held job (manual) is released, so
        // it is not listed as "not transcribed yet" while the live preview
        // is written (PLAN.md 4.11).
        setReleased(job, true)
        job.isUsingLivePreview = true
        job.stepTask?.cancel()
        job.finishTask = Task { [weak self] in
            defer { job.finishTask = nil }
            guard let self else { return }
            await job.settleTask?.value
            if job.tracker.language == nil, let location = try? self.modelLocation(self.modelStore),
               await self.loadSamples(job), let samples = job.samples {
                await self.settleLanguage(job, samples: samples, location: location)
            }
            await self.finishLiveTail(job)
            await job.stepTask?.value
            guard job.isPending, !self.isQuitting else { return }
            await self.complete(job, with: job.liveSegments)
            self.evaluate()
        }
        evaluate()
    }

    /// Retry after a failure: the job waits in line again, reading its kept WAV.
    func retry(_ job: TranscriptionJob) {
        guard job.canRetry else { return }
        // The user asked for this pass: released under manual (PLAN.md 4.11).
        setReleased(job, true)
        job.state = .waiting
        job.errorMessage = nil
        job.needsModel = false
        job.progress = 0
        job.checkpoint = nil
        job.isUsingLivePreview = false
        job.offersNotes = false
        persist()
        evaluate()
    }

    /// Manual timing, "Transcribe" on a held row (PLAN.md 4.11): the job may
    /// run; released jobs run first in, first out (queue order), so
    /// releasing a later job first does not let it jump an earlier released
    /// one. A pending job only; harmless under the other timings, which
    /// ignore the flag.
    func release(_ job: TranscriptionJob) {
        guard job.isPending else { return }
        job.trashError = nil
        setReleased(job, true)
        updateHold()
        evaluate()
    }

    /// Manual timing, "Transcribe All": releases every held job.
    func releaseAll() {
        for job in jobs where job.isPending {
            job.trashError = nil
            setReleased(job, true)
        }
        updateHold()
        evaluate()
    }

    /// Manual timing, "Hold" on a released row (PLAN.md 4.11): clears the
    /// flag. A running job suspends at its next 30 s window and keeps its
    /// checkpoint, so a later `release` continues where it stopped.
    func hold(_ job: TranscriptionJob) {
        guard job.isPending else { return }
        setReleased(job, false)
        updateHold()
        evaluate()
    }

    /// Manual timing, "Move to Trash…" on a held row, after the user
    /// confirmed (PLAN.md 4.11): moves the job's own WAV (and nothing else; a
    /// retry's saved live preview stays) to the Trash, then drops the job:
    /// its language detection is cancelled, its live sink closed, its live
    /// segments file removed, and `queue.json` saved. When the WAV cannot be
    /// moved, the job stays and its row shows `trashError`. A no-op unless
    /// `canTrash(job)`.
    func trash(_ job: TranscriptionJob) {
        guard canTrash(job), jobs.contains(where: { $0 === job }) else { return }
        let url = job.recording.url
        // A retry's WAV is in the output folder, reached through its bookmark.
        let folder = job.recording.inSpool ? nil : try? TranscriptOutput.resolveFolder(settings: settings)
        defer { folder?.stopAccessing() }
        var landed: NSURL?
        do {
            try FileManager.default.trashItem(at: url, resultingItemURL: &landed)
        } catch {
            job.trashError = String(
                localized: "Could not move the recording to the Trash: \(error.localizedDescription)",
                comment: "Record tab queue row error after Move to Trash… on a recording that waits. %@ is the system reason.")
            Self.logger.error("could not trash \(job.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return
        }
        job.trashError = nil
        job.settleTask?.cancel()
        job.settleTask = nil
        if let sink = job.liveSink {
            sink.onResult = nil
            sink.close()
            job.liveSink = nil
        }
        job.samples = nil
        job.checkpoint = nil
        job.onChange = nil
        job.liveSegmentsDirty = false
        jobs.removeAll { $0 === job }
        store.removeLiveSegments(jobID: job.id)
        if notesRequest?.jobID == job.id { notesRequest = nil }
        eventObserver?(.trashed(id: job.id, recording: url, trashedAs: landed as URL?))
        Self.logger.info("trashed \(job.id, privacy: .public) \(url.lastPathComponent, privacy: .public)")
        updateHold()
        persist()
        evaluate()
    }

    /// Removes a done or failed job from the queue.
    func dismiss(_ job: TranscriptionJob) {
        guard job.canDismiss else { return }
        jobs.removeAll { $0 === job }
        store.removeLiveSegments(jobID: job.id)
        persist()
    }

    /// A plain Start (not Stop & Start Next) clears the finished card as it
    /// always did: done jobs whose notes flow already opened are dismissed.
    func dismissFinishedForNewSession() {
        let finished = jobs.filter { $0.state == .done && !$0.offersNotes && !$0.isRerunning }
        guard !finished.isEmpty else { return }
        for job in finished { dismiss(job) }
    }

    /// "Generate Notes…" on a row: the Record tab starts the flow itself.
    func notesStarted(for job: TranscriptionJob) {
        job.offersNotes = false
    }

    /// The notes flow renamed the files of `srt` (PLAN.md 4.3 step 7).
    func filesRenamed(from srt: URL, to files: [URL]) {
        guard let job = jobs.first(where: { $0.srt == srt }) else { return }
        if let newSRT = files.first(where: { $0.pathExtension.lowercased() == "srt" }) { job.srt = newSRT }
        if let newWAV = files.first(where: { $0.pathExtension.lowercased() == "wav" }) {
            job.wav = newWAV
            if !job.recording.inSpool { job.recording = .init(url: newWAV, inSpool: false) }
        }
    }

    func dismissLanguageNotice(_ job: TranscriptionJob) {
        job.languageNotice = nil
        if !job.isRerunning { job.rerunSamples = nil }
    }

    /// A language notice button. Before the pass starts, the pass uses
    /// `language`; after it finished, one full pass runs again in it
    /// (foreground work, like today) and rewrites the same SRT. Never
    /// changes `languageChoice` or `preferredLanguage`.
    func transcribeAgain(_ job: TranscriptionJob, in language: TranscriptLanguage) {
        guard job.canChangeLanguage else { return }
        job.tracker.choose(language)
        job.languageNotice = nil
        job.rerunError = nil
        languageChanged(job)
        guard job.state == .done, let srt = job.srt else { return }
        let samples = job.rerunSamples
        let wav = job.wav
        job.isRerunning = true
        job.rerunProgress = 0
        job.rerunTask = Task { [weak self] in
            await self?.runRerun(job, srt: srt, wav: wav, samples: samples, language: language)
            job.rerunTask = nil
        }
    }

    private func runRerun(
        _ job: TranscriptionJob, srt: URL, wav: URL?, samples held: [Float]?, language: TranscriptLanguage
    ) async {
        defer { job.isRerunning = false }
        let location: WhisperModelLocation
        do {
            location = try modelLocation(modelStore)
        } catch {
            job.rerunError = String(localized: "Could not transcribe again: \(RecordingController.describe(error))",
                                    comment: "Error. %@ is the reason.")
            return
        }
        let folder = try? TranscriptOutput.resolveFolder(settings: settings)
        defer { folder?.stopAccessing() }
        do {
            let samples: [Float]
            if let held {
                samples = held
            } else if let wav {
                samples = try await Task.detached(priority: .userInitiated) {
                    try AudioFileLoader.loadMono16k(url: wav)
                }.value
            } else {
                job.rerunError = String(localized: "Could not transcribe again: the recording was not kept.",
                                        comment: "Error")
                return
            }
            let result = try await engine.transcribe(
                samples: samples, location: location,
                options: TranscriptionOptions.app(language: language),
                progress: { [weak job] value in
                    Task { @MainActor in job?.rerunProgress = min(max(value, 0), 1) }
                }
            )
            guard FileManager.default.fileExists(atPath: srt.path) else {
                job.rerunError = String(localized: "Could not transcribe again: \(srt.lastPathComponent) was moved or renamed.",
                                        comment: "Error. %@ is a file name.")
                return
            }
            try TranscriptOutput.writeSRT(result.cues(script: job.chineseScript), to: srt)
            job.rerunSamples = nil
            offerNotes(job, srt: srt)
        } catch {
            if !error.isTranscriptionCancelled {
                job.rerunError = String(localized: "Could not transcribe again: \(RecordingController.describe(error))",
                                        comment: "Error. %@ is the reason.")
            }
        }
    }

    func openTranscript(_ job: TranscriptionJob) {
        guard let srt = job.srt else { return }
        NSWorkspace.shared.open(srt)
    }

    func revealInFinder(_ job: TranscriptionJob) {
        let files = job.files.filter { FileManager.default.fileExists(atPath: $0.path) }
        guard !files.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(files)
    }

    // MARK: - Quit

    /// Something must be stopped before quitting: a step, a live-preview
    /// save, or a re-run.
    var hasWorkInFlight: Bool {
        jobs.contains {
            $0.stepTask != nil || $0.settleTask != nil || $0.finishTask != nil || $0.rerunTask != nil
        }
    }

    /// Quit: stops every step and re-run (the decoder stops before its next
    /// window), then saves the queue and the live segments. The spool WAVs
    /// stay; the next launch continues with them (PLAN.md 4.9).
    func prepareForQuit() async {
        isQuitting = true
        pollTask?.cancel()
        pollTask = nil
        for job in jobs {
            job.stepTask?.cancel()
            job.settleTask?.cancel()
            job.finishTask?.cancel()
            if let rerun = job.rerunTask {
                rerun.cancel()
                job.rerunCancelledByQuit = true
            }
        }
        for job in jobs {
            await job.stepTask?.value
            await job.rerunTask?.value
        }
        flushLiveSegments()
        persist()
    }

    /// The quit did not happen after all (a restart failed): jobs stopped
    /// by `prepareForQuit` wait in line again.
    func quitCancelled() {
        guard isQuitting else { return }
        isQuitting = false
        for job in jobs where job.isPending && job.isUsingLivePreview && job.finishTask == nil {
            // Its live-preview save was stopped by the quit; offer it again.
            job.isUsingLivePreview = false
        }
        for job in jobs where job.state == .running && job.stepTask == nil {
            job.state = job.checkpoint == nil ? .waiting : .suspended
        }
        // A "Transcribe again" pass stopped by the quit is not restarted;
        // the previous SRT stays, and the row says why.
        for job in jobs where job.rerunCancelledByQuit {
            job.rerunCancelledByQuit = false
            job.rerunError = String(localized: "Transcription was cancelled because Hearsay quit.",
                                    comment: "Recording error")
        }
        persist()
        evaluate()
    }

    // MARK: - Persistence

    private func persist() {
        guard drives else { return }
        // Only spool WAVs can be named in queue.json; a retry of a WAV in the
        // output folder is not continued after a relaunch.
        let entries = jobs.filter(\.recording.inSpool).map { job in
            TranscriptionQueueManifest.Job(
                id: job.id,
                wavFileName: job.recording.url.lastPathComponent,
                stopTime: job.stopTime,
                languageChoice: job.tracker.choice,
                settledLanguage: job.tracker.isSettled ? job.tracker.language : nil,
                chineseScript: job.chineseScript,
                keepRecording: job.keepRecording,
                state: job.state,
                displayName: job.displayName
            )
        }
        do {
            if entries.isEmpty {
                try? FileManager.default.removeItem(at: store.manifestURL)
            } else {
                try store.save(TranscriptionQueueManifest(jobs: entries))
            }
        } catch {
            Self.logger.error("could not save queue.json: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func markLiveSegmentsChanged(_ job: TranscriptionJob) {
        guard drives, job.recording.inSpool else { return }
        job.liveSegmentsDirty = true
        guard liveWriteTask == nil else { return }
        liveWriteTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1))
            guard let self else { return }
            self.liveWriteTask = nil
            self.flushLiveSegments()
        }
    }

    private func flushLiveSegments() {
        for job in jobs where job.liveSegmentsDirty {
            job.liveSegmentsDirty = false
            guard job.isPending else { continue }
            do {
                try store.writeLiveSegments(job.liveSegments, jobID: job.id)
            } catch {
                Self.logger.error("could not save live segments of \(job.id, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }
    }
}
