import AppKit
import Foundation
import HearsayCore
import HearsayWhisper
import Observation

/// The File flow (PLAN.md 4.2): decode any AVFoundation-readable file to
/// 16 kHz mono, transcribe it with the active model, and write
/// `<timestamp>.srt` into the output folder, where the timestamp follows the
/// Python rule (`Timestamps.sourceFileTimestamp`: embedded in the filename,
/// else birth time, else modification time). The user's source file is
/// never renamed or moved. A finished SRT is offered to the notes flow
/// through `notesRequest`.
///
/// One exception to "never touch the source": a recording Hearsay itself
/// kept in the output folder (a failed recording or a recovered spool WAV,
/// named `<timestamp>.wav`) gets its SRT under the same stem, replacing a
/// saved live preview, so the pair is renamed together by the notes flow.
@MainActor
@Observable
final class FileViewModel {
    enum Phase: Equatable {
        case idle
        case loading(source: URL)
        case transcribing(source: URL, progress: Double)
        case finished(srt: URL, source: URL)
        case failed(message: String)
    }

    private(set) var phase: Phase = .idle
    /// The failure was the missing model; the view links to Models.
    private(set) var needsModel = false
    /// A finished SRT waiting for the notes flow; see `takeNotesRequest()`.
    private(set) var notesRequest: URL?
    /// Short note shown in the idle state, e.g. after Cancel.
    private(set) var note: String?

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let modelStore: ModelStore
    @ObservationIgnored private let engine: WhisperEngine
    @ObservationIgnored private var job = 0
    @ObservationIgnored private var runTask: Task<Void, Never>?

    init(settings: AppSettings, modelStore: ModelStore, engine: WhisperEngine) {
        self.settings = settings
        self.modelStore = modelStore
        self.engine = engine
    }

    /// Shared with the Record tab.
    var languageCode: String {
        get { settings.defaultLanguageCode }
        set { settings.defaultLanguageCode = newValue }
    }

    var isBusy: Bool {
        switch phase {
        case .loading, .transcribing: true
        case .idle, .finished, .failed: false
        }
    }

    var errorMessage: String? {
        if case .failed(let message) = phase { return message }
        return nil
    }

    /// Starts transcribing `source` unless a file is already running.
    func transcribe(_ source: URL) {
        guard !isBusy else { return }
        runTask = Task { await run(source: source) }
    }

    /// Stops the running file at the next 30 s window. Nothing is written.
    func cancel() {
        guard isBusy else { return }
        runTask?.cancel()
    }

    func takeNotesRequest() -> URL? {
        defer { notesRequest = nil }
        return notesRequest
    }

    func reveal() {
        if case .finished(let srt, _) = phase {
            NSWorkspace.shared.activateFileViewerSelecting([srt])
        }
    }

    /// Runs the whole flow and returns the SRT, or nil after a failure
    /// (the message is in `phase`). `location` overrides the active model
    /// (debug entry point only).
    @discardableResult
    func run(source: URL, location override: WhisperModelLocation? = nil) async -> URL? {
        job += 1
        let id = job
        needsModel = false
        notesRequest = nil
        note = nil

        let location: WhisperModelLocation
        if let override {
            location = override
        } else {
            do {
                location = try WhisperModelLocation.active(in: modelStore)
            } catch {
                needsModel = true
                phase = .failed(message: RecordingController.describe(error))
                return nil
            }
        }

        phase = .loading(source: source)
        let accessingSource = source.startAccessingSecurityScopedResource()
        defer { if accessingSource { source.stopAccessingSecurityScopedResource() } }
        let folder: ResolvedOutputFolder
        do {
            folder = try TranscriptOutput.resolveFolder(settings: settings)
        } catch {
            phase = .failed(message: "Could not open the output folder: \(error.localizedDescription)")
            return nil
        }
        defer { folder.stopAccessing() }

        do {
            let samples = try await Task.detached(priority: .userInitiated) {
                try AudioFileLoader.loadMono16k(url: source)
            }.value
            try Task.checkCancellation()
            phase = .transcribing(source: source, progress: 0)
            let result = try await engine.transcribe(
                samples: samples, location: location,
                options: TranscriptionOptions.app(languageCode: languageCode),
                progress: { [weak self] value in
                    Task { @MainActor in self?.updateProgress(value, job: id) }
                }
            )
            let srt = try Self.writeSRT(result.cues(), for: source, in: folder.url)
            phase = .finished(srt: srt, source: source)
            notesRequest = srt
            return srt
        } catch where error.isTranscriptionCancelled {
            phase = .idle
            note = "Cancelled"
            return nil
        } catch {
            phase = .failed(message: "Could not transcribe \(source.lastPathComponent): "
                + RecordingController.describe(error))
            return nil
        }
    }

