using System.Text;
using System.Text.Json;
using Hearsay.Core.Naming;

namespace Hearsay.Tests.Naming;

// Port of mac/HearsayCore/Tests/HearsayCoreTests/NamingTests.swift (itself a
// port of the naming and output-file tests in whisper-tools
// tests/test_run_whisper.py). One class per Swift suite, same test names.

/// <summary>A scratch folder under the temp directory, deleted by <see cref="Dispose"/>.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public TemporaryDirectory(string prefix = "NamingTests")
    {
        Url = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():D}");
        Directory.CreateDirectory(Url);
    }

    public string Url { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Url, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string File(string name) => Path.Combine(Url, name);

    public string Write(string name, string text)
    {
        var target = File(name);
        System.IO.File.WriteAllBytes(target, Utf8.GetBytes(text));
        return target;
    }

    /// <summary>Names in the folder (files and folders), ordinal order like Swift's <c>sorted()</c>.</summary>
    public List<string> Listing() =>
        Directory.EnumerateFileSystemEntries(Url)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToList();

    public string Read(string name) => Utf8.GetString(System.IO.File.ReadAllBytes(File(name)));

    /// <summary>Mirrors <c>_recording()</c> in the Python test suite.</summary>
    public (string Srt, string Wav) Recording(string stem = "2026-09-03_14-05-06")
    {
        var srt = Write($"{stem}.srt", "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n");
        var wav = Write($"{stem}.wav", "RIFF");
        return (srt, wav);
    }
}

internal sealed class SimulatedFailure(string message) : Exception(message);

internal static class NamingAssert
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static T Error<T>(Action action)
        where T : OutputWriterError
    {
        var exception = Assert.Throws<OutputWriterException>(action);
        return Assert.IsType<T>(exception.Error);
    }

    public static string Name(string path) => Path.GetFileName(path);

    public static List<string> Names(IEnumerable<string> paths) => paths.Select(Name).ToList();

    public static List<string> Sorted(IEnumerable<string> names) => names.Order(StringComparer.Ordinal).ToList();
}

// MARK: - FilenameSanitizer

public class FilenameSanitizerTests
{
    // test_meeting_names_are_lowercase_ascii_and_filename_safe
    [Theory]
    [InlineData(" Biweekly Cross Country Resource Management ", "biweekly-cross-country-resource-management")]
    [InlineData("Planning / Budget: Q4? <2026> | * Review\\Draft", "planning-budget-q4-2026-review-draft")]
    [InlineData("Café__ＲＥＶＩＥＷ\t會議 😀.MD", "cafe-review")]
    [InlineData("../../Launch---Plan.srt", "launch-plan")]
    [InlineData("會議😀 /:*?", null)]
    [InlineData("   ", null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MeetingNamesAreLowercaseAsciiAndFilenameSafe(string value, string? expected)
    {
        Assert.Equal(expected, FilenameSanitizer.Sanitize(value));
    }

    // sanitize assertion in test_ai_result_parsing_and_filename_sanitizing
    [Fact]
    public void StripsMarkdownExtensionAndSlash()
    {
        Assert.Equal("launch-plan", FilenameSanitizer.Sanitize(" Launch / Plan.md "));
    }

    // Expected values produced by running whisper-tools sanitize_ai_filename.
    [Theory]
    [InlineData("Straße Ölfeld", "stra-e-olfeld")]
    [InlineData("Ｑ４ ﬁnal", "q4-final")]
    [InlineData("İstanbul—Plan", "istanbul-plan")]
    [InlineData("x.Srt  ", "x")]
    [InlineData("notes.md.md", "notes-md")]
    [InlineData("-a-.txt", "a-txt")]
    [InlineData("日本 Meeting 2", "meeting-2")]
    [InlineData("ǅemal", "dzemal")]
    public void MatchesPythonOnExtraInputs(string value, string? expected)
    {
        Assert.Equal(expected, FilenameSanitizer.Sanitize(value));
    }

    [Fact]
    public void CapDoesNotLeaveTrailingHyphen()
    {
        var value = new string('a', 79) + " b";
        Assert.Equal(new string('a', 79), FilenameSanitizer.Sanitize(value));
    }
}

// MARK: - MeetingNameInserter

public class MeetingNameInserterTests
{
    // test_meeting_name_is_first_content_under_first_heading
    [Theory]
    [InlineData("# Structured Transcript\n\nDiscuss launch\n\n## Decisions\nShip it.\n")]
    [InlineData("# Structured Transcript\n\n**Meeting Name:** old-name\n\nDiscuss launch\n\n## Decisions\nShip it.\n")]
    public void MeetingNameIsFirstContentUnderFirstHeading(string original)
    {
        var result = MeetingNameInserter.Insert(original, "launch-plan", "Structured Transcript");
        Assert.Equal(
            "# Structured Transcript\n\n**Meeting Name:** launch-plan\n\nDiscuss launch\n\n## Decisions\nShip it.\n",
            result);
    }

    [Fact]
    public void FallbackHeadingIsAddedWithoutHeading()
    {
        var result = MeetingNameInserter.Insert("Discuss launch", "launch-plan", "Structured Transcript");
        Assert.Equal("# Structured Transcript\n\n**Meeting Name:** launch-plan\n\nDiscuss launch", result);
    }

    [Fact]
    public void OnlyTheFirstSectionLosesItsMeetingName()
    {
        var original = "# Notes\n**meeting name:** old\n## Later\n**Meeting Name:** keep\n";
        var result = MeetingNameInserter.Insert(original, "new", "Meeting Notes");
        Assert.Equal("# Notes\n\n**Meeting Name:** new\n\n## Later\n**Meeting Name:** keep\n", result);
    }

    // Expected values produced by running whisper-tools insert_meeting_name.
    [Theory]
    [InlineData("  ## Agenda\r\nItem\r\n", "  ## Agenda\n\n**Meeting Name:** n\n\nItem\r\n")]
    [InlineData("Intro\n#NoSpace\n# Real\n.**Meeting Name:** x\nBody", "Intro\n#NoSpace\n# Real\n\n**Meeting Name:** n\n\nBody")]
    [InlineData("\n\n  text  ", "# F\n\n**Meeting Name:** n\n\ntext  ")]
    public void MatchesPythonOnExtraInputs(string original, string expected)
    {
        Assert.Equal(expected, MeetingNameInserter.Insert(original, "n", "F"));
    }

    [Fact]
    public void HeadingOnlyDocumentEndsAfterName()
    {
        var result = MeetingNameInserter.Insert("# Notes\n", "x", "Meeting Notes");
        Assert.Equal("# Notes\n\n**Meeting Name:** x\n", result);
    }
}

// MARK: - Timestamps

public class TimestampsTests
{
    // test_source_file_timestamp_prefers_timestamp_in_filename
    [Fact]
    public void SourceFileTimestampPrefersTimestampInFilename()
    {
        Assert.Equal("2025-04-03_14-05-06",
            Timestamps.SourceFileTimestamp(@"C:\recordings\2025-04-03_14-05-06_customer-call.wav"));
        Assert.Equal("2025-04-03_14-05-06",
            Timestamps.SourceFileTimestamp(@"C:\recordings\customer-call-2025-04-03-14-05-06.wav"));
    }

    [Fact]
    public void ParseRejectsImpossibleDatesAndAdjacentDigits()
    {
        Assert.Equal("2025-04-03_14-05-06", Timestamps.ParseFromFilename("20250403T140506.wav"));
        Assert.Null(Timestamps.ParseFromFilename("2025-02-30_14-05-06.wav"));
        Assert.Null(Timestamps.ParseFromFilename("2025-04-03_24-05-06.wav"));
        Assert.Null(Timestamps.ParseFromFilename("12025-04-03_14-05-06.wav"));
        Assert.Null(Timestamps.ParseFromFilename("meeting.wav"));
    }

    [Fact]
    public void SourceFileTimestampFallsBackToBirthTime()
    {
        using var directory = new TemporaryDirectory();
        var file = directory.Write("customer-call.wav", "RIFF");
        File.SetCreationTime(file, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local));
        Assert.Equal("2024-01-02_03-04-05", Timestamps.SourceFileTimestamp(file));
    }

    [Fact]
    public void MissingFileWithoutEmbeddedTimestampHasNone()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():D}", "call.wav");
        Assert.Null(Timestamps.SourceFileTimestamp(missing));
    }

    [Fact]
    public void FormatUsesPosixPattern()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(0);
        Assert.Equal("1970-01-01_00-00-00", Timestamps.FromDate(date, TimeZoneInfo.Utc));
    }
}

