import Foundation
import Testing
@testable import HearsayCore

// MARK: - shared/transcription-queue-tests.json

private struct QueueVectors: Decodable {
    struct Job: Decodable {
        let id: String
        let state: String
        let released: Bool?
    }

    struct NextExpect: Decodable {
        let action: String
        let job: String?
    }

    struct NextCase: Decodable, CustomTestStringConvertible {
        let note: String
        let timing: String
        let sessionActive: Bool
        let foregroundWaiting: Int
        let jobs: [Job]
        let expect: NextExpect
        var testDescription: String { note }
    }

    struct YieldCase: Decodable, CustomTestStringConvertible {
        let timing: String
        let sessionActive: Bool
        let foregroundWaiting: Int
        let expect: Bool
        var testDescription: String { "\(timing) session=\(sessionActive) foreground=\(foregroundWaiting)" }
    }

    struct NotesCase: Decodable, CustomTestStringConvertible {
        let note: String
        let sessionActive: Bool
        let notesOnScreen: Bool
        let expect: Bool
        var testDescription: String { note }
    }

    struct CountCase: Decodable, CustomTestStringConvertible {
        let note: String
        let jobs: [Job]
        let expect: Int
        var testDescription: String { note }
    }

    struct BlockCase: Decodable, CustomTestStringConvertible {
        let note: String
        let jobs: [Job]
        let expect: Bool
        var testDescription: String { note }
    }

    struct ManualYieldCase: Decodable, CustomTestStringConvertible {
        let timing: String
        let sessionActive: Bool
        let foregroundWaiting: Int
        let released: Bool?
        let expect: Bool
        var testDescription: String {
            "\(timing) session=\(sessionActive) foreground=\(foregroundWaiting) released=\(released.map(String.init) ?? "absent")"
        }
    }

    struct TimedBlockCase: Decodable, CustomTestStringConvertible {
        let note: String
        let timing: String
        let jobs: [Job]
        let expect: Bool
        var testDescription: String { note }
    }

    /// The top-level `manual` section (PLAN.md 4.11).
    struct Manual: Decodable {
        let about: String
        let next: [NextCase]
        let shouldYield: [ManualYieldCase]
        let blocksUpdateInstall: [TimedBlockCase]
        let allPendingHeld: [TimedBlockCase]

        var allJobs: [Job] {
            next.flatMap(\.jobs) + blocksUpdateInstall.flatMap(\.jobs) + allPendingHeld.flatMap(\.jobs)
        }

        var allTimings: [String] {
            next.map(\.timing) + shouldYield.map(\.timing) + blocksUpdateInstall.map(\.timing)
                + allPendingHeld.map(\.timing)
        }
    }

    let about: String
    let manual: Manual
    let next: [NextCase]
    let shouldYield: [YieldCase]
    let presentsNotes: [NotesCase]
    let quitNeedsConfirmation: [CountCase]
    let blocksUpdateInstall: [BlockCase]

    /// Every job list in the file, for the raw-value check.
    var allJobs: [Job] {
        next.flatMap(\.jobs) + quitNeedsConfirmation.flatMap(\.jobs) + blocksUpdateInstall.flatMap(\.jobs)
    }

    var allTimings: [String] {
        next.map(\.timing) + shouldYield.map(\.timing)
    }

    static func load() -> QueueVectors {
        // Loaded for test arguments, so a failure must not throw here: the
        // `vectorsLoad` test reports it.
        (try? JSONDecoder().decode(QueueVectors.self, from: sharedData("transcription-queue-tests.json")))
            ?? QueueVectors(
                about: "",
                manual: Manual(about: "", next: [], shouldYield: [], blocksUpdateInstall: [], allPendingHeld: []),
                next: [], shouldYield: [], presentsNotes: [], quitNeedsConfirmation: [], blocksUpdateInstall: [])
    }
}

private func policyJobs(_ jobs: [QueueVectors.Job]) throws -> [TranscriptionQueuePolicy.Job] {
    try jobs.map { job in
        let state = try #require(TranscriptionJobState(rawValue: job.state), "unknown state \(job.state)")
        return TranscriptionQueuePolicy.Job(id: job.id, state: state, released: job.released ?? false)
    }
}

private func timing(_ raw: String) throws -> FinalPassTiming {
    try #require(FinalPassTiming(rawValue: raw), "unknown timing \(raw)")
}

struct TranscriptionQueuePolicyVectorTests {
    typealias P = TranscriptionQueuePolicy

