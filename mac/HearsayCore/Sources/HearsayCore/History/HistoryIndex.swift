import Foundation

/// One meeting in the output folder: every file sharing a stem
/// (`<stem>.srt`, `<stem>.md`, `<stem>_transcript.md`, `<stem>.wav`).
public struct HistoryEntry: Identifiable, Sendable, Equatable {
    /// The stem; unique within a folder.
    public var id: String { stem }
    public let stem: String
    /// Parsed from the timestamp embedded in the stem, in the local time zone.
    public let timestamp: Date?
    /// The part after `<timestamp>_`, nil for plain timestamp names.
    public let meetingName: String?
    public let srt: URL?
    public let notes: URL?
    public let transcript: URL?
    public let audio: URL?
    /// Duration of `audio`, nil when there is none or it is unreadable.
    public let audioDuration: TimeInterval?

    public init(
        stem: String,
        timestamp: Date?,
        meetingName: String?,
        srt: URL?,
        notes: URL?,
        transcript: URL?,
        audio: URL?,
        audioDuration: TimeInterval?
    ) {
        self.stem = stem
        self.timestamp = timestamp
        self.meetingName = meetingName
        self.srt = srt
        self.notes = notes
        self.transcript = transcript
        self.audio = audio
        self.audioDuration = audioDuration
    }

    /// Every existing file of the entry, in SRT, notes, transcript, audio order.
    public var files: [URL] {
        [srt, notes, transcript, audio].compactMap { $0 }
    }
}

/// Builds the History list from the output folder (PLAN.md section 10, Ship).
public enum HistoryIndex {
    private enum Kind {
        case srt, notes, transcript, audio
    }

    /// Suffixes in match order: `_transcript.md` before `.md`.
    private static let suffixes: [(suffix: String, kind: Kind)] = [
        ("_transcript.md", .transcript),
        (".md", .notes),
        (".srt", .srt),
        (".wav", .audio),
    ]

    /// Groups the regular files directly inside `folder` by stem, newest
    /// first (entries without a timestamp last, then by stem descending).
    /// Hidden files, `.partial`/`.tmp` files and unrelated extensions are
    /// ignored.
    public static func scan(folder: URL) throws -> [HistoryEntry] {
        let fileManager = FileManager.default
        let urls = try fileManager.contentsOfDirectory(
            at: folder,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles, .skipsSubdirectoryDescendants]
        )
        var groups: [String: [Kind: URL]] = [:]
        for url in urls {
            let name = url.lastPathComponent
            guard !name.hasPrefix(".") else { continue }
            let lower = name.lowercased()
            guard !lower.hasSuffix(".partial"), !lower.hasSuffix(".tmp") else { continue }
            let isRegular = (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile ?? false
            guard isRegular else { continue }
            guard let match = suffixes.first(where: { lower.hasSuffix($0.suffix) }) else { continue }
            let stem = String(name.dropLast(match.suffix.count))
            guard !stem.isEmpty else { continue }
            groups[stem, default: [:]][match.kind] = url
        }
        return groups
            .map { stem, files in
                let audio = files[.audio]
                return HistoryEntry(
                    stem: stem,
                    timestamp: timestampDate(stem: stem),
                    meetingName: meetingName(stem: stem),
                    srt: files[.srt],
                    notes: files[.notes],
                    transcript: files[.transcript],
                    audio: audio,
                    audioDuration: audio.flatMap { try? WavWriter.duration(of: $0) }
                )
            }
            .sorted(by: isNewer)
    }

    /// The meeting name in `<yyyy-MM-dd_HH-mm-ss>_<name>`; nil when the stem
    /// does not start with a timestamp followed by `_` and a name.
    public static func meetingName(stem: String) -> String? {
        let prefixLength = Timestamps.format.count
        guard stem.count > prefixLength + 1 else { return nil }
        let prefix = String(stem.prefix(prefixLength))
        guard Timestamps.parse(fromFilename: prefix) == prefix else { return nil }
        let rest = stem.dropFirst(prefixLength)
        guard rest.first == "_" else { return nil }
        let name = String(rest.dropFirst())
        return name.isEmpty ? nil : name
    }

    /// The first embedded timestamp in `stem` (the `Timestamps` rules) as a
    /// local-time date.
    public static func timestampDate(stem: String, timeZone: TimeZone = .current) -> Date? {
        guard let text = Timestamps.parse(fromFilename: stem) else { return nil }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.timeZone = timeZone
        formatter.dateFormat = Timestamps.format
        return formatter.date(from: text)
    }

    private static func isNewer(_ lhs: HistoryEntry, _ rhs: HistoryEntry) -> Bool {
        switch (lhs.timestamp, rhs.timestamp) {
        case let (left?, right?) where left != right:
            return left > right
        case (.some, nil):
            return true
        case (nil, .some):
            return false
        default:
            return lhs.stem > rhs.stem
        }
    }
}
