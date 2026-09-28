import Foundation

/// Progress of one model download across all of its files.
public struct DownloadProgress: Sendable, Equatable {
    /// Bytes on disk for this download, including resumed partial files and
    /// files that were already present.
    public var bytesReceived: Int64
    /// Best known total. Grows slightly while small files report their size.
    public var totalBytes: Int64
    /// File currently being fetched, for example `model.safetensors`.
    public var currentFile: String

    public init(bytesReceived: Int64, totalBytes: Int64, currentFile: String) {
        self.bytesReceived = bytesReceived
        self.totalBytes = totalBytes
        self.currentFile = currentFile
    }

    /// 0...1, or 0 while the total is unknown.
    public var fractionCompleted: Double {
        guard totalBytes > 0 else { return 0 }
        return min(1, Double(bytesReceived) / Double(totalBytes))
    }
}

/// Written to `manifest.json` in a model folder after every file arrived.
public struct ModelManifest: Codable, Sendable, Equatable {
    public struct File: Codable, Sendable, Equatable {
        public let name: String
        public let size: Int64

        public init(name: String, size: Int64) {
            self.name = name
            self.size = size
        }
    }

    public let repo: String
    public let files: [File]
    /// `x-repo-commit` from Hugging Face, when the server sent it.
    public let commit: String?
    public let downloadedAt: Date

    public init(repo: String, files: [File], commit: String?, downloadedAt: Date) {
        self.repo = repo
        self.files = files
        self.commit = commit
        self.downloadedAt = downloadedAt
    }

    public static let fileName = "manifest.json"

    static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}

public enum ModelDownloadError: Error, Equatable, LocalizedError {
    case httpStatus(file: String, status: Int)
    case sizeMismatch(file: String, expected: Int64, actual: Int64)
    case rangeNotSatisfiable(file: String)
    case invalidResponse(file: String)

    public var errorDescription: String? {
        switch self {
        case let .httpStatus(file, status):
            String(localized: "Downloading \(file) failed with HTTP status \(status).", bundle: .module,
                   comment: "Model download error. %1$@ is a file name, %2$lld an HTTP status code.")
        case let .sizeMismatch(file, expected, actual):
            String(localized: "\(file) is \(actual) bytes but the server announced \(expected). The file was discarded; try again.",
                   bundle: .module,
                   comment: "Model download error. %1$@ is a file name, %2$lld and %3$lld are byte counts.")
        case let .rangeNotSatisfiable(file):
            String(localized: "The server refused to resume \(file).", bundle: .module,
                   comment: "Model download error. %@ is a file name.")
        case let .invalidResponse(file):
            String(localized: "The server sent an unexpected response for \(file).", bundle: .module,
                   comment: "Model download error. %@ is a file name.")
        }
    }
}