// MARK: - OutputWriter.SaveNamed

public class SaveNamedOutputsTests
{
    private static void FailingTranscriptWriter(string text, string destination)
    {
        if (destination.EndsWith("_transcript.md", StringComparison.Ordinal))
        {
            throw new SimulatedFailure("disk full");
        }
        OutputWriter.WriteAtomically(text, destination);
    }

    private static void FailingWavMove(string source, string destination)
    {
        if (Path.GetExtension(source) == ".wav")
        {
            throw new SimulatedFailure("device is busy");
        }
        OutputWriter.DefaultMove(source, destination);
    }

    [Fact]
    public void SavesAllThreeOutputsWithMeetingNameInserted()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("2026-09-03_14-05-06.srt", "1\n");

        var result = OutputWriter.SaveNamed(srt, "Launch Plan!", "# Notes\n\nBody\n", "Speaker: hi", null);
        Assert.Equal("2026-09-03_14-05-06_launch-plan.srt", NamingAssert.Name(result.Srt));
        Assert.Equal("2026-09-03_14-05-06_launch-plan.md", NamingAssert.Name(result.Markdown));
        Assert.Equal("2026-09-03_14-05-06_launch-plan_transcript.md", NamingAssert.Name(result.Transcript));
        Assert.Equal(
            [
                "2026-09-03_14-05-06_launch-plan.md",
                "2026-09-03_14-05-06_launch-plan.srt",
                "2026-09-03_14-05-06_launch-plan_transcript.md",
            ],
            directory.Listing());
        Assert.Equal("# Notes\n\n**Meeting Name:** launch-plan\n\nBody\n",
            directory.Read("2026-09-03_14-05-06_launch-plan.md"));
        Assert.Equal("# Structured Transcript\n\n**Meeting Name:** launch-plan\n\nSpeaker: hi",
            directory.Read("2026-09-03_14-05-06_launch-plan_transcript.md"));
    }

    [Fact]
    public void GivenTimestampWinsAndCollisionOnAnyNameAddsSuffix()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("2026-09-03_14-05-06.srt", "1\n");
        directory.Write("2020-01-01_00-00-00_launch_transcript.md", "taken");

        var result = OutputWriter.SaveNamed(srt, "launch", "# N", "# T", "2020-01-01_00-00-00");
        Assert.Equal("2020-01-01_00-00-00_launch-2.srt", NamingAssert.Name(result.Srt));
        Assert.Equal("taken", directory.Read("2020-01-01_00-00-00_launch_transcript.md"));
    }

    [Fact]
    public void UnusableNameThrowsAndTouchesNothing()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("meeting.srt", "1\n");
        NamingAssert.Error<OutputWriterError.UnusableMeetingName>(
            () => OutputWriter.SaveNamed(srt, "會議 ?", "# N", "# T", null));
        Assert.Equal(["meeting.srt"], directory.Listing());
    }

    // Output-file safety from test_api_http_json_and_empty_content_failures_are_atomic:
    // a failed write leaves no partial Markdown, restores the SRT, and leaves
    // unrelated existing Markdown untouched.
    [Fact]
    public void WriteFailureRemovesPartialMarkdownAndRestoresSRT()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("meeting.srt", "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        directory.Write("meeting_summary.md", "old");

        var exception = Assert.Throws<OutputWriterException>(() => OutputWriter.SaveNamed(
            srt, "launch", "# N", "# T", "2026-09-03_14-05-06", OutputWriter.DefaultMove, FailingTranscriptWriter));
        var error = Assert.IsType<OutputWriterError.WriteFailed>(exception.Error);
        Assert.Equal("2026-09-03_14-05-06_launch_transcript.md", NamingAssert.Name(error.Path));
        Assert.Equal("disk full", error.Reason);
        Assert.Empty(error.Unrestored);
        Assert.Contains("disk full", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["meeting.srt", "meeting_summary.md"], directory.Listing());
        Assert.Equal("old", directory.Read("meeting_summary.md"));
    }

    // Deliberate departure from Python (PLAN.md 4.3 step 7): the retained WAV
    // follows the SRT's new name.
    [Fact]
    public void SaveNamedRenamesASiblingWavTogetherWithTheSRT()
    {
        using var directory = new TemporaryDirectory();
        var (srt, wav) = directory.Recording();

        var result = OutputWriter.SaveNamed(srt, "Launch Plan", "# N", "# T", null);
        Assert.Equal("2026-09-03_14-05-06_launch-plan.srt", NamingAssert.Name(result.Srt));
        Assert.Equal(["2026-09-03_14-05-06_launch-plan.wav"], NamingAssert.Names(result.Companions));
        Assert.False(NamingAssert.Exists(srt));
        Assert.False(NamingAssert.Exists(wav));
        Assert.Equal(
            [
                "2026-09-03_14-05-06_launch-plan.md",
                "2026-09-03_14-05-06_launch-plan.srt",
                "2026-09-03_14-05-06_launch-plan.wav",
                "2026-09-03_14-05-06_launch-plan_transcript.md",
            ],
            directory.Listing());
        Assert.Equal("RIFF", directory.Read("2026-09-03_14-05-06_launch-plan.wav"));
    }

    [Fact]
    public void SaveNamedWithoutWavHasNoCompanions()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("2026-09-03_14-05-06.srt", "1\n");

        var result = OutputWriter.SaveNamed(srt, "launch", "# N", "# T", null);
        Assert.Empty(result.Companions);
        Assert.Equal(
            [
                "2026-09-03_14-05-06_launch.md",
                "2026-09-03_14-05-06_launch.srt",
                "2026-09-03_14-05-06_launch_transcript.md",
            ],
            directory.Listing());
    }

    [Fact]
    public void FailingTranscriptWriteRestoresBothSRTAndWav()
    {
        using var directory = new TemporaryDirectory();
        var (srt, wav) = directory.Recording();

        var error = NamingAssert.Error<OutputWriterError.WriteFailed>(() => OutputWriter.SaveNamed(
            srt, "launch", "# N", "# T", null, OutputWriter.DefaultMove, FailingTranscriptWriter));
        Assert.Equal(Path.GetFullPath(directory.File("2026-09-03_14-05-06_launch_transcript.md")), error.Path);
        Assert.Equal("disk full", error.Reason);
        Assert.Empty(error.Unrestored);
        Assert.True(NamingAssert.Exists(srt));
        Assert.True(NamingAssert.Exists(wav));
        Assert.Equal(["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"], directory.Listing());
    }

    [Fact]
    public void FailingWavRenameRollsBackTheSRTAndWritesNothing()
    {
        using var directory = new TemporaryDirectory();
        var (srt, wav) = directory.Recording();

        Assert.Throws<OutputWriterException>(() => OutputWriter.SaveNamed(
            srt, "launch", "# N", "# T", null, FailingWavMove, OutputWriter.WriteAtomically));
        Assert.True(NamingAssert.Exists(srt));
        Assert.True(NamingAssert.Exists(wav));
        Assert.Equal(["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"], directory.Listing());
    }

    [Fact]
    public void CollisionOnTheWavNameAloneBumpsTheSuffixForAllFiles()
    {
        using var directory = new TemporaryDirectory();
        var (srt, _) = directory.Recording();
        directory.Write("2026-09-03_14-05-06_launch.wav", "taken");

        var result = OutputWriter.SaveNamed(srt, "launch", "# N", "# T", null);
        Assert.Equal("2026-09-03_14-05-06_launch-2.srt", NamingAssert.Name(result.Srt));
        Assert.Equal("2026-09-03_14-05-06_launch-2.md", NamingAssert.Name(result.Markdown));
        Assert.Equal("2026-09-03_14-05-06_launch-2_transcript.md", NamingAssert.Name(result.Transcript));
        Assert.Equal(["2026-09-03_14-05-06_launch-2.wav"], NamingAssert.Names(result.Companions));
        Assert.Equal("taken", directory.Read("2026-09-03_14-05-06_launch.wav"));
    }

    [Fact]
    public void AtomicWriteLeavesNoTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        OutputWriter.WriteAtomically("hello", directory.File("notes.md"));
        Assert.Equal(["notes.md"], directory.Listing());
        Assert.Equal("hello", directory.Read("notes.md"));
    }
}

