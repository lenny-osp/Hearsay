using System.Buffers.Binary;
using System.Text;

namespace Hearsay.Tests.Audio;

/// <summary>A scratch folder under the temp directory, deleted on dispose.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HearsayAudioTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A minimal RIFF/WAVE reader for the tests, independent of the code under
/// test: walks the chunks and returns the fmt fields and the data chunk.
/// </summary>
internal sealed record RiffWave(
    ushort FormatTag, ushort Channels, uint SampleRate, uint ByteRate, ushort BlockAlign, ushort BitsPerSample,
    IReadOnlyList<string> ChunkIds, int DataOffset, byte[] Data)
{
    public short[] Int16Samples()
    {
        var samples = new short[Data.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(index * 2));
        }
        return samples;
    }

    public static RiffWave Read(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        var ids = new List<string>();
        (ushort Tag, ushort Channels, uint Rate, uint ByteRate, ushort Align, ushort Bits)? fmt = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            ids.Add(id);
            var body = offset + 8;
            if (id == "fmt ")
            {
                var span = bytes.AsSpan(body);
                fmt = (BinaryPrimitives.ReadUInt16LittleEndian(span), BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(span[4..]), BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(span[12..]), BinaryPrimitives.ReadUInt16LittleEndian(span[14..]));
            }
            else if (id == "data")
            {
                Assert.NotNull(fmt);
                var f = fmt.Value;
                var length = Math.Min(size, bytes.Length - body);
                return new RiffWave(f.Tag, f.Channels, f.Rate, f.ByteRate, f.Align, f.Bits, ids, body,
                    bytes.AsSpan(body, length).ToArray());
            }
            offset = body + size + size % 2;
        }
        throw new InvalidDataException("no data chunk in " + path);
    }
}

internal static class AudioSignals
{
    /// <summary>A 440 Hz sine at half scale, <paramref name="count"/> samples at 16 kHz.</summary>
    public static float[] Sine(int count) =>
        Enumerable.Range(0, count).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 16_000))).ToArray();

    public static string Fixture(string name) => SharedFiles.Path("fixtures", name);
}