    private func updateProgress(_ value: Double, job id: Int) {
        guard id == job, case .transcribing(let source, _) = phase else { return }
        phase = .transcribing(source: source, progress: min(max(value, 0), 1))
    }

    /// `<folder>/<timestamp>.srt` with a numeric suffix when that name or
    /// its WAV is taken, except for Hearsay's own `<timestamp>.wav` in the
    /// output folder, whose SRT uses the same stem.
    static func writeSRT(_ cues: [CoreSegment], for source: URL, in folder: URL) throws -> URL {
        let base = Timestamps.sourceFileTimestamp(url: source) ?? Timestamps.now()
        let sourceFolder = source.deletingLastPathComponent().resolvingSymlinksInPath().path
        let isOwnRecording = sourceFolder == folder.resolvingSymlinksInPath().path
            && source.pathExtension.lowercased() == "wav"
            && source.deletingPathExtension().lastPathComponent == base
        let stem = isOwnRecording
            ? base
            : TranscriptOutput.freeStem(base: base, directory: folder, suffixes: [".srt", ".wav"])
        let srt = folder.appendingPathComponent(stem + ".srt")
        try TranscriptOutput.writeSRT(cues, to: srt)
        return srt
    }

    // MARK: - Debug entry point

    /// Debug only. When the app is launched with `HEARSAY_TRANSCRIBE_FILE=<audio>`
    /// and `HEARSAY_MODEL_DIR=<dir>` (one folder holding both the model and
    /// the tokenizer files), transcribes that file with that folder,
    /// bypassing `ModelStore`, writes the SRT into the output folder, prints
    /// its path to stdout (timings to stderr), and quits with status 0, or 1
    /// on failure. `HEARSAY_LANGUAGE` (en / zh) is optional. Example:
    ///
    ///     HEARSAY_TRANSCRIBE_FILE=Fixtures/en-30s.wav \
    ///     HEARSAY_MODEL_DIR=Spike/models/mlx-community_whisper-large-v3-turbo \
    ///     .build/derived/Build/Products/Debug/Hearsay.app/Contents/MacOS/Hearsay
    ///
    /// Returns false (and does nothing) when the variables are not set.
    static func runDebugTranscriptionIfRequested(
        settings: AppSettings, modelStore: ModelStore, engine: WhisperEngine
    ) -> Bool {
        let environment = ProcessInfo.processInfo.environment
        guard let file = environment["HEARSAY_TRANSCRIBE_FILE"], !file.isEmpty,
              let directory = environment["HEARSAY_MODEL_DIR"], !directory.isEmpty
        else { return false }
        let model = FileViewModel(settings: settings, modelStore: modelStore, engine: engine)
        if let language = environment["HEARSAY_LANGUAGE"], ["en", "zh"].contains(language) {
            model.languageCode = language
        }
        let modelURL = URL(fileURLWithPath: directory, isDirectory: true)
        let source = URL(fileURLWithPath: file)
        Task { @MainActor in
            let start = Date()
            let srt = await model.run(
                source: source,
                location: WhisperModelLocation(modelDirectory: modelURL, tokenizerDirectory: modelURL)
            )
            let elapsed = Date().timeIntervalSince(start)
            if let srt {
                FileHandle.standardOutput.write(Data((srt.path + "\n").utf8))
                FileHandle.standardError.write(Data(String(
                    format: "hearsay debug: load + decode + transcribe %.2f s\n", elapsed
                ).utf8))
                exit(0)
            } else {
                FileHandle.standardError.write(Data("hearsay debug: \(model.errorMessage ?? "failed")\n".utf8))
                exit(1)
            }
        }
        return true
    }
}