// MARK: - OutputWriter.ReplaceNamed (History > Regenerate notes)

/// <summary>
/// Stands in for the Recycle Bin: moves files into a folder beside the
/// meeting folder and records them.
/// </summary>
internal sealed class FakeTrash
{
    public FakeTrash(TemporaryDirectory root)
    {
        Folder = Path.Combine(root.Url, ".FakeTrash");
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public List<string> Trashed { get; } = [];

    public string? Trash(string path)
    {
        var destination = Path.Combine(Folder, Path.GetFileName(path));
        File.Move(path, destination);
        Trashed.Add(destination);
        return destination;
    }

    public List<string> Listing() =>
        Directory.EnumerateFileSystemEntries(Folder)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToList();

    public string Read(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Folder, name)));
}

public class ReplaceNamedOutputsTests
{
    private const string Stem = "2026-09-28_11-49-45_trip-to-genhe";

    /// <summary>A meeting with SRT, WAV, notes and structured transcript.</summary>
    private static void Meeting(TemporaryDirectory directory, string stem = Stem)
    {
        directory.Write($"{stem}.srt", "1\n");
        directory.Write($"{stem}.wav", "RIFF");
        directory.Write($"{stem}.md", "old notes");
        directory.Write($"{stem}_transcript.md", "old transcript");
    }

