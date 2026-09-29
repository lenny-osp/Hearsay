using System.Buffers.Binary;
using System.Text;
using Hearsay.Core.Audio;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Port of the <c>WavWriterTests</c> suite in
/// mac/HearsayCore/Tests/HearsayCoreTests/AudioTests.swift, plus the fixture
/// round trip.
/// </summary>
public class WavWriterTests
{
    [Fact]
    public void HeaderIsCanonical44BytesForPythonFormat()
    {
        var header = WavWriter.Header(320);
        Assert.Equal(44, header.Length);
        Assert.Equal("RIFF"u8.ToArray(), header[0..4]);
        Assert.Equal("WAVEfmt "u8.ToArray(), header[8..16]);
        Assert.Equal("data"u8.ToArray(), header[36..40]);
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(o));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(o));
        Assert.Equal(36u + 320, U32(4));
        Assert.Equal(1, U16(20));        // PCM
        Assert.Equal(1, U16(22));        // -ac 1
        Assert.Equal(16_000u, U32(24));  // -ar 16000
        Assert.Equal(32_000u, U32(28));  // byte rate
        Assert.Equal(2, U16(32));        // block align
        Assert.Equal(16, U16(34));       // pcm_s16le
        Assert.Equal(320u, U32(40));
    }

    [Fact]
    public void SampleConversionClampsAndScales()
    {
        Assert.Equal(0, WavWriter.ToInt16(0));
        Assert.Equal(32767, WavWriter.ToInt16(1));
        Assert.Equal(-32767, WavWriter.ToInt16(-1));
        Assert.Equal(32767, WavWriter.ToInt16(2.5f));
        Assert.Equal(-32767, WavWriter.ToInt16(-7));
        Assert.Equal(0, WavWriter.ToInt16(float.NaN));
        Assert.Equal(16384, WavWriter.ToInt16(0.5f));
    }

    [Fact]
    public void WrittenFileReadsBackWithFormatCountAndValues()
    {
        using var scratch = new ScratchDirectory();
        var path = scratch.File("out.wav");
        float[] samples = [.. AudioSignals.Sine(16_000), 1.5f, -1.5f];
        var writer = new WavWriter(path);
        writer.Append(samples.AsSpan(0, 7_000));
        writer.Append(samples.AsSpan(7_000));
        Assert.Equal(samples.Length, writer.SampleCount);
        writer.Close();
        writer.Close(); // idempotent

        Assert.Equal(44 + samples.Length * 2, new FileInfo(path).Length);

        var wave = RiffWave.Read(path);
        Assert.Equal(16_000u, wave.SampleRate);
        Assert.Equal(1, wave.Channels);
        Assert.Equal(1, wave.FormatTag);
        Assert.Equal(16, wave.BitsPerSample);
        var values = wave.Int16Samples();
        Assert.Equal(samples.Length, values.Length);
        for (var index = 0; index < samples.Length; index++)
        {
            Assert.Equal(WavWriter.ToInt16(samples[index]), values[index]);
        }
        Assert.Equal(32767, values[^2]);
        Assert.Equal(-32767, values[^1]);
        Assert.True(Math.Abs(WavWriter.DurationOf(path) - (double)samples.Length / 16_000) < 1e-9);
        Assert.False(WavWriter.HasUnfinishedHeader(path));
        Assert.False(WavWriter.PatchHeader(path));
    }

    [Fact]
    public void AppendAfterCloseThrows()
    {
        using var scratch = new ScratchDirectory();
        var writer = new WavWriter(scratch.File("a.wav"));
        writer.Close();
        var error = Assert.Throws<WavException>(() => writer.Append([0.1f]));
        Assert.Equal(WavErrorKind.Closed, error.Kind);
    }

    [Fact]
    public void PatchHeaderRepairsZeroSizesLeftByACrash()
    {
        using var scratch = new ScratchDirectory();
        var path = scratch.File("crashed.wav");
        var samples = AudioSignals.Sine(8_000);
        // What a crash leaves: the initial zero-size header plus the audio,
        // plus a torn half sample at the end.
        byte[] contents = [.. WavWriter.Header(0), .. WavWriter.PcmData(samples), 0x7F];
        File.WriteAllBytes(path, contents);

        Assert.True(WavWriter.HasUnfinishedHeader(path));
        Assert.True(Math.Abs(WavWriter.DurationOf(path) - 0.5) < 1e-9);
        Assert.True(WavWriter.PatchHeader(path));
        Assert.False(WavWriter.HasUnfinishedHeader(path));
        Assert.False(WavWriter.PatchHeader(path));

        var info = WavWriter.Inspect(path);
        Assert.Equal((uint)(samples.Length * 2), info.DeclaredDataBytes);
        Assert.Equal((uint)(contents.Length - 8), info.RiffSize);
        var values = RiffWave.Read(path).Int16Samples();
        Assert.Equal(samples.Length, values.Length);
        Assert.Equal(WavWriter.ToInt16(samples[0]), values[0]);
        Assert.Equal(WavWriter.ToInt16(samples[^1]), values[^1]);
    }

    // Not in the Swift suite: Dispose without Close leaves a crash-like file
    // that the repair restores.
    [Fact]
    public void DisposeWithoutCloseLeavesARepairableFile()
    {
        using var scratch = new ScratchDirectory();
        var path = scratch.File("abandoned.wav");
        using (var writer = new WavWriter(path))
        {
            writer.Append(AudioSignals.Sine(1_600));
        }
        Assert.True(WavWriter.HasUnfinishedHeader(path));
        Assert.True(WavWriter.PatchHeader(path));
        Assert.True(Math.Abs(WavWriter.DurationOf(path) - 0.1) < 1e-9);
    }

    [Fact]
    public void EmptyRecordingIsNotUnfinished()
    {
        using var scratch = new ScratchDirectory();
        var path = scratch.File("empty.wav");
        File.WriteAllBytes(path, WavWriter.Header(0));
        Assert.False(WavWriter.HasUnfinishedHeader(path));
        Assert.Equal(0, WavWriter.DurationOf(path));
    }

    [Fact]
    public void NonWavFileIsRejected()
    {
        using var scratch = new ScratchDirectory();
        var path = scratch.File("not.wav");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello, this is not audio at all"));
        Assert.Equal(WavErrorKind.NotAWavFile, Assert.Throws<WavException>(() => WavWriter.PatchHeader(path)).Kind);
        Assert.Equal(WavErrorKind.NotAWavFile, Assert.Throws<WavException>(() => WavWriter.DurationOf(path)).Kind);
    }

    [Fact]
    public void DurationOfFixturesWithExtraChunks()
    {
        Assert.True(Math.Abs(WavWriter.DurationOf(AudioSignals.Fixture("en-30s.wav")) - 18.94) < 0.01);
    }

    /// <summary>
    /// zh-30s.wav is the only fixture with a plain 44-byte header (the others
    /// carry Apple's FLLR padding chunk); writing its samples back must give
    /// the same file byte for byte.
    /// </summary>
    [Fact]
    public void RewritingThePlainFixtureIsByteIdentical()
    {
        var fixture = AudioSignals.Fixture("zh-30s.wav");
        var original = File.ReadAllBytes(fixture);
        var wave = RiffWave.Read(fixture);
        Assert.Equal(["fmt ", "data"], wave.ChunkIds);
        Assert.Equal(WavWriter.HeaderSize, wave.DataOffset);
        Assert.Equal(original.Length, WavWriter.HeaderSize + wave.Data.Length);

        using var scratch = new ScratchDirectory();
        var path = scratch.File("zh-rewritten.wav");
        WriteBack(wave.Int16Samples(), path);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    /// <summary>
    /// en-30s.wav is not a plain 44-byte header (it has a 4044-byte FLLR chunk
    /// between fmt and data), so only its PCM payload can match; the header
    /// the writer produces is the canonical one.
    /// </summary>
    [Fact]
    public void RewritingTheEnglishFixtureKeepsItsPcmByteIdentical()
    {
        var wave = RiffWave.Read(AudioSignals.Fixture("en-30s.wav"));
        Assert.Equal(["fmt ", "FLLR", "data"], wave.ChunkIds);
        Assert.Equal(4096, wave.DataOffset);
        Assert.Equal((1, 1, 16_000u, 16), (wave.FormatTag, wave.Channels, wave.SampleRate, wave.BitsPerSample));

        using var scratch = new ScratchDirectory();
        var path = scratch.File("en-rewritten.wav");
        WriteBack(wave.Int16Samples(), path);
        var written = File.ReadAllBytes(path);
        Assert.Equal(WavWriter.Header((uint)wave.Data.Length), written[..WavWriter.HeaderSize]);
        Assert.Equal(wave.Data, written[WavWriter.HeaderSize..]);
    }

    /// <summary>Feeds Int16 samples through the Float32 writer in capture-sized pieces.</summary>
    private static void WriteBack(short[] values, string path)
    {
        // -32768 would clamp to -32767; the fixtures do not contain it.
        Assert.DoesNotContain((short)-32768, values);
        var floats = values.Select(v => v / 32767f).ToArray();
        var writer = new WavWriter(path);
        for (var start = 0; start < floats.Length; start += 1_600)
        {
            writer.Append(floats.AsSpan(start, Math.Min(1_600, floats.Length - start)));
        }
        writer.Close();
    }
}

