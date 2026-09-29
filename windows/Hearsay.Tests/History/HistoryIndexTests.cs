using Hearsay.Core.History;

namespace Hearsay.Tests.History;

/// <summary>Port of mac/HearsayCore/Tests/HearsayCoreTests/HistoryIndexTests.swift.</summary>
public sealed class HistoryIndexTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string MakeFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"HearsayHistoryTests-{Guid.NewGuid():D}");
        Directory.CreateDirectory(path);
        folders.Add(path);
        return path;
    }

    private static void Touch(string folder, string name) =>
        File.WriteAllBytes(Path.Combine(folder, name), "x"u8.ToArray());

    /// <summary>
    /// Writes a 16 kHz mono 16-bit WAV of <paramref name="seconds"/> of silence
    /// in the layout the Mac's <c>WavWriter</c> writes (canonical 44-byte header).
    /// </summary>
    private static void WriteWav(string path, double seconds)
    {
        var dataBytes = (int)(seconds * 16_000) * 2;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }

    [Fact]
    public void GroupsNamesSortsAndMeasuresDuration()
    {
        var folder = MakeFolder();

        // Full set with a meeting name.
        const string full = "2026-09-28_10-00-00_weekly-sync";
        Touch(folder, full + ".srt");
        Touch(folder, full + ".md");
        Touch(folder, full + "_transcript.md");
        WriteWav(Path.Combine(folder, full + ".wav"), 1.5);
        // Timestamp-only set, newer.
        const string plain = "2026-09-28_14-30-15";
        Touch(folder, plain + ".srt");
        WriteWav(Path.Combine(folder, plain + ".wav"), 0.25);
        // Lone notes file without a timestamp.
        Touch(folder, "notes.md");
        // Ignored: unrelated, hidden, partial and tmp files, a folder.
        Touch(folder, "readme.txt");
        Touch(folder, ".2026-09-28_10-00-00_weekly-sync.md.ABC.tmp");
        Touch(folder, ".DS_Store");
        Touch(folder, "2026-09-27_09-00-00.wav.partial");
        Touch(folder, "2026-09-27_09-00-00.srt.tmp");
        Directory.CreateDirectory(Path.Combine(folder, "2026-01-01_00-00-00.wav"));
        // Windows only: the hidden attribute counts like a leading dot.
        Touch(folder, "2026-09-26_08-00-00.srt");
        File.SetAttributes(Path.Combine(folder, "2026-09-26_08-00-00.srt"), FileAttributes.Hidden);

        var entries = HistoryIndex.Scan(folder);
        Assert.Equal([plain, full, "notes"], entries.Select(entry => entry.Stem));

        var newest = entries[0];
        Assert.Null(newest.MeetingName);
        Assert.Equal(plain + ".srt", Path.GetFileName(newest.Srt));
        Assert.Null(newest.Notes);
        Assert.Null(newest.Transcript);
        Assert.Equal(plain + ".wav", Path.GetFileName(newest.Audio));
        Assert.Equal(0.25, newest.AudioDuration);

        var named = entries[1];
        Assert.Equal(full, named.Id);
        Assert.Equal("weekly-sync", named.MeetingName);
        Assert.Equal(full + ".srt", Path.GetFileName(named.Srt));
        Assert.Equal(full + ".md", Path.GetFileName(named.Notes));
        Assert.Equal(full + "_transcript.md", Path.GetFileName(named.Transcript));
        Assert.Equal(full + ".wav", Path.GetFileName(named.Audio));
        Assert.Equal(1.5, named.AudioDuration);
        Assert.Equal(4, named.Files.Count);

        var local = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), named.Timestamp);

        var lone = entries[2];
        Assert.Null(lone.Timestamp);
        Assert.Null(lone.MeetingName);
        Assert.Equal("notes.md", Path.GetFileName(lone.Notes));
        Assert.True(lone.Srt is null && lone.Audio is null && lone.Transcript is null);
        Assert.Null(lone.AudioDuration);
    }

    [Fact]
    public void MeetingNameRequiresLeadingTimestampAndName()
    {
        Assert.Equal("standup", HistoryIndex.MeetingName("2026-09-28_10-00-00_standup"));
        Assert.Equal("standup-2", HistoryIndex.MeetingName("2026-09-28_10-00-00_standup-2"));
        Assert.Null(HistoryIndex.MeetingName("2026-09-28_10-00-00"));
        Assert.Null(HistoryIndex.MeetingName("2026-09-28_10-00-00-2"));
        Assert.Null(HistoryIndex.MeetingName("2026-09-28_10-00-00_"));
        Assert.Null(HistoryIndex.MeetingName("standup"));
        Assert.Null(HistoryIndex.MeetingName("2026-13-28_10-00-00_bad-month"));
    }

    [Fact]
    public void TimestampDateUsesTimestampRules()
    {
        var date = HistoryIndex.TimestampDate("2026-09-28_10-00-00_x", TimeZoneInfo.Utc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_589_600), date);
        Assert.Null(HistoryIndex.TimestampDate("notes"));
    }

    [Fact]
    public void MissingFolderThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"HearsayHistoryMissing-{Guid.NewGuid():D}");
        Assert.ThrowsAny<IOException>(() => HistoryIndex.Scan(missing));
    }
}