    /// <summary>Meeting-folder listing without the fake Recycle Bin.</summary>
    private static List<string> Listing(TemporaryDirectory directory) =>
        directory.Listing().Where(name => name != ".FakeTrash").ToList();

    private static void FailingTranscriptWriter(string text, string destination)
    {
        if (destination.EndsWith("_transcript.md", StringComparison.Ordinal))
        {
            throw new SimulatedFailure("disk full");
        }
        OutputWriter.WriteAtomically(text, destination);
    }

    [Fact]
    public void SameNameRegeneratesInPlaceWithoutSuffix()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var trash = new FakeTrash(directory);

        var result = OutputWriter.ReplaceNamed(Stem, directory.Url, "trip-to-genhe", "# Notes\n\nNew\n",
            "# T\n\nNew T\n", null, OutputWriter.DefaultMove, OutputWriter.WriteAtomically, trash.Trash);
        Assert.Equal($"{Stem}.srt", NamingAssert.Name(result.Srt));
        Assert.Equal($"{Stem}.md", NamingAssert.Name(result.Markdown));
        Assert.Equal($"{Stem}_transcript.md", NamingAssert.Name(result.Transcript));
        Assert.Equal([$"{Stem}.wav"], NamingAssert.Names(result.Companions));
        Assert.Equal([$"{Stem}.md", $"{Stem}.srt", $"{Stem}.wav", $"{Stem}_transcript.md"], Listing(directory));
        Assert.Equal("# Notes\n\n**Meeting Name:** trip-to-genhe\n\nNew\n", directory.Read($"{Stem}.md"));
        Assert.Equal("# T\n\n**Meeting Name:** trip-to-genhe\n\nNew T\n", directory.Read($"{Stem}_transcript.md"));
        Assert.Equal("RIFF", directory.Read($"{Stem}.wav"));
    }

    [Fact]
    public void OldNotesEndUpInTheTrash()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var trash = new FakeTrash(directory);

        OutputWriter.ReplaceNamed(Stem, directory.Url, "trip-to-genhe", "# N", "# T", null,
            OutputWriter.DefaultMove, OutputWriter.WriteAtomically, trash.Trash);
        Assert.Equal([$"{Stem}.md", $"{Stem}_transcript.md"], trash.Listing());
        Assert.Equal("old notes", trash.Read($"{Stem}.md"));
        Assert.Equal("old transcript", trash.Read($"{Stem}_transcript.md"));
    }

    [Fact]
    public void NewNameRenamesSRTAndWavAndReplacesTheNotes()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var trash = new FakeTrash(directory);

        var result = OutputWriter.ReplaceNamed(Stem, directory.Url, "Genhe Road Trip", "# N", "# T", null,
            OutputWriter.DefaultMove, OutputWriter.WriteAtomically, trash.Trash);
        const string newStem = "2026-09-28_11-49-45_genhe-road-trip";
        Assert.Equal($"{newStem}.srt", NamingAssert.Name(result.Srt));
        Assert.Equal([$"{newStem}.wav"], NamingAssert.Names(result.Companions));
        Assert.Equal([$"{newStem}.md", $"{newStem}.srt", $"{newStem}.wav", $"{newStem}_transcript.md"],
            Listing(directory));
        Assert.Equal("1\n", directory.Read($"{newStem}.srt"));
        Assert.Equal("RIFF", directory.Read($"{newStem}.wav"));
        Assert.Equal("# N\n\n**Meeting Name:** genhe-road-trip\n", directory.Read($"{newStem}.md"));
        Assert.Equal([$"{Stem}.md", $"{Stem}_transcript.md"], trash.Listing());
    }

    [Fact]
    public void UnrelatedFileWithTheNewNameStillBumpsTheSuffix()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        directory.Write("2026-09-28_11-49-45_launch.md", "taken");
        var trash = new FakeTrash(directory);

        var result = OutputWriter.ReplaceNamed(Stem, directory.Url, "launch", "# N", "# T", null,
            OutputWriter.DefaultMove, OutputWriter.WriteAtomically, trash.Trash);
        Assert.Equal("2026-09-28_11-49-45_launch-2.srt", NamingAssert.Name(result.Srt));
        Assert.Equal("2026-09-28_11-49-45_launch-2.md", NamingAssert.Name(result.Markdown));
        Assert.Equal("taken", directory.Read("2026-09-28_11-49-45_launch.md"));
    }

    [Fact]
    public void EntryWithASuffixKeepsItWhenTheNameIsUnchanged()
    {
        // `<ts>_launch` belongs to another meeting, so this one is `-2`.
        using var directory = new TemporaryDirectory();
        const string stem = "2026-09-28_11-49-45_launch-2";
        Meeting(directory, stem);
        directory.Write("2026-09-28_11-49-45_launch.srt", "other");
        var trash = new FakeTrash(directory);

        var result = OutputWriter.ReplaceNamed(stem, directory.Url, "launch", "# N", "# T", null,
            OutputWriter.DefaultMove, OutputWriter.WriteAtomically, trash.Trash);
        Assert.Equal($"{stem}.srt", NamingAssert.Name(result.Srt));
        Assert.Equal("other", directory.Read("2026-09-28_11-49-45_launch.srt"));
    }

    [Fact]
    public void FailingWriteRestoresTheOldNotesAndNames()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var before = Listing(directory);
        var trash = new FakeTrash(directory);

        const string newStem = "2026-09-28_11-49-45_genhe-road-trip";
        var error = NamingAssert.Error<OutputWriterError.WriteFailed>(() => OutputWriter.ReplaceNamed(
            Stem, directory.Url, "genhe road trip", "# N", "# T", null,
            OutputWriter.DefaultMove, FailingTranscriptWriter, trash.Trash));
        Assert.Equal(Path.Combine(Path.GetFullPath(directory.Url), $"{newStem}_transcript.md"), error.Path);
        Assert.Equal("disk full", error.Reason);
        Assert.Empty(error.Unrestored);
        Assert.Equal(before, Listing(directory));
        Assert.Equal("old notes", directory.Read($"{Stem}.md"));
        Assert.Equal("old transcript", directory.Read($"{Stem}_transcript.md"));
        Assert.Empty(trash.Listing());
    }

    [Fact]
    public void FailingWriteWithTheSameNameRestoresTheOldNotes()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var trash = new FakeTrash(directory);

        Assert.Throws<OutputWriterException>(() => OutputWriter.ReplaceNamed(
            Stem, directory.Url, "trip-to-genhe", "# N", "# T", null,
            OutputWriter.DefaultMove, FailingTranscriptWriter, trash.Trash));
        Assert.Equal("old notes", directory.Read($"{Stem}.md"));
        Assert.Equal("old transcript", directory.Read($"{Stem}_transcript.md"));
        Assert.Empty(trash.Listing());
    }

    [Fact]
    public void UnrestorableFilesAreNamedInTheError()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var trash = new FakeTrash(directory);

        // Moving anything out of the fake Recycle Bin fails.
        var trashFolder = Path.GetFullPath(trash.Folder);
        void Move(string source, string destination)
        {
            if (Path.GetFullPath(source).StartsWith(trashFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedFailure("no access");
            }
            OutputWriter.DefaultMove(source, destination);
        }

        var exception = Assert.Throws<OutputWriterException>(() => OutputWriter.ReplaceNamed(
            Stem, directory.Url, "trip-to-genhe", "# N", "# T", null,
            Move, (_, _) => throw new SimulatedFailure("disk full"), trash.Trash));
        var error = Assert.IsType<OutputWriterError.WriteFailed>(exception.Error);
        Assert.Equal([$"{Stem}.md", $"{Stem}_transcript.md"], NamingAssert.Sorted(NamingAssert.Names(error.Unrestored)));
        Assert.Contains("could not be restored", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailingTrashPutsBackWhatWasTrashedAndWritesNothing()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var before = Listing(directory);
        var trash = new FakeTrash(directory);
        string? FailingTrash(string path) =>
            path.EndsWith("_transcript.md", StringComparison.Ordinal)
                ? throw new SimulatedFailure("Trash unavailable")
                : trash.Trash(path);

        Assert.Throws<OutputWriterException>(() => OutputWriter.ReplaceNamed(
            Stem, directory.Url, "new name", "# N", "# T", null,
            OutputWriter.DefaultMove, OutputWriter.WriteAtomically, FailingTrash));
        Assert.Equal(before, Listing(directory));
        Assert.Equal("old notes", directory.Read($"{Stem}.md"));
        Assert.Empty(trash.Listing());
    }

    [Fact]
    public void FailingWavRenameRestoresTheSRTAndTheOldNotes()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var before = Listing(directory);
        var trash = new FakeTrash(directory);
        static void FailingMove(string source, string destination)
        {
            if (Path.GetExtension(source) == ".wav")
            {
                throw new SimulatedFailure("device is busy");
            }
            OutputWriter.DefaultMove(source, destination);
        }

        Assert.Throws<OutputWriterException>(() => OutputWriter.ReplaceNamed(
            Stem, directory.Url, "new name", "# N", "# T", null,
            FailingMove, OutputWriter.WriteAtomically, trash.Trash));
        Assert.Equal(before, Listing(directory));
        Assert.Equal("old transcript", directory.Read($"{Stem}_transcript.md"));
        Assert.Empty(trash.Listing());
    }

    [Fact]
    public void UnusableNameThrowsAndTouchesNothing()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var before = directory.Listing();
        NamingAssert.Error<OutputWriterError.UnusableMeetingName>(() => OutputWriter.ReplaceNamed(
            Stem, directory.Url, "會議", "# N", "# T", null));
        Assert.Equal(before, directory.Listing());
    }
}

