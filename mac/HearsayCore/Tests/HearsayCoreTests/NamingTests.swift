import Foundation
import Testing
@testable import HearsayCore

// Ports of the naming and output-file tests in whisper-tools
// tests/test_run_whisper.py.

private struct TemporaryDirectory {
    let url: URL

    init() throws {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("NamingTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    func remove() {
        try? FileManager.default.removeItem(at: url)
    }

    func file(_ name: String) -> URL {
        url.appendingPathComponent(name)
    }

    func write(_ name: String, _ text: String) throws -> URL {
        let target = file(name)
        try Data(text.utf8).write(to: target)
        return target
    }

    func listing() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: url.path).sorted()
    }

    func read(_ name: String) throws -> String {
        try String(contentsOf: file(name), encoding: .utf8)
    }

    /// Mirrors `_recording()` in the Python test suite.
    func recording(stem: String = "2026-09-03_14-05-06") throws -> (srt: URL, wav: URL) {
        let srt = try write("\(stem).srt", "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n")
        let wav = file("\(stem).wav")
        try Data("RIFF".utf8).write(to: wav)
        return (srt, wav)
    }
}

private struct SimulatedFailure: Error, LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private func exists(_ url: URL) -> Bool {
    FileManager.default.fileExists(atPath: url.path)
}

// MARK: - FilenameSanitizer

@Suite struct FilenameSanitizerTests {
    // test_meeting_names_are_lowercase_ascii_and_filename_safe
    @Test(arguments: [
        (" Biweekly Cross Country Resource Management ", "biweekly-cross-country-resource-management"),
        ("Planning / Budget: Q4? <2026> | * Review\\Draft", "planning-budget-q4-2026-review-draft"),
        ("Café__ＲＥＶＩＥＷ\t會議 😀.MD", "cafe-review"),
        ("../../Launch---Plan.srt", "launch-plan"),
        ("會議😀 /:*?", nil),
        ("   ", nil),
        (String(repeating: "a", count: 100), String(repeating: "a", count: 80)),
    ] as [(String, String?)])
    func meetingNamesAreLowercaseAsciiAndFilenameSafe(value: String, expected: String?) {
        #expect(FilenameSanitizer.sanitize(value) == expected)
    }

    // sanitize assertion in test_ai_result_parsing_and_filename_sanitizing
    @Test func stripsMarkdownExtensionAndSlash() {
        #expect(FilenameSanitizer.sanitize(" Launch / Plan.md ") == "launch-plan")
    }

    // Expected values produced by running whisper-tools sanitize_ai_filename.
    @Test(arguments: [
        ("Straße Ölfeld", "stra-e-olfeld"),
        ("Ｑ４ ﬁnal", "q4-final"),
        ("İstanbul—Plan", "istanbul-plan"),
        ("x.Srt  ", "x"),
        ("notes.md.md", "notes-md"),
        ("-a-.txt", "a-txt"),
        ("日本 Meeting 2", "meeting-2"),
        ("ǅemal", "dzemal"),
    ] as [(String, String?)])
    func matchesPythonOnExtraInputs(value: String, expected: String?) {
        #expect(FilenameSanitizer.sanitize(value) == expected)
    }

    @Test func capDoesNotLeaveTrailingHyphen() {
        let value = String(repeating: "a", count: 79) + " b"
        #expect(FilenameSanitizer.sanitize(value) == String(repeating: "a", count: 79))
    }
}

// MARK: - MeetingNameInserter

@Suite struct MeetingNameInserterTests {
    // test_meeting_name_is_first_content_under_first_heading
    @Test(arguments: [
        "# Structured Transcript\n\nDiscuss launch\n\n## Decisions\nShip it.\n",
        "# Structured Transcript\n\n**Meeting Name:** old-name\n\nDiscuss launch\n\n## Decisions\nShip it.\n",
    ])
    func meetingNameIsFirstContentUnderFirstHeading(original: String) {
        let result = MeetingNameInserter.insert(
            into: original, meetingName: "launch-plan", fallbackHeading: "Structured Transcript"
        )
        #expect(result == "# Structured Transcript\n\n**Meeting Name:** launch-plan\n\nDiscuss launch\n\n## Decisions\nShip it.\n")
    }

    @Test func fallbackHeadingIsAddedWithoutHeading() {
        let result = MeetingNameInserter.insert(
            into: "Discuss launch", meetingName: "launch-plan", fallbackHeading: "Structured Transcript"
        )
        #expect(result == "# Structured Transcript\n\n**Meeting Name:** launch-plan\n\nDiscuss launch")
    }

    @Test func onlyTheFirstSectionLosesItsMeetingName() {
        let original = "# Notes\n**meeting name:** old\n## Later\n**Meeting Name:** keep\n"
        let result = MeetingNameInserter.insert(into: original, meetingName: "new", fallbackHeading: "Meeting Notes")
        #expect(result == "# Notes\n\n**Meeting Name:** new\n\n## Later\n**Meeting Name:** keep\n")
    }

    // Expected values produced by running whisper-tools insert_meeting_name.
    @Test(arguments: [
        ("  ## Agenda\r\nItem\r\n", "  ## Agenda\n\n**Meeting Name:** n\n\nItem\r\n"),
        ("Intro\n#NoSpace\n# Real\n.**Meeting Name:** x\nBody", "Intro\n#NoSpace\n# Real\n\n**Meeting Name:** n\n\nBody"),
        ("\n\n  text  ", "# F\n\n**Meeting Name:** n\n\ntext  "),
    ])
    func matchesPythonOnExtraInputs(original: String, expected: String) {
        #expect(MeetingNameInserter.insert(into: original, meetingName: "n", fallbackHeading: "F") == expected)
    }

    @Test func headingOnlyDocumentEndsAfterName() {
        let result = MeetingNameInserter.insert(into: "# Notes\n", meetingName: "x", fallbackHeading: "Meeting Notes")
        #expect(result == "# Notes\n\n**Meeting Name:** x\n")
    }
}

// MARK: - Timestamps