    @Test func vectorsLoadAndCoverEveryFunction() throws {
        let vectors = try JSONDecoder().decode(
            QueueVectors.self, from: sharedData("transcription-queue-tests.json"))
        #expect(!vectors.about.isEmpty)
        #expect(!vectors.next.isEmpty)
        #expect(vectors.shouldYield.count == 12)
        #expect(vectors.presentsNotes.count == 4)
        #expect(!vectors.quitNeedsConfirmation.isEmpty)
        #expect(!vectors.blocksUpdateInstall.isEmpty)
        let actions = Set(vectors.next.map(\.expect.action))
        #expect(actions == ["start", "resume", "suspend", "none"])
        #expect(vectors.next.contains { $0.note.hasPrefix("invalid") })
        // The manual section (PLAN.md 4.11).
        #expect(vectors.manual.next.count == 33)
        #expect(vectors.manual.shouldYield.count == 12)
        #expect(vectors.manual.blocksUpdateInstall.count == 12)
        #expect(vectors.manual.allPendingHeld.count == 11)
        #expect(Set(vectors.manual.next.map(\.expect.action)) == ["start", "resume", "suspend", "none"])
    }

    @Test func everyRawValueInTheVectorsIsKnown() {
        let vectors = QueueVectors.load()
        let jobs = vectors.allJobs + vectors.manual.allJobs
        let timings = vectors.allTimings + vectors.manual.allTimings
        for job in jobs {
            #expect(TranscriptionJobState(rawValue: job.state) != nil, "state \(job.state)")
        }
        for raw in timings {
            #expect(FinalPassTiming(rawValue: raw) != nil, "timing \(raw)")
        }
        for vector in vectors.next + vectors.manual.next {
            #expect(["start", "resume", "suspend", "none"].contains(vector.expect.action), "\(vector.note)")
        }
        // And every known value is exercised by the vectors (both sections).
        #expect(Set(jobs.map(\.state)) == Set(TranscriptionJobState.allCases.map(\.rawValue)))
        #expect(Set(timings) == Set(FinalPassTiming.allCases.map(\.rawValue)))
    }

    @Test(arguments: QueueVectors.load().next)
    fileprivate func next(_ vector: QueueVectors.NextCase) throws {
        try checkNext(vector)
    }

    @Test(arguments: QueueVectors.load().manual.next)
    fileprivate func manualNext(_ vector: QueueVectors.NextCase) throws {
        try checkNext(vector)
    }

    private func checkNext(_ vector: QueueVectors.NextCase) throws {
        let expected: P.Decision
        switch vector.expect.action {
        case "start": expected = .start(try #require(vector.expect.job))
        case "resume": expected = .resume(try #require(vector.expect.job))
        case "suspend": expected = .suspend(try #require(vector.expect.job))
        case "none":
            #expect(vector.expect.job == nil)
            expected = .none
        default:
            Issue.record("unknown action \(vector.expect.action)")
            return
        }
        let decision = P.next(
            timing: try timing(vector.timing),
            sessionActive: vector.sessionActive,
            foregroundWaiting: vector.foregroundWaiting,
            jobs: try policyJobs(vector.jobs)
        )
        #expect(decision == expected)
    }

    @Test(arguments: QueueVectors.load().shouldYield)
    fileprivate func shouldYield(_ vector: QueueVectors.YieldCase) throws {
        let timing = try timing(vector.timing)
        let yields = P.shouldYield(
            timing: timing, sessionActive: vector.sessionActive, foregroundWaiting: vector.foregroundWaiting)
        #expect(yields == vector.expect)
        // True exactly when `next` suspends a running job.
        let decision = P.next(
            timing: timing, sessionActive: vector.sessionActive, foregroundWaiting: vector.foregroundWaiting,
            jobs: [.init(id: "r", state: .running), .init(id: "w", state: .waiting)])
        #expect((decision == .suspend("r")) == yields)
    }

    @Test(arguments: QueueVectors.load().manual.shouldYield)
    fileprivate func manualShouldYield(_ vector: QueueVectors.ManualYieldCase) throws {
        let timing = try timing(vector.timing)
        let released = vector.released ?? false
        let yields = P.shouldYield(
            timing: timing, sessionActive: vector.sessionActive, foregroundWaiting: vector.foregroundWaiting,
            runningReleased: released)
        #expect(yields == vector.expect)
        // True exactly when `next` suspends a running job with that flag.
        let decision = P.next(
            timing: timing, sessionActive: vector.sessionActive, foregroundWaiting: vector.foregroundWaiting,
            jobs: [.init(id: "r", state: .running, released: released), .init(id: "w", state: .waiting, released: true)])
        #expect((decision == .suspend("r")) == yields)
    }

    @Test(arguments: QueueVectors.load().manual.blocksUpdateInstall)
    fileprivate func manualBlocksUpdateInstall(_ vector: QueueVectors.TimedBlockCase) throws {
        #expect(P.blocksUpdateInstall(timing: try timing(vector.timing), jobs: try policyJobs(vector.jobs))
            == vector.expect)
    }

    @Test(arguments: QueueVectors.load().manual.allPendingHeld)
    fileprivate func allPendingHeld(_ vector: QueueVectors.TimedBlockCase) throws {
        #expect(P.allPendingHeld(timing: try timing(vector.timing), jobs: try policyJobs(vector.jobs))
            == vector.expect)
    }

    @Test(arguments: QueueVectors.load().presentsNotes)
    fileprivate func presentsNotes(_ vector: QueueVectors.NotesCase) {
        #expect(P.presentsNotes(sessionActive: vector.sessionActive, notesOnScreen: vector.notesOnScreen)
            == vector.expect)
    }