/// Downloads one catalog entry plus the shared tokenizer from Hugging Face
/// (PLAN.md section 5).
///
/// Layout under `modelsRoot`:
/// - `<repo with "/" -> "_">/config.json`, the weights file, `manifest.json`
/// - `_tokenizer/<tokenizer repo with "/" -> "_">/<tokenizer files>`
///
/// Each file is written to `<name>.partial` first and renamed once its size
/// matches what the server announced. A cancelled or failed transfer keeps
/// the partial file so the next attempt resumes with an HTTP `Range` header.
public actor ModelDownloader {
    public static let tokenizerFolderName = "_tokenizer"
    public static let partialSuffix = ".partial"

    public let modelsRoot: URL
    public let tokenizer: TokenizerSource
    private let configuration: URLSessionConfiguration
    private let baseURL: URL
    private let fileManager = FileManager.default

    /// Transfers in flight by destination, so two model downloads that both
    /// need the shared tokenizer never write the same partial file.
    private var inFlight: [URL: Task<Void, Error>] = [:]

    public init(
        modelsRoot: URL,
        tokenizer: TokenizerSource,
        configuration: URLSessionConfiguration = .default,
        baseURL: URL = URL(string: "https://huggingface.co") ?? URL(fileURLWithPath: "/")
    ) {
        self.modelsRoot = modelsRoot
        self.tokenizer = tokenizer
        self.configuration = configuration
        self.baseURL = baseURL
    }

    // MARK: - Layout

    public static func folderName(for repo: String) -> String {
        repo.replacingOccurrences(of: "/", with: "_")
    }

    public static func modelDirectory(for repo: String, in root: URL) -> URL {
        root.appendingPathComponent(folderName(for: repo), isDirectory: true)
    }

    public static func tokenizerDirectory(for tokenizer: TokenizerSource, in root: URL) -> URL {
        root.appendingPathComponent(tokenizerFolderName, isDirectory: true)
            .appendingPathComponent(folderName(for: tokenizer.repo), isDirectory: true)
    }

    func fileURL(repo: String, file: String) -> URL {
        baseURL.appendingPathComponent(repo)
            .appendingPathComponent("resolve")
            .appendingPathComponent("main")
            .appendingPathComponent(file)
    }

    // MARK: - Download

    /// Downloads `entry` and the tokenizer (files already present are
    /// skipped), then writes `manifest.json`. Progress is yielded to
    /// `progress`, which is finished when this returns or throws. Cancel the
    /// calling task to stop; partial files are kept for resume.
    @discardableResult
    public func download(
        _ entry: ModelCatalogEntry,
        progress: AsyncStream<DownloadProgress>.Continuation? = nil
    ) async throws -> ModelManifest {
        defer { progress?.finish() }

        let modelDir = Self.modelDirectory(for: entry.repo, in: modelsRoot)
        let tokenizerDir = Self.tokenizerDirectory(for: tokenizer, in: modelsRoot)
        try fileManager.createDirectory(at: modelDir, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: tokenizerDir, withIntermediateDirectories: true)

        // A stale manifest must not mark a half-replaced folder as installed.
        try? fileManager.removeItem(at: modelDir.appendingPathComponent(ModelManifest.fileName))

        var jobs: [Job] = tokenizer.files.map { name in
            Job(name: name, source: fileURL(repo: tokenizer.repo, file: name),
                destination: tokenizerDir.appendingPathComponent(name), estimate: 0, isModelFile: false)
        }
        jobs += entry.files.map { name in
            Job(name: name, source: fileURL(repo: entry.repo, file: name),
                destination: modelDir.appendingPathComponent(name),
                estimate: name == entry.weightsFile ? entry.sizeBytes : 0, isModelFile: true)
        }

        let tracker = ProgressTracker(files: jobs.map { ($0.name, $0.estimate) }, continuation: progress)
        var commit: String?
        var manifestFiles: [ModelManifest.File] = []

        for (index, job) in jobs.enumerated() {
            try Task.checkCancellation()
            tracker.begin(index)
            let fileCommit = try await fetch(job) { written, expected in
                tracker.update(index, written: written, expected: expected)
            }
            let size = try fileSize(job.destination) ?? 0
            tracker.finish(index, size: size)
            if job.isModelFile {
                manifestFiles.append(.init(name: job.name, size: size))
                if commit == nil { commit = fileCommit }
            }
        }

        let manifest = ModelManifest(repo: entry.repo, files: manifestFiles, commit: commit,
                                     // Whole seconds, so the value matches what ISO 8601 stores.
                                     downloadedAt: Date(timeIntervalSince1970: Date().timeIntervalSince1970.rounded(.down)))
        let data = try ModelManifest.encoder().encode(manifest)
        try data.write(to: modelDir.appendingPathComponent(ModelManifest.fileName), options: .atomic)
        tracker.emitFinal()
        return manifest
    }

    private struct Job: Sendable {
        let name: String
        let source: URL
        let destination: URL
        let estimate: Int64
        let isModelFile: Bool
    }

    /// Fetches one file unless it is already complete. Returns the
    /// `x-repo-commit` header when a transfer happened and carried one.
    private func fetch(
        _ job: Job,
        onProgress: @escaping @Sendable (Int64, Int64?) -> Void
    ) async throws -> String? {
        while let other = inFlight[job.destination] {
            _ = try? await other.value
            try Task.checkCancellation()
        }
        if fileManager.fileExists(atPath: job.destination.path) {
            return nil
        }

        let configuration = self.configuration
        let box = CommitBox()
        let task = Task {
            let commit = try await Self.transferWithRetry(
                name: job.name, source: job.source, destination: job.destination,
                configuration: configuration, onProgress: onProgress
            )
            box.set(commit)
        }
        inFlight[job.destination] = task
        defer { inFlight[job.destination] = nil }
        try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
        return box.value
    }

    private static func transferWithRetry(
        name: String,
        source: URL,
        destination: URL,
        configuration: URLSessionConfiguration,
        onProgress: @escaping @Sendable (Int64, Int64?) -> Void
    ) async throws -> String? {
        let partial = destination.appendingPathExtension(String(partialSuffix.dropFirst()))
        do {
            return try await transfer(name: name, source: source, partial: partial,
                                      destination: destination, configuration: configuration,
                                      onProgress: onProgress)
        } catch ModelDownloadError.rangeNotSatisfiable {
            // The partial file does not match the remote file; start over once.
            try? FileManager.default.removeItem(at: partial)
            return try await transfer(name: name, source: source, partial: partial,
                                      destination: destination, configuration: configuration,
                                      onProgress: onProgress)
        }
    }

    private static func transfer(
        name: String,
        source: URL,
        partial: URL,
        destination: URL,
        configuration: URLSessionConfiguration,
        onProgress: @escaping @Sendable (Int64, Int64?) -> Void
    ) async throws -> String? {
        try Task.checkCancellation()
        let fileManager = FileManager.default
        let offset = (try? fileManager.attributesOfItem(atPath: partial.path)[.size] as? Int64) ?? 0

        var request = URLRequest(url: source)
        // Compressed transfer would make Content-Length disagree with the
        // bytes written to disk.
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        if offset > 0 {
            request.setValue("bytes=\(offset)-", forHTTPHeaderField: "Range")
        }

        let delegate = FileTransfer(name: name, partialURL: partial, resumeOffset: offset, onProgress: onProgress)
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        let dataTask = session.dataTask(with: request)

        let outcome = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                delegate.start(continuation)
                dataTask.resume()
            }
        } onCancel: {
            dataTask.cancel()
        }

        let actual = (try? fileManager.attributesOfItem(atPath: partial.path)[.size] as? Int64) ?? 0
        if let expected = outcome.expectedSize, expected != actual {
            try? fileManager.removeItem(at: partial)
            throw ModelDownloadError.sizeMismatch(file: name, expected: expected, actual: actual)
        }
        if fileManager.fileExists(atPath: destination.path) {
            try fileManager.removeItem(at: destination)
        }
        try fileManager.moveItem(at: partial, to: destination)
        return outcome.commit
    }

    private func fileSize(_ url: URL) throws -> Int64? {
        try fileManager.attributesOfItem(atPath: url.path)[.size] as? Int64
    }
}

