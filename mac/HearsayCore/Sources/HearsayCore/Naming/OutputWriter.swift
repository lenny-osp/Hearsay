import Foundation

public enum OutputWriterError: Error, LocalizedError, Equatable {
    /// The meeting name has no letters or digits left after sanitizing.
    case unusableMeetingName
    /// A rename failed. `unrestored` lists files whose rollback also failed,
    /// by their current path.
    case renameFailed(source: URL, destination: URL, reason: String, unrestored: [URL])
    /// Writing a Markdown document failed. The partial Markdown was removed
    /// and every rename (SRT and retained audio) rolled back; `unrestored`
    /// lists files whose rollback failed, by their current path.
    case writeFailed(url: URL, reason: String, unrestored: [URL])
    /// Moving the previous notes to the Trash failed (`replaceNamed`).
    /// Notes already trashed were put back; `unrestored` lists files whose
    /// restore failed, by their current path.
    case trashFailed(url: URL, reason: String, unrestored: [URL])

    public var errorDescription: String? {
        switch self {
        case .unusableMeetingName:
            return String(
                localized: "The meeting name contains no usable characters. Use English letters or digits.",
                bundle: .module, comment: "Naming error. File names use English letters and digits only.")
        case let .renameFailed(source, destination, reason, unrestored):
            let message = String(
                localized: "Unable to rename \(source.path) to \(destination.lastPathComponent): \(reason).",
                bundle: .module,
                comment: "Naming error. %1$@ is a file path, %2$@ a file name, %3$@ the system error message.")
            let restored = String(localized: "All files keep their original names.", bundle: .module,
                                  comment: "Follows a rename error: nothing was changed.")
            return message + " " + Self.restoreSentence(unrestored, otherwise: restored)
        case let .writeFailed(url, reason, unrestored):
            let message = String(
                localized: "Unable to write meeting notes (\(url.path)): \(reason).", bundle: .module,
                comment: "Naming error. %1$@ is a file path, %2$@ the system error message.")
            let restored = String(
                localized: "The transcript, recording and any earlier notes keep their original names.",
                bundle: .module, comment: "Follows a write error: nothing was renamed.")
            return message + " " + Self.restoreSentence(unrestored, otherwise: restored)
        case let .trashFailed(url, reason, unrestored):
            let message = String(
                localized: "Unable to move the current notes (\(url.path)) to the Trash: \(reason).",
                bundle: .module,
                comment: "Regenerate notes error. %1$@ is a file path, %2$@ the system error message.")
            let restored = String(localized: "The current notes are unchanged.", bundle: .module,
                                  comment: "Follows a Trash error: the old notes are still in place.")
            return message + " " + Self.restoreSentence(unrestored, otherwise: restored)
        }
    }

    /// `otherwise` when every file was restored, else the list of the files
    /// that were not.
    private static func restoreSentence(_ unrestored: [URL], otherwise: String) -> String {
        guard !unrestored.isEmpty else { return otherwise }
        let paths = unrestored.map(\.path).joined(separator: ", ")
        return String(localized: "These files could not be restored: \(paths).", bundle: .module,
                      comment: "Follows a file error. %@ is a comma-separated list of file paths.")
    }
}

/// Ports of `save_named_outputs` and `rename_transcription_outputs` from
/// whisper-tools `run_whisper.py`.
public enum OutputWriter {
    /// Recordings kept beside the SRT under the same base name.
    public static let retainedAudioExtensions = ["wav"]

    public static let notesFallbackHeading = "Meeting Notes"
    public static let transcriptFallbackHeading = "Structured Transcript"

    typealias Mover = (_ source: URL, _ destination: URL) throws -> Void
    typealias TextWriter = (_ text: String, _ destination: URL) throws -> Void
    /// Moves a file to the Trash and returns where it ended up (nil when the
    /// system does not say).
    typealias Trasher = (_ url: URL) throws -> URL?

    public typealias NamedOutputs = (srt: URL, markdown: URL, transcript: URL, companions: [URL])

    // MARK: - save_named_outputs