    @Test(arguments: QueueVectors.load().quitNeedsConfirmation)
    fileprivate func quitNeedsConfirmation(_ vector: QueueVectors.CountCase) throws {
        #expect(P.quitNeedsConfirmation(jobs: try policyJobs(vector.jobs)) == vector.expect)
    }

    @Test(arguments: QueueVectors.load().blocksUpdateInstall)
    fileprivate func blocksUpdateInstall(_ vector: QueueVectors.BlockCase) throws {
        #expect(P.blocksUpdateInstall(jobs: try policyJobs(vector.jobs)) == vector.expect)
    }
}

struct TranscriptionQueuePolicyTests {
    @Test func rawValuesAreTheSharedStrings() {
        #expect(FinalPassTiming.allCases.map(\.rawValue) == ["immediate", "whenIdle", "manual"])
        #expect(TranscriptionJobState.allCases.map(\.rawValue)
            == ["waiting", "running", "suspended", "done", "failed"])
        #expect(TranscriptionJobState.allCases.filter(\.isPending) == [.waiting, .running, .suspended])
    }

    @Test func timingDisplayNames() {
        // Localized, so only shape is checked (the test runner's language varies).
        let names = FinalPassTiming.allCases.map(\.displayName)
        #expect(names.allSatisfy { !$0.isEmpty })
        #expect(Set(names).count == names.count)
    }

    @Test func isHeldOnlyUnderManualForPendingUnreleasedJobs() {
        typealias P = TranscriptionQueuePolicy
        for state in TranscriptionJobState.allCases {
            for released in [false, true] {
                let job = P.Job(id: "j", state: state, released: released)
                for timing in FinalPassTiming.allCases {
                    let expected = timing == .manual && state.isPending && !released
                    #expect(P.isHeld(timing: timing, job: job) == expected, "\(timing) \(state) \(released)")
                }
            }
        }
    }

    @Test func timingChangeRule() {
        typealias P = TranscriptionQueuePolicy
        // Switch to manual: started jobs are released, waiting ones held.
        for previous in [FinalPassTiming.immediate, .whenIdle] {
            #expect(P.releasedAfterTimingChange(from: previous, to: .manual, state: .running) == true)
            #expect(P.releasedAfterTimingChange(from: previous, to: .manual, state: .suspended) == true)
            #expect(P.releasedAfterTimingChange(from: previous, to: .manual, state: .waiting) == false)
            #expect(P.releasedAfterTimingChange(from: previous, to: .manual, state: .done) == nil)
            #expect(P.releasedAfterTimingChange(from: previous, to: .manual, state: .failed) == nil)
        }
        // Any other switch leaves the flags alone.
        for state in TranscriptionJobState.allCases {
            #expect(P.releasedAfterTimingChange(from: .manual, to: .manual, state: state) == nil)
            #expect(P.releasedAfterTimingChange(from: .manual, to: .immediate, state: state) == nil)
            #expect(P.releasedAfterTimingChange(from: .manual, to: .whenIdle, state: state) == nil)
            #expect(P.releasedAfterTimingChange(from: .immediate, to: .whenIdle, state: state) == nil)
            #expect(P.releasedAfterTimingChange(from: .whenIdle, to: .immediate, state: state) == nil)
        }
    }

    @Test func unknownStoredTimingReadsAsNil() {
        #expect(FinalPassTiming(rawValue: "someFutureValue") == nil)
        #expect(FinalPassTiming(rawValue: "manual") == .manual)
    }
}
