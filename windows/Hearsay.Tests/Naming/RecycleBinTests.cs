using System.Security.Cryptography;
using Hearsay.Core.Naming;

namespace Hearsay.Tests.Naming;

/// <summary>
/// A fact that runs only with <c>HEARSAY_TEST_RECYCLE_BIN=1</c>: it uses the
/// real Recycle Bin of the signed-in user, so it is off by default (the skip
/// is set at discovery, as in <c>EnvironmentFactAttribute</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RecycleBinFactAttribute : FactAttribute
{
    public const string Variable = "HEARSAY_TEST_RECYCLE_BIN";

    public RecycleBinFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Set {Variable}=1 to run this test against the real Recycle Bin.";
        }
        else if (!OperatingSystem.IsWindows())
        {
            Skip = "The Recycle Bin is available on Windows only.";
        }
    }
}

/// <summary>
/// The real <c>OutputWriter.DefaultTrash</c> / <c>DefaultRestore</c> pair
/// (RecycleBin.cs, PLAN.md 18.4 "Recycle Bin"); the rollback rules
/// themselves are covered with the fake Recycle Bin in NamingTests.cs.
/// Every file these tests recycle is named with a fresh GUID and restored
/// before the test ends; nothing is left in the bin, and nothing is ever
/// emptied from it. A file that could not be brought back is named in the
/// failure message so it can be found in the Recycle Bin.
/// </summary>
public sealed class RecycleBinTests
{
    private static byte[] RandomBytes() => RandomNumberGenerator.GetBytes(4096);

    /// <summary>Restores what is still recycled; returns a failure text naming each file left in the bin.</summary>
    private static string? CleanUp(List<(string Location, string Original)> recycled)
    {
        var left = new List<string>();
        foreach (var (location, original) in recycled)
        {
            if (File.Exists(original))
            {
                continue;
            }
            try
            {
                OutputWriter.DefaultRestore(location, original);
            }
            catch (IOException error)
            {
                left.Add($"{Path.GetFileName(original)} ({location}): {error.Message}");
            }
        }
        return left.Count == 0
            ? null
            : "Left in the Recycle Bin, restore by hand: " + string.Join("; ", left);
    }

    private static byte[] VersionTwoRecord(string path)
    {
        var text = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(2L));
        stream.Write(BitConverter.GetBytes(4096L));
        stream.Write(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        stream.Write(BitConverter.GetBytes(path.Length + 1));
        stream.Write(text);
        return stream.ToArray();
    }

    [Fact]
    public void BinRecordIsMatchedOnlyToItsOwnOriginalPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        const string original = @"C:\Users\someone\Documents\Hearsay\2026-09-30_10-00-00_launch.md";
        var record = VersionTwoRecord(original);
        Assert.True(RecycleBin.RecordNames(record, original));
        Assert.True(RecycleBin.RecordNames(record, original.ToUpperInvariant()));
        Assert.False(RecycleBin.RecordNames(record, original + ".bak"));
        Assert.False(RecycleBin.RecordNames(record[..30], original));
        Assert.False(RecycleBin.RecordNames([], original));

        var versionOne = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(versionOne, 0);
        System.Text.Encoding.Unicode.GetBytes(original).CopyTo(versionOne, 24);
        Assert.True(RecycleBin.RecordNames(versionOne, original));