@Suite struct TimestampsTests {
    // test_source_file_timestamp_prefers_timestamp_in_filename
    @Test func sourceFileTimestampPrefersTimestampInFilename() {
        #expect(
            Timestamps.sourceFileTimestamp(url: URL(fileURLWithPath: "/recordings/2025-04-03_14-05-06_customer-call.wav"))
                == "2025-04-03_14-05-06"
        )
        #expect(
            Timestamps.sourceFileTimestamp(url: URL(fileURLWithPath: "/recordings/customer-call-2025-04-03-14-05-06.wav"))
                == "2025-04-03_14-05-06"
        )
    }

    @Test func parseRejectsImpossibleDatesAndAdjacentDigits() {
        #expect(Timestamps.parse(fromFilename: "20250403T140506.wav") == "2025-04-03_14-05-06")
        #expect(Timestamps.parse(fromFilename: "2025-02-30_14-05-06.wav") == nil)
        #expect(Timestamps.parse(fromFilename: "2025-04-03_24-05-06.wav") == nil)
        #expect(Timestamps.parse(fromFilename: "12025-04-03_14-05-06.wav") == nil)
        #expect(Timestamps.parse(fromFilename: "meeting.wav") == nil)
    }

    @Test func sourceFileTimestampFallsBackToBirthTime() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let file = try directory.write("customer-call.wav", "RIFF")
        var components = DateComponents()
        (components.year, components.month, components.day) = (2024, 1, 2)
        (components.hour, components.minute, components.second) = (3, 4, 5)
        let date = try #require(Calendar(identifier: .gregorian).date(from: components))
        try FileManager.default.setAttributes([.creationDate: date], ofItemAtPath: file.path)
        #expect(Timestamps.sourceFileTimestamp(url: file) == "2024-01-02_03-04-05")
    }

    @Test func missingFileWithoutEmbeddedTimestampHasNone() {
        #expect(Timestamps.sourceFileTimestamp(url: URL(fileURLWithPath: "/nonexistent-\(UUID().uuidString)/call.wav")) == nil)
    }

    @Test func formatUsesPosixPattern() {
        let date = Date(timeIntervalSince1970: 0)
        #expect(Timestamps.string(from: date, timeZone: TimeZone(identifier: "UTC") ?? .current) == "1970-01-01_00-00-00")
    }
}

// MARK: - OutputWriter.saveNamed

@Suite struct SaveNamedOutputsTests {
    @Test func savesAllThreeOutputsWithMeetingNameInserted() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("2026-09-03_14-05-06.srt", "1\n")

