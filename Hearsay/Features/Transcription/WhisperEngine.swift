import Foundation
import HearsayCore
import HearsayWhisper
import SwiftUI

/// Errors raised before the decoder runs. Decoder and loading errors pass
/// through unchanged.
enum WhisperEngineError: Error, LocalizedError, Equatable {
    /// No model is chosen in the Models tab.
    case noActiveModel
    /// The chosen model (or the shared tokenizer) is not fully downloaded.
    case modelNotReady(String)

    var errorDescription: String? {
        switch self {
        case .noActiveModel:
            "No model installed. Choose a model in Models."
        case .modelNotReady(let name):
            "The model \(name) is not fully downloaded. Finish the download in Models."
        }
    }
}

/// The folders `WhisperEngine.load` needs, read from `ModelStore` on the
/// main actor.
struct WhisperModelLocation: Sendable, Equatable {
    var modelDirectory: URL
    var tokenizerDirectory: URL

    /// The active model's folders, or `.noActiveModel` / `.modelNotReady`.
    @MainActor
    static func active(in store: ModelStore) throws -> WhisperModelLocation {
        guard let entry = store.activeEntry else { throw WhisperEngineError.noActiveModel }
        guard store.isReady(entry) else { throw WhisperEngineError.modelNotReady(entry.displayName) }
        return WhisperModelLocation(
            modelDirectory: store.modelDirectory(for: entry.repo),
            tokenizerDirectory: store.tokenizerDirectory
        )
    }
}

/// Owns at most one loaded Whisper model (PLAN.md section 4). Every job runs
/// inside the actor, so jobs are serialized and MLX never runs on the main
/// actor. The model stays loaded between jobs and is released after
/// `idleUnloadSeconds` without work (PLAN.md section 12, memory).
actor WhisperEngine {
    static let idleUnloadSeconds: TimeInterval = 600

    private var transcriber: Transcriber?
    private var loaded: WhisperModelLocation?
    /// Jobs waiting for or running in the actor; the idle timer only unloads
    /// when this is zero.
    private var activeJobs = 0
    private var idleTimer: Task<Void, Never>?

    init() {}

    var isLoaded: Bool { transcriber != nil }

    /// Loads the model unless the same folders are already loaded.
    func load(modelDirectory: URL, tokenizerDirectory: URL) async throws {
        let location = WhisperModelLocation(modelDirectory: modelDirectory, tokenizerDirectory: tokenizerDirectory)
        if loaded == location, transcriber != nil { return }
        transcriber = nil
        loaded = nil
        let model = try await Self.loadTranscriber(location)
        transcriber = model.transcriber
        loaded = location
    }

    func load(_ location: WhisperModelLocation) async throws {
        try await load(modelDirectory: location.modelDirectory, tokenizerDirectory: location.tokenizerDirectory)
    }

    /// Transcribes 16 kHz mono samples with the loaded model. `progress`
    /// receives 0...1 from the decoder's thread. `shouldCancel` is checked
    /// before every 30 s window; without it, cancelling the calling Task
    /// stops the pass at the next window. Either way the call throws
    /// `TranscriptionError.cancelled(partial:)`.
    func transcribe(
        samples: [Float],
        options: TranscriptionOptions,
        progress: @escaping @Sendable (Double) -> Void,
        shouldCancel: (@Sendable () -> Bool)? = nil
    ) async throws -> Transcription {
        beginJob()
        defer { endJob() }
        guard let transcriber else { throw WhisperEngineError.noActiveModel }
        let cancel: @Sendable () -> Bool = shouldCancel ?? { Task.isCancelled }
        if cancel() || Task.isCancelled { throw TranscriptionError.cancelled(partial: []) }
        return try transcriber.transcribe(
            samples: samples, options: options, progress: progress, shouldCancel: cancel
        )
    }

    /// Loads `location` if needed, then transcribes. The load and the job
    /// count as one job for the idle timer.
    func transcribe(
        samples: [Float],
        location: WhisperModelLocation,
        options: TranscriptionOptions,
        progress: @escaping @Sendable (Double) -> Void,
        shouldCancel: (@Sendable () -> Bool)? = nil
    ) async throws -> Transcription {
        beginJob()
        defer { endJob() }
        try await load(location)
        return try await transcribe(
            samples: samples, options: options, progress: progress, shouldCancel: shouldCancel
        )
    }

    /// Loads `location` if needed, then detects the language of `samples`
    /// among the supported languages (`TranscriptLanguage.allCases`), with
    /// the detector's defaults: up to three speech windows averaged, silent
    /// and no-speech windows skipped. The caller applies
    /// `LanguageDecision.decide` to the result.
    func detectLanguage(samples: [Float], location: WhisperModelLocation) async throws -> DetectionResult {
        beginJob()
        defer { endJob() }
        try await load(location)
        guard let transcriber else { throw WhisperEngineError.noActiveModel }
        if Task.isCancelled { throw CancellationError() }
        return try transcriber.detectLanguage(
            samples: samples, candidates: TranscriptLanguage.allCases.map(\.code)
        )
    }

    /// Releases the model when no job has run for `seconds`. Called by the
    /// timer that every finished job restarts.
    func unloadIfIdle(after seconds: TimeInterval) {
        idleTimer?.cancel()
        idleTimer = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            await self?.unloadNowIfIdle()
        }
    }

    func unload() {
        idleTimer?.cancel()
        idleTimer = nil
        transcriber = nil
        loaded = nil
    }

    // MARK: - Helpers

    private func unloadNowIfIdle() {
        guard activeJobs == 0 else { return }
        transcriber = nil
        loaded = nil
        idleTimer = nil
    }

    private func beginJob() {
        activeJobs += 1
        idleTimer?.cancel()
        idleTimer = nil
    }

    private func endJob() {
        activeJobs -= 1
        if activeJobs == 0 {
            unloadIfIdle(after: Self.idleUnloadSeconds)
        }
    }

    /// Wraps the non-Sendable transcriber so it can be handed from the
    /// loader to the actor that owns it from then on.
    private struct Loaded: @unchecked Sendable {
        let transcriber: Transcriber
    }

    private static func loadTranscriber(_ location: WhisperModelLocation) async throws -> Loaded {
        let transcriber = try await Transcriber.load(
            modelDirectory: location.modelDirectory,
            tokenizerDirectory: location.tokenizerDirectory
        )
        return Loaded(transcriber: transcriber)
    }
}

extension Error {
    /// The decoder stopped because the job was cancelled.
    var isTranscriptionCancelled: Bool {
        if let error = self as? TranscriptionError, case .cancelled = error { return true }
        return self is CancellationError
    }
}

extension Transcription {
    /// The segments as SRT cues, cleaned like mlx_whisper's `WriteSRT`
    /// (text stripped, `-->` replaced with `->`), shifted by `offset`
    /// seconds, and converted to `script` (see `ChineseScript.app`). Every
    /// cue Hearsay shows or writes comes from here.
    func cues(offset: TimeInterval = 0, script: ChineseScript) -> [CoreSegment] {
        let cues = segments.map { segment in
            CoreSegment(
                start: segment.start + offset,
                end: segment.end + offset,
                text: segment.text
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                    .replacingOccurrences(of: "-->", with: "->")
            )
        }
        return ChineseScript.convert(cues, to: script)
    }
}

extension EnvironmentValues {
    /// The app's one `WhisperEngine`, injected by `HearsayApp`.
    @Entry var whisperEngine: WhisperEngine?
}