// MARK: - OutputWriter.RenameRetained

public class RenameRetainedTests
{
    // test_renaming_moves_the_srt_and_the_retained_wav_together
    [Fact]
    public void RenamingMovesTheSRTAndTheRetainedWavTogether()
    {
        using var directory = new TemporaryDirectory();
        var (srt, wav) = directory.Recording();

        var renamed = OutputWriter.RenameRetained(srt, "Launch Plan!", "2026-09-03_14-05-06");
        Assert.Equal(["2026-09-03_14-05-06_launch-plan.srt", "2026-09-03_14-05-06_launch-plan.wav"],
            NamingAssert.Names(renamed));
        Assert.False(NamingAssert.Exists(srt));
        Assert.False(NamingAssert.Exists(wav));
        Assert.Equal(["2026-09-03_14-05-06_launch-plan.srt", "2026-09-03_14-05-06_launch-plan.wav"],
            directory.Listing());
    }

    // test_renaming_reuses_the_srt_timestamp_and_avoids_collisions
    [Fact]
    public void RenamingReusesTheSRTTimestampAndAvoidsCollisions()
    {
        using var directory = new TemporaryDirectory();
        var (srt, _) = directory.Recording("2026-09-03-14-05-06");
        foreach (var extension in new[] { ".srt", ".wav" })
        {
            directory.Write($"2026-09-03_14-05-06_launch{extension}", "");
        }

        var renamed = OutputWriter.RenameRetained(srt, "launch", null);
        Assert.Equal(["2026-09-03_14-05-06_launch-2.srt", "2026-09-03_14-05-06_launch-2.wav"],
            NamingAssert.Names(renamed));
    }