        let result = try OutputWriter.saveNamed(
            srtURL: srt, meetingName: "Launch Plan!", markdown: "# Notes\n\nBody\n",
            transcriptMarkdown: "Speaker: hi", timestamp: nil
        )
        #expect(result.srt.lastPathComponent == "2026-09-03_14-05-06_launch-plan.srt")
        #expect(result.markdown.lastPathComponent == "2026-09-03_14-05-06_launch-plan.md")
        #expect(result.transcript.lastPathComponent == "2026-09-03_14-05-06_launch-plan_transcript.md")
        #expect(try directory.listing() == [
            "2026-09-03_14-05-06_launch-plan.md",
            "2026-09-03_14-05-06_launch-plan.srt",
            "2026-09-03_14-05-06_launch-plan_transcript.md",
        ])
        #expect(try directory.read("2026-09-03_14-05-06_launch-plan.md") == "# Notes\n\n**Meeting Name:** launch-plan\n\nBody\n")
        #expect(
            try directory.read("2026-09-03_14-05-06_launch-plan_transcript.md")
                == "# Structured Transcript\n\n**Meeting Name:** launch-plan\n\nSpeaker: hi"
        )
    }

    @Test func givenTimestampWinsAndCollisionOnAnyNameAddsSuffix() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("2026-09-03_14-05-06.srt", "1\n")
        _ = try directory.write("2020-01-01_00-00-00_launch_transcript.md", "taken")

        let result = try OutputWriter.saveNamed(
            srtURL: srt, meetingName: "launch", markdown: "# N", transcriptMarkdown: "# T",
            timestamp: "2020-01-01_00-00-00"
        )
        #expect(result.srt.lastPathComponent == "2020-01-01_00-00-00_launch-2.srt")
        #expect(try directory.read("2020-01-01_00-00-00_launch_transcript.md") == "taken")
    }

    @Test func unusableNameThrowsAndTouchesNothing() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("meeting.srt", "1\n")
        #expect(throws: OutputWriterError.unusableMeetingName) {
            try OutputWriter.saveNamed(srtURL: srt, meetingName: "會議 ?", markdown: "# N",
                                       transcriptMarkdown: "# T", timestamp: nil)
        }
        #expect(try directory.listing() == ["meeting.srt"])
    }

    // Output-file safety from test_api_http_json_and_empty_content_failures_are_atomic:
    // a failed write leaves no partial Markdown, restores the SRT, and leaves
    // unrelated existing Markdown untouched.
    @Test func writeFailureRemovesPartialMarkdownAndRestoresSRT() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("meeting.srt", "1\n00:00:01,000 --> 00:00:02,000\nText\n")
        _ = try directory.write("meeting_summary.md", "old")

        let failingWriter: OutputWriter.TextWriter = { text, destination in
            if destination.lastPathComponent.hasSuffix("_transcript.md") {
                throw SimulatedFailure(message: "disk full")
            }
            try OutputWriter.writeAtomically(text, to: destination)
        }
        do {
            _ = try OutputWriter.saveNamed(
                srtURL: srt, meetingName: "launch", markdown: "# N", transcriptMarkdown: "# T",
                timestamp: "2026-09-03_14-05-06", move: OutputWriter.defaultMove, writeText: failingWriter
            )
            Issue.record("saveNamed should have thrown")
        } catch let error as OutputWriterError {
            guard case let .writeFailed(url, reason, unrestored) = error else {
                Issue.record("unexpected error \(error)")
                return
            }
            #expect(url.lastPathComponent == "2026-09-03_14-05-06_launch_transcript.md")
            #expect(reason == "disk full")
            #expect(unrestored.isEmpty)
            #expect(error.localizedDescription.contains("disk full"))
        }
        #expect(try directory.listing() == ["meeting.srt", "meeting_summary.md"])
        #expect(try directory.read("meeting_summary.md") == "old")
    }

    // Deliberate departure from Python (PLAN.md 4.3 step 7): the retained WAV
    // follows the SRT's new name.
    @Test func saveNamedRenamesASiblingWavTogetherWithTheSRT() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, wav) = try directory.recording()

        let result = try OutputWriter.saveNamed(
            srtURL: srt, meetingName: "Launch Plan", markdown: "# N", transcriptMarkdown: "# T", timestamp: nil
        )
        #expect(result.srt.lastPathComponent == "2026-09-03_14-05-06_launch-plan.srt")
        #expect(result.companions.map(\.lastPathComponent) == ["2026-09-03_14-05-06_launch-plan.wav"])
        #expect(!exists(srt))
        #expect(!exists(wav))
        #expect(try directory.listing() == [
            "2026-09-03_14-05-06_launch-plan.md",
            "2026-09-03_14-05-06_launch-plan.srt",
            "2026-09-03_14-05-06_launch-plan.wav",
            "2026-09-03_14-05-06_launch-plan_transcript.md",
        ])
        #expect(try directory.read("2026-09-03_14-05-06_launch-plan.wav") == "RIFF")
    }

    @Test func saveNamedWithoutWavHasNoCompanions() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("2026-09-03_14-05-06.srt", "1\n")

        let result = try OutputWriter.saveNamed(
            srtURL: srt, meetingName: "launch", markdown: "# N", transcriptMarkdown: "# T", timestamp: nil
        )
        #expect(result.companions.isEmpty)
        #expect(try directory.listing() == [
            "2026-09-03_14-05-06_launch.md",
            "2026-09-03_14-05-06_launch.srt",
            "2026-09-03_14-05-06_launch_transcript.md",
        ])
    }

    @Test func failingTranscriptWriteRestoresBothSRTAndWav() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, wav) = try directory.recording()

        let failingWriter: OutputWriter.TextWriter = { text, destination in
            if destination.lastPathComponent.hasSuffix("_transcript.md") {
                throw SimulatedFailure(message: "disk full")
            }
            try OutputWriter.writeAtomically(text, to: destination)
        }
        #expect(throws: OutputWriterError.writeFailed(
            url: directory.file("2026-09-03_14-05-06_launch_transcript.md").standardizedFileURL,
            reason: "disk full",
            unrestored: []
        )) {
            try OutputWriter.saveNamed(
                srtURL: srt, meetingName: "launch", markdown: "# N", transcriptMarkdown: "# T",
                timestamp: nil, move: OutputWriter.defaultMove, writeText: failingWriter
            )
        }
        #expect(exists(srt))
        #expect(exists(wav))
        #expect(try directory.listing() == ["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"])
    }

    @Test func failingWavRenameRollsBackTheSRTAndWritesNothing() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, wav) = try directory.recording()

        let failingMove: OutputWriter.Mover = { source, destination in
            if source.pathExtension == "wav" {
                throw SimulatedFailure(message: "device is busy")
            }
            try OutputWriter.defaultMove(source, destination)
        }
        #expect(throws: OutputWriterError.self) {
            try OutputWriter.saveNamed(
                srtURL: srt, meetingName: "launch", markdown: "# N", transcriptMarkdown: "# T",
                timestamp: nil, move: failingMove, writeText: OutputWriter.writeAtomically
            )
        }
        #expect(exists(srt))
        #expect(exists(wav))
        #expect(try directory.listing() == ["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"])
    }

    @Test func collisionOnTheWavNameAloneBumpsTheSuffixForAllFiles() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, _) = try directory.recording()
        _ = try directory.write("2026-09-03_14-05-06_launch.wav", "taken")

        let result = try OutputWriter.saveNamed(
            srtURL: srt, meetingName: "launch", markdown: "# N", transcriptMarkdown: "# T", timestamp: nil
        )
        #expect(result.srt.lastPathComponent == "2026-09-03_14-05-06_launch-2.srt")
        #expect(result.markdown.lastPathComponent == "2026-09-03_14-05-06_launch-2.md")
        #expect(result.transcript.lastPathComponent == "2026-09-03_14-05-06_launch-2_transcript.md")
        #expect(result.companions.map(\.lastPathComponent) == ["2026-09-03_14-05-06_launch-2.wav"])
        #expect(try directory.read("2026-09-03_14-05-06_launch.wav") == "taken")
    }

    @Test func atomicWriteLeavesNoTemporaryFiles() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try OutputWriter.writeAtomically("hello", to: directory.file("notes.md"))
        #expect(try directory.listing() == ["notes.md"])
        #expect(try directory.read("notes.md") == "hello")
    }
}

// MARK: - OutputWriter.replaceNamed (History > Regenerate notes)

/// Stands in for the Trash: moves files into a folder beside the meeting
/// folder and records them.
private final class FakeTrash: @unchecked Sendable {
    let folder: URL
    private(set) var trashed: [URL] = []

    init(root: TemporaryDirectory) throws {
        folder = root.url.appendingPathComponent(".FakeTrash", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    func trash(_ url: URL) throws -> URL? {
        let destination = folder.appendingPathComponent(url.lastPathComponent)
        try FileManager.default.moveItem(at: url, to: destination)
        trashed.append(destination)
        return destination
    }

    func listing() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted()
    }

    func read(_ name: String) throws -> String {
        try String(contentsOf: folder.appendingPathComponent(name), encoding: .utf8)
    }
}

@Suite struct ReplaceNamedOutputsTests {
    private static let stem = "2026-09-28_11-49-45_trip-to-genhe"

    /// A meeting with SRT, WAV, notes and structured transcript.
    private func meeting(in directory: TemporaryDirectory, stem: String = stem) throws {
        _ = try directory.write("\(stem).srt", "1\n")
        _ = try directory.write("\(stem).wav", "RIFF")
        _ = try directory.write("\(stem).md", "old notes")
        _ = try directory.write("\(stem)_transcript.md", "old transcript")
    }

