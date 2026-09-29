using System.Buffers.Binary;
using Hearsay.Core.Audio;
using static Hearsay.Tests.Audio.CaptureTestHelpers;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Tests of the buffer path from WASAPI bytes to stamped 16 kHz chunks.
/// <c>PassesTargetFormatThrough</c> and <c>Resamples48kStereo</c> port
/// <c>sampleBufferConverterPassesTargetFormatThrough</c> and
/// <c>sampleBufferConverterResamples48kStereo</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/AudioTests.swift, with byte spans in
/// place of <c>CMSampleBuffer</c>s; the rest cover what only Windows does
/// (integer mix formats, the WAVEFORMATEX blob, QPC stamps).
/// </summary>
public class CaptureReceiverTests
{
    private static readonly CaptureFormat Target = new(16_000, 1, 32, IsFloat: true);

    private static (CaptureReceiver Receiver, ChunkFanout Output, DiagnosticsBox Diagnostics) Make()
    {
        var output = new ChunkFanout();
        var diagnostics = new DiagnosticsBox();
        return (new CaptureReceiver(output, diagnostics), output, diagnostics);
    }

    private static List<TimedChunk> Drain(ChunkFanout output)
    {
        var reader = output.TimedSamples;
        output.Finish();
        var chunks = new List<TimedChunk>();
        while (reader.TryRead(out var chunk))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    [Fact]
    public void PassesTargetFormatThrough()
    {
        var (receiver, output, diagnostics) = Make();
        float[] samples = [0, 0.25f, -0.5f, 1];
        receiver.Handle(FloatBytes(samples), Target, startHostTime: 12.5);
        var chunks = Drain(output);
        Assert.Single(chunks);
        Assert.Equal(samples, chunks[0].Samples);
        Assert.Equal(12.5, chunks[0].HostTime);
        var value = diagnostics.Value;
        Assert.Equal(1, value.Callbacks);
        Assert.Equal(4, value.FramesReceived);
        Assert.Equal(4, value.SamplesDelivered);
        Assert.Equal("1 ch, 16000 Hz, Float32 interleaved", value.DeliveredFormat);
    }

    [Fact]
    public void Resamples48kStereoIntoContiguousChunks()
    {
        var (receiver, output, _) = Make();
        var format = new CaptureFormat(48_000, 2, 32, IsFloat: true);
        var buffer = FloatBytes(Enumerable.Repeat(0.5f, 4800 * 2).ToArray());
        for (var index = 0; index < 10; index++)
        {
            receiver.Handle(buffer, format, startHostTime: 100 + index * 0.1);
        }
        receiver.Flush();
        var chunks = Drain(output);
        var total = chunks.Sum(chunk => chunk.Samples.Length);
        Assert.InRange(total, 16_000 - 32, 16_000 + 32);
        Assert.Equal(100, chunks[0].HostTime, 9);
        // Each chunk starts where the previous one ended, the held-back
        // resampler output included.
        for (var index = 1; index < chunks.Count; index++)
        {
            var previousEnd = chunks[index - 1].HostTime + chunks[index - 1].Samples.Length / 16_000.0;
            Assert.Equal(previousEnd, chunks[index].HostTime, 6);
        }
    }

    [Fact]
    public void DecodesIntegerMixFormats()
    {
        var int16 = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(int16, 16_384);
        BinaryPrimitives.WriteInt16LittleEndian(int16.AsSpan(2), -32_768);
        Assert.Equal([0.5f, -1f], CaptureSampleDecoder.Decode(int16, new CaptureFormat(16_000, 1, 16, false)));

        // 24-bit little endian: 0x400000 = 0.5, 0xC00000 = -0.5.
        byte[] int24 = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0];
        Assert.Equal([0.5f, -0.5f], CaptureSampleDecoder.Decode(int24, new CaptureFormat(16_000, 1, 24, false)));

        var int32 = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(int32, int.MinValue / 2);
        Assert.Equal([-0.5f], CaptureSampleDecoder.Decode(int32, new CaptureFormat(16_000, 1, 32, false)));

        byte[] uint8 = [192, 64];
        Assert.Equal([0.5f, -0.5f], CaptureSampleDecoder.Decode(uint8, new CaptureFormat(16_000, 1, 8, false)));
    }