    /// Renames the SRT to `<stem>.srt` together with its same-basename
    /// retained audio (`<stem>.wav`), then writes `<stem>.md` and
    /// `<stem>_transcript.md` atomically. The stem is
    /// `<timestamp>_<meetingName>` plus `-N` when any of those names is taken.
    /// The timestamp is the given one, else the one in the SRT filename, else
    /// now.
    ///
    /// Deliberate departure from Python `save_named_outputs`, which renames
    /// only the SRT (PLAN.md 4.3 step 7): Hearsay may keep the WAV in the
    /// output folder before naming, and it must follow the SRT's new name.
    ///
    /// If a rename fails, completed renames are rolled back. If a write fails,
    /// the partial Markdown is removed and every rename rolled back. Either
    /// way the error is thrown.
    public static func saveNamed(
        srtURL: URL,
        meetingName: String,
        markdown: String,
        transcriptMarkdown: String,
        timestamp: String?
    ) throws -> NamedOutputs {
        try saveNamed(
            srtURL: srtURL,
            meetingName: meetingName,
            markdown: markdown,
            transcriptMarkdown: transcriptMarkdown,
            timestamp: timestamp,
            move: defaultMove,
            writeText: writeAtomically
        )
    }

    static func saveNamed(
        srtURL: URL,
        meetingName: String,
        markdown: String,
        transcriptMarkdown: String,
        timestamp: String?,
        move: Mover,
        writeText: TextWriter
    ) throws -> NamedOutputs {
        guard let safeName = FilenameSanitizer.sanitize(meetingName) else {
            throw OutputWriterError.unusableMeetingName
        }
        let notes = MeetingNameInserter.insert(
            into: markdown, meetingName: safeName, fallbackHeading: notesFallbackHeading
        )
        let transcript = MeetingNameInserter.insert(
            into: transcriptMarkdown, meetingName: safeName, fallbackHeading: transcriptFallbackHeading
        )

        let srt = srtURL.standardizedFileURL
        let directory = srt.deletingLastPathComponent()
        let sources = [srt] + retainedAudioFiles(srtURL: srt)
        let sourceSuffixes = sources.map(extensionSuffix)
        let outputTimestamp = timestamp
            ?? Timestamps.parse(fromFilename: srt.lastPathComponent)
            ?? Timestamps.now()
        let stem = freeStem(
            base: "\(outputTimestamp)_\(safeName)",
            directory: directory,
            suffixes: sourceSuffixes + [".md", "_transcript.md"]
        )

        let finalNotes = directory.appendingPathComponent(stem + ".md")
        let finalTranscript = directory.appendingPathComponent(stem + "_transcript.md")
        let renamed = try renameAll(sources: sources, suffixes: sourceSuffixes, stem: stem,
                                    directory: directory, move: move)

        var failed = finalNotes
        do {
            try writeText(notes, finalNotes)
            failed = finalTranscript
            try writeText(transcript, finalTranscript)
        } catch {
            for output in [finalNotes, finalTranscript] {
                try? FileManager.default.removeItem(at: output)
            }
            let unrestored = rollBack(renamed, move: move)
            throw OutputWriterError.writeFailed(url: failed, reason: reason(error), unrestored: unrestored)
        }
        let destinations = renamed.map(\.destination)
        return (destinations[0], finalNotes, finalTranscript, Array(destinations.dropFirst()))
    }

    // MARK: - Regenerate notes

