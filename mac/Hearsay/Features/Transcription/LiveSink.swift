import HearsayCore
import Observation

/// Where one recording session's live chunks go (PLAN.md 4.1 step 4, 4.9).
/// `RecordingController` creates one per session; its live task waits here
/// for the session language and hands every result to `onResult`. When the
/// session ends normally the sink moves to the session's queue job with the
/// live task still running, so the tail of the live preview keeps being
/// transcribed (as foreground work) and lands in the job, while the next
/// session gets a fresh sink.
@MainActor
@Observable
final class LiveSink {
    /// The language live chunks are transcribed in; nil while Auto is
    /// undecided (chunks wait).
    private(set) var language: TranscriptLanguage?
    /// The script the cues are converted to (zh only).
    private(set) var script: ChineseScript?
    /// Live chunks queued or being transcribed.
    private(set) var waiting = 0
    /// Closed: chunks still queued are dropped without being transcribed.
    private(set) var isClosed = false

    /// Receives each chunk's cues (already offset and converted) or error.
    @ObservationIgnored var onResult: (@MainActor (_ index: Int, _ outcome: Result<[CoreSegment], Error>) -> Void)?
    /// The task that transcribes the chunks in order; it ends once the
    /// session's chunk stream is finished and drained.
    @ObservationIgnored var task: Task<Void, Never>?
    @ObservationIgnored private var waiters: [CheckedContinuation<Void, Never>] = []

    init(language: TranscriptLanguage?) {
        setLanguage(language)
    }

    /// Chunks still to come: queued, being transcribed, or waiting for the
    /// language.
    var hasPendingChunks: Bool { waiting > 0 }

    func setLanguage(_ language: TranscriptLanguage?) {
        self.language = language
        if let language { script = language.chineseScript }
        guard language != nil else { return }
        resumeWaiters()
    }

    /// Drops the chunks still queued (a chunk already in the engine
    /// finishes, and its result is still delivered).
    func close() {
        isClosed = true
        resumeWaiters()
    }

    func chunkQueued() {
        waiting += 1
    }

    /// The language once it is known; nil when the sink was closed first.
    func waitForLanguage() async -> TranscriptLanguage? {
        while !isClosed, language == nil {
            await withCheckedContinuation { waiters.append($0) }
        }
        return isClosed ? nil : language
    }

    /// A chunk was skipped (closed) or transcribed.
    func deliver(index: Int, outcome: Result<[CoreSegment], Error>?) {
        waiting = max(0, waiting - 1)
        if let outcome { onResult?(index, outcome) }
    }

    /// Waits until every chunk of the session is done (or dropped).
    func drain() async {
        await task?.value
    }

    private func resumeWaiters() {
        let pending = waiters
        waiters = []
        for waiter in pending { waiter.resume() }
    }
}