/// <summary>Port of the <c>RecordingSpoolTests</c> suite in AudioTests.swift.</summary>
public class RecordingSpoolTests
{
    private static void WriteFinished(string path, int samples)
    {
        var writer = new WavWriter(path);
        writer.Append(AudioSignals.Sine(samples));
        writer.Close();
    }

    [Fact]
    public void DefaultRootIsLocalAppDataHearsayRecording()
    {
        var root = RecordingSpool.DefaultRoot();
        Assert.Equal("Recording", Path.GetFileName(root));
        var parent = Path.GetDirectoryName(root) ?? "";
        Assert.Equal("Hearsay", Path.GetFileName(parent));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetDirectoryName(parent));
    }

    [Fact]
    public void NewRecordingPathUsesTimestampAndAvoidsCollisions()
    {
        using var scratch = new ScratchDirectory();
        var spool = new RecordingSpool(scratch.Path);
        var first = spool.NewRecordingPath("2026-09-28_10-00-00");
        Assert.Equal(scratch.File("2026-09-28_10-00-00.wav"), first);
        WriteFinished(first, 10);
        Assert.Equal("2026-09-28_10-00-00-2.wav", Path.GetFileName(spool.NewRecordingPath("2026-09-28_10-00-00")));
        // The Swift test also round-trips Timestamps.now() through
        // Timestamps.parse; that belongs to the Naming port.
    }

    [Fact]
    public void UnfinishedRecordingsListsZeroHeadersAndMissingSrt()
    {
        using var scratch = new ScratchDirectory();
        var spool = new RecordingSpool(scratch.Path);

        // Finished with its SRT: not listed.
        WriteFinished(scratch.File("2026-09-28_09-00-00.wav"), 100);
        File.WriteAllText(scratch.File("2026-09-28_09-00-00.srt"), "1\n");
        // Finished header but no SRT: listed.
        WriteFinished(scratch.File("2026-09-28_10-00-00.wav"), 100);
        // Crashed (zero header) even though an SRT exists: listed.
        byte[] contents = [.. WavWriter.Header(0), .. WavWriter.PcmData(AudioSignals.Sine(100))];
        File.WriteAllBytes(scratch.File("2026-09-28_11-00-00.wav"), contents);
        File.WriteAllText(scratch.File("2026-09-28_11-00-00.srt"), "1\n");
        // Not a WAV: ignored.
        File.WriteAllText(scratch.File("notes.txt"), "x");

        Assert.Equal(["2026-09-28_10-00-00.wav", "2026-09-28_11-00-00.wav"],
            spool.UnfinishedRecordings().Select(Path.GetFileName));
    }

    [Fact]
    public void UnfinishedRecordingsIsEmptyWhenFolderIsMissing()
    {
        var spool = new RecordingSpool(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(spool.UnfinishedRecordings());
    }

    [Fact]
    public void FinalizeKeepMovesWithNumericSuffixOnCollision()
    {
        using var root = new ScratchDirectory();
        using var outputParent = new ScratchDirectory();
        var output = outputParent.File("out");
        var spool = new RecordingSpool(root.Path);

        var first = spool.NewRecordingPath("2026-09-28_10-00-00");
        WriteFinished(first, 50);
        var moved = RecordingSpool.Finalize(first, keep: true, outputFolder: output);
        Assert.Equal(Path.Combine(output, "2026-09-28_10-00-00.wav"), moved);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(Path.Combine(output, "2026-09-28_10-00-00.wav")));

        var second = spool.NewRecordingPath("2026-09-28_10-00-00");
        WriteFinished(second, 60);
        var movedAgain = RecordingSpool.Finalize(second, keep: true, outputFolder: output);
        Assert.Equal("2026-09-28_10-00-00-2.wav", Path.GetFileName(movedAgain));
        Assert.Equal(50.0 / 16_000, WavWriter.DurationOf(Path.Combine(output, "2026-09-28_10-00-00.wav")));
    }

    [Fact]
    public void FinalizeWithoutKeepDeletes()
    {
        using var root = new ScratchDirectory();
        using var output = new ScratchDirectory();
        var spool = new RecordingSpool(root.Path);
        var path = spool.NewRecordingPath("2026-09-28_10-00-00");
        WriteFinished(path, 10);
        Assert.Null(RecordingSpool.Finalize(path, keep: false, outputFolder: output.Path));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output.Path));
    }
}
