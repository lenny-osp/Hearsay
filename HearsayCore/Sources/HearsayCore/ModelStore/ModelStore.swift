import Foundation
import Observation

/// Where a catalog entry stands on this Mac.
public enum DownloadState: Equatable, Sendable {
    case notInstalled
    case downloading(DownloadProgress)
    case installed
    case failed(String)
}

/// A model folder that holds `config.json`, its weights file, and
/// `manifest.json`.
public struct InstalledModel: Identifiable, Equatable, Sendable {
    public let repo: String
    public let folder: URL
    public let sizeOnDisk: Int64
    public let manifest: ModelManifest?

    public var id: String { repo }
}

/// Installed models, downloads, deletion, and the active model
/// (PLAN.md section 5). Model folders and the shared tokenizer folder are
/// kept apart; the Whisper loader receives both directories.
@MainActor
@Observable
public final class ModelStore {
    public let catalog: ModelCatalog
    public let rootURL: URL
    /// Set when the bundled catalog could not be read.
    public let catalogError: String?

    public private(set) var installed: [InstalledModel] = []
    public private(set) var isTokenizerInstalled = false
    /// Size of everything under `rootURL`, including partial downloads.
    public private(set) var totalSizeOnDisk: Int64 = 0

    /// Transient states (downloading, failed) keyed by repo. Everything else
    /// is derived from `installed`.
    private var transient: [String: DownloadState] = [:]

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let downloader: ModelDownloader
    @ObservationIgnored private var tasks: [String: (id: UUID, task: Task<Void, Never>)] = [:]

