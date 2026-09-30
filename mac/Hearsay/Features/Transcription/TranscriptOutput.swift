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

    /// A recording's WAV while its transcript is made: still in the spool
    /// (after a recording) or already in the output folder (after a failed
    /// pass or a capture failure).
    struct PendingRecording: Equatable {
        var url: URL
        var inSpool: Bool
    }

    /// Where `saveTranscript` put the files.
    struct SavedTranscript: Equatable {
        var srt: URL
        /// The kept recording, or nil when "Keep the recording" is off.
        var wav: URL?
        /// Set when the WAV could not be moved and stays in the spool.
        var notice: String?
    }

    /// Why `saveTranscript` wrote nothing; the caller then keeps the
    /// recording with `keepAfterFailure`.
    struct SaveFailure: Error, Equatable {
        var message: String
    }

    /// Writes `<stem>.srt` into the output folder beside the recording and
    /// then keeps or deletes the WAV as `keepRecording` says (PLAN.md 4.1
    /// steps 4 and 6). A recording still in the spool gets a free stem in
    /// the output folder; one already in the output folder (a retry) keeps
    /// its stem, and its SRT (possibly the saved live preview) is replaced.
    @MainActor
    static func saveTranscript(
        _ cues: [CoreSegment], recording: PendingRecording, keepRecording: Bool, settings: AppSettings
    ) -> Result<SavedTranscript, SaveFailure> {
        let folder: ResolvedOutputFolder
        do {
            folder = try resolveFolder(settings: settings)
        } catch {
            return .failure(SaveFailure(message: String(
                localized: "Could not open the output folder: \(error.localizedDescription)",
                comment: "Error. %@ is the system error message.")))
        }
        defer { folder.stopAccessing() }
        let fileManager = FileManager.default
        let source = recording.url
        guard fileManager.fileExists(atPath: source.path) else {
            return .failure(SaveFailure(message: String(localized: "The recording is missing.", comment: "Transcription error")))
        }
        let base = source.deletingPathExtension().lastPathComponent
        let directory = recording.inSpool ? folder.url : source.deletingLastPathComponent()
        let stem = recording.inSpool
            ? freeStem(base: base, directory: directory, suffixes: [".srt", ".wav"])
            : base
        let srt = directory.appendingPathComponent(stem + ".srt")
        do {
            try writeSRT(cues, to: srt)
        } catch {
            return .failure(SaveFailure(message: String(
                localized: "Could not write the transcript: \(error.localizedDescription)",
                comment: "Transcription error. %@ is the system error message.")))
        }

        var wav: URL?
        var notice: String?
        if keepRecording {
            if recording.inSpool {
                let destination = directory.appendingPathComponent(stem + ".wav")
                do {
                    try fileManager.moveItem(at: source, to: destination)
                    wav = destination
                } catch {
                    wav = source
                    notice = String(
                        localized: "Could not move the recording to the output folder: \(error.localizedDescription) It is kept at \(source.path).",
                        comment: "Record tab notice. %1$@ is the system error message, %2$@ a file path.")
                }
            } else {
                wav = source
            }
        } else {
            try? fileManager.removeItem(at: source)
        }
        return .success(SavedTranscript(srt: srt, wav: wav, notice: notice))
    }

    /// What `keepAfterFailure` kept.
    struct KeptRecording: Equatable {
        /// The full message: the reason plus where the files are.
        var message: String
        /// The kept WAV (in the output folder unless moving it failed).
        var recording: PendingRecording?
        /// The saved live preview, or the transcript that was already there.
        var srt: URL?
    }

    /// PLAN.md 4.1 failure rules: keeps the WAV (moved to the output folder
    /// when it is still in the spool), saves `liveSegments` as its SRT when
    /// there are any and no transcript exists yet, and appends the paths to
    /// `message`.
    @MainActor
    static func keepAfterFailure(
        _ message: String, recording: PendingRecording?, liveSegments: [CoreSegment],
        existingTranscript: URL?, settings: AppSettings, spool: RecordingSpool
    ) -> KeptRecording {
        var lines = [message]
        var kept = recording
        var srt = existingTranscript
        let folder = try? resolveFolder(settings: settings)
        defer { folder?.stopAccessing() }

        if let spoolWAV = recording, spoolWAV.inSpool, let folder {
            do {
                if let moved = try spool.finalize(spoolWAV.url, keep: true, outputFolder: folder.url) {
                    kept = PendingRecording(url: moved, inSpool: false)
                }
            } catch {
                lines.append(String(localized: "Could not move the recording to the output folder: \(error.localizedDescription)",
                                    comment: "Recording error. %@ is the system error message."))
            }
        }

        if let wav = kept?.url, !liveSegments.isEmpty, existingTranscript == nil {
            let live = wav.deletingPathExtension().appendingPathExtension("srt")
            do {
                try writeSRT(liveSegments, to: live)
                srt = live
                lines.append(String(localized: "The live preview was saved as \(live.path).",
                                    comment: "After a failed transcription. %@ is a file path."))
            } catch {
                lines.append(String(localized: "The live preview could not be saved: \(error.localizedDescription)",
                                    comment: "After a failed transcription. %@ is the system error message."))
            }
        }
        if let wav = kept?.url {
            lines.append(String(localized: "The recording is kept at \(wav.path).",
                                comment: "After a failed transcription. %@ is a file path."))
        }
        return KeptRecording(message: lines.joined(separator: "\n"), recording: kept, srt: srt)
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
    /// default to `language`, the session's resolved language; the confirm
    /// sheet can pick another for this run. A flow
    /// still waiting at the confirm sheet is replaced (the transcript was
    /// just rewritten in another language); one that is further along is
    /// left alone.
    func start(srtURL: URL, language: TranscriptLanguage, store: AIProviderStore, settings: AppSettings) {
        if let notes, notes.isRunning, notes.phase != .confirming { return }
        releaseFolder()
        folder = try? TranscriptOutput.resolveFolder(settings: settings)
        let model = NotesFlowViewModel(store: store)
        notes = model
        model.run(srtURL: srtURL, language: language)
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
