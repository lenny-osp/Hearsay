import Foundation
import Testing
@testable import HearsayCore

// MARK: - Stub Hugging Face server

/// Serves files for one fake host. Supports `Range`, an announced size that
/// disagrees with the body, and a "hang after N bytes" mode for testing
/// cancellation.
final class FakeHub: @unchecked Sendable {
    struct Behavior {
        var linkedSizeOverride: Int64?
        /// Send only this many bytes on the first request, then stall.
        var hangAfterBytes: Int?
    }

    let host = "hub-\(UUID().uuidString.lowercased()).test"
    let commit = "0123456789abcdef0123456789abcdef01234567"
    private let lock = NSLock()
    private var files: [String: Data] = [:]
    private var behaviors: [String: Behavior] = [:]
    private var hung: Set<String> = []
    private(set) var requests: [URLRequest] = []

    var baseURL: URL { URL(string: "https://\(host)")! }

    init() { StubURLProtocol.register(self) }

    deinit { StubURLProtocol.unregister(host) }

    func serve(_ path: String, _ data: Data, behavior: Behavior = Behavior()) {
        lock.withLock {
            files[path] = data
            behaviors[path] = behavior
        }
    }

    func recordedRequests() -> [URLRequest] {
        lock.withLock { requests }
    }

    struct Reply {
        let status: Int
        let headers: [String: String]
        let body: Data
        let hang: Bool
    }

    func reply(to request: URLRequest) -> Reply {
        lock.withLock {
            requests.append(request)
            let path = request.url?.path ?? ""
            guard let data = files[path] else {
                return Reply(status: 404, headers: [:], body: Data(), hang: false)
            }
            let behavior = behaviors[path] ?? Behavior()
            var headers = ["x-repo-commit": commit]
            if let linked = behavior.linkedSizeOverride {
                headers["x-linked-size"] = String(linked)
            }
            if let hangAfter = behavior.hangAfterBytes, !hung.contains(path) {
                hung.insert(path)
                headers["Content-Length"] = String(data.count)
                return Reply(status: 200, headers: headers, body: data.prefix(hangAfter), hang: true)
            }
            if let range = request.value(forHTTPHeaderField: "Range"),
               range.hasPrefix("bytes="), range.hasSuffix("-"),
               let start = Int(range.dropFirst(6).dropLast()) {
                guard start < data.count else {
                    return Reply(status: 416, headers: headers, body: Data(), hang: false)
                }
                let slice = data.subdata(in: start..<data.count)
                headers["Content-Length"] = String(slice.count)
                headers["Content-Range"] = "bytes \(start)-\(data.count - 1)/\(data.count)"
                return Reply(status: 206, headers: headers, body: slice, hang: false)
            }
            headers["Content-Length"] = String(data.count)
            return Reply(status: 200, headers: headers, body: data, hang: false)
        }
    }
}

final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var hubs: [String: FakeHub] = [:]

    static func register(_ hub: FakeHub) {
        lock.withLock { hubs[hub.host] = hub }
    }

    static func unregister(_ host: String) {
        _ = lock.withLock { hubs.removeValue(forKey: host) }
    }

    static func hub(for host: String?) -> FakeHub? {
        guard let host else { return nil }
        return lock.withLock { hubs[host] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url, let hub = Self.hub(for: url.host) else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotFindHost))
            return
        }
        let reply = hub.reply(to: request)
        guard let response = HTTPURLResponse(url: url, statusCode: reply.status,
                                             httpVersion: "HTTP/1.1", headerFields: reply.headers) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if !reply.body.isEmpty {
            client?.urlProtocol(self, didLoad: reply.body)
        }
        if !reply.hang {
            client?.urlProtocolDidFinishLoading(self)
        }
    }

    override func stopLoading() {}
}

// MARK: - Fixtures