    [Fact]
    public void UnsupportedFormatDropsTheBufferOnly()
    {
        var (receiver, output, diagnostics) = Make();
        receiver.Handle(new byte[40], new CaptureFormat(16_000, 1, 20, false), 1);
        receiver.Handle(FloatBytes([0.25f]), Target, 2);
        var chunks = Drain(output);
        Assert.Single(chunks);
        Assert.Equal(1, diagnostics.Value.ConversionFailures);
        Assert.Contains("Unsupported audio format", diagnostics.Value.LastConversionError, StringComparison.Ordinal);
        Assert.Equal(2, diagnostics.Value.Callbacks);
    }

    [Fact]
    public void FormatChangeRebuildsTheConverter()
    {
        var (receiver, output, _) = Make();
        receiver.Handle(FloatBytes([0.1f, 0.2f]), Target, 1);
        receiver.Handle(FloatBytes([0.3f, 0.3f, 0.4f, 0.4f]), new CaptureFormat(16_000, 2, 32, true), 2);
        var chunks = Drain(output);
        Assert.Equal([0.1f, 0.2f], chunks[0].Samples);
        Assert.Equal([0.3f, 0.4f], chunks[1].Samples);
        Assert.Equal(2, chunks[1].HostTime);
    }

    [Fact]
    public void SynthesizedSilenceIsNotACallback()
    {
        var (receiver, output, diagnostics) = Make();
        receiver.Handle(new byte[16 * 4], Target, 3, synthesized: true);
        Assert.Single(Drain(output));
        Assert.Equal(0, diagnostics.Value.Callbacks);
        Assert.Equal(16, diagnostics.Value.SilenceFramesSynthesized);
        Assert.Equal(16, diagnostics.Value.SamplesDelivered);
    }

    [Fact]
    public void PacketStampPrefersTheQpcPosition()
    {
        // 100 ns units, as IAudioCaptureClient::GetBuffer reports them.
        Assert.Equal(12.345_678_9, PacketClock.StartSeconds(123_456_789, 99, 480, 48_000), 9);
        // No QPC position: the callback time minus the packet's duration.
        Assert.Equal(98.99, PacketClock.StartSeconds(0, 99, 480, 48_000), 9);
    }

    [Fact]
    public void ReadsWaveFormatBlobs()
    {
        // WAVEFORMATEXTENSIBLE, 48 kHz stereo IEEE float.
        var extensible = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(extensible, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(2), 2);
        BinaryPrimitives.WriteInt32LittleEndian(extensible.AsSpan(4), 48_000);
        BinaryPrimitives.WriteInt32LittleEndian(extensible.AsSpan(8), 384_000);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(24), 3);
        Assert.Equal(new CaptureFormat(48_000, 2, 32, true), CaptureFormat.FromWaveFormatBlob(extensible));

        // Plain WAVEFORMATEX PCM, 44.1 kHz mono 16-bit.
        var pcm = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(pcm, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(pcm.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(4), 44_100);
        BinaryPrimitives.WriteUInt16LittleEndian(pcm.AsSpan(14), 16);
        Assert.Equal(new CaptureFormat(44_100, 1, 16, false), CaptureFormat.FromWaveFormatBlob(pcm));

        Assert.Null(CaptureFormat.FromWaveFormatBlob(new byte[8]));
        var compressed = (byte[])pcm.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(compressed, 0x55);
        Assert.Null(CaptureFormat.FromWaveFormatBlob(compressed));
    }
}

/// <summary>Windows-only: making up the silence WASAPI loopback does not deliver.</summary>
public class SilenceGapFillerTests
{
    [Fact]
    public void FillsNothingBeforeTheGapIsLongEnough()
    {
        var filler = new SilenceGapFiller(10);
        Assert.Equal(0, filler.Take(10.19, 48_000, out _));
        Assert.Equal(10, filler.LastEnd);
    }

    [Fact]
    public void FillsTheWholeGapAndAdvances()
    {
        var filler = new SilenceGapFiller(10);
        Assert.Equal(12_000, filler.Take(10.25, 48_000, out var end));
        Assert.Equal(10.25, end, 9);
        Assert.Equal(0, filler.Take(10.3, 48_000, out _));
    }

    [Fact]
    public void DeliveredAudioPostponesTheFill()
    {
        var filler = new SilenceGapFiller(10);
        filler.Delivered(10.2);
        Assert.Equal(0, filler.Take(10.35, 48_000, out _));
        // An older buffer never moves the end back.
        filler.Delivered(10.1);
        Assert.Equal(10.2, filler.LastEnd);
        Assert.Equal(9_600, filler.Take(10.4, 48_000, out _));
    }

    [Fact]
    public void ResetForgetsTheGap()
    {
        var filler = new SilenceGapFiller(10);
        filler.Reset(20);
        Assert.Equal(0, filler.Take(20.1, 48_000, out _));
    }
}