    /// Meeting-folder listing without the fake Trash.
    private func listing(_ directory: TemporaryDirectory) throws -> [String] {
        try directory.listing().filter { $0 != ".FakeTrash" }
    }

    @Test func sameNameRegeneratesInPlaceWithoutSuffix() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let trash = try FakeTrash(root: directory)

        let result = try OutputWriter.replaceNamed(
            existingStem: Self.stem, directory: directory.url, meetingName: "trip-to-genhe",
            markdown: "# Notes\n\nNew\n", transcriptMarkdown: "# T\n\nNew T\n", timestamp: nil,
            move: OutputWriter.defaultMove, writeText: OutputWriter.writeAtomically, trash: trash.trash
        )
        #expect(result.srt.lastPathComponent == "\(Self.stem).srt")
        #expect(result.markdown.lastPathComponent == "\(Self.stem).md")
        #expect(result.transcript.lastPathComponent == "\(Self.stem)_transcript.md")
        #expect(result.companions.map(\.lastPathComponent) == ["\(Self.stem).wav"])
        #expect(try listing(directory) == [
            "\(Self.stem).md", "\(Self.stem).srt", "\(Self.stem).wav", "\(Self.stem)_transcript.md",
        ])
        #expect(try directory.read("\(Self.stem).md") == "# Notes\n\n**Meeting Name:** trip-to-genhe\n\nNew\n")
        #expect(try directory.read("\(Self.stem)_transcript.md") == "# T\n\n**Meeting Name:** trip-to-genhe\n\nNew T\n")
        #expect(try directory.read("\(Self.stem).wav") == "RIFF")
    }

    @Test func oldNotesEndUpInTheTrash() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let trash = try FakeTrash(root: directory)

        _ = try OutputWriter.replaceNamed(
            existingStem: Self.stem, directory: directory.url, meetingName: "trip-to-genhe",
            markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
            move: OutputWriter.defaultMove, writeText: OutputWriter.writeAtomically, trash: trash.trash
        )
        #expect(try trash.listing() == ["\(Self.stem).md", "\(Self.stem)_transcript.md"])
        #expect(try trash.read("\(Self.stem).md") == "old notes")
        #expect(try trash.read("\(Self.stem)_transcript.md") == "old transcript")
    }

    @Test func newNameRenamesSRTAndWavAndReplacesTheNotes() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let trash = try FakeTrash(root: directory)

        let result = try OutputWriter.replaceNamed(
            existingStem: Self.stem, directory: directory.url, meetingName: "Genhe Road Trip",
            markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
            move: OutputWriter.defaultMove, writeText: OutputWriter.writeAtomically, trash: trash.trash
        )
        let newStem = "2026-09-28_11-49-45_genhe-road-trip"
        #expect(result.srt.lastPathComponent == "\(newStem).srt")
        #expect(result.companions.map(\.lastPathComponent) == ["\(newStem).wav"])
        #expect(try listing(directory) == [
            "\(newStem).md", "\(newStem).srt", "\(newStem).wav", "\(newStem)_transcript.md",
        ])
        #expect(try directory.read("\(newStem).srt") == "1\n")
        #expect(try directory.read("\(newStem).wav") == "RIFF")
        #expect(try directory.read("\(newStem).md") == "# N\n\n**Meeting Name:** genhe-road-trip\n")
        #expect(try trash.listing() == ["\(Self.stem).md", "\(Self.stem)_transcript.md"])
    }

    @Test func unrelatedFileWithTheNewNameStillBumpsTheSuffix() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        _ = try directory.write("2026-09-28_11-49-45_launch.md", "taken")
        let trash = try FakeTrash(root: directory)

        let result = try OutputWriter.replaceNamed(
            existingStem: Self.stem, directory: directory.url, meetingName: "launch",
            markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
            move: OutputWriter.defaultMove, writeText: OutputWriter.writeAtomically, trash: trash.trash
        )
        #expect(result.srt.lastPathComponent == "2026-09-28_11-49-45_launch-2.srt")
        #expect(result.markdown.lastPathComponent == "2026-09-28_11-49-45_launch-2.md")
        #expect(try directory.read("2026-09-28_11-49-45_launch.md") == "taken")
    }

    @Test func entryWithASuffixKeepsItWhenTheNameIsUnchanged() throws {
        // `<ts>_launch` belongs to another meeting, so this one is `-2`.
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let stem = "2026-09-28_11-49-45_launch-2"
        try meeting(in: directory, stem: stem)
        _ = try directory.write("2026-09-28_11-49-45_launch.srt", "other")
        let trash = try FakeTrash(root: directory)

        let result = try OutputWriter.replaceNamed(
            existingStem: stem, directory: directory.url, meetingName: "launch",
            markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
            move: OutputWriter.defaultMove, writeText: OutputWriter.writeAtomically, trash: trash.trash
        )
        #expect(result.srt.lastPathComponent == "\(stem).srt")
        #expect(try directory.read("2026-09-28_11-49-45_launch.srt") == "other")
    }

    @Test func failingWriteRestoresTheOldNotesAndNames() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let before = try listing(directory)
        let trash = try FakeTrash(root: directory)

        let failingWriter: OutputWriter.TextWriter = { text, destination in
            if destination.lastPathComponent.hasSuffix("_transcript.md") {
                throw SimulatedFailure(message: "disk full")
            }
            try OutputWriter.writeAtomically(text, to: destination)
        }
        let newStem = "2026-09-28_11-49-45_genhe-road-trip"
        #expect(throws: OutputWriterError.writeFailed(
            url: directory.url.standardizedFileURL.appendingPathComponent("\(newStem)_transcript.md"),
            reason: "disk full",
            unrestored: []
        )) {
            try OutputWriter.replaceNamed(
                existingStem: Self.stem, directory: directory.url, meetingName: "genhe road trip",
                markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
                move: OutputWriter.defaultMove, writeText: failingWriter, trash: trash.trash
            )
        }
        #expect(try listing(directory) == before)
        #expect(try directory.read("\(Self.stem).md") == "old notes")
        #expect(try directory.read("\(Self.stem)_transcript.md") == "old transcript")
        #expect(try trash.listing().isEmpty)
    }

    @Test func failingWriteWithTheSameNameRestoresTheOldNotes() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let trash = try FakeTrash(root: directory)

        let failingWriter: OutputWriter.TextWriter = { text, destination in
            if destination.lastPathComponent.hasSuffix("_transcript.md") {
                throw SimulatedFailure(message: "disk full")
            }
            try OutputWriter.writeAtomically(text, to: destination)
        }
        #expect(throws: OutputWriterError.self) {
            try OutputWriter.replaceNamed(
                existingStem: Self.stem, directory: directory.url, meetingName: "trip-to-genhe",
                markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
                move: OutputWriter.defaultMove, writeText: failingWriter, trash: trash.trash
            )
        }
        #expect(try directory.read("\(Self.stem).md") == "old notes")
        #expect(try directory.read("\(Self.stem)_transcript.md") == "old transcript")
        #expect(try trash.listing().isEmpty)
    }

    @Test func unrestorableFilesAreNamedInTheError() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let trash = try FakeTrash(root: directory)

        let failingWriter: OutputWriter.TextWriter = { _, _ in throw SimulatedFailure(message: "disk full") }
        // Moving anything out of the fake Trash fails.
        let trashFolder = trash.folder.standardizedFileURL.path
        let move: OutputWriter.Mover = { source, destination in
            if source.standardizedFileURL.path.hasPrefix(trashFolder) {
                throw SimulatedFailure(message: "no access")
            }
            try OutputWriter.defaultMove(source, destination)
        }
        do {
            _ = try OutputWriter.replaceNamed(
                existingStem: Self.stem, directory: directory.url, meetingName: "trip-to-genhe",
                markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
                move: move, writeText: failingWriter, trash: trash.trash
            )
            Issue.record("replaceNamed should have thrown")
        } catch let error as OutputWriterError {
            guard case let .writeFailed(_, _, unrestored) = error else {
                Issue.record("unexpected error \(error)")
                return
            }
            #expect(unrestored.map(\.lastPathComponent).sorted() == ["\(Self.stem).md", "\(Self.stem)_transcript.md"])
            #expect(error.localizedDescription.contains("could not be restored"))
        }
    }

    @Test func failingTrashPutsBackWhatWasTrashedAndWritesNothing() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let before = try listing(directory)
        let trash = try FakeTrash(root: directory)
        let failingTrash: OutputWriter.Trasher = { url in
            if url.lastPathComponent.hasSuffix("_transcript.md") {
                throw SimulatedFailure(message: "Trash unavailable")
            }
            return try trash.trash(url)
        }
        #expect(throws: OutputWriterError.self) {
            try OutputWriter.replaceNamed(
                existingStem: Self.stem, directory: directory.url, meetingName: "new name",
                markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
                move: OutputWriter.defaultMove, writeText: OutputWriter.writeAtomically, trash: failingTrash
            )
        }
        #expect(try listing(directory) == before)
        #expect(try directory.read("\(Self.stem).md") == "old notes")
        #expect(try trash.listing().isEmpty)
    }

    @Test func failingWavRenameRestoresTheSRTAndTheOldNotes() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let before = try listing(directory)
        let trash = try FakeTrash(root: directory)
        let failingMove: OutputWriter.Mover = { source, destination in
            if source.pathExtension == "wav" {
                throw SimulatedFailure(message: "device is busy")
            }
            try OutputWriter.defaultMove(source, destination)
        }
        #expect(throws: OutputWriterError.self) {
            try OutputWriter.replaceNamed(
                existingStem: Self.stem, directory: directory.url, meetingName: "new name",
                markdown: "# N", transcriptMarkdown: "# T", timestamp: nil,
                move: failingMove, writeText: OutputWriter.writeAtomically, trash: trash.trash
            )
        }
        #expect(try listing(directory) == before)
        #expect(try directory.read("\(Self.stem)_transcript.md") == "old transcript")
        #expect(try trash.listing().isEmpty)
    }

    @Test func unusableNameThrowsAndTouchesNothing() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let before = try directory.listing()
        #expect(throws: OutputWriterError.unusableMeetingName) {
            try OutputWriter.replaceNamed(
                existingStem: Self.stem, directory: directory.url, meetingName: "會議",
                markdown: "# N", transcriptMarkdown: "# T", timestamp: nil
            )
        }
        #expect(try directory.listing() == before)
    }
}