private enum Fixture {
    static let tokenizer = TokenizerSource(repo: "openai/whisper-test", files: ["tokenizer.json", "vocab.json"])
    static let entry = ModelCatalogEntry(
        repo: "mlx-community/whisper-test", displayName: "Test", sizeBytes: 4096 + 20,
        weightsFile: "model.safetensors", quantization: "fp16", family: "tiny", recommended: false
    )
    static let config = Data(#"{"n_mels": 80}"#.utf8).padded(to: 20)
    static let weights = Data((0..<4096).map { UInt8($0 % 251) })
    static let tokenizerJSON = Data(repeating: 0x41, count: 300)
    static let vocab = Data(repeating: 0x42, count: 200)

    static func tempRoot() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("HearsayModelTests-\(UUID().uuidString)", isDirectory: true)
    }

    static func session() -> URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return configuration
    }

    static func hub(weightsBehavior: FakeHub.Behavior = .init()) -> FakeHub {
        let hub = FakeHub()
        hub.serve("/openai/whisper-test/resolve/main/tokenizer.json", tokenizerJSON)
        hub.serve("/openai/whisper-test/resolve/main/vocab.json", vocab)
        hub.serve("/mlx-community/whisper-test/resolve/main/config.json", config)
        hub.serve("/mlx-community/whisper-test/resolve/main/model.safetensors", weights, behavior: weightsBehavior)
        return hub
    }

    static var totalBytes: Int64 {
        Int64(config.count + weights.count + tokenizerJSON.count + vocab.count)
    }
}

private extension Data {
    func padded(to count: Int) -> Data {
        self + Data(repeating: 0x20, count: Swift.max(0, count - self.count))
    }
}

private func fileSize(_ url: URL) -> Int64? {
    (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? nil
}

// MARK: - Catalog

struct ModelCatalogTests {
    @Test func bundledCatalogHasElevenEntriesAndOneRecommended() throws {
        let catalog = try ModelCatalog.bundled()
        #expect(catalog.entries.count == 11)
        #expect(Set(catalog.entries.map(\.repo)).count == 11)
        let recommended = catalog.entries.filter(\.recommended)
        #expect(recommended.map(\.repo) == ["mlx-community/whisper-large-v3-turbo"])
        #expect(catalog.recommended?.weightsFile == "weights.safetensors")
        for entry in catalog.entries where entry.repo != "mlx-community/whisper-large-v3-turbo" {
            #expect(entry.weightsFile == "model.safetensors")
        }
        for entry in catalog.entries {
            #expect(entry.sizeBytes > 0)
            #expect(["fp16", "8bit", "4bit"].contains(entry.quantization))
            #expect(ModelCatalog.familyOrder.contains(entry.family))
            #expect(entry.id == entry.repo)
            #expect(entry.files == ["config.json", entry.weightsFile])
        }
        #expect(catalog.tokenizer.repo == "openai/whisper-large-v3")
        #expect(catalog.tokenizer.files == [
            "tokenizer.json", "tokenizer_config.json", "generation_config.json", "vocab.json",
            "merges.txt", "added_tokens.json", "special_tokens_map.json",
        ])
    }
}

// MARK: - Downloader

