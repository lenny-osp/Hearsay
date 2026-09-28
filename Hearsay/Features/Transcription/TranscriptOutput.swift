import Foundation
import HearsayCore
import Observation

/// Writing transcripts into the output folder (PLAN.md 4.1 step 6, 4.2 step 3).
enum TranscriptOutput {
    /// First of `base`, `base-2`, `base-3`, ... for which no `stem + suffix`
    /// exists in `directory` (the same rule as `OutputWriter` and
    /// `RecordingSpool`).
    static func freeStem(base: String, directory: URL, suffixes: [String]) -> String {
        let fileManager = FileManager.default
        var candidate = base
        var sequence = 2
        while suffixes.contains(where: {
            fileManager.fileExists(atPath: directory.appendingPathComponent(candidate + $0).path)
        }) {
            candidate = "\(base)-\(sequence)"
            sequence += 1
        }
        return candidate
    }

    /// Renders `cues` with `SRT.render` and writes them to `url` atomically,
    /// replacing a file that is already there.
    static func writeSRT(_ cues: [CoreSegment], to url: URL) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true
        )
        try Data(SRT.render(cues).utf8).write(to: url, options: [.atomic])
    }

    /// Resolves the output folder, persisting a refreshed bookmark. The
    /// caller must call `stopAccessing()` on the result.
    @MainActor
    static func resolveFolder(settings: AppSettings) throws -> ResolvedOutputFolder {
        let folder = try OutputLocation.resolve(bookmark: settings.outputFolderBookmark)
        if let refreshed = folder.refreshedBookmark {
            settings.outputFolderBookmark = refreshed
        }
        return folder
    }
}

/// Runs the notes flow (PLAN.md 4.3) for a freshly written SRT and keeps the
/// output folder's security scope open while it runs, so the renames and
/// Markdown writes are allowed. Shared by the Record and File tabs.
@MainActor
@Observable
final class NotesHandoff {
    private(set) var notes: NotesFlowViewModel?
    @ObservationIgnored private var folder: ResolvedOutputFolder?

    /// Starts the flow: the confirm sheet appears unless "Ask before
    /// sending" is off, in which case generation starts at once. The notes
    /// are written in `language`, the session's resolved language. A flow
    /// still waiting at the confirm sheet is replaced (the transcript was
    /// just rewritten in another language); one that is further along is
    /// left alone.
    func start(srtURL: URL, language: TranscriptLanguage, store: AIProviderStore, settings: AppSettings) {
        if let notes, notes.isRunning, notes.phase != .confirming { return }
        releaseFolder()
        folder = try? TranscriptOutput.resolveFolder(settings: settings)
        let model = NotesFlowViewModel(store: store)
        notes = model
        model.run(srtURL: srtURL, languageCode: language.code)
    }

    /// Clears a finished flow.
    func reset() {
        guard notes?.isRunning != true else { return }
        notes = nil
        releaseFolder()
    }

    private func releaseFolder() {
        folder?.stopAccessing()
        folder = nil
    }
}