// MARK: - OutputWriter.renameRetained

@Suite struct RenameRetainedTests {
    // test_renaming_moves_the_srt_and_the_retained_wav_together
    @Test func renamingMovesTheSRTAndTheRetainedWavTogether() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, wav) = try directory.recording()

        let renamed = try OutputWriter.renameRetained(
            srtURL: srt, meetingName: "Launch Plan!", timestamp: "2026-09-03_14-05-06"
        )
        #expect(renamed.map(\.lastPathComponent) == [
            "2026-09-03_14-05-06_launch-plan.srt",
            "2026-09-03_14-05-06_launch-plan.wav",
        ])
        #expect(!exists(srt))
        #expect(!exists(wav))
        #expect(try directory.listing() == [
            "2026-09-03_14-05-06_launch-plan.srt",
            "2026-09-03_14-05-06_launch-plan.wav",
        ])
    }

    // test_renaming_reuses_the_srt_timestamp_and_avoids_collisions
    @Test func renamingReusesTheSRTTimestampAndAvoidsCollisions() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, _) = try directory.recording(stem: "2026-09-03-14-05-06")
        for ext in [".srt", ".wav"] {
            _ = try directory.write("2026-09-03_14-05-06_launch\(ext)", "")
        }

        let renamed = try OutputWriter.renameRetained(srtURL: srt, meetingName: "launch", timestamp: nil)
        #expect(renamed.map(\.lastPathComponent) == [
            "2026-09-03_14-05-06_launch-2.srt",
            "2026-09-03_14-05-06_launch-2.wav",
        ])
    }

    // test_renaming_rolls_back_when_a_companion_cannot_be_renamed
    @Test func renamingRollsBackWhenACompanionCannotBeRenamed() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let (srt, wav) = try directory.recording()

        let failingMove: OutputWriter.Mover = { source, destination in
            if source.pathExtension == "wav" {
                throw SimulatedFailure(message: "device is busy")
            }
            try OutputWriter.defaultMove(source, destination)
        }
        do {
            _ = try OutputWriter.renameRetained(
                srtURL: srt, meetingName: "launch", timestamp: "2026-09-03_14-05-06", move: failingMove
            )
            Issue.record("renameRetained should have thrown")
        } catch let error as OutputWriterError {
            guard case let .renameFailed(source, _, reason, unrestored) = error else {
                Issue.record("unexpected error \(error)")
                return
            }
            #expect(source.lastPathComponent == wav.lastPathComponent)
            #expect(reason == "device is busy")
            #expect(unrestored.isEmpty)
            #expect(error.localizedDescription.contains("device is busy"))
        }
        #expect(exists(srt))
        #expect(exists(wav))
        #expect(try directory.listing() == ["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"])
    }

    // Same rollback, driven by a real filesystem failure: the WAV carries the
    // immutable flag, so its rename fails after the SRT was already moved.
    @Test func renamingRollsBackOnARealFilesystemFailure() throws {
        let directory = try TemporaryDirectory()
        defer {
            if let wav = try? directory.listing().first(where: { $0.hasSuffix(".wav") }) {
                try? FileManager.default.setAttributes([.immutable: false], ofItemAtPath: directory.file(wav).path)
            }
            directory.remove()
        }
        let (srt, wav) = try directory.recording()
        try FileManager.default.setAttributes([.immutable: true], ofItemAtPath: wav.path)

        #expect(throws: OutputWriterError.self) {
            try OutputWriter.renameRetained(srtURL: srt, meetingName: "launch", timestamp: "2026-09-03_14-05-06")
        }
        #expect(exists(srt))
        #expect(exists(wav))
        #expect(try directory.listing() == ["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"])
    }

    @Test func srtWithoutCompanionRenamesAlone() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("2026-09-03_14-05-06.srt", "1\n")
        try FileManager.default.createDirectory(at: directory.file("2026-09-03_14-05-06.wav"), withIntermediateDirectories: false)

        let renamed = try OutputWriter.renameRetained(srtURL: srt, meetingName: "launch", timestamp: nil)
        #expect(renamed.map(\.lastPathComponent) == ["2026-09-03_14-05-06_launch.srt"])
    }
}