struct ModelDownloaderTests {
    @Test func layoutReplacesSlashes() {
        let root = URL(fileURLWithPath: "/tmp/models")
        #expect(ModelDownloader.modelDirectory(for: "mlx-community/whisper-tiny-fp16", in: root).path
            == "/tmp/models/mlx-community_whisper-tiny-fp16")
        #expect(ModelDownloader.tokenizerDirectory(
            for: TokenizerSource(repo: "openai/whisper-large-v3", files: []), in: root).path
            == "/tmp/models/_tokenizer/openai_whisper-large-v3")
    }

    @Test func fullDownloadWritesFilesManifestAndReachesTotal() async throws {
        let hub = Fixture.hub()
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let downloader = ModelDownloader(modelsRoot: root, tokenizer: Fixture.tokenizer,
                                         configuration: Fixture.session(), baseURL: hub.baseURL)

        let (stream, continuation) = AsyncStream.makeStream(of: DownloadProgress.self)
        let collector = Task {
            var all: [DownloadProgress] = []
            for await progress in stream { all.append(progress) }
            return all
        }
        let manifest = try await downloader.download(Fixture.entry, progress: continuation)
        let updates = await collector.value

        let modelDir = ModelDownloader.modelDirectory(for: Fixture.entry.repo, in: root)
        let tokenizerDir = ModelDownloader.tokenizerDirectory(for: Fixture.tokenizer, in: root)
        #expect(try Data(contentsOf: modelDir.appendingPathComponent("config.json")) == Fixture.config)
        #expect(try Data(contentsOf: modelDir.appendingPathComponent("model.safetensors")) == Fixture.weights)
        #expect(try Data(contentsOf: tokenizerDir.appendingPathComponent("tokenizer.json")) == Fixture.tokenizerJSON)
        #expect(try Data(contentsOf: tokenizerDir.appendingPathComponent("vocab.json")) == Fixture.vocab)
        #expect(!FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("tokenizer.json").path))
        #expect(!FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("model.safetensors.partial").path))

        let stored = try ModelManifest.decoder().decode(
            ModelManifest.self, from: Data(contentsOf: modelDir.appendingPathComponent("manifest.json")))
        #expect(stored == manifest)
        #expect(stored.repo == Fixture.entry.repo)
        #expect(stored.commit == hub.commit)
        #expect(stored.files == [
            .init(name: "config.json", size: Int64(Fixture.config.count)),
            .init(name: "model.safetensors", size: Int64(Fixture.weights.count)),
        ])

        let last = try #require(updates.last)
        #expect(last.bytesReceived == Fixture.totalBytes)
        #expect(last.totalBytes == Fixture.totalBytes)
        #expect(last.fractionCompleted == 1)
        #expect(updates.contains { $0.currentFile == "model.safetensors" })
        let requests = hub.recordedRequests()
        #expect(requests.allSatisfy { $0.value(forHTTPHeaderField: "Range") == nil })
        #expect(requests.count == 4)
    }

    @Test func sharedTokenizerIsDownloadedOnce() async throws {
        let hub = Fixture.hub()
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let downloader = ModelDownloader(modelsRoot: root, tokenizer: Fixture.tokenizer,
                                         configuration: Fixture.session(), baseURL: hub.baseURL)
        try await downloader.download(Fixture.entry)
        try FileManager.default.removeItem(at: ModelDownloader.modelDirectory(for: Fixture.entry.repo, in: root))
        try await downloader.download(Fixture.entry)
        let tokenizerRequests = hub.recordedRequests().filter { $0.url?.path.hasPrefix("/openai/") == true }
        #expect(tokenizerRequests.count == 2)
    }

    @Test func sizeMismatchFailsAndKeepsNoFinalFile() async throws {
        let hub = Fixture.hub(weightsBehavior: .init(linkedSizeOverride: Int64(Fixture.weights.count + 10)))
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let downloader = ModelDownloader(modelsRoot: root, tokenizer: Fixture.tokenizer,
                                         configuration: Fixture.session(), baseURL: hub.baseURL)

        await #expect(throws: ModelDownloadError.sizeMismatch(
            file: "model.safetensors", expected: Int64(Fixture.weights.count + 10),
            actual: Int64(Fixture.weights.count))) {
            try await downloader.download(Fixture.entry)
        }
        let modelDir = ModelDownloader.modelDirectory(for: Fixture.entry.repo, in: root)
        #expect(!FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("model.safetensors").path))
        #expect(!FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("model.safetensors.partial").path))
        #expect(!FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("manifest.json").path))
    }

    @Test func httpErrorFails() async throws {
        let hub = FakeHub()
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let downloader = ModelDownloader(modelsRoot: root, tokenizer: Fixture.tokenizer,
                                         configuration: Fixture.session(), baseURL: hub.baseURL)
        await #expect(throws: ModelDownloadError.httpStatus(file: "tokenizer.json", status: 404)) {
            try await downloader.download(Fixture.entry)
        }
    }

    @Test func cancellationKeepsPartialAndSecondDownloadResumesWithRange() async throws {
        let firstChunk = 1000
        let hub = Fixture.hub(weightsBehavior: .init(hangAfterBytes: firstChunk))
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let downloader = ModelDownloader(modelsRoot: root, tokenizer: Fixture.tokenizer,
                                         configuration: Fixture.session(), baseURL: hub.baseURL)
        let modelDir = ModelDownloader.modelDirectory(for: Fixture.entry.repo, in: root)
        let partial = modelDir.appendingPathComponent("model.safetensors.partial")
        let final = modelDir.appendingPathComponent("model.safetensors")

        let task = Task { try await downloader.download(Fixture.entry) }
        let deadline = Date().addingTimeInterval(10)
        while fileSize(partial) != Int64(firstChunk), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(fileSize(partial) == Int64(firstChunk))
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(fileSize(partial) == Int64(firstChunk))
        #expect(!FileManager.default.fileExists(atPath: final.path))
        #expect(!FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("manifest.json").path))

        try await downloader.download(Fixture.entry)
        let weightRequests = hub.recordedRequests().filter { $0.url?.lastPathComponent == "model.safetensors" }
        #expect(weightRequests.count == 2)
        #expect(weightRequests.last?.value(forHTTPHeaderField: "Range") == "bytes=\(firstChunk)-")
        #expect(try Data(contentsOf: final) == Fixture.weights)
        #expect(!FileManager.default.fileExists(atPath: partial.path))
        #expect(FileManager.default.fileExists(atPath: modelDir.appendingPathComponent("manifest.json").path))
    }
}