    /// Replaces the notes of an existing meeting `<existingStem>.*` in
    /// `directory` (History > Regenerate notes). The new stem is
    /// `<timestamp>_<meetingName>` plus `-N` on collision, where the
    /// timestamp is the given one, else the one in `existingStem`, else now,
    /// and the entry's own current files (`<existingStem>.srt/.wav/.md/
    /// _transcript.md`) do not count as collisions. So the same name
    /// regenerates in place.
    ///
    /// Order: the old `.md` and `_transcript.md` go to the Trash, the SRT and
    /// WAV are renamed only when the stem changed, then the new Markdown is
    /// written atomically. On any failure the partial new Markdown is
    /// removed, renames are rolled back and the trashed notes are moved back
    /// from the Trash; the error lists what could not be restored.
    public static func replaceNamed(
        existingStem: String,
        directory: URL,
        meetingName: String,
        markdown: String,
        transcriptMarkdown: String,
        timestamp: String?
    ) throws -> NamedOutputs {
        try replaceNamed(
            existingStem: existingStem,
            directory: directory,
            meetingName: meetingName,
            markdown: markdown,
            transcriptMarkdown: transcriptMarkdown,
            timestamp: timestamp,
            move: defaultMove,
            writeText: writeAtomically,
            trash: defaultTrash
        )
    }

    static func replaceNamed(
        existingStem: String,
        directory: URL,
        meetingName: String,
        markdown: String,
        transcriptMarkdown: String,
        timestamp: String?,
        move: Mover,
        writeText: TextWriter,
        trash: Trasher
    ) throws -> NamedOutputs {
        guard let safeName = FilenameSanitizer.sanitize(meetingName) else {
            throw OutputWriterError.unusableMeetingName
        }
        let notes = MeetingNameInserter.insert(
            into: markdown, meetingName: safeName, fallbackHeading: notesFallbackHeading
        )
        let transcript = MeetingNameInserter.insert(
            into: transcriptMarkdown, meetingName: safeName, fallbackHeading: transcriptFallbackHeading
        )

        let folder = directory.standardizedFileURL
        let srt = folder.appendingPathComponent(existingStem + ".srt")
        let sources = [srt] + retainedAudioFiles(srtURL: srt)
        let sourceSuffixes = sources.map(extensionSuffix)
        let oldNotes = [".md", "_transcript.md"]
            .map { folder.appendingPathComponent(existingStem + $0) }
            .filter { exists($0) }
        let ownFiles = (sources + oldNotes).compactMap(fileIdentity)

        let outputTimestamp = timestamp
            ?? Timestamps.parse(fromFilename: existingStem)
            ?? Timestamps.now()
        let stem = freeStem(
            base: "\(outputTimestamp)_\(safeName)",
            directory: folder,
            suffixes: sourceSuffixes + [".md", "_transcript.md"],
            ignoring: ownFiles
        )
        let finalNotes = folder.appendingPathComponent(stem + ".md")
        let finalTranscript = folder.appendingPathComponent(stem + "_transcript.md")

        // 1. Old notes to the Trash.
        var trashed: [(original: URL, trashed: URL?)] = []
        for url in oldNotes {
            do {
                trashed.append((url, try trash(url)))
            } catch {
                let unrestored = restoreFromTrash(trashed, move: move)
                throw OutputWriterError.trashFailed(url: url, reason: reason(error), unrestored: unrestored)
            }
        }

        // 2. SRT and WAV follow a changed name.
        var renamed: [(source: URL, destination: URL)] = []
        if stem != existingStem {
            do {
                renamed = try renameAll(sources: sources, suffixes: sourceSuffixes, stem: stem,
                                        directory: folder, move: move)
            } catch let OutputWriterError.renameFailed(source, destination, failure, unrestored) {
                let notRestored = unrestored + restoreFromTrash(trashed, move: move)
                throw OutputWriterError.renameFailed(
                    source: source, destination: destination, reason: failure, unrestored: notRestored
                )
            }
        }

        // 3. New Markdown.
        var failed = finalNotes
        do {
            try writeText(notes, finalNotes)
            failed = finalTranscript
            try writeText(transcript, finalTranscript)
        } catch {
            for output in [finalNotes, finalTranscript] {
                try? FileManager.default.removeItem(at: output)
            }
            let unrestored = rollBack(renamed, move: move) + restoreFromTrash(trashed, move: move)
            throw OutputWriterError.writeFailed(url: failed, reason: reason(error), unrestored: unrestored)
        }
        let srtResult = renamed.first?.destination ?? srt
        let companions = renamed.isEmpty ? Array(sources.dropFirst()) : renamed.dropFirst().map(\.destination)
        return (srtResult, finalNotes, finalTranscript, companions)
    }