        var unknown = (byte[])record.Clone();
        BitConverter.GetBytes(3L).CopyTo(unknown, 0);
        Assert.False(RecycleBin.RecordNames(unknown, original));
    }

    /// <summary>The debug replay's clean-up (<c>Purge</c>) deletes only a <c>$R</c> item whose record names the original; a look-alike folder stands in for the bin, so the real one is never touched.</summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void PurgeRemovesOnlyAnItemWhoseRecordNamesTheOriginal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TemporaryDirectory("RecycleBinPurge");
        var user = Path.Combine(directory.Url, "$Recycle.Bin", "S-1-5-21-1");
        Directory.CreateDirectory(user);
        const string original = @"C:\spool\2026-09-30_10-00-00.wav";
        var item = Path.Combine(user, "$RABCDEF.wav");
        var record = Path.Combine(user, "$IABCDEF.wav");
        var other = Path.Combine(user, "$RZZZZZZ.wav");
        File.WriteAllBytes(item, [1]);
        File.WriteAllBytes(record, VersionTwoRecord(original));
        File.WriteAllBytes(other, [2]);
        File.WriteAllBytes(Path.Combine(user, "$IZZZZZZ.wav"), VersionTwoRecord(@"C:\elsewhere\other.wav"));

        // Another original, another bin item, a path outside a bin: nothing is deleted.
        Assert.Throws<IOException>(() => RecycleBin.Purge(item, original + ".bak"));
        Assert.Throws<IOException>(() => RecycleBin.Purge(other, original));
        Assert.Throws<IOException>(() => RecycleBin.Purge(record, original));
        var outside = Path.Combine(directory.Url, "$RABCDEF.wav");
        File.WriteAllBytes(outside, [3]);
        Assert.Throws<IOException>(() => RecycleBin.Purge(outside, original));
        Assert.True(File.Exists(item) && File.Exists(record) && File.Exists(other) && File.Exists(outside));

        RecycleBin.Purge(item, original);
        Assert.False(File.Exists(item));
        Assert.False(File.Exists(record));
        Assert.True(File.Exists(other), "other items stay");
        Assert.True(File.Exists(Path.Combine(user, "$IZZZZZZ.wav")));
    }

    [RecycleBinFact]
    public void RecycledFileIsGoneAndComesBackByteIdentical()
    {
        using var directory = new TemporaryDirectory("RecycleBinTests");
        var name = $"hearsay-recycle-test-{Guid.NewGuid():D}.md";
        var path = directory.File(name);
        var bytes = RandomBytes();
        File.WriteAllBytes(path, bytes);
        var recycled = new List<(string Location, string Original)>();
        try
        {
            var location = OutputWriter.DefaultTrash(path);
            Assert.True(location is not null,
                $"PostDeleteItem gave no Recycle Bin item for {name}; it may have been deleted for good.");
            recycled.Add((location, path));
            Assert.False(File.Exists(path), $"{name} is still on disk after recycling.");

            OutputWriter.DefaultRestore(location, path);
            Assert.True(File.Exists(path), $"{name} is not back after the restore.");
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(location), $"{location} is still in the Recycle Bin.");
            var record = Path.Combine(Path.GetDirectoryName(location) ?? "", "$I" + Path.GetFileName(location)[2..]);
            Assert.False(File.Exists(record), $"The Recycle Bin still has a record of {name}: {record}.");
        }
        finally
        {
            var left = CleanUp(recycled);
            Assert.True(left is null, left);
        }
    }

    [RecycleBinFact]
    public void RestoreNeverReplacesAFileAtTheOriginalPath()
    {
        using var directory = new TemporaryDirectory("RecycleBinTests");
        var name = $"hearsay-recycle-test-{Guid.NewGuid():D}.md";
        var path = directory.File(name);
        var bytes = RandomBytes();
        File.WriteAllBytes(path, bytes);
        var recycled = new List<(string Location, string Original)>();
        try
        {
            var location = OutputWriter.DefaultTrash(path);
            Assert.True(location is not null, $"PostDeleteItem gave no Recycle Bin item for {name}.");
            recycled.Add((location, path));

            File.WriteAllText(path, "newer");
            Assert.Throws<IOException>(() => OutputWriter.DefaultRestore(location, path));
            Assert.Equal("newer", File.ReadAllText(path));

            File.Delete(path);
            OutputWriter.DefaultRestore(location, path);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            var left = CleanUp(recycled);
            Assert.True(left is null, left);
        }
    }

    [RecycleBinFact]
    public void FailingReplaceNamedMovesTheOldNotesBackFromTheRecycleBin()
    {
        using var directory = new TemporaryDirectory("RecycleBinTests");
        var name = $"hearsay-recycle-test-{Guid.NewGuid():D}";
        var stem = $"2026-09-30_10-00-00_{name}";
        var srt = directory.File(stem + ".srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\nHello\n");
        var notes = directory.File(stem + ".md");
        var transcript = directory.File(stem + "_transcript.md");
        var notesBytes = RandomBytes();
        var transcriptBytes = RandomBytes();
        File.WriteAllBytes(notes, notesBytes);
        File.WriteAllBytes(transcript, transcriptBytes);

        var recycled = new List<(string Location, string Original)>();
        string? Trash(string path)
        {
            var location = OutputWriter.DefaultTrash(path);
            if (location is not null)
            {
                recycled.Add((location, path));
            }
            return location;
        }
        static void FailingTranscriptWriter(string text, string destination)
        {
            if (destination.EndsWith("_transcript.md", StringComparison.Ordinal))
            {
                throw new SimulatedFailure("disk full");
            }
            OutputWriter.WriteAtomically(text, destination);
        }

        try
        {
            var error = NamingAssert.Error<OutputWriterError.WriteFailed>(() => OutputWriter.ReplaceNamed(
                stem, directory.Url, name, "# N", "# T", null,
                OutputWriter.DefaultMove, FailingTranscriptWriter, Trash, OutputWriter.DefaultRestore));
            Assert.Equal(2, recycled.Count);
            Assert.Equal("disk full", error.Reason);
            Assert.True(error.Unrestored.Count == 0, "Not restored: " + string.Join(", ", error.Unrestored));
            Assert.Equal(notesBytes, File.ReadAllBytes(notes));
            Assert.Equal(transcriptBytes, File.ReadAllBytes(transcript));
        }
        finally
        {
            var left = CleanUp(recycled);
            Assert.True(left is null, left);
        }
    }
}