// MARK: - Store

@MainActor
struct ModelStoreTests {
    private static func freshSettings() -> (AppSettings, UserDefaults, String) {
        let suite = "tw.og1o.hearsay.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        return (AppSettings(defaults: defaults), defaults, suite)
    }

    private static let catalog = ModelCatalog(entries: [Fixture.entry], tokenizer: Fixture.tokenizer)

    private static func writeModel(in root: URL, manifest: Bool = true) throws {
        let dir = ModelDownloader.modelDirectory(for: Fixture.entry.repo, in: root)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Fixture.config.write(to: dir.appendingPathComponent("config.json"))
        try Fixture.weights.write(to: dir.appendingPathComponent("model.safetensors"))
        if manifest {
            let value = ModelManifest(repo: Fixture.entry.repo, files: [], commit: nil, downloadedAt: Date())
            try ModelManifest.encoder().encode(value).write(to: dir.appendingPathComponent("manifest.json"))
        }
    }

    private static func writeTokenizer(in root: URL) throws {
        let dir = ModelDownloader.tokenizerDirectory(for: Fixture.tokenizer, in: root)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Fixture.tokenizerJSON.write(to: dir.appendingPathComponent("tokenizer.json"))
        try Fixture.vocab.write(to: dir.appendingPathComponent("vocab.json"))
    }

    @Test func emptyRootHasNothingInstalled() {
        let (settings, defaults, suite) = Self.freshSettings()
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ModelStore(settings: settings, catalog: Self.catalog, rootURL: Fixture.tempRoot())
        #expect(store.installed.isEmpty)
        #expect(!store.isTokenizerInstalled)
        #expect(store.state(for: Fixture.entry) == .notInstalled)
        #expect(store.totalSizeOnDisk == 0)
    }

    @Test func scanningRequiresManifestAndReadyRequiresTokenizer() throws {
        let (settings, defaults, suite) = Self.freshSettings()
        defer { defaults.removePersistentDomain(forName: suite) }
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }

        try Self.writeModel(in: root, manifest: false)
        let store = ModelStore(settings: settings, catalog: Self.catalog, rootURL: root)
        #expect(store.installed.isEmpty)