    /// Moves trashed notes back to their original paths, newest first.
    /// Returns the current paths of files that could not be restored (the
    /// original path when the Trash location is unknown).
    private static func restoreFromTrash(_ trashed: [(original: URL, trashed: URL?)], move: Mover) -> [URL] {
        var unrestored: [URL] = []
        for item in trashed.reversed() {
            guard let location = item.trashed else {
                unrestored.append(item.original)
                continue
            }
            do {
                try move(location, item.original)
            } catch {
                unrestored.append(location)
            }
        }
        return unrestored
    }

    // MARK: - rename_transcription_outputs

    /// Renames the SRT and its same-basename retained audio to
    /// `<timestamp>_<meetingName>` (plus `-N` on collision). The timestamp is
    /// the given one, else the SRT's own (name, birth time, modification
    /// time), else now. Returns the new SRT URL first, then the companions.
    /// If any rename fails, every completed rename is rolled back.
    public static func renameRetained(srtURL: URL, meetingName: String, timestamp: String?) throws -> [URL] {
        try renameRetained(srtURL: srtURL, meetingName: meetingName, timestamp: timestamp, move: defaultMove)
    }

    static func renameRetained(
        srtURL: URL,
        meetingName: String,
        timestamp: String?,
        move: Mover
    ) throws -> [URL] {
        guard let safeName = FilenameSanitizer.sanitize(meetingName) else {
            throw OutputWriterError.unusableMeetingName
        }
        let srt = srtURL.standardizedFileURL
        let directory = srt.deletingLastPathComponent()
        let sources = [srt] + retainedAudioFiles(srtURL: srt)
        let suffixes = sources.map(extensionSuffix)

        let outputTimestamp = timestamp
            ?? Timestamps.sourceFileTimestamp(url: srt)
            ?? Timestamps.now()
        let stem = freeStem(base: "\(outputTimestamp)_\(safeName)", directory: directory, suffixes: suffixes)
        return try renameAll(sources: sources, suffixes: suffixes, stem: stem, directory: directory, move: move)
            .map(\.destination)
    }

    // MARK: - Rename a History entry

    /// A History entry after `renameEntry`.
    public struct RenamedEntry: Equatable, Sendable {
        public let stem: String
        /// The entry's files under `stem`, in SRT, notes, transcript, audio
        /// order (only those that exist).
        public let files: [URL]
    }

    /// Every file suffix a History entry can have, in `HistoryEntry.files`
    /// order.
    static let entrySuffixes = [".srt", ".md", "_transcript.md"] + retainedAudioExtensions.map { "." + $0 }

    /// Renames every existing file of the History entry `<stem>.*` in
    /// `directory` (SRT, notes, structured transcript, WAV) to
    /// `<timestamp>_<meetingName>` (History > Rename, PLAN.md 4.8). The name
    /// is sanitized like every saved name. The timestamp is the one in
    /// `stem`, else the first file's birth or modification time (the
    /// `renameRetained` rule), else now. `-N` is added when any of the
    /// entry's names (all four, so a foreign file never joins the entry) is
    /// taken by a file that is not the entry's own. Renaming to the current
    /// name changes nothing.
    ///
    /// Notes and structured transcript whose first section has a
    /// `**Meeting Name:**` line get it rewritten to the new sanitized name
    /// (the value `saveNamed` writes), atomically after the moves; other
    /// Markdown is only renamed. On a failed move, completed moves are rolled
    /// back; on a failed write, rewritten files get their old text back and
    /// every move is rolled back. Either way the error lists what could not
    /// be restored.
    public static func renameEntry(stem: String, directory: URL, meetingName: String) throws -> RenamedEntry {
        try renameEntry(stem: stem, directory: directory, meetingName: meetingName,
                        move: defaultMove, writeText: writeReplacing)
    }

