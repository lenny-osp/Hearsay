using System.Buffers.Binary;

namespace Hearsay.Whisper;

/// <summary>
/// Reads a 16 kHz mono 16-bit PCM WAV into Float32 samples (s / 32768),
/// walking the RIFF chunks so Apple's <c>FLLR</c> padding in the fixtures is
/// skipped (PLAN.md 18.4, "Fixture WAV layout"). Used for the speed probe's
/// embedded clip and by the tests; File mode decodes other formats elsewhere.
/// No Swift counterpart (the Mac reads WAVs with AVFoundation).
/// </summary>
internal static class PcmWav
{
    /// <exception cref="InvalidDataException">Not a RIFF/WAVE file, or not 16 kHz mono 16-bit PCM.</exception>
    public static float[] ReadMono16k(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < 12
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"{name}: not a RIFF/WAVE file.");
        }
        int position = 12;
        int format = 0, channels = 0, rate = 0, bits = 0;
        while (position + 8 <= bytes.Length)
        {
            var id = bytes.Slice(position, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 4, 4));
            int body = position + 8;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= bytes.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || channels != 1 || rate != SilenceGate.SampleRate || bits != 16)
                {
                    throw new InvalidDataException(
                        $"{name}: need 16 kHz mono 16-bit PCM, got format {format}, {rate} Hz, {channels} channels, {bits} bits.");
                }
                int available = (int)Math.Min(size, bytes.Length - body);
                var data = bytes.Slice(body, available - available % 2);
                var samples = new float[data.Length / 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2 * i, 2)) / 32768f;
                }
                return samples;
            }
            long next = body + size + (size & 1);
            if (next > int.MaxValue) break;
            position = (int)next;
        }
        throw new InvalidDataException($"{name}: no data chunk.");
    }

    public static float[] ReadMono16k(string path) => ReadMono16k(File.ReadAllBytes(path), path);
}
