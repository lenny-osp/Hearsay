import Foundation
import Testing
@testable import HearsayCore

// MARK: - shared/transcription-queue-tests.json

private struct QueueVectors: Decodable {
    struct Job: Decodable {
        let id: String
        let state: String
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

    let about: String
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
            ?? QueueVectors(about: "", next: [], shouldYield: [], presentsNotes: [],
                            quitNeedsConfirmation: [], blocksUpdateInstall: [])
    }
}

private func policyJobs(_ jobs: [QueueVectors.Job]) throws -> [TranscriptionQueuePolicy.Job] {
    try jobs.map { job in
        let state = try #require(TranscriptionJobState(rawValue: job.state), "unknown state \(job.state)")
        return TranscriptionQueuePolicy.Job(id: job.id, state: state)
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
    }

    @Test func everyRawValueInTheVectorsIsKnown() {
        let vectors = QueueVectors.load()
        for job in vectors.allJobs {
            #expect(TranscriptionJobState(rawValue: job.state) != nil, "state \(job.state)")
        }
        for raw in vectors.allTimings {
            #expect(FinalPassTiming(rawValue: raw) != nil, "timing \(raw)")
        }
        for vector in vectors.next {
            #expect(["start", "resume", "suspend", "none"].contains(vector.expect.action), "\(vector.note)")
        }
        // And every known value is exercised by the vectors.
        #expect(Set(vectors.allJobs.map(\.state)) == Set(TranscriptionJobState.allCases.map(\.rawValue)))
        #expect(Set(vectors.allTimings) == Set(FinalPassTiming.allCases.map(\.rawValue)))
    }

    @Test(arguments: QueueVectors.load().next)
    fileprivate func next(_ vector: QueueVectors.NextCase) throws {
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
        #expect(FinalPassTiming.allCases.map(\.rawValue) == ["immediate", "whenIdle"])
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
}
