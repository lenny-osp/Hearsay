import Foundation

/// The persisted background transcription queue, `<spool>/queue.json`
/// (PLAN.md 4.9 "Persistence and recovery"; Windows keeps the same fields,
/// 18.10). Jobs are in queue order.
///
/// JSON layout (`version` 1): `{"version": 1, "jobs": [{"id", "wavFileName",
/// "stopTime" (ISO 8601, UTC), "languageChoice" ("auto" or a transcript
/// language), "settledLanguage" (a transcript language or null),
/// "chineseScript" ("traditional", "simplified", or null), "keepRecording",
/// "state" (a `TranscriptionJobState` raw value), "displayName" (or null)}]}`.
public struct TranscriptionQueueManifest: Codable, Equatable, Sendable {
    public static let fileName = "queue.json"
    public static let currentVersion = 1

    /// One queued recording.
    public struct Job: Codable, Equatable, Sendable, Identifiable {
        /// Plain file-name-safe string; also names `<id>.live.srt`.
        public var id: String
        /// The spool WAV's file name (no folder), for example
        /// `2026-09-30_10-00-00.wav`.
        public var wavFileName: String
        /// When the recording was stopped.
        public var stopTime: Date
        /// The language choice the session was recorded with.
        public var languageChoice: LanguageChoice
        /// The language the session settled on (Auto detection or the fixed
        /// choice), or nil when not settled yet.
        public var settledLanguage: TranscriptLanguage?
        /// The Chinese script for the transcript, or nil.
        public var chineseScript: ChineseScript?
        /// Keep the WAV in the output folder after success.
        public var keepRecording: Bool
        public var state: TranscriptionJobState
        /// The meeting name the user gave the recording, if any.
        public var displayName: String?

        public init(
            id: String,
            wavFileName: String,
            stopTime: Date,
            languageChoice: LanguageChoice,
            settledLanguage: TranscriptLanguage? = nil,
            chineseScript: ChineseScript? = nil,
            keepRecording: Bool,
            state: TranscriptionJobState = .waiting,
            displayName: String? = nil
        ) {
            self.id = id
            self.wavFileName = wavFileName
            self.stopTime = stopTime
            self.languageChoice = languageChoice
            self.settledLanguage = settledLanguage
            self.chineseScript = chineseScript
            self.keepRecording = keepRecording
            self.state = state
            self.displayName = displayName
        }

        // Nil optionals are written as null, so every field is always present.
        public func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            try container.encode(id, forKey: .id)
            try container.encode(wavFileName, forKey: .wavFileName)
            try container.encode(stopTime, forKey: .stopTime)
            try container.encode(languageChoice, forKey: .languageChoice)
            try container.encode(settledLanguage, forKey: .settledLanguage)
            try container.encode(chineseScript, forKey: .chineseScript)
            try container.encode(keepRecording, forKey: .keepRecording)
            try container.encode(state, forKey: .state)
            try container.encode(displayName, forKey: .displayName)
        }
    }

    public var version: Int
    public var jobs: [Job]

    public init(jobs: [Job] = []) {
        self.version = Self.currentVersion
        self.jobs = jobs
    }

    /// The jobs as `TranscriptionQueuePolicy` reads them.
    public var policyJobs: [TranscriptionQueuePolicy.Job] {
        jobs.map { TranscriptionQueuePolicy.Job(id: $0.id, state: $0.state) }
    }

    /// True for a plain file name: not empty, not `.` or `..`, no `/`, no
    /// NUL, and not hidden. Job ids and WAV names must be one.
    public static func isPlainFileName(_ name: String) -> Bool {
        !name.isEmpty && name != "." && name != ".." && !name.hasPrefix(".")
            && !name.contains("/") && !name.contains("\0")
    }
}

/// Reads and writes the queue manifest and the per-job live segments in the
/// spool folder.
public struct TranscriptionQueueStore: Sendable {
    /// Why `load` returned an empty queue although a file was there.
    public enum LoadError: Error, Equatable, Sendable, CustomStringConvertible {
        /// The file could not be read or is not a queue manifest.
        case unreadable(String)
        /// The manifest's `version` is not one this build reads.
        case unsupportedVersion(Int)

        public var description: String {
            switch self {
            case .unreadable(let reason): "queue.json is unreadable: \(reason)"
            case .unsupportedVersion(let version): "queue.json has unsupported version \(version)"
            }
        }
    }

    /// A job `load` left out.
    public struct DroppedJob: Equatable, Sendable {
        public enum Reason: Equatable, Sendable {
            /// Its WAV is no longer in the spool folder.
            case missingRecording
            /// The entry could not be decoded or names an unsafe file.
            case invalidEntry
        }

        /// The job id, when the entry had a readable one.
        public let id: String?
        /// The WAV file name, when the entry had a readable one.
        public let wavFileName: String?
        public let reason: Reason
    }

    public struct LoadResult: Equatable, Sendable {
        /// The jobs to queue again, in order. A job that was running or
        /// suspended is `waiting` again (it starts over, PLAN.md 4.9).
        public var manifest: TranscriptionQueueManifest
        /// Set when the file existed but could not be used; the caller logs it.
        public var error: LoadError?
        /// Jobs left out, for the caller to log.
        public var dropped: [DroppedJob]
    }

    public enum WriteError: Error, Equatable, Sendable {
        case invalidJobID(String)
    }

    public let root: URL

    public init(root: URL) {
        self.root = root
    }

    public init(spool: RecordingSpool) {
        self.root = spool.root
    }

    public var manifestURL: URL {
        root.appendingPathComponent(TranscriptionQueueManifest.fileName)
    }