    static func renameEntry(
        stem: String,
        directory: URL,
        meetingName: String,
        move: Mover,
        writeText: TextWriter
    ) throws -> RenamedEntry {
        guard let safeName = FilenameSanitizer.sanitize(meetingName) else {
            throw OutputWriterError.unusableMeetingName
        }
        let folder = directory.standardizedFileURL
        let present = entrySuffixes.compactMap { suffix -> (url: URL, suffix: String)? in
            let url = folder.appendingPathComponent(stem + suffix)
            return isRegularFile(url) ? (url, suffix) : nil
        }
        let sources = present.map(\.url)
        guard let first = sources.first else { return RenamedEntry(stem: stem, files: []) }

        let outputTimestamp = Timestamps.parse(fromFilename: stem)
            ?? Timestamps.sourceFileTimestamp(url: first)
            ?? Timestamps.now()
        let newStem = freeStem(
            base: "\(outputTimestamp)_\(safeName)",
            directory: folder,
            suffixes: entrySuffixes,
            ignoring: sources.compactMap(fileIdentity)
        )
        guard newStem != stem else { return RenamedEntry(stem: stem, files: sources) }

        // Meeting-name lines to rewrite, read before anything moves.
        let headings = [".md": notesFallbackHeading, "_transcript.md": transcriptFallbackHeading]
        var rewrites: [(suffix: String, old: String, new: String)] = []
        for (url, suffix) in present {
            guard let heading = headings[suffix],
                  let old = try? String(contentsOf: url, encoding: .utf8),
                  MeetingNameInserter.hasMeetingName(old)
            else { continue }
            let new = MeetingNameInserter.insert(into: old, meetingName: safeName, fallbackHeading: heading)
            if new != old { rewrites.append((suffix, old, new)) }
        }

        let renamed = try renameAll(sources: sources, suffixes: present.map(\.suffix), stem: newStem,
                                    directory: folder, move: move)

        var written: [(url: URL, old: String)] = []
        for rewrite in rewrites {
            let url = folder.appendingPathComponent(newStem + rewrite.suffix)
            do {
                try writeText(rewrite.new, url)
                written.append((url, rewrite.old))
            } catch {
                var oldText: [URL] = []
                for done in written.reversed() {
                    do {
                        try writeText(done.old, done.url)
                    } catch {
                        oldText.append(done.url)
                    }
                }
                // Every file still moves back so the entry stays together; a
                // file that kept the new meeting-name line is reported by
                // the path it ends up at.
                let unmoved = rollBack(renamed, move: move)
                let unrestored = unmoved + oldText.compactMap { url in
                    guard !unmoved.contains(url) else { return nil }
                    return renamed.first(where: { $0.destination == url })?.source ?? url
                }
                throw OutputWriterError.writeFailed(url: url, reason: reason(error), unrestored: unrestored)
            }
        }
        return RenamedEntry(stem: newStem, files: renamed.map(\.destination))
    }