    // test_renaming_rolls_back_when_a_companion_cannot_be_renamed
    [Fact]
    public void RenamingRollsBackWhenACompanionCannotBeRenamed()
    {
        using var directory = new TemporaryDirectory();
        var (srt, wav) = directory.Recording();
        static void FailingMove(string source, string destination)
        {
            if (Path.GetExtension(source) == ".wav")
            {
                throw new SimulatedFailure("device is busy");
            }
            OutputWriter.DefaultMove(source, destination);
        }

        var exception = Assert.Throws<OutputWriterException>(
            () => OutputWriter.RenameRetained(srt, "launch", "2026-09-03_14-05-06", FailingMove));
        var error = Assert.IsType<OutputWriterError.RenameFailed>(exception.Error);
        Assert.Equal(NamingAssert.Name(wav), NamingAssert.Name(error.Source));
        Assert.Equal("device is busy", error.Reason);
        Assert.Empty(error.Unrestored);
        Assert.Contains("device is busy", exception.Message, StringComparison.Ordinal);
        Assert.True(NamingAssert.Exists(srt));
        Assert.True(NamingAssert.Exists(wav));
        Assert.Equal(["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"], directory.Listing());
    }

    // Same rollback, driven by a real file-system failure. The Mac sets the
    // immutable flag on the WAV; Windows has no such flag (read-only does not
    // block a rename), so the WAV is held open without FILE_SHARE_DELETE,
    // which makes its rename fail after the SRT was already moved.
    [Fact]
    public void RenamingRollsBackOnARealFilesystemFailure()
    {
        using var directory = new TemporaryDirectory();
        var (srt, wav) = directory.Recording();
        using (new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<OutputWriterException>(
                () => OutputWriter.RenameRetained(srt, "launch", "2026-09-03_14-05-06"));
        }
        Assert.True(NamingAssert.Exists(srt));
        Assert.True(NamingAssert.Exists(wav));
        Assert.Equal(["2026-09-03_14-05-06.srt", "2026-09-03_14-05-06.wav"], directory.Listing());
    }

    [Fact]
    public void SrtWithoutCompanionRenamesAlone()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("2026-09-03_14-05-06.srt", "1\n");
        Directory.CreateDirectory(directory.File("2026-09-03_14-05-06.wav"));

        var renamed = OutputWriter.RenameRetained(srt, "launch", null);
        Assert.Equal(["2026-09-03_14-05-06_launch.srt"], NamingAssert.Names(renamed));
    }
}

// MARK: - OutputWriter.RenameEntry (History > Rename)

// No Python counterpart: whisper-tools has no rename of a finished meeting.
// These follow the rules of the ported `rename_transcription_outputs` tests.
public class RenameEntryTests
{
    private const string Stem = "2026-09-28_15-44-12_history-of-coffee";
    private const string Renamed = "2026-09-28_15-44-12_coffee-origins";
    private const string Notes =
        "# Meeting Notes\n\n**Meeting Name:** history-of-coffee\n\n## Summary\n\n- Beans came from Ethiopia.\n";
    private const string Transcript =
        "# Structured Transcript\n\n**Meeting Name:** history-of-coffee\n\n## Speaker 1\n\nHello.\n";

    private static readonly string[] AllSuffixes = [".srt", ".md", "_transcript.md", ".wav"];

    /// <summary>A meeting with the given files, written as <c>SaveNamed</c> writes them.</summary>
    private static void Meeting(TemporaryDirectory directory, string stem = Stem, string[]? suffixes = null)
    {
        foreach (var suffix in suffixes ?? AllSuffixes)
        {
            var text = suffix switch
            {
                ".md" => Notes,
                "_transcript.md" => Transcript,
                ".wav" => "RIFF",
                _ => "1\n00:00:01,000 --> 00:00:02,000\nCoffee\n",
            };
            directory.Write(stem + suffix, text);
        }
    }

    private static List<string> Names(string stem, string[]? suffixes = null) =>
        (suffixes ?? [".md", ".srt", ".wav", "_transcript.md"]).Select(suffix => stem + suffix).ToList();

