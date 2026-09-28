import Foundation
import Testing
@testable import HearsayCore

struct HistoryIndexTests {
    private func makeFolder() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("HearsayHistoryTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func touch(_ folder: URL, _ name: String) throws {
        try Data("x".utf8).write(to: folder.appendingPathComponent(name))
    }

    /// Writes a 16 kHz mono WAV of `seconds` of silence with `WavWriter`.
    private func writeWav(_ url: URL, seconds: Double) throws {
        let writer = try WavWriter(url: url)
        try writer.append([Float](repeating: 0, count: Int(seconds * 16_000)))
        try writer.close()
    }

    @Test func groupsNamesSortsAndMeasuresDuration() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }

        // Full set with a meeting name.
        let full = "2026-09-28_10-00-00_weekly-sync"
        try touch(folder, full + ".srt")
        try touch(folder, full + ".md")
        try touch(folder, full + "_transcript.md")
        try writeWav(folder.appendingPathComponent(full + ".wav"), seconds: 1.5)
        // Timestamp-only set, newer.
        let plain = "2026-09-28_14-30-15"
        try touch(folder, plain + ".srt")
        try writeWav(folder.appendingPathComponent(plain + ".wav"), seconds: 0.25)
        // Lone notes file without a timestamp.
        try touch(folder, "notes.md")
        // Ignored: unrelated, hidden, partial and tmp files, a folder.
        try touch(folder, "readme.txt")
        try touch(folder, ".2026-09-28_10-00-00_weekly-sync.md.ABC.tmp")
        try touch(folder, ".DS_Store")
        try touch(folder, "2026-09-27_09-00-00.wav.partial")
        try touch(folder, "2026-09-27_09-00-00.srt.tmp")
        try FileManager.default.createDirectory(
            at: folder.appendingPathComponent("2026-01-01_00-00-00.wav"), withIntermediateDirectories: true
        )

        let entries = try HistoryIndex.scan(folder: folder)
        #expect(entries.map(\.stem) == [plain, full, "notes"])

        let newest = entries[0]
        #expect(newest.meetingName == nil)
        #expect(newest.srt?.lastPathComponent == plain + ".srt")
        #expect(newest.notes == nil)
        #expect(newest.transcript == nil)
        #expect(newest.audio?.lastPathComponent == plain + ".wav")
        #expect(newest.audioDuration == 0.25)

        let named = entries[1]
        #expect(named.id == full)
        #expect(named.meetingName == "weekly-sync")
        #expect(named.srt?.lastPathComponent == full + ".srt")
        #expect(named.notes?.lastPathComponent == full + ".md")
        #expect(named.transcript?.lastPathComponent == full + "_transcript.md")
        #expect(named.audio?.lastPathComponent == full + ".wav")
        #expect(named.audioDuration == 1.5)
        #expect(named.files.count == 4)

        let components = DateComponents(
            calendar: Calendar(identifier: .gregorian), timeZone: .current,
            year: 2026, month: 9, day: 28, hour: 10, minute: 0, second: 0
        )
        #expect(named.timestamp == components.date)

        let lone = entries[2]
        #expect(lone.timestamp == nil)
        #expect(lone.meetingName == nil)
        #expect(lone.notes?.lastPathComponent == "notes.md")
        #expect(lone.srt == nil && lone.audio == nil && lone.transcript == nil)
        #expect(lone.audioDuration == nil)
    }

    @Test func meetingNameRequiresLeadingTimestampAndName() {
        #expect(HistoryIndex.meetingName(stem: "2026-09-28_10-00-00_standup") == "standup")
        #expect(HistoryIndex.meetingName(stem: "2026-09-28_10-00-00_standup-2") == "standup-2")
        #expect(HistoryIndex.meetingName(stem: "2026-09-28_10-00-00") == nil)
        #expect(HistoryIndex.meetingName(stem: "2026-09-28_10-00-00-2") == nil)
        #expect(HistoryIndex.meetingName(stem: "2026-09-28_10-00-00_") == nil)
        #expect(HistoryIndex.meetingName(stem: "standup") == nil)
        #expect(HistoryIndex.meetingName(stem: "2026-13-28_10-00-00_bad-month") == nil)
    }

    @Test func timestampDateUsesTimestampRules() throws {
        let utc = try #require(TimeZone(identifier: "UTC"))
        let date = HistoryIndex.timestampDate(stem: "2026-09-28_10-00-00_x", timeZone: utc)
        #expect(date == Date(timeIntervalSince1970: 1_790_589_600))
        #expect(HistoryIndex.timestampDate(stem: "notes") == nil)
    }

    @Test func missingFolderThrows() {
        let missing = FileManager.default.temporaryDirectory
            .appendingPathComponent("HearsayHistoryMissing-\(UUID().uuidString)")
        #expect(throws: (any Error).self) { try HistoryIndex.scan(folder: missing) }
    }
}