// MARK: - OutputWriter.renameEntry (History > Rename)

// No Python counterpart: whisper-tools has no rename of a finished meeting.
// These follow the rules of the ported `rename_transcription_outputs` tests.
@Suite struct RenameEntryTests {
    private static let stem = "2026-09-28_15-44-12_history-of-coffee"
    private static let renamed = "2026-09-28_15-44-12_coffee-origins"
    private static let notes = "# Meeting Notes\n\n**Meeting Name:** history-of-coffee\n\n## Summary\n\n- Beans came from Ethiopia.\n"
    private static let transcript = "# Structured Transcript\n\n**Meeting Name:** history-of-coffee\n\n## Speaker 1\n\nHello.\n"

    /// A meeting with the given files, written as `saveNamed` writes them.
    private func meeting(
        in directory: TemporaryDirectory,
        stem: String = stem,
        suffixes: [String] = [".srt", ".md", "_transcript.md", ".wav"]
    ) throws {
        for suffix in suffixes {
            let text = switch suffix {
            case ".md": Self.notes
            case "_transcript.md": Self.transcript
            case ".wav": "RIFF"
            default: "1\n00:00:01,000 --> 00:00:02,000\nCoffee\n"
            }
            _ = try directory.write(stem + suffix, text)
        }
    }

    private func names(_ stem: String, _ suffixes: [String] = [".md", ".srt", ".wav", "_transcript.md"]) -> [String] {
        suffixes.map { stem + $0 }
    }