// MARK: - Helpers

private final class CommitBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: String?

    func set(_ value: String?) {
        lock.withLock { stored = value }
    }

    var value: String? {
        lock.withLock { stored }
    }
}

/// Aggregates per-file byte counts and yields throttled progress.
private final class ProgressTracker: @unchecked Sendable {
    private struct FileState {
        let name: String
        var received: Int64 = 0
        var expected: Int64?
        let estimate: Int64
    }

    private let lock = NSLock()
    private var files: [FileState]
    private var current = 0
    private var lastEmit = Date.distantPast
    private let continuation: AsyncStream<DownloadProgress>.Continuation?
    private static let interval: TimeInterval = 0.1

    init(files: [(name: String, estimate: Int64)], continuation: AsyncStream<DownloadProgress>.Continuation?) {
        self.files = files.map { FileState(name: $0.name, estimate: $0.estimate) }
        self.continuation = continuation
    }

    func begin(_ index: Int) {
        emit(force: true) { current = index }
    }

    func update(_ index: Int, written: Int64, expected: Int64?) {
        emit(force: false) {
            files[index].received = written
            if let expected { files[index].expected = expected }
        }
    }

    func finish(_ index: Int, size: Int64) {
        emit(force: true) {
            files[index].received = size
            files[index].expected = size
        }
    }

    func emitFinal() {
        emit(force: true) {}
    }

    private func emit(force: Bool, _ mutate: () -> Void) {
        let snapshot: DownloadProgress? = lock.withLock {
            mutate()
            let now = Date()
            guard force || now.timeIntervalSince(lastEmit) >= Self.interval else { return nil }
            lastEmit = now
            let received = files.reduce(Int64(0)) { $0 + $1.received }
            let total = files.reduce(Int64(0)) { $0 + ($1.expected ?? max($1.estimate, $1.received)) }
            return DownloadProgress(bytesReceived: received, totalBytes: max(total, received),
                                    currentFile: files[current].name)
        }
        if let snapshot {
            continuation?.yield(snapshot)
        }
    }
}