    [Fact]
    public void RenamesAllFourFilesAndTheMeetingNameLines()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);

        var result = OutputWriter.RenameEntry(Stem, directory.Url, "Coffee Origins");
        Assert.Equal(Renamed, result.Stem);
        Assert.Equal([$"{Renamed}.srt", $"{Renamed}.md", $"{Renamed}_transcript.md", $"{Renamed}.wav"],
            NamingAssert.Names(result.Files));
        Assert.Equal(Names(Renamed), directory.Listing());
        Assert.Equal(
            "# Meeting Notes\n\n**Meeting Name:** coffee-origins\n\n## Summary\n\n- Beans came from Ethiopia.\n",
            directory.Read($"{Renamed}.md"));
        Assert.Equal("# Structured Transcript\n\n**Meeting Name:** coffee-origins\n\n## Speaker 1\n\nHello.\n",
            directory.Read($"{Renamed}_transcript.md"));
        Assert.Equal("RIFF", directory.Read($"{Renamed}.wav"));
    }

    [Fact]
    public void RenamesOnlyTheFilesThatExist()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory, suffixes: [".srt", ".wav"]);

        var result = OutputWriter.RenameEntry(Stem, directory.Url, "coffee-origins");
        Assert.Equal([$"{Renamed}.srt", $"{Renamed}.wav"], NamingAssert.Names(result.Files));
        Assert.Equal(Names(Renamed, [".srt", ".wav"]), directory.Listing());
    }

    [Fact]
    public void NotesOnlyEntryIsRenamed()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory, suffixes: [".md"]);

        var result = OutputWriter.RenameEntry(Stem, directory.Url, "coffee-origins");
        Assert.Equal([$"{Renamed}.md"], NamingAssert.Names(result.Files));
        Assert.Equal([$"{Renamed}.md"], directory.Listing());
    }

    [Fact]
    public void CollisionWithAnotherMeetingAddsSuffix()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        directory.Write($"{Renamed}.srt", "other");

        var result = OutputWriter.RenameEntry(Stem, directory.Url, "coffee-origins");
        Assert.Equal($"{Renamed}-2", result.Stem);
        Assert.Equal(NamingAssert.Sorted([$"{Renamed}.srt", .. Names($"{Renamed}-2")]), directory.Listing());
        Assert.Equal("other", directory.Read($"{Renamed}.srt"));
    }

    /// <summary>
    /// A foreign <c>&lt;new&gt;.wav</c> would join the renamed entry in History,
    /// so it is a collision even when the entry has no WAV.
    /// </summary>
    [Fact]
    public void ForeignFileOfAKindTheEntryLacksStillAddsSuffix()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory, suffixes: [".srt"]);
        directory.Write($"{Renamed}.wav", "other");

        var result = OutputWriter.RenameEntry(Stem, directory.Url, "coffee-origins");
        Assert.Equal($"{Renamed}-2", result.Stem);
    }

    [Fact]
    public void SameNameIsANoOp()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);

        var result = OutputWriter.RenameEntry(Stem, directory.Url, "History of Coffee",
            (_, _) => throw new SimulatedFailure("must not move"),
            (_, _) => throw new SimulatedFailure("must not write"));
        Assert.Equal(Stem, result.Stem);
        Assert.Equal(4, result.Files.Count);
        Assert.Equal(Names(Stem), directory.Listing());
        Assert.Equal(Notes, directory.Read($"{Stem}.md"));
    }

    /// <summary>
    /// An entry that already carries <c>-2</c> keeps it when renamed to its own
    /// name while the plain name is taken by another meeting.
    /// </summary>
    [Fact]
    public void SuffixedEntryKeepsItsNameWhenUnchanged()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        Meeting(directory, $"{Stem}-2");

        var result = OutputWriter.RenameEntry($"{Stem}-2", directory.Url, "history-of-coffee");
        Assert.Equal($"{Stem}-2", result.Stem);
        Assert.Equal(NamingAssert.Sorted([.. Names(Stem), .. Names($"{Stem}-2")]), directory.Listing());
    }

    [Fact]
    public void UnusableNameThrowsAndChangesNothing()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);

        NamingAssert.Error<OutputWriterError.UnusableMeetingName>(
            () => OutputWriter.RenameEntry(Stem, directory.Url, "會議 !!"));
        Assert.Equal(Names(Stem), directory.Listing());
        Assert.Equal(Notes, directory.Read($"{Stem}.md"));
    }

    [Fact]
    public void FailingThirdMoveRollsBackTheFirstTwo()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        var moves = 0;
        void FailingMove(string source, string destination)
        {
            moves++;
            if (moves == 3)
            {
                throw new SimulatedFailure("device is busy");
            }
            OutputWriter.DefaultMove(source, destination);
        }

        var error = NamingAssert.Error<OutputWriterError.RenameFailed>(() => OutputWriter.RenameEntry(
            Stem, directory.Url, "coffee-origins", FailingMove, OutputWriter.WriteReplacing));
        Assert.Equal($"{Stem}_transcript.md", NamingAssert.Name(error.Source));
        Assert.Equal($"{Renamed}_transcript.md", NamingAssert.Name(error.Destination));
        Assert.Equal("device is busy", error.Reason);
        Assert.Empty(error.Unrestored);
        Assert.Equal(Names(Stem), directory.Listing());
        Assert.Equal(Notes, directory.Read($"{Stem}.md"));
    }

    [Fact]
    public void UnrestorableMoveIsNamedInTheError()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory, suffixes: [".srt", ".wav"]);
        static void FailingMove(string source, string destination)
        {
            // The WAV cannot move, and the SRT cannot move back.
            if (Path.GetExtension(source) == ".wav" || Path.GetFileName(source).StartsWith(Renamed, StringComparison.Ordinal))
            {
                throw new SimulatedFailure("device is busy");
            }
            OutputWriter.DefaultMove(source, destination);
        }

        var exception = Assert.Throws<OutputWriterException>(() => OutputWriter.RenameEntry(
            Stem, directory.Url, "coffee-origins", FailingMove, OutputWriter.WriteReplacing));
        var error = Assert.IsType<OutputWriterError.RenameFailed>(exception.Error);
        Assert.Equal([$"{Renamed}.srt"], NamingAssert.Names(error.Unrestored));
        Assert.Contains($"{Renamed}.srt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailingNotesWriteRestoresTextAndNames()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory);
        // The notes are rewritten first, then the transcript fails.
        static void FailingWrite(string text, string destination)
        {
            if (destination.EndsWith("_transcript.md", StringComparison.Ordinal))
            {
                throw new SimulatedFailure("disk full");
            }
            OutputWriter.WriteReplacing(text, destination);
        }

        var error = NamingAssert.Error<OutputWriterError.WriteFailed>(() => OutputWriter.RenameEntry(
            Stem, directory.Url, "coffee-origins", OutputWriter.DefaultMove, FailingWrite));
        Assert.Equal($"{Renamed}_transcript.md", NamingAssert.Name(error.Path));
        Assert.Equal("disk full", error.Reason);
        Assert.Empty(error.Unrestored);
        Assert.Equal(Names(Stem), directory.Listing());
        Assert.Equal(Notes, directory.Read($"{Stem}.md"));
        Assert.Equal(Transcript, directory.Read($"{Stem}_transcript.md"));
    }

    [Fact]
    public void NotesWithoutAMeetingNameLineAreOnlyRenamed()
    {
        using var directory = new TemporaryDirectory();
        const string plain = "# Meeting Notes\n\n## Summary\n\n**Meeting Name:** in a later section\n";
        const string noHeading = "Just text.\n**Meeting Name:** history-of-coffee\n";
        directory.Write($"{Stem}.srt", "1\n");
        directory.Write($"{Stem}.md", plain);
        directory.Write($"{Stem}_transcript.md", noHeading);

        OutputWriter.RenameEntry(Stem, directory.Url, "coffee-origins");
        Assert.Equal(plain, directory.Read($"{Renamed}.md"));
        Assert.Equal(noHeading, directory.Read($"{Renamed}_transcript.md"));
    }

    [Fact]
    public void StemWithoutTimestampUsesTheFirstFileBirthTime()
    {
        using var directory = new TemporaryDirectory();
        var srt = directory.Write("interview.srt", "1\n");
        directory.Write("interview.wav", "RIFF");
        File.SetCreationTime(srt, new DateTime(2026, 9, 3, 14, 5, 6, DateTimeKind.Local));

        var result = OutputWriter.RenameEntry("interview", directory.Url, "Customer Interview");
        Assert.Equal("2026-09-03_14-05-06_customer-interview", result.Stem);
        Assert.Equal(
            ["2026-09-03_14-05-06_customer-interview.srt", "2026-09-03_14-05-06_customer-interview.wav"],
            directory.Listing());
    }

    [Fact]
    public void PlainTimestampStemGetsTheName()
    {
        using var directory = new TemporaryDirectory();
        Meeting(directory, "2026-09-28_15-44-12", [".srt", ".wav"]);

        var result = OutputWriter.RenameEntry("2026-09-28_15-44-12", directory.Url, "coffee-origins");
        Assert.Equal(Renamed, result.Stem);
    }

    [Fact]
    public void ReplacingWriteLeavesNoTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var target = directory.Write("notes.md", "old");
        OutputWriter.WriteReplacing("new", target);
        Assert.Equal("new", directory.Read("notes.md"));
        Assert.Equal(["notes.md"], directory.Listing());
    }
}