    private static func isRegularFile(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) && !isDirectory.boolValue
    }

    /// Renames each source to `stem + suffix` in order. On the first failure,
    /// rolls back the completed renames in reverse and throws `renameFailed`.
    private static func renameAll(
        sources: [URL],
        suffixes: [String],
        stem: String,
        directory: URL,
        move: Mover
    ) throws -> [(source: URL, destination: URL)] {
        var renamed: [(source: URL, destination: URL)] = []
        for (source, suffix) in zip(sources, suffixes) {
            let destination = directory.appendingPathComponent(stem + suffix)
            do {
                try move(source, destination)
            } catch {
                let unrestored = rollBack(renamed, move: move)
                throw OutputWriterError.renameFailed(
                    source: source, destination: destination, reason: reason(error), unrestored: unrestored
                )
            }
            renamed.append((source, destination))
        }
        return renamed
    }

    /// Moves every renamed file back, newest first. Returns the current paths
    /// of files that could not be restored.
    private static func rollBack(_ renamed: [(source: URL, destination: URL)], move: Mover) -> [URL] {
        var unrestored: [URL] = []
        for done in renamed.reversed() {
            do {
                try move(done.destination, done.source)
            } catch {
                unrestored.append(done.destination)
            }
        }
        return unrestored
    }

    private static func extensionSuffix(_ url: URL) -> String {
        url.pathExtension.isEmpty ? "" : "." + url.pathExtension
    }

    /// Port of `retained_audio_files`: regular files beside the SRT sharing
    /// its base name, one per extension in `retainedAudioExtensions`.
    public static func retainedAudioFiles(srtURL: URL) -> [URL] {
        let srt = srtURL.standardizedFileURL
        let directory = srt.deletingLastPathComponent()
        let stem = srt.deletingPathExtension().lastPathComponent
        return retainedAudioExtensions.compactMap { ext in
            let candidate = directory.appendingPathComponent("\(stem).\(ext)")
            var isDirectory: ObjCBool = false
            let exists = FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory)
            return exists && !isDirectory.boolValue ? candidate : nil
        }
    }

    // MARK: - Helpers

    /// First of `base`, `base-2`, `base-3`, ... for which no `stem + suffix`
    /// exists in `directory`. Files in `ignoring` (by file identity, so a
    /// case-only difference on a case-insensitive volume still matches) do
    /// not count as taken.
    static func freeStem(
        base: String,
        directory: URL,
        suffixes: [String],
        ignoring: [FileIdentity] = []
    ) -> String {
        func taken(_ url: URL) -> Bool {
            guard exists(url) else { return false }
            guard !ignoring.isEmpty, let identity = fileIdentity(url) else { return true }
            return !ignoring.contains(identity)
        }
        var candidate = base
        var sequence = 2
        while suffixes.contains(where: { taken(directory.appendingPathComponent(candidate + $0)) }) {
            candidate = "\(base)-\(sequence)"
            sequence += 1
        }
        return candidate
    }

    struct FileIdentity: Equatable {
        let device: Int
        let inode: Int
    }

    private static func fileIdentity(_ url: URL) -> FileIdentity? {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let device = (attributes[.systemNumber] as? NSNumber)?.intValue,
              let inode = (attributes[.systemFileNumber] as? NSNumber)?.intValue
        else { return nil }
        return FileIdentity(device: device, inode: inode)
    }

    private static func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    static func defaultMove(_ source: URL, _ destination: URL) throws {
        try FileManager.default.moveItem(at: source, to: destination)
    }

    static func defaultTrash(_ url: URL) throws -> URL? {
        var resulting: NSURL?
        try FileManager.default.trashItem(at: url, resultingItemURL: &resulting)
        return resulting as URL?
    }

    /// Writes to a hidden temp file in the destination directory, syncs it,
    /// then moves it into place. The temp file is removed on failure.
    static func writeAtomically(_ text: String, to destination: URL) throws {
        try writeThroughTemporary(text, to: destination) { temporary in
            try FileManager.default.moveItem(at: temporary, to: destination)
        }
    }

    /// Like `writeAtomically`, but atomically replaces a file already at
    /// `destination` (`rename(2)`), so a reader sees the old or the new text,
    /// never a partial one.
    static func writeReplacing(_ text: String, to destination: URL) throws {
        try writeThroughTemporary(text, to: destination) { temporary in
            guard rename(temporary.path, destination.path) == 0 else {
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
        }
    }

    private static func writeThroughTemporary(
        _ text: String,
        to destination: URL,
        place: (URL) throws -> Void
    ) throws {
        let directory = destination.deletingLastPathComponent()
        let temporary = directory.appendingPathComponent(
            ".\(destination.lastPathComponent).\(UUID().uuidString).tmp"
        )
        do {
            try Data(text.utf8).write(to: temporary, options: [.withoutOverwriting])
            do {
                let handle = try FileHandle(forUpdating: temporary)
                defer { try? handle.close() }
                try handle.synchronize()
            }
            try place(temporary)
        } catch {
            try? FileManager.default.removeItem(at: temporary)
            throw error
        }
    }

    private static func reason(_ error: Error) -> String {
        if let described = error as? LocalizedError, let text = described.errorDescription {
            return text
        }
        return (error as NSError).localizedDescription
    }
}