/// URLSession delegate for one file: appends the body to the partial file
/// and captures the headers needed for size verification and the manifest.
/// Mutable state is touched on the session's serial delegate queue and
/// guarded by a lock for the hand-off to the awaiting task.
private final class FileTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    struct Outcome: Sendable {
        let expectedSize: Int64?
        let commit: String?
    }

    private let name: String
    private let partialURL: URL
    private let resumeOffset: Int64
    private let onProgress: @Sendable (Int64, Int64?) -> Void

    private let lock = NSLock()
    private var continuation: CheckedContinuation<Outcome, Error>?
    private var handle: FileHandle?
    private var written: Int64 = 0
    private var expected: Int64?
    private var linkedSize: Int64?
    private var commit: String?
    private var failure: Error?

    init(name: String, partialURL: URL, resumeOffset: Int64, onProgress: @escaping @Sendable (Int64, Int64?) -> Void) {
        self.name = name
        self.partialURL = partialURL
        self.resumeOffset = resumeOffset
        self.onProgress = onProgress
    }

    func start(_ continuation: CheckedContinuation<Outcome, Error>) {
        lock.withLock { self.continuation = continuation }
    }

    private func captureHeaders(_ response: HTTPURLResponse) {
        if let value = response.value(forHTTPHeaderField: "x-linked-size"), let size = Int64(value) {
            linkedSize = size
        }
        if let value = response.value(forHTTPHeaderField: "x-repo-commit"), !value.isEmpty {
            commit = value
        }
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        lock.withLock { captureHeaders(response) }
        var redirected = request
        if let range = task.originalRequest?.value(forHTTPHeaderField: "Range") {
            redirected.setValue(range, forHTTPHeaderField: "Range")
        }
        redirected.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        completionHandler(redirected)
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse,
        completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void
    ) {
        let disposition: URLSession.ResponseDisposition = lock.withLock {
            guard let http = response as? HTTPURLResponse else {
                failure = ModelDownloadError.invalidResponse(file: name)
                return .cancel
            }
            captureHeaders(http)
            let length = http.expectedContentLength
            let append: Bool
            switch http.statusCode {
            case 200:
                append = false
                written = 0
                expected = linkedSize ?? (length >= 0 ? length : nil)
            case 206:
                append = true
                written = resumeOffset
                expected = linkedSize ?? Self.contentRangeTotal(http) ?? (length >= 0 ? resumeOffset + length : nil)
            case 416:
                failure = ModelDownloadError.rangeNotSatisfiable(file: name)
                return .cancel
            default:
                failure = ModelDownloadError.httpStatus(file: name, status: http.statusCode)
                return .cancel
            }
            do {
                let fileManager = FileManager.default
                if !append || !fileManager.fileExists(atPath: partialURL.path) {
                    fileManager.createFile(atPath: partialURL.path, contents: nil)
                    written = 0
                }
                let handle = try FileHandle(forWritingTo: partialURL)
                if append {
                    try handle.seekToEnd()
                } else {
                    try handle.truncate(atOffset: 0)
                }
                self.handle = handle
            } catch {
                failure = error
                return .cancel
            }
            return .allow
        }
        if disposition == .allow {
            let (written, expected) = lock.withLock { (self.written, self.expected) }
            onProgress(written, expected)
        }
        completionHandler(disposition)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        let report: (Int64, Int64?)? = lock.withLock {
            guard failure == nil, let handle else { return nil }
            do {
                try handle.write(contentsOf: data)
                written += Int64(data.count)
                return (written, expected)
            } catch {
                failure = error
                return nil
            }
        }
        if let report {
            onProgress(report.0, report.1)
        } else {
            dataTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let (continuation, result): (CheckedContinuation<Outcome, Error>?, Result<Outcome, Error>) = lock.withLock {
            try? handle?.synchronize()
            try? handle?.close()
            handle = nil
            let result: Result<Outcome, Error>
            if let failure {
                result = .failure(failure)
            } else if let error {
                if (error as? URLError)?.code == .cancelled {
                    result = .failure(CancellationError())
                } else {
                    result = .failure(error)
                }
            } else {
                result = .success(Outcome(expectedSize: expected, commit: commit))
            }
            let continuation = self.continuation
            self.continuation = nil
            return (continuation, result)
        }
        continuation?.resume(with: result)
    }

    /// Total length from `Content-Range: bytes a-b/total`.
    static func contentRangeTotal(_ response: HTTPURLResponse) -> Int64? {
        guard let value = response.value(forHTTPHeaderField: "Content-Range"),
              let slash = value.lastIndex(of: "/") else { return nil }
        return Int64(value[value.index(after: slash)...].trimmingCharacters(in: .whitespaces))
    }
}