    @Test func renamesAllFourFilesAndTheMeetingNameLines() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)

        let result = try OutputWriter.renameEntry(
            stem: Self.stem, directory: directory.url, meetingName: "Coffee Origins"
        )
        #expect(result.stem == Self.renamed)
        #expect(result.files.map(\.lastPathComponent) == [
            "\(Self.renamed).srt", "\(Self.renamed).md", "\(Self.renamed)_transcript.md", "\(Self.renamed).wav",
        ])
        #expect(try directory.listing() == names(Self.renamed))
        #expect(try directory.read("\(Self.renamed).md")
            == "# Meeting Notes\n\n**Meeting Name:** coffee-origins\n\n## Summary\n\n- Beans came from Ethiopia.\n")
        #expect(try directory.read("\(Self.renamed)_transcript.md")
            == "# Structured Transcript\n\n**Meeting Name:** coffee-origins\n\n## Speaker 1\n\nHello.\n")
        #expect(try directory.read("\(Self.renamed).wav") == "RIFF")
    }

    @Test func renamesOnlyTheFilesThatExist() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory, suffixes: [".srt", ".wav"])

        let result = try OutputWriter.renameEntry(
            stem: Self.stem, directory: directory.url, meetingName: "coffee-origins"
        )
        #expect(result.files.map(\.lastPathComponent) == ["\(Self.renamed).srt", "\(Self.renamed).wav"])
        #expect(try directory.listing() == names(Self.renamed, [".srt", ".wav"]))
    }

    @Test func notesOnlyEntryIsRenamed() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory, suffixes: [".md"])

        let result = try OutputWriter.renameEntry(
            stem: Self.stem, directory: directory.url, meetingName: "coffee-origins"
        )
        #expect(result.files.map(\.lastPathComponent) == ["\(Self.renamed).md"])
        #expect(try directory.listing() == ["\(Self.renamed).md"])
    }

    @Test func collisionWithAnotherMeetingAddsSuffix() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        _ = try directory.write("\(Self.renamed).srt", "other")

        let result = try OutputWriter.renameEntry(
            stem: Self.stem, directory: directory.url, meetingName: "coffee-origins"
        )
        #expect(result.stem == "\(Self.renamed)-2")
        #expect(try directory.listing() == (["\(Self.renamed).srt"] + names("\(Self.renamed)-2")).sorted())
        #expect(try directory.read("\(Self.renamed).srt") == "other")
    }

    /// A foreign `<new>.wav` would join the renamed entry in History, so it
    /// is a collision even when the entry has no WAV.
    @Test func foreignFileOfAKindTheEntryLacksStillAddsSuffix() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory, suffixes: [".srt"])
        _ = try directory.write("\(Self.renamed).wav", "other")

        let result = try OutputWriter.renameEntry(
            stem: Self.stem, directory: directory.url, meetingName: "coffee-origins"
        )
        #expect(result.stem == "\(Self.renamed)-2")
    }

    @Test func sameNameIsANoOp() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        let failingMove: OutputWriter.Mover = { _, _ in throw SimulatedFailure(message: "must not move") }
        let failingWrite: OutputWriter.TextWriter = { _, _ in throw SimulatedFailure(message: "must not write") }

        let result = try OutputWriter.renameEntry(
            stem: Self.stem, directory: directory.url, meetingName: "History of Coffee",
            move: failingMove, writeText: failingWrite
        )
        #expect(result.stem == Self.stem)
        #expect(result.files.count == 4)
        #expect(try directory.listing() == names(Self.stem))
        #expect(try directory.read("\(Self.stem).md") == Self.notes)
    }

    /// An entry that already carries `-2` keeps it when renamed to its own
    /// name while the plain name is taken by another meeting.
    @Test func suffixedEntryKeepsItsNameWhenUnchanged() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        try meeting(in: directory, stem: "\(Self.stem)-2")

        let result = try OutputWriter.renameEntry(
            stem: "\(Self.stem)-2", directory: directory.url, meetingName: "history-of-coffee"
        )
        #expect(result.stem == "\(Self.stem)-2")
        #expect(try directory.listing() == (names(Self.stem) + names("\(Self.stem)-2")).sorted())
    }

    @Test func unusableNameThrowsAndChangesNothing() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)

        #expect(throws: OutputWriterError.unusableMeetingName) {
            try OutputWriter.renameEntry(stem: Self.stem, directory: directory.url, meetingName: "會議 !!")
        }
        #expect(try directory.listing() == names(Self.stem))
        #expect(try directory.read("\(Self.stem).md") == Self.notes)
    }

    @Test func failingThirdMoveRollsBackTheFirstTwo() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        var moves = 0
        let failingMove: OutputWriter.Mover = { source, destination in
            moves += 1
            if moves == 3 { throw SimulatedFailure(message: "device is busy") }
            try OutputWriter.defaultMove(source, destination)
        }

        do {
            _ = try OutputWriter.renameEntry(
                stem: Self.stem, directory: directory.url, meetingName: "coffee-origins",
                move: failingMove, writeText: OutputWriter.writeReplacing
            )
            Issue.record("renameEntry should have thrown")
        } catch let error as OutputWriterError {
            guard case let .renameFailed(source, destination, reason, unrestored) = error else {
                Issue.record("unexpected error \(error)")
                return
            }
            #expect(source.lastPathComponent == "\(Self.stem)_transcript.md")
            #expect(destination.lastPathComponent == "\(Self.renamed)_transcript.md")
            #expect(reason == "device is busy")
            #expect(unrestored.isEmpty)
        }
        #expect(try directory.listing() == names(Self.stem))
        #expect(try directory.read("\(Self.stem).md") == Self.notes)
    }

    @Test func unrestorableMoveIsNamedInTheError() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory, suffixes: [".srt", ".wav"])
        let failingMove: OutputWriter.Mover = { source, destination in
            // The WAV cannot move, and the SRT cannot move back.
            if source.pathExtension == "wav" || source.lastPathComponent.hasPrefix(Self.renamed) {
                throw SimulatedFailure(message: "device is busy")
            }
            try OutputWriter.defaultMove(source, destination)
        }

        do {
            _ = try OutputWriter.renameEntry(
                stem: Self.stem, directory: directory.url, meetingName: "coffee-origins",
                move: failingMove, writeText: OutputWriter.writeReplacing
            )
            Issue.record("renameEntry should have thrown")
        } catch let error as OutputWriterError {
            guard case let .renameFailed(_, _, _, unrestored) = error else {
                Issue.record("unexpected error \(error)")
                return
            }
            #expect(unrestored.map(\.lastPathComponent) == ["\(Self.renamed).srt"])
            #expect(error.localizedDescription.contains("\(Self.renamed).srt"))
        }
    }

    @Test func failingNotesWriteRestoresTextAndNames() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory)
        // The notes are rewritten first, then the transcript fails.
        let failingWrite: OutputWriter.TextWriter = { text, destination in
            if destination.lastPathComponent.hasSuffix("_transcript.md") {
                throw SimulatedFailure(message: "disk full")
            }
            try OutputWriter.writeReplacing(text, to: destination)
        }

        do {
            _ = try OutputWriter.renameEntry(
                stem: Self.stem, directory: directory.url, meetingName: "coffee-origins",
                move: OutputWriter.defaultMove, writeText: failingWrite
            )
            Issue.record("renameEntry should have thrown")
        } catch let error as OutputWriterError {
            guard case let .writeFailed(url, reason, unrestored) = error else {
                Issue.record("unexpected error \(error)")
                return
            }
            #expect(url.lastPathComponent == "\(Self.renamed)_transcript.md")
            #expect(reason == "disk full")
            #expect(unrestored.isEmpty)
        }
        #expect(try directory.listing() == names(Self.stem))
        #expect(try directory.read("\(Self.stem).md") == Self.notes)
        #expect(try directory.read("\(Self.stem)_transcript.md") == Self.transcript)
    }

    @Test func notesWithoutAMeetingNameLineAreOnlyRenamed() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let plain = "# Meeting Notes\n\n## Summary\n\n**Meeting Name:** in a later section\n"
        let noHeading = "Just text.\n**Meeting Name:** history-of-coffee\n"
        _ = try directory.write("\(Self.stem).srt", "1\n")
        _ = try directory.write("\(Self.stem).md", plain)
        _ = try directory.write("\(Self.stem)_transcript.md", noHeading)

        _ = try OutputWriter.renameEntry(stem: Self.stem, directory: directory.url, meetingName: "coffee-origins")
        #expect(try directory.read("\(Self.renamed).md") == plain)
        #expect(try directory.read("\(Self.renamed)_transcript.md") == noHeading)
    }

    @Test func stemWithoutTimestampUsesTheFirstFileBirthTime() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let srt = try directory.write("interview.srt", "1\n")
        _ = try directory.write("interview.wav", "RIFF")
        let birth = try #require(Calendar(identifier: .gregorian).date(
            from: DateComponents(timeZone: .current, year: 2026, month: 9, day: 3, hour: 14, minute: 5, second: 6)
        ))
        try FileManager.default.setAttributes([.creationDate: birth], ofItemAtPath: srt.path)

        let result = try OutputWriter.renameEntry(
            stem: "interview", directory: directory.url, meetingName: "Customer Interview"
        )
        #expect(result.stem == "2026-09-03_14-05-06_customer-interview")
        #expect(try directory.listing() == [
            "2026-09-03_14-05-06_customer-interview.srt", "2026-09-03_14-05-06_customer-interview.wav",
        ])
    }

    @Test func plainTimestampStemGetsTheName() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        try meeting(in: directory, stem: "2026-09-28_15-44-12", suffixes: [".srt", ".wav"])

        let result = try OutputWriter.renameEntry(
            stem: "2026-09-28_15-44-12", directory: directory.url, meetingName: "coffee-origins"
        )
        #expect(result.stem == Self.renamed)
    }

    @Test func replacingWriteLeavesNoTemporaryFiles() throws {
        let directory = try TemporaryDirectory()
        defer { directory.remove() }
        let target = try directory.write("notes.md", "old")
        try OutputWriter.writeReplacing("new", to: target)
        #expect(try directory.read("notes.md") == "new")
        #expect(try directory.listing() == ["notes.md"])
    }
}