    public static var defaultRootURL: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return support.appendingPathComponent("Hearsay", isDirectory: true)
            .appendingPathComponent("Models", isDirectory: true)
    }

    public init(
        settings: AppSettings,
        catalog: ModelCatalog? = nil,
        rootURL: URL = ModelStore.defaultRootURL,
        configuration: URLSessionConfiguration = .default,
        baseURL: URL? = nil
    ) {
        var loadError: String?
        let resolved: ModelCatalog
        if let catalog {
            resolved = catalog
        } else {
            do {
                resolved = try ModelCatalog.bundled()
            } catch {
                loadError = "The built-in model list could not be read: \(error.localizedDescription)"
                resolved = ModelCatalog(entries: [], tokenizer: TokenizerSource(repo: "openai/whisper-large-v3", files: []))
            }
        }
        self.catalog = resolved
        self.catalogError = loadError
        self.rootURL = rootURL
        self.settings = settings
        if let baseURL {
            self.downloader = ModelDownloader(modelsRoot: rootURL, tokenizer: resolved.tokenizer,
                                              configuration: configuration, baseURL: baseURL)
        } else {
            self.downloader = ModelDownloader(modelsRoot: rootURL, tokenizer: resolved.tokenizer,
                                              configuration: configuration)
        }
        refresh()
    }

    // MARK: - Layout

    public func modelDirectory(for repo: String) -> URL {
        ModelDownloader.modelDirectory(for: repo, in: rootURL)
    }

    public var tokenizerDirectory: URL {
        ModelDownloader.tokenizerDirectory(for: catalog.tokenizer, in: rootURL)
    }

    // MARK: - Active model

    public var activeModelRepo: String? {
        get { settings.activeModelRepo }
        set { settings.activeModelRepo = newValue }
    }

    public var activeEntry: ModelCatalogEntry? {
        activeModelRepo.flatMap(catalog.entry(forRepo:))
    }

    /// Makes `entry` the active model when it is ready to load.
    public func use(_ entry: ModelCatalogEntry) {
        guard isReady(entry) else { return }
        activeModelRepo = entry.repo
    }

    // MARK: - State

    public func isInstalled(_ entry: ModelCatalogEntry) -> Bool {
        installed.contains { $0.repo == entry.repo }
    }

    /// Model files and the shared tokenizer are both on disk.
    public func isReady(_ entry: ModelCatalogEntry) -> Bool {
        isInstalled(entry) && isTokenizerInstalled
    }

    public func state(for entry: ModelCatalogEntry) -> DownloadState {
        if let state = transient[entry.repo] { return state }
        return isReady(entry) ? .installed : .notInstalled
    }

    /// Rescans the models folder.
    public func refresh() {
        let fileManager = FileManager.default
        installed = catalog.entries.compactMap { entry in
            let folder = modelDirectory(for: entry.repo)
            let required = ["config.json", entry.weightsFile, ModelManifest.fileName]
            guard required.allSatisfy({ fileManager.fileExists(atPath: folder.appendingPathComponent($0).path) }) else {
                return nil
            }
            let manifest = (try? Data(contentsOf: folder.appendingPathComponent(ModelManifest.fileName)))
                .flatMap { try? ModelManifest.decoder().decode(ModelManifest.self, from: $0) }
            return InstalledModel(repo: entry.repo, folder: folder,
                                  sizeOnDisk: Self.directorySize(folder), manifest: manifest)
        }
        let tokenizerFiles = catalog.tokenizer.files
        isTokenizerInstalled = !tokenizerFiles.isEmpty && tokenizerFiles.allSatisfy {
            fileManager.fileExists(atPath: tokenizerDirectory.appendingPathComponent($0).path)
        }
        totalSizeOnDisk = Self.directorySize(rootURL)
    }

    // MARK: - Download, cancel, delete

    public func download(_ entry: ModelCatalogEntry) {
        let repo = entry.repo
        if tasks[repo] != nil { return }
        let id = UUID()
        let (stream, continuation) = AsyncStream.makeStream(
            of: DownloadProgress.self, bufferingPolicy: .bufferingNewest(1)
        )
        transient[repo] = .downloading(DownloadProgress(bytesReceived: 0, totalBytes: entry.sizeBytes,
                                                        currentFile: ""))
        let downloader = self.downloader

        Task { [weak self] in
            for await progress in stream {
                guard let self, self.tasks[repo]?.id == id else { break }
                self.transient[repo] = .downloading(progress)
            }
        }

        let task = Task { [weak self] in
            let outcome: DownloadState?
            do {
                try await downloader.download(entry, progress: continuation)
                outcome = nil
            } catch is CancellationError {
                outcome = nil
            } catch let error as URLError where error.code == .cancelled {
                outcome = nil
            } catch {
                outcome = .failed(error.localizedDescription)
            }
            guard let self, self.tasks[repo]?.id == id else { return }
            self.tasks[repo] = nil
            self.transient[repo] = outcome
            self.refresh()
            // The first model the user downloads becomes the active one.
            if self.activeModelRepo == nil, self.isReady(entry) {
                self.activeModelRepo = repo
            }
        }
        tasks[repo] = (id, task)
    }

    /// Stops a download. Partial files stay on disk so the next download
    /// resumes where this one stopped.
    public func cancelDownload(_ entry: ModelCatalogEntry) {
        guard let running = tasks.removeValue(forKey: entry.repo) else { return }
        running.task.cancel()
        transient[entry.repo] = nil
        refresh()
    }

    /// Removes the model folder, including partial files. The shared
    /// tokenizer stays. Deleting the active model clears the choice.
    public func delete(_ entry: ModelCatalogEntry) throws {
        cancelDownload(entry)
        transient[entry.repo] = nil
        let folder = modelDirectory(for: entry.repo)
        if FileManager.default.fileExists(atPath: folder.path) {
            try FileManager.default.removeItem(at: folder)
        }
        if activeModelRepo == entry.repo {
            activeModelRepo = nil
        }
        refresh()
    }

    /// Whether the model folder exists at all, including partial downloads.
    public func hasLocalFiles(_ entry: ModelCatalogEntry) -> Bool {
        FileManager.default.fileExists(atPath: modelDirectory(for: entry.repo).path)
    }

    /// Clears a failed state so the row offers Download again.
    public func dismissFailure(_ entry: ModelCatalogEntry) {
        if case .failed = transient[entry.repo] {
            transient[entry.repo] = nil
        }
    }

    // MARK: - Helpers

    nonisolated static func directorySize(_ url: URL) -> Int64 {
        guard let enumerator = FileManager.default.enumerator(
            at: url, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey]
        ) else { return 0 }
        var total: Int64 = 0
        for case let file as URL in enumerator {
            guard let values = try? file.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                  values.isRegularFile == true else { continue }
            total += Int64(values.fileSize ?? 0)
        }
        return total
    }
}
