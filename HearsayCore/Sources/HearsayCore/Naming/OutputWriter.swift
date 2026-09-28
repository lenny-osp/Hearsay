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

    public var errorDescription: String? {
        switch self {
        case .unusableMeetingName:
            return "The meeting name contains no usable characters. Use English letters or digits."
        case let .renameFailed(source, destination, reason, unrestored):
            var message = "Unable to rename \(source.path) to \(destination.lastPathComponent): \(reason)."
            if unrestored.isEmpty {
                message += " All files keep their original names."
            } else {
                message += " These files could not be restored: "
                    + unrestored.map(\.path).joined(separator: ", ") + "."
            }
            return message
        case let .writeFailed(url, reason, unrestored):
            var message = "Unable to write meeting notes (\(url.path)): \(reason)."
            if unrestored.isEmpty {
                message += " The transcript and recording keep their original names."
            } else {
                message += " These files could not be restored: "
                    + unrestored.map(\.path).joined(separator: ", ") + "."
            }
            return message
        }
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
    /// exists in `directory`.
    static func freeStem(base: String, directory: URL, suffixes: [String]) -> String {
        var candidate = base
        var sequence = 2
        while suffixes.contains(where: { exists(directory.appendingPathComponent(candidate + $0)) }) {
            candidate = "\(base)-\(sequence)"
            sequence += 1
        }
        return candidate
    }

    private static func exists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    static func defaultMove(_ source: URL, _ destination: URL) throws {
        try FileManager.default.moveItem(at: source, to: destination)
    }

    /// Writes to a hidden temp file in the destination directory, syncs it,
    /// then moves it into place. The temp file is removed on failure.
    static func writeAtomically(_ text: String, to destination: URL) throws {
        let directory = destination.deletingLastPathComponent()
        let temporary = directory.appendingPathComponent(
            ".\(destination.lastPathComponent).\(UUID().uuidString).tmp"
        )
        do {
            try Data(text.utf8).write(to: temporary, options: [.withoutOverwriting])
            let handle = try FileHandle(forUpdating: temporary)
            defer { try? handle.close() }
            try handle.synchronize()
            try FileManager.default.moveItem(at: temporary, to: destination)
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
