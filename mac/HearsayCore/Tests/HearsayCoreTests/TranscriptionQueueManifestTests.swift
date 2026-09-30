import Foundation
import Testing
@testable import HearsayCore

final class TranscriptionQueueManifestTests {
    private let root: URL
    private let store: TranscriptionQueueStore

    init() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("HearsayQueueTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        store = TranscriptionQueueStore(root: root)
    }

    deinit {
        try? FileManager.default.removeItem(at: root)
    }

    private func touchWAV(_ name: String) throws {
        try Data("RIFF".utf8).write(to: root.appendingPathComponent(name))
    }

    private func job(
        _ id: String, state: TranscriptionJobState = .waiting, wav: String? = nil
    ) -> TranscriptionQueueManifest.Job {
        TranscriptionQueueManifest.Job(
            id: id, wavFileName: wav ?? id + ".wav",
            stopTime: Date(timeIntervalSince1970: 1_790_750_000),  // whole seconds: ISO 8601 keeps seconds
            languageChoice: .auto, keepRecording: true, state: state)
    }

    private func writeManifest(_ text: String) throws {
        try Data(text.utf8).write(to: store.manifestURL)
    }

    @Test func missingFileIsAnEmptyQueueWithoutError() {
        let result = store.load()
        #expect(result.manifest == TranscriptionQueueManifest())
        #expect(result.manifest.version == 1)
        #expect(result.error == nil)
        #expect(result.dropped.isEmpty)
        #expect(store.queuedWAVFileNames().isEmpty)
    }

