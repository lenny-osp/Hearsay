import Foundation

/// The folder where recordings are spooled while they are made (PLAN.md 4.1
/// step 4 and 4.5). The WAV is always written here first, so a crash never
/// loses audio; afterwards it is moved to the output folder or deleted.
public struct RecordingSpool: Sendable {
    public let root: URL

    public init(root: URL = RecordingSpool.defaultRoot()) {
        self.root = root
    }

    /// `~/Library/Application Support/Hearsay/Recording` (inside the app
    /// container when sandboxed, so no permission is needed).
    public static func defaultRoot(fileManager: FileManager = .default) -> URL {
        let support = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fileManager.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Application Support", isDirectory: true)
        return support
            .appendingPathComponent("Hearsay", isDirectory: true)
            .appendingPathComponent("Recording", isDirectory: true)
    }

    /// `<root>/<timestamp>.wav`, with `-2`, `-3`, ... when that name is taken.
    /// Pass `Timestamps.now()` for a new recording. The folder is created by
    /// `WavWriter` when the file is opened.
    public func newRecordingURL(timestamp: String) -> URL {
        let stem = OutputWriter.freeStem(base: timestamp, directory: root, suffixes: [".wav"])
        return root.appendingPathComponent(stem + ".wav")
    }

    /// Spooled WAVs whose header sizes are still 0 or that have no `.srt`
    /// sibling in the spool, sorted by name. Empty when the folder is missing.
    /// WAVs of jobs still pending in `queue.json` are left out: the
    /// transcription queue continues with them (PLAN.md 4.9).
    public func unfinishedRecordings() -> [URL] {
        let fileManager = FileManager.default
        guard let entries = try? fileManager.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]
        ) else { return [] }
        let queued = TranscriptionQueueStore(root: root).queuedWAVFileNames()
        return entries
            .filter { $0.pathExtension.lowercased() == "wav" }
            .filter { !queued.contains($0.lastPathComponent) }
            .filter { url in
                let unfinishedHeader = (try? WavWriter.hasUnfinishedHeader(at: url)) ?? false
                let srt = url.deletingPathExtension().appendingPathExtension("srt")
                return unfinishedHeader || !fileManager.fileExists(atPath: srt.path)
            }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    /// Moves the spooled WAV into `outputFolder` (creating it if needed, with
    /// a numeric suffix on collision) when `keep` is true and returns the new
    /// location; deletes it and returns nil otherwise.
    @discardableResult
    public func finalize(_ url: URL, keep: Bool, outputFolder: URL) throws -> URL? {
        let fileManager = FileManager.default
        guard keep else {
            try fileManager.removeItem(at: url)
            return nil
        }
        try fileManager.createDirectory(at: outputFolder, withIntermediateDirectories: true)
        let base = url.deletingPathExtension().lastPathComponent
        let stem = OutputWriter.freeStem(base: base, directory: outputFolder, suffixes: [".wav"])
        let destination = outputFolder.appendingPathComponent(stem + ".wav")
        try fileManager.moveItem(at: url, to: destination)
        return destination
    }
}