@Suite struct MeetingNameDetectionTests {
    @Test(arguments: [
        ("# Notes\n\n**Meeting Name:** a\n\nBody\n", true),
        ("# Notes\n\n**meeting name:** a\n", true),
        ("# Notes\n\nBody\n\n## Next\n\n**Meeting Name:** a\n", false),
        ("**Meeting Name:** a\n\nNo heading\n", false),
        ("# Notes\n\nBody\n", false),
    ])
    func findsTheLineInsertReplaces(markdown: String, expected: Bool) {
        #expect(MeetingNameInserter.hasMeetingName(markdown) == expected)
    }
}

// MARK: - shared/naming-tests.json

/// The naming vectors both platforms run. Expected values come from the
/// whisper-tools Python CLI (`shared/scripts/make-naming-tests.py`).
private struct NamingVectors: Decodable {
    struct Sanitize: Decodable {
        let input: String
        let output: String?
    }

    struct Insert: Decodable {
        let markdown: String
        let name: String
        let fallbackHeading: String
        let output: String
    }

    struct TimestampCase: Decodable {
        let filename: String
        let timestamp: String?
    }

    struct OutputName: Decodable {
        let timestamp: String
        let name: String
        let existing: [String]
        let stem: String
    }

    let sanitize: [Sanitize]
    let insertMeetingName: [Insert]
    let timestampFromFilename: [TimestampCase]
    let outputNames: [OutputName]

    static func load() throws -> NamingVectors {
        try JSONDecoder().decode(NamingVectors.self, from: sharedData("naming-tests.json"))
    }
}

@Suite struct SharedNamingVectorTests {
    @Test func sanitize() throws {
        let vectors = try NamingVectors.load().sanitize
        #expect(!vectors.isEmpty)
        for vector in vectors {
            #expect(FilenameSanitizer.sanitize(vector.input) == vector.output, "input: \(vector.input.debugDescription)")
        }
    }

    @Test func insertMeetingName() throws {
        let vectors = try NamingVectors.load().insertMeetingName
        #expect(!vectors.isEmpty)
        for vector in vectors {
            let result = MeetingNameInserter.insert(
                into: vector.markdown, meetingName: vector.name, fallbackHeading: vector.fallbackHeading
            )
            #expect(result == vector.output, "markdown: \(vector.markdown.debugDescription)")
        }
    }

    @Test func timestampFromFilename() throws {
        let vectors = try NamingVectors.load().timestampFromFilename
        #expect(!vectors.isEmpty)
        for vector in vectors {
            #expect(Timestamps.parse(fromFilename: vector.filename) == vector.timestamp, "filename: \(vector.filename)")
            // Python source_file_timestamp on a path that does not exist.
            let missing = URL(fileURLWithPath: "/nonexistent-\(UUID().uuidString)").appendingPathComponent(vector.filename)
            #expect(Timestamps.sourceFileTimestamp(url: missing) == vector.timestamp, "filename: \(vector.filename)")
        }
    }

    /// Each vector: the existing files plus `source.srt` in an empty folder,
    /// then `saveNamed` with the given timestamp.
    @Test func outputNames() throws {
        let vectors = try NamingVectors.load().outputNames
        #expect(!vectors.isEmpty)
        for vector in vectors {
            let directory = try TemporaryDirectory()
            defer { directory.remove() }
            for name in vector.existing {
                _ = try directory.write(name, "")
            }
            let srt = try directory.write("source.srt", "1\n")
            let result = try OutputWriter.saveNamed(
                srtURL: srt, meetingName: vector.name, markdown: "# N", transcriptMarkdown: "# T",
                timestamp: vector.timestamp
            )
            #expect(result.srt.lastPathComponent == vector.stem + ".srt", "vector: \(vector.stem)")
            #expect(result.markdown.lastPathComponent == vector.stem + ".md")
            #expect(result.transcript.lastPathComponent == vector.stem + "_transcript.md")
            #expect(try directory.listing() == (vector.existing + [
                vector.stem + ".md", vector.stem + ".srt", vector.stem + "_transcript.md",
            ]).sorted())
        }
    }
}