public class MeetingNameDetectionTests
{
    [Theory]
    [InlineData("# Notes\n\n**Meeting Name:** a\n\nBody\n", true)]
    [InlineData("# Notes\n\n**meeting name:** a\n", true)]
    [InlineData("# Notes\n\nBody\n\n## Next\n\n**Meeting Name:** a\n", false)]
    [InlineData("**Meeting Name:** a\n\nNo heading\n", false)]
    [InlineData("# Notes\n\nBody\n", false)]
    public void FindsTheLineInsertReplaces(string markdown, bool expected)
    {
        Assert.Equal(expected, MeetingNameInserter.HasMeetingName(markdown));
    }
}

// MARK: - shared/naming-tests.json

/// <summary>
/// The naming vectors both platforms run. Expected values come from the
/// whisper-tools Python CLI (<c>shared/scripts/make-naming-tests.py</c>).
/// </summary>
internal sealed record NamingVectors(
    string About,
    List<NamingVectors.Sanitize> SanitizeCases,
    List<NamingVectors.Insert> InsertMeetingName,
    List<NamingVectors.TimestampCase> TimestampFromFilename,
    List<NamingVectors.OutputName> OutputNames)
{
    internal sealed record Sanitize(string Input, string? Output);

    internal sealed record Insert(string Markdown, string Name, string FallbackHeading, string Output);

    internal sealed record TimestampCase(string Filename, string? Timestamp);

    internal sealed record OutputName(string Timestamp, string Name, List<string> Existing, string Stem);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static NamingVectors Load()
    {
        // Byte-exact read: the vectors are LF and must not be translated.
        using var document = JsonDocument.Parse(File.ReadAllBytes(SharedFiles.Path("naming-tests.json")));
        var root = document.RootElement;
        T Section<T>(string name) =>
            root.GetProperty(name).Deserialize<T>(Options)
            ?? throw new InvalidDataException($"naming-tests.json: section {name} is empty");
        return new NamingVectors(
            root.GetProperty("about").GetString() ?? "",
            Section<List<Sanitize>>("sanitize"),
            Section<List<Insert>>("insertMeetingName"),
            Section<List<TimestampCase>>("timestampFromFilename"),
            Section<List<OutputName>>("outputNames"));
    }
}

public class SharedNamingVectorTests
{
    [Fact]
    public void About()
    {
        var about = NamingVectors.Load().About;
        Assert.Contains("save_named_outputs", about, StringComparison.Ordinal);
        Assert.Contains("PLAN.md 4.3 step 7", about, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize()
    {
        var vectors = NamingVectors.Load().SanitizeCases;
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            Assert.True(vector.Output == FilenameSanitizer.Sanitize(vector.Input),
                $"input: {JsonSerializer.Serialize(vector.Input)}, got {FilenameSanitizer.Sanitize(vector.Input)}");
        }
    }

    [Fact]
    public void InsertMeetingName()
    {
        var vectors = NamingVectors.Load().InsertMeetingName;
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            var result = MeetingNameInserter.Insert(vector.Markdown, vector.Name, vector.FallbackHeading);
            Assert.True(vector.Output == result,
                $"markdown: {JsonSerializer.Serialize(vector.Markdown)}, got {JsonSerializer.Serialize(result)}");
        }
    }

    [Fact]
    public void TimestampFromFilename()
    {
        var vectors = NamingVectors.Load().TimestampFromFilename;
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            Assert.True(vector.Timestamp == Timestamps.ParseFromFilename(vector.Filename), $"filename: {vector.Filename}");
            // Python source_file_timestamp on a path that does not exist.
            var missing = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():D}", vector.Filename);
            Assert.True(vector.Timestamp == Timestamps.SourceFileTimestamp(missing), $"filename: {vector.Filename}");
        }
    }

    /// <summary>
    /// Each vector: the existing files plus <c>source.srt</c> in an empty
    /// folder, then <c>SaveNamed</c> with the given timestamp.
    /// </summary>
    [Fact]
    public void OutputNames()
    {
        var vectors = NamingVectors.Load().OutputNames;
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            using var directory = new TemporaryDirectory();
            foreach (var name in vector.Existing)
            {
                directory.Write(name, "");
            }
            var srt = directory.Write("source.srt", "1\n");
            var result = OutputWriter.SaveNamed(srt, vector.Name, "# N", "# T", vector.Timestamp);
            Assert.True(vector.Stem + ".srt" == NamingAssert.Name(result.Srt), $"vector: {vector.Stem}");
            Assert.Equal(vector.Stem + ".md", NamingAssert.Name(result.Markdown));
            Assert.Equal(vector.Stem + "_transcript.md", NamingAssert.Name(result.Transcript));
            Assert.Equal(
                NamingAssert.Sorted([.. vector.Existing, vector.Stem + ".md", vector.Stem + ".srt",
                    vector.Stem + "_transcript.md"]),
                directory.Listing());
        }
    }
}
