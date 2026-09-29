using System.Buffers.Binary;

namespace Hearsay.Core.Audio;

/// <summary>What went wrong reading or repairing a WAV header.</summary>
public enum WavErrorKind
{
    NotAWavFile,
    Closed,
}

/// <summary>
/// Errors from reading or repairing a WAV header. Port of <c>WavError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/WavWriter.swift. The messages are
/// the English catalog keys; localization comes with the Windows string
/// resources.
/// </summary>
public sealed class WavException : Exception
{
    public WavException()
        : this(WavErrorKind.Closed, null)
    {
    }

    public WavException(string message)
        : base(message)
    {
    }

    public WavException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WavException(WavErrorKind kind, string? path)
        : base(Describe(kind, path))
    {
        Kind = kind;
        FilePath = path;
    }

    public WavErrorKind Kind { get; }

    /// <summary>The file for <see cref="WavErrorKind.NotAWavFile"/>.</summary>
    public string? FilePath { get; }

    private static string Describe(WavErrorKind kind, string? path) => kind switch
    {
        WavErrorKind.NotAWavFile => $"Not a readable WAV file: {path}",
        _ => "The WAV file is already closed.",
    };
}

/// <summary>
/// Writes the spooled recording in the same format the Python tool keeps
/// (<c>build_streaming_ffmpeg_argv</c>: <c>-ar 16000 -ac 1 -c:a pcm_s16le -f wav</c>):
/// a canonical 44-byte RIFF/WAVE header followed by 16 kHz mono signed 16-bit
/// little-endian PCM. Port of mac/HearsayCore/Sources/HearsayCore/Audio/WavWriter.swift.
/// </summary>
/// <remarks>
/// The header is written with zero sizes and patched on <see cref="Close"/>.
/// A crash leaves zeros there, which <see cref="PatchHeader"/> repairs from
/// the file size. <see cref="Dispose"/> closes the file without patching, like
/// the Swift <c>deinit</c>, so an abandoned writer looks like a crash and is
/// found by the spool scan. Not thread-safe: append and close from one context.
/// </remarks>
public sealed class WavWriter : IDisposable
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int HeaderSize = 44;

    /// <summary>Bytes of header scanned when inspecting a WAV (<c>MAX_WAV_HEADER_BYTES</c>).</summary>
    public const int MaxHeaderBytes = 4096;

    private FileStream? stream;

    /// <summary>
    /// Creates (or truncates) the file at <paramref name="path"/>, creating its
    /// folder if needed, and writes a header with zero sizes.
    /// </summary>
    public WavWriter(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FilePath = path;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        try
        {
            file.Write(Header(0));
            file.Flush();
        }
        catch
        {
            file.Dispose();
            throw;
        }
        stream = file;
    }

    public string FilePath { get; }

    /// <summary>Samples written so far.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Seconds of audio written so far.</summary>
    public double Duration => (double)SampleCount / SampleRate;

    /// <summary>Appends samples, clamping each to [-1, 1] and converting to Int16.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        var file = stream ?? throw new WavException(WavErrorKind.Closed, null);
        if (samples.IsEmpty)
        {
            return;
        }
        file.Write(PcmData(samples));
        SampleCount += samples.Length;
    }

    /// <summary>Patches the RIFF and data sizes and closes the file. Calling it again does nothing.</summary>
    public void Close()
    {
        var file = stream;
        if (file is null)
        {
            return;
        }
        stream = null;
        try
        {
            var dataBytes = ClampedUInt32(SampleCount * BitsPerSample / 8 * Channels);
            file.Seek(4, SeekOrigin.Begin);
            file.Write(LittleEndian(ClampedUInt32((long)dataBytes + HeaderSize - 8)));
            file.Seek(40, SeekOrigin.Begin);
            file.Write(LittleEndian(dataBytes));
            file.Flush(flushToDisk: true);
        }
        finally
        {
            file.Dispose();
        }
    }

    /// <summary>Closes the file without patching the header (see remarks).</summary>
    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }

    // Crash recovery and inspection

    /// <summary>
    /// Whether the header of the file at <paramref name="path"/> still carries
    /// the zero sizes of an unfinished recording while the file holds audio.
    /// </summary>
    public static bool HasUnfinishedHeader(string path) => Inspect(path).NeedsPatch;

    /// <summary>
    /// Rewrites the RIFF and data sizes from the file size when the header
    /// still says 0 (or claims more data than the file holds). Returns whether
    /// the file was changed.
    /// </summary>
    public static bool PatchHeader(string path)
    {
        var info = Inspect(path);
        if (!info.NeedsPatch)
        {
            return false;
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        file.Seek(4, SeekOrigin.Begin);
        file.Write(LittleEndian(ClampedUInt32((long)info.FileSize - 8)));
        file.Seek(info.DataSizeOffset, SeekOrigin.Begin);
        file.Write(LittleEndian(ClampedUInt32((long)info.AvailableDataBytes)));
        file.Flush(flushToDisk: true);
        return true;
    }

    /// <summary>
    /// Duration in seconds of the audio in the WAV at <paramref name="path"/>.
    /// An unfinished header is measured from the file size.
    /// </summary>
    public static double DurationOf(string path)
    {
        var info = Inspect(path);
        var bytesPerSecond = (double)info.SampleRate * info.BlockAlign;
        if (!(bytesPerSecond > 0))
        {
            throw new WavException(WavErrorKind.NotAWavFile, path);
        }
        return info.EffectiveDataBytes / bytesPerSecond;
    }

    // Header layout

    /// <summary>What <see cref="Inspect"/> read from a WAV header (internal in Swift).</summary>
    public readonly record struct Info(
        ulong FileSize,
        uint RiffSize,
        uint SampleRate,
        ushort Channels,
        ushort BitsPerSample,
        ushort BlockAlign,
        int DataSizeOffset,
        int DataOffset,
        uint DeclaredDataBytes)
    {
        /// <summary>
        /// Data bytes the file actually holds after the data chunk header,
        /// rounded down to whole frames.
        /// </summary>
        public ulong AvailableDataBytes
        {
            get
            {
                var raw = FileSize > (ulong)DataOffset ? FileSize - (ulong)DataOffset : 0;
                var align = (ulong)Math.Max(BlockAlign, (ushort)1);
                return raw - raw % align;
            }
        }

        public bool NeedsPatch =>
            AvailableDataBytes > 0
            && (DeclaredDataBytes == 0 || RiffSize == 0 || DeclaredDataBytes > AvailableDataBytes);

        public ulong EffectiveDataBytes => NeedsPatch ? AvailableDataBytes : DeclaredDataBytes;
    }

    /// <summary>Reads the RIFF, fmt and data headers of the WAV at <paramref name="path"/>.</summary>
    public static Info Inspect(string path)
    {
        byte[] bytes;
        ulong fileSize;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var head = new byte[MaxHeaderBytes];
            var count = file.ReadAtLeast(head, MaxHeaderBytes, throwOnEndOfStream: false);
            bytes = head[..count];
            fileSize = (ulong)file.Length;
        }

        uint? U32(int offset) =>
            offset >= 0 && offset + 4 <= bytes.Length ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) : null;
        ushort? U16(int offset) =>
            offset >= 0 && offset + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) : null;
        bool Tag(int offset, string expected) =>
            offset >= 0 && offset + 4 <= bytes.Length
            && bytes[offset] == expected[0] && bytes[offset + 1] == expected[1]
            && bytes[offset + 2] == expected[2] && bytes[offset + 3] == expected[3];
        bool HasTag(int offset) => offset >= 0 && offset + 4 <= bytes.Length;

        if (!Tag(0, "RIFF") || !Tag(8, "WAVE") || U32(4) is not uint riffSize)
        {
            throw new WavException(WavErrorKind.NotAWavFile, path);
        }
        (uint Rate, ushort Channels, ushort Bits, ushort Align)? format = null;
        long offset = 12;
        while (offset <= int.MaxValue - 8 && HasTag((int)offset) && U32((int)offset + 4) is uint size)
        {
            var at = (int)offset;
            var body = at + 8;
            if (Tag(at, "fmt "))
            {
                if (U16(body + 2) is not ushort channels || U32(body + 4) is not uint rate
                    || U16(body + 12) is not ushort align || U16(body + 14) is not ushort bits)
                {
                    break;
                }
                format = (rate, channels, bits, align);
            }
            else if (Tag(at, "data"))
            {
                if (format is not { } f)
                {
                    break;
                }
                return new Info(fileSize, riffSize, f.Rate, f.Channels, f.Bits, f.Align,
                    DataSizeOffset: at + 4, DataOffset: body, DeclaredDataBytes: size);
            }
            // Chunks are padded to an even size.
            offset = (long)body + size + size % 2;
        }
        throw new WavException(WavErrorKind.NotAWavFile, path);
    }

    /// <summary>Canonical 44-byte header for 16 kHz mono s16le with <paramref name="dataBytes"/> of audio.</summary>
    public static byte[] Header(uint dataBytes)
    {
        const uint byteRate = SampleRate * Channels * BitsPerSample / 8;
        const ushort blockAlign = Channels * BitsPerSample / 8;
        var header = new byte[HeaderSize];
        var span = header.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ClampedUInt32((long)dataBytes + HeaderSize - 8));
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], dataBytes);
        return header;
    }

    /// <summary>
    /// Float samples as s16le bytes: NaN becomes 0, the rest is clamped to
    /// [-1, 1] and scaled by 32767.
    /// </summary>
    public static byte[] PcmData(ReadOnlySpan<float> samples)
    {
        var data = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2), ToInt16(samples[index]));
        }
        return data;
    }

    /// <summary>One sample as Int16: NaN is 0, clamped to [-1, 1], scaled by 32767, rounded half away from zero.</summary>
    public static short ToInt16(float sample)
    {
        if (float.IsNaN(sample))
        {
            return 0;
        }
        var clamped = Math.Max(-1f, Math.Min(1f, sample));
        return (short)MathF.Round(clamped * 32767f, MidpointRounding.AwayFromZero);
    }

    private static uint ClampedUInt32(long value) => (uint)Math.Clamp(value, 0, uint.MaxValue);

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
