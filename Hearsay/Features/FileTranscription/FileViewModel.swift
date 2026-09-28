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
        /// Auto: detecting the language before transcribing.
        case detecting(source: URL)
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
    /// The language of the last file and how it was chosen (PLAN.md
    /// section 1, "Languages").
    private(set) var tracker: SessionLanguageTracker?
    /// The suggestion or fallback banner of the last file.
    private(set) var languageNotice: LanguageNotice?
    /// Why the last "Transcribe again" failed.
    private(set) var rerunError: String?
    /// The last detection result, for the debug path.
    private(set) var lastDetection: DetectionResult?

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let modelStore: ModelStore
    @ObservationIgnored private let engine: WhisperEngine
    @ObservationIgnored private var job = 0
    @ObservationIgnored private var runTask: Task<Void, Never>?
    /// The decoded samples and model of the last file, held while a
    /// language notice offers a re-run.
    @ObservationIgnored private var rerunInput: (samples: [Float], location: WhisperModelLocation)?

    init(settings: AppSettings, modelStore: ModelStore, engine: WhisperEngine) {
        self.settings = settings
        self.modelStore = modelStore
        self.engine = engine
    }

    /// Shared with the Record tab.
    var languageChoice: LanguageChoice {
        get { settings.languageChoice }
        set { settings.languageChoice = newValue }
    }

    /// The resolved language of the last file: what its SRT, the Chinese
    /// conversion, and the notes flow use.
    var sessionLanguage: TranscriptLanguage? { tracker?.language }

    var isBusy: Bool {
        switch phase {
        case .loading, .detecting, .transcribing: true
        case .idle, .finished, .failed: false
        }
    }

    /// The language notice's buttons apply: the file is finished and its
    /// samples are still held.
    var canRerun: Bool {
        if case .finished = phase, rerunInput != nil { return true }
        return false
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

    /// "Dismiss" on the suggestion banner.
    func dismissLanguageNotice() {
        languageNotice = nil
        rerunInput = nil
    }

    /// A language notice button: transcribes the last file again in
    /// `language` and rewrites the same SRT. Never changes `languageChoice`
    /// or `preferredLanguage`.
    func transcribeAgain(in language: TranscriptLanguage) {
        guard canRerun, !isBusy else { return }
        runTask = Task { await rerun(in: language) }
    }

    /// Runs the whole flow and returns the SRT, or nil after a failure
    /// (the message is in `phase`). `location` overrides the active model
    /// and `choice` the Language picker (debug entry point only).
    @discardableResult
    func run(
        source: URL, location override: WhisperModelLocation? = nil, choice: LanguageChoice? = nil
    ) async -> URL? {
        job += 1
        let id = job
        needsModel = false
        notesRequest = nil
        note = nil
        languageNotice = nil
        rerunError = nil
        rerunInput = nil
        lastDetection = nil
        tracker = nil

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
            phase = .failed(message: String(localized: "Could not open the output folder: \(error.localizedDescription)",
                                            comment: "Error. %@ is the system error message."))
            return nil
        }
        defer { folder.stopAccessing() }

        var tracker = SessionLanguageTracker(
            choice: choice ?? languageChoice, preferred: settings.preferredLanguage
        )
        do {
            let samples = try await Task.detached(priority: .userInitiated) {
                try AudioFileLoader.loadMono16k(url: source)
            }.value
            try Task.checkCancellation()
            if tracker.isUndecided {
                // Auto: detect first; unsure or no speech falls back to the
                // preferred language.
                phase = .detecting(source: source)
                let detection = try? await engine.detectLanguage(samples: samples, location: location)
                try Task.checkCancellation()
                lastDetection = detection
                tracker.finish(detection: detection?.decisionInput)
            }
            guard let language = tracker.language else { throw CancellationError() }
            self.tracker = tracker
            phase = .transcribing(source: source, progress: 0)
            let result = try await engine.transcribe(
                samples: samples, location: location,
                options: TranscriptionOptions.app(language: language),
                progress: { [weak self] value in
                    Task { @MainActor in self?.updateProgress(value, job: id) }
                }
            )
            let srt = try Self.writeSRT(result.cues(script: language.chineseScript), for: source, in: folder.url)
            phase = .finished(srt: srt, source: source)
            notesRequest = srt
            if !tracker.isSettled {
                // Fixed language: one background check for a mismatch.
                let detection = try? await engine.detectLanguage(samples: samples, location: location)
                guard id == job else { return srt }
                lastDetection = detection
                tracker.finish(detection: detection?.decisionInput)
                self.tracker = tracker
            }
            languageNotice = LanguageNotice(decision: tracker.decision)
            if languageNotice != nil {
                rerunInput = (samples, location)
            }
            return srt
        } catch where error.isTranscriptionCancelled {
            phase = .idle
            note = String(localized: "Cancelled", comment: "File tab: the transcription was cancelled")
            return nil
        } catch {
            phase = .failed(message: String(
                localized: "Could not transcribe \(source.lastPathComponent): \(RecordingController.describe(error))",
                comment: "File tab error. %1$@ is a file name, %2$@ the reason."))
            return nil
        }
    }

    /// One full pass over the held samples in `language`, written over the
    /// same SRT. On failure or Cancel the previous SRT stays.
    @discardableResult
    func rerun(in language: TranscriptLanguage) async -> URL? {
        guard case .finished(let srt, let source) = phase, let input = rerunInput, var tracker else { return nil }
        job += 1
        let id = job
        rerunError = nil
        let folder = try? TranscriptOutput.resolveFolder(settings: settings)
        defer { folder?.stopAccessing() }
        phase = .transcribing(source: source, progress: 0)
        do {
            let result = try await engine.transcribe(
                samples: input.samples, location: input.location,
                options: TranscriptionOptions.app(language: language),
                progress: { [weak self] value in
                    Task { @MainActor in self?.updateProgress(value, job: id) }
                }
            )
            guard FileManager.default.fileExists(atPath: srt.path) else {
                phase = .finished(srt: srt, source: source)
                rerunError = String(localized: "Could not transcribe again: \(srt.lastPathComponent) was moved or renamed.",
                                    comment: "Error. %@ is a file name.")
                return nil
            }
            try TranscriptOutput.writeSRT(result.cues(script: language.chineseScript), to: srt)
            tracker.choose(language)
            self.tracker = tracker
            languageNotice = nil
            rerunInput = nil
            phase = .finished(srt: srt, source: source)
            notesRequest = srt
            return srt
        } catch {
            phase = .finished(srt: srt, source: source)
            if !error.isTranscriptionCancelled {
                rerunError = String(localized: "Could not transcribe again: \(RecordingController.describe(error))",
                                    comment: "Error. %@ is the reason.")
            }
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
    /// its path and the language decision to stdout (timings to stderr), and
    /// quits with status 0, or 1 on failure. `HEARSAY_LANGUAGE` (auto, en,
    /// zh-TW, zh-CN, de, es; zh is an alias for zh-TW) is optional and overrides the Language picker for this
    /// run only. `settings` is the throwaway copy from `DebugDefaults`, so
    /// nothing the run does reaches the user's settings. The app is not
    /// sandboxed, so both paths can be anywhere the user can read,
    /// relative to the working directory or absolute. Example:
    ///
    ///     HEARSAY_TRANSCRIBE_FILE=Fixtures/en-30s.wav HEARSAY_LANGUAGE=auto \
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
        let choice = environment["HEARSAY_LANGUAGE"].flatMap(LanguageChoice.init(debugValue:))
        let modelURL = URL(fileURLWithPath: directory, isDirectory: true)
        let source = URL(fileURLWithPath: file)
        Task { @MainActor in
            let start = Date()
            let srt = await model.run(
                source: source,
                location: WhisperModelLocation(modelDirectory: modelURL, tokenizerDirectory: modelURL),
                choice: choice
            )
            let elapsed = Date().timeIntervalSince(start)
            DebugDefaults.removeSuite()
            if let srt {
                var lines = [srt.path]
                lines.append("choice \((choice ?? model.languageChoice).storageValue), preferred "
                    + settings.preferredLanguage.rawValue)
                if let detection = model.lastDetection {
                    lines.append(detection.debugSummary)
                } else {
                    lines.append("detection did not run or failed")
                }
                if let decision = model.tracker?.decision {
                    lines.append("decision: " + decision.debugSummary)
                }
                if let notice = model.languageNotice {
                    lines.append("notice: " + notice.message)
                }
                FileHandle.standardOutput.write(Data((lines.joined(separator: "\n") + "\n").utf8))
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