    @Test func roundTripsEveryField() throws {
        let first = TranscriptionQueueManifest.Job(
            id: "2026-09-30_10-00-00", wavFileName: "2026-09-30_10-00-00.wav",
            stopTime: Date(timeIntervalSince1970: 1_790_750_000),
            languageChoice: .fixed(.chineseTaiwan), settledLanguage: .chineseTaiwan,
            chineseScript: .traditional, keepRecording: false, state: .waiting, displayName: "Weekly sync")
        let second = TranscriptionQueueManifest.Job(
            id: "2026-09-30_11-00-00", wavFileName: "2026-09-30_11-00-00.wav",
            stopTime: Date(timeIntervalSince1970: 1_790_753_600),
            languageChoice: .auto, keepRecording: true, state: .done)
        for item in [first, second] { try touchWAV(item.wavFileName) }
        let manifest = TranscriptionQueueManifest(jobs: [first, second])
        try store.save(manifest)

        let result = store.load()
        #expect(result.error == nil)
        #expect(result.dropped.isEmpty)
        #expect(result.manifest == manifest)
        #expect(result.manifest.policyJobs == [
            .init(id: first.id, state: .waiting), .init(id: second.id, state: .done),
        ])
        #expect(store.queuedWAVFileNames() == ["2026-09-30_10-00-00.wav"])

        // The shared field names and values (Windows reads the same layout).
        let object = try #require(
            try JSONSerialization.jsonObject(with: Data(contentsOf: store.manifestURL)) as? [String: Any])
        #expect(object["version"] as? Int == 1)
        let jobs = try #require(object["jobs"] as? [[String: Any]])
        #expect(Set(jobs[1].keys) == [
            "id", "wavFileName", "stopTime", "languageChoice", "settledLanguage", "chineseScript",
            "keepRecording", "state", "displayName",
        ])
        #expect(jobs[0]["languageChoice"] as? String == "zh-TW")
        #expect(jobs[0]["settledLanguage"] as? String == "zh-TW")
        #expect(jobs[0]["chineseScript"] as? String == "traditional")
        #expect(jobs[0]["state"] as? String == "waiting")
        #expect(jobs[0]["stopTime"] as? String == "2026-09-30T06:33:20Z")
        #expect(jobs[1]["languageChoice"] as? String == "auto")
        #expect(jobs[1]["settledLanguage"] is NSNull)
        #expect(jobs[1]["displayName"] is NSNull)
    }

    @Test func runningAndSuspendedJobsStartOverAsWaiting() throws {
        let jobs = [job("a", state: .running), job("b", state: .suspended), job("c", state: .failed)]
        for item in jobs { try touchWAV(item.wavFileName) }
        try store.save(TranscriptionQueueManifest(jobs: jobs))
        #expect(store.load().manifest.policyJobs == [
            .init(id: "a", state: .waiting), .init(id: "b", state: .waiting), .init(id: "c", state: .failed),
        ])
    }

    @Test func saveReplacesAtomicallyAndLeavesNoTemporaryFiles() throws {
        try touchWAV("a.wav")
        try touchWAV("b.wav")
        try store.save(TranscriptionQueueManifest(jobs: [job("a"), job("b")]))
        try store.save(TranscriptionQueueManifest(jobs: [job("b", state: .running)]))
        #expect(store.load().manifest.jobs.map(\.id) == ["b"])
        let names = try FileManager.default.contentsOfDirectory(atPath: root.path)
        #expect(Set(names) == ["a.wav", "b.wav", "queue.json"])
    }

    @Test func saveCreatesTheFolder() throws {
        let nested = TranscriptionQueueStore(root: root.appendingPathComponent("spool", isDirectory: true))
        try nested.save(TranscriptionQueueManifest())
        #expect(FileManager.default.fileExists(atPath: nested.manifestURL.path))
        #expect(nested.load().error == nil)
    }

    @Test func saveRefusesUnsafeJobIDs() {
        #expect(throws: TranscriptionQueueStore.WriteError.invalidJobID("../x")) {
            try self.store.save(TranscriptionQueueManifest(jobs: [self.job("../x", wav: "x.wav")]))
        }
        #expect(!FileManager.default.fileExists(atPath: store.manifestURL.path))
    }

    @Test func corruptFileIsAnEmptyQueueWithAnError() throws {
        try touchWAV("a.wav")
        try writeManifest("{\"version\": 1, \"jobs\": [")
        let result = store.load()
        #expect(result.manifest.jobs.isEmpty)
        guard case .unreadable = result.error else {
            Issue.record("expected unreadable, got \(String(describing: result.error))")
            return
        }
        #expect(store.queuedWAVFileNames().isEmpty)

        try writeManifest("[]")
        guard case .unreadable = store.load().error else {
            Issue.record("a JSON array is not a manifest")
            return
        }
    }

    @Test func unsupportedVersionIsAnEmptyQueueWithAnError() throws {
        try touchWAV("a.wav")
        try writeManifest("""
        {"version": 2, "jobs": [{"id": "a", "wavFileName": "a.wav", "stopTime": "2026-09-30T06:33:20Z",
          "languageChoice": "auto", "settledLanguage": null, "chineseScript": null,
          "keepRecording": true, "state": "waiting", "displayName": null}]}
        """)
        let result = store.load()
        #expect(result.manifest.jobs.isEmpty)
        #expect(result.error == .unsupportedVersion(2))
        #expect(store.queuedWAVFileNames().isEmpty)
    }

    @Test func jobsWhoseWAVIsMissingAreDroppedAndReported() throws {
        try touchWAV("a.wav")
        try touchWAV("c.wav")
        try store.save(TranscriptionQueueManifest(jobs: [job("a"), job("b"), job("c")]))
        let result = store.load()
        #expect(result.error == nil)
        #expect(result.manifest.jobs.map(\.id) == ["a", "c"])
        #expect(result.dropped == [.init(id: "b", wavFileName: "b.wav", reason: .missingRecording)])
    }

    @Test func invalidEntriesAreDroppedOneByOne() throws {
        for name in ["a.wav", "b.wav", "c.wav", "d.wav"] { try touchWAV(name) }
        func entry(_ id: String, wav: String, state: String = "waiting", language: String = "auto") -> String {
            """
            {"id": "\(id)", "wavFileName": "\(wav)", "stopTime": "2026-09-30T06:33:20Z",
             "languageChoice": "\(language)", "settledLanguage": null, "chineseScript": null,
             "keepRecording": true, "state": "\(state)", "displayName": null}
            """
        }
        try writeManifest("""
        {"version": 1, "jobs": [
          \(entry("a", wav: "a.wav")),
          \(entry("b", wav: "b.wav", state: "paused")),
          \(entry("c", wav: "c.wav", language: "fr")),
          \(entry("d", wav: "../d.wav")),
          \(entry("a", wav: "d.wav")),
          {"id": 7},
          \(entry("d", wav: "d.wav", state: "suspended"))
        ]}
        """)
        let result = store.load()
        #expect(result.error == nil)
        #expect(result.manifest.policyJobs == [.init(id: "a", state: .waiting), .init(id: "d", state: .waiting)])
        #expect(result.dropped == [
            .init(id: "b", wavFileName: "b.wav", reason: .invalidEntry),
            .init(id: "c", wavFileName: "c.wav", reason: .invalidEntry),
            .init(id: "d", wavFileName: "../d.wav", reason: .invalidEntry),
            .init(id: "a", wavFileName: "d.wav", reason: .invalidEntry),
            .init(id: nil, wavFileName: nil, reason: .invalidEntry),
        ])
    }

    @Test func liveSegmentsRoundTripThroughSRT() throws {
        #expect(store.readLiveSegments(jobID: "a").isEmpty)
        let segments = [
            TranscriptSegment(start: 0, end: 2.5, text: "Hello there."),
            TranscriptSegment(start: 2.5, end: 61.25, text: "第二行"),
        ]
        try store.writeLiveSegments(segments, jobID: "a")
        #expect(store.liveSegmentsURL(jobID: "a").lastPathComponent == "a.live.srt")
        #expect(try String(contentsOf: store.liveSegmentsURL(jobID: "a"), encoding: .utf8) == SRT.render(segments))
        #expect(store.readLiveSegments(jobID: "a") == segments)

        // Replaced, not appended.
        try store.writeLiveSegments(Array(segments.prefix(1)), jobID: "a")
        #expect(store.readLiveSegments(jobID: "a") == Array(segments.prefix(1)))
        let names = try FileManager.default.contentsOfDirectory(atPath: root.path)
        #expect(names == ["a.live.srt"])

        store.removeLiveSegments(jobID: "a")
        store.removeLiveSegments(jobID: "a")
        #expect(store.readLiveSegments(jobID: "a").isEmpty)
        #expect(throws: TranscriptionQueueStore.WriteError.invalidJobID("x/y")) {
            try self.store.writeLiveSegments(segments, jobID: "x/y")
        }
    }

    @Test func plainFileNames() {
        typealias M = TranscriptionQueueManifest
        #expect(M.isPlainFileName("2026-09-30_10-00-00"))
        #expect(M.isPlainFileName("2026-09-30_10-00-00-2.wav"))
        for bad in ["", ".", "..", ".hidden", "a/b", "../a", "a\0b"] {
            #expect(!M.isPlainFileName(bad), "\(bad)")
        }
    }
}
