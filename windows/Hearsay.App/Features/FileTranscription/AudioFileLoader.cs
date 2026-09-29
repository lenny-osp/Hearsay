using System.Runtime.InteropServices;
using Hearsay.Core.Audio;
using NAudio.Wave;

namespace Hearsay.App.Features.FileTranscription;

/// <summary>
/// Decodes any file Media Foundation can read (wav, m4a, mp3, aac, wma,
/// flac, the audio track of mp4 / mov, ...) to 16 kHz mono Float32 (PLAN.md
/// 4.2 step 2). Port of <c>AudioFileLoader</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/AudioFileLoader.swift, which
/// reads with <c>AVAudioFile</c> and converts with <c>AVAudioConverter</c>.
/// </summary>
/// <remarks>
/// The decoder is NAudio's <see cref="MediaFoundationReader"/>, which ships in
/// the NAudio.Wasapi package Hearsay.Core already uses for capture (PLAN.md
/// 18.3), so no package is added; it asks Media Foundation for IEEE float
/// PCM at the file's own rate and channel count. The downmix and the
/// resampling to 16 kHz are Core's <see cref="MonoResampler"/>, the same
/// filter the recorders use (PLAN.md 18.4, "Resampler"). Windows.Media's
/// <c>AudioGraph</c> was the alternative; it needs a WinRT async graph per
/// file and resamples with its own filter, so recordings and files would be
/// resampled differently. Lives in the app rather than Hearsay.Core because
/// Core targets plain net10.0.
/// </remarks>
internal static class AudioFileLoader
{
    /// <summary>Samples per decoder read, per channel (about 0.1 s at 48 kHz).</summary>
    private const int ReadFrames = 4800;

    /// <exception cref="AudioFileLoaderException">The file cannot be opened or decoded.</exception>
    public static float[] LoadMono16k(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new AudioFileLoaderException(Strings.FileNotFound(Path.GetFileName(path)));
        MediaFoundationReader reader;
        try
        {
            reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true,
                RepositionInRead = false,
            });
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or IOException
                                          or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AudioFileLoaderException(error.Message, error);
        }
        using (reader)
        {
            var format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32 || format.Channels < 1)
            {
                throw new AudioFileLoaderException(
                    Strings.UnexpectedDecodedFormat(format.Encoding.ToString(), format.BitsPerSample, format.Channels));
            }
            var resampler = new MonoResampler(format.SampleRate, format.Channels);
            var output = new List<float>(capacity: (int)Math.Min(int.MaxValue / 2, reader.Length / 4 / format.Channels * 16_000 / Math.Max(1, format.SampleRate) + 16_000));
            var bytes = new byte[ReadFrames * format.Channels * 4];
            var floats = new float[ReadFrames * format.Channels];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = reader.Read(bytes, 0, bytes.Length);
                }
                catch (Exception error) when (error is COMException or InvalidOperationException or IOException)
                {
                    throw new AudioFileLoaderException(error.Message, error);
                }
                if (read <= 0) break;
                var count = read / 4;
                Buffer.BlockCopy(bytes, 0, floats, 0, count * 4);
                // Whole frames only; a decoder never splits one, but stay safe.
                count -= count % format.Channels;
                output.AddRange(resampler.Convert(floats.AsSpan(0, count)));
            }
            output.AddRange(resampler.Flush());
            return [.. output];
        }
    }
}

/// <summary>A file could not be decoded; the message is ready for the File tab.</summary>
internal sealed class AudioFileLoaderException : Exception
{
    public AudioFileLoaderException()
    {
    }

    public AudioFileLoaderException(string message)
        : base(message)
    {
    }

    public AudioFileLoaderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