        try Self.writeModel(in: root)
        store.refresh()
        #expect(store.installed.map(\.repo) == [Fixture.entry.repo])
        #expect(store.installed.first?.folder == store.modelDirectory(for: Fixture.entry.repo))
        #expect((store.installed.first?.sizeOnDisk ?? 0) >= Int64(Fixture.config.count + Fixture.weights.count))
        #expect(!store.isReady(Fixture.entry))
        #expect(store.state(for: Fixture.entry) == .notInstalled)

        try Self.writeTokenizer(in: root)
        store.refresh()
        #expect(store.isTokenizerInstalled)
        #expect(store.isReady(Fixture.entry))
        #expect(store.state(for: Fixture.entry) == .installed)
        #expect(store.tokenizerDirectory.path.hasSuffix("_tokenizer/openai_whisper-test"))
        #expect(store.totalSizeOnDisk >= Fixture.totalBytes)
    }

    @Test func useAndDeleteClearsActiveModel() throws {
        let (settings, defaults, suite) = Self.freshSettings()
        defer { defaults.removePersistentDomain(forName: suite) }
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        try Self.writeModel(in: root)
        try Self.writeTokenizer(in: root)
        let store = ModelStore(settings: settings, catalog: Self.catalog, rootURL: root)

        store.use(Fixture.entry)
        #expect(store.activeModelRepo == Fixture.entry.repo)
        #expect(defaults.string(forKey: AppSettings.Key.activeModelRepo) == Fixture.entry.repo)
        #expect(AppSettings(defaults: defaults).activeModelRepo == Fixture.entry.repo)

        try store.delete(Fixture.entry)
        #expect(store.activeModelRepo == nil)
        #expect(defaults.string(forKey: AppSettings.Key.activeModelRepo) == nil)
        #expect(store.installed.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: store.modelDirectory(for: Fixture.entry.repo).path))
        #expect(store.isTokenizerInstalled, "the shared tokenizer survives deleting a model")
    }

    @Test func useIgnoresModelThatIsNotReady() {
        let (settings, defaults, suite) = Self.freshSettings()
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ModelStore(settings: settings, catalog: Self.catalog, rootURL: Fixture.tempRoot())
        store.use(Fixture.entry)
        #expect(store.activeModelRepo == nil)
    }

    @Test func storeDownloadEndsInstalled() async throws {
        let (settings, defaults, suite) = Self.freshSettings()
        defer { defaults.removePersistentDomain(forName: suite) }
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let hub = Fixture.hub()
        let store = ModelStore(settings: settings, catalog: Self.catalog, rootURL: root,
                               configuration: Fixture.session(), baseURL: hub.baseURL)
        store.download(Fixture.entry)
        if case .downloading = store.state(for: Fixture.entry) {} else {
            Issue.record("expected downloading state right after download()")
        }
        let deadline = Date().addingTimeInterval(10)
        while store.state(for: Fixture.entry) != .installed, Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(store.state(for: Fixture.entry) == .installed)
        #expect(store.isReady(Fixture.entry))
        #expect(store.activeModelRepo == Fixture.entry.repo, "the first downloaded model becomes active")
        #expect(store.hasLocalFiles(Fixture.entry))
    }

    @Test func storeDownloadFailureIsReported() async throws {
        let (settings, defaults, suite) = Self.freshSettings()
        defer { defaults.removePersistentDomain(forName: suite) }
        let root = Fixture.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let hub = FakeHub()
        let store = ModelStore(settings: settings, catalog: Self.catalog, rootURL: root,
                               configuration: Fixture.session(), baseURL: hub.baseURL)
        store.download(Fixture.entry)
        let deadline = Date().addingTimeInterval(10)
        while case .downloading = store.state(for: Fixture.entry), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        guard case let .failed(message) = store.state(for: Fixture.entry) else {
            Issue.record("expected failed, got \(store.state(for: Fixture.entry))")
            return
        }
        #expect(message.contains("404"))
    }
}