    /// `<root>/<id>.live.srt`.
    public func liveSegmentsURL(jobID: String) -> URL {
        root.appendingPathComponent(jobID + ".live.srt")
    }

    // MARK: Manifest

    /// Writes the manifest, replacing the old one atomically (a reader sees
    /// the old or the new file, never a partial one). Creates the folder.
    public func save(_ manifest: TranscriptionQueueManifest) throws {
        for job in manifest.jobs where !TranscriptionQueueManifest.isPlainFileName(job.id) {
            throw WriteError.invalidJobID(job.id)
        }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let data = try Self.encoder.encode(manifest)
        try OutputWriter.writeReplacing(String(decoding: data, as: UTF8.self) + "\n", to: manifestURL)
    }

    /// Reads the manifest. A missing file is an empty queue with no error.
    /// An unreadable file or an unknown version is an empty queue plus an
    /// error. Entries that cannot be decoded, name an unsafe file, or whose
    /// WAV is missing are dropped and reported; running and suspended jobs
    /// come back as waiting.
    public func load() -> LoadResult {
        let empty = TranscriptionQueueManifest()
        guard FileManager.default.fileExists(atPath: manifestURL.path) else {
            return LoadResult(manifest: empty, error: nil, dropped: [])
        }
        let data: Data
        let raw: RawManifest
        do {
            data = try Data(contentsOf: manifestURL)
            raw = try Self.decoder.decode(RawManifest.self, from: data)
        } catch {
            return LoadResult(manifest: empty, error: .unreadable(Self.reason(error)), dropped: [])
        }
        guard raw.version == TranscriptionQueueManifest.currentVersion else {
            return LoadResult(manifest: empty, error: .unsupportedVersion(raw.version), dropped: [])
        }
        var jobs: [TranscriptionQueueManifest.Job] = []
        var dropped: [DroppedJob] = []
        var seenIDs = Set<String>()
        for entry in raw.jobs {
            guard var job = entry.job,
                  TranscriptionQueueManifest.isPlainFileName(job.id),
                  TranscriptionQueueManifest.isPlainFileName(job.wavFileName),
                  !seenIDs.contains(job.id)
            else {
                dropped.append(DroppedJob(id: entry.id, wavFileName: entry.wavFileName, reason: .invalidEntry))
                continue
            }
            guard FileManager.default.fileExists(atPath: root.appendingPathComponent(job.wavFileName).path) else {
                dropped.append(DroppedJob(id: job.id, wavFileName: job.wavFileName, reason: .missingRecording))
                continue
            }
            if job.state == .running || job.state == .suspended {
                job.state = .waiting
            }
            seenIDs.insert(job.id)
            jobs.append(job)
        }
        return LoadResult(manifest: TranscriptionQueueManifest(jobs: jobs), error: nil, dropped: dropped)
    }

    /// WAV file names (in the spool folder) of the jobs `load` returns that
    /// are not transcribed yet (waiting, running, or suspended). Crash
    /// recovery (`RecordingSpool.unfinishedRecordings`) does not offer
    /// them: the queue continues with them. Empty when the manifest is
    /// missing or unusable, so those WAVs are offered as before.
    public func queuedWAVFileNames() -> Set<String> {
        Set(load().manifest.jobs.filter(\.state.isPending).map(\.wavFileName))
    }

    // MARK: Live segments

    /// Writes a job's live segments as SRT, replacing the file atomically.
    public func writeLiveSegments(_ segments: [TranscriptSegment], jobID: String) throws {
        guard TranscriptionQueueManifest.isPlainFileName(jobID) else { throw WriteError.invalidJobID(jobID) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try OutputWriter.writeReplacing(SRT.render(segments), to: liveSegmentsURL(jobID: jobID))
    }

    /// A job's live segments; empty when the file is missing or unreadable.
    public func readLiveSegments(jobID: String) -> [TranscriptSegment] {
        guard TranscriptionQueueManifest.isPlainFileName(jobID),
              let data = try? Data(contentsOf: liveSegmentsURL(jobID: jobID))
        else { return [] }
        return SRT.parse(String(decoding: data, as: UTF8.self))
    }

    /// Removes a job's live-segments file; no error when it is missing.
    public func removeLiveSegments(jobID: String) {
        guard TranscriptionQueueManifest.isPlainFileName(jobID) else { return }
        try? FileManager.default.removeItem(at: liveSegmentsURL(jobID: jobID))
    }

    // MARK: Coding

    private static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    private static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }

    /// The file with each job decoded on its own, so one bad entry drops
    /// only itself.
    private struct RawManifest: Decodable {
        let version: Int
        let jobs: [RawJob]

        private enum CodingKeys: String, CodingKey { case version, jobs }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            version = try container.decode(Int.self, forKey: .version)
            jobs = try container.decodeIfPresent([RawJob].self, forKey: .jobs) ?? []
        }
    }

    private struct RawJob: Decodable {
        let job: TranscriptionQueueManifest.Job?
        let id: String?
        let wavFileName: String?

        private enum CodingKeys: String, CodingKey { case id, wavFileName }

        init(from decoder: Decoder) throws {
            job = try? TranscriptionQueueManifest.Job(from: decoder)
            let container = try? decoder.container(keyedBy: CodingKeys.self)
            id = try? container?.decodeIfPresent(String.self, forKey: .id)
            wavFileName = try? container?.decodeIfPresent(String.self, forKey: .wavFileName)
        }
    }

    private static func reason(_ error: Error) -> String {
        if let described = error as? LocalizedError, let text = described.errorDescription {
            return text
        }
        return (error as NSError).localizedDescription
    }
}
