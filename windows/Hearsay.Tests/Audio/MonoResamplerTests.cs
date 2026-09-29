using Hearsay.Core.Audio;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Tests of <see cref="MonoResampler"/> and <see cref="PcmBufferConverter"/>.
/// Ports <c>sampleBufferConverterPassesTargetFormatThrough</c>,
/// <c>sampleBufferConverterResamples48kStereo</c> (MicrophoneDiagnosticsTests)
/// and the resampling half of <c>resamplesStereo44kToMono16k</c>
/// (AudioFileLoaderTests) from mac/HearsayCore/Tests/HearsayCoreTests/AudioTests.swift.
/// The Swift class wraps AVAudioConverter, so the rest proves the C# math
/// against a direct reference implementation and against hand-computed signals.
/// </summary>
public class MonoResamplerTests
{
    private static float[] Tone(int rate, int channels, int frames, double frequency, double amplitude)
    {
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / rate));
            for (var channel = 0; channel < channels; channel++)
            {
                samples[frame * channels + channel] = value;
            }
        }
        return samples;
    }

    private static float[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 1.6 - 0.8)).ToArray();
    }

    /// <summary>Converts in chunks of the given sizes (cycled), then flushes.</summary>
    private static float[] Stream(MonoResampler resampler, float[] interleaved, params int[] frameSizes)
    {
        var output = new List<float>();
        var channels = resampler.Channels;
        var position = 0;
        var turn = 0;
        while (position < interleaved.Length)
        {
            var size = Math.Min(frameSizes[turn++ % frameSizes.Length] * channels, interleaved.Length - position);
            output.AddRange(resampler.Convert(interleaved.AsSpan(position, size)));
            position += size;
        }
        output.AddRange(resampler.Flush());
        return output.ToArray();
    }

    /// <summary>
    /// The resampling formula of MonoResampler's summary, evaluated directly for
    /// every output sample from the whole input (no streaming, no tables).
    /// </summary>
    private static double[] Reference(float[] interleaved, int rate, int channels)
    {
        var frames = interleaved.Length / channels;
        var mono = new double[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += interleaved[frame * channels + channel];
            }
            mono[frame] = sum / channels;
        }
        var cutoff = 0.5 * Math.Min(1.0, 16_000.0 / rate) * 0.9;
        var halfWidth = 16 / (2 * cutoff);
        var k = (int)Math.Ceiling(halfWidth);
        var count = (int)(((long)frames * 16_000 + rate - 1) / rate);
        var output = new double[count];
        var i0Beta = I0(8.0);
        for (var n = 0; n < count; n++)
        {
            var center = (long)n * rate / 16_000;
            var t = center + (double)((long)n * rate % 16_000) / 16_000;
            double numerator = 0, denominator = 0;
            for (var index = center - k + 1; index <= center + k; index++)
            {
                var d = index - t;
                var h = 0.0;
                if (Math.Abs(d) < halfWidth)
                {
                    var x = 2 * cutoff * d;
                    var sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    var r = d / halfWidth;
                    h = 2 * cutoff * sinc * I0(8.0 * Math.Sqrt(1 - r * r)) / i0Beta;
                }
                denominator += h;
                if (index >= 0 && index < frames)
                {
                    numerator += mono[index] * h;
                }
            }
            output[n] = numerator / denominator;
        }
        return output;
    }

    /// <summary>I0 by its power series, written independently of the code under test.</summary>
    private static double I0(double x)
    {
        double sum = 0, factorial = 1;
        for (var m = 0; m < 60; m++)
        {
            if (m > 0)
            {
                factorial *= m;
            }
            var term = Math.Pow(x / 2, m) / factorial;
            sum += term * term;
        }
        return sum;
    }

    private static double RmsDB(ReadOnlySpan<float> samples) => LevelMeter.RmsDB(samples) ?? double.NaN;

    [Fact]
    public void ConverterPassesTargetFormatThrough()
    {
        var converter = new PcmBufferConverter(16_000, 1);
        float[] samples = [0, 0.25f, -0.5f, 1];
        Assert.Equal(samples, converter.Convert(samples));
        Assert.Empty(converter.Flush());
    }

    [Fact]
    public void ConverterResamples48kStereo()
    {
        var converter = new PcmBufferConverter(48_000, 2);
        var total = 0;
        for (var index = 0; index < 10; index++)
        {
            total += converter.Convert(Enumerable.Repeat(0.5f, 4800 * 2).ToArray()).Length;
        }
        total += converter.Flush().Length;
        Assert.True(Math.Abs(total - 16_000) <= 32, $"got {total}");
        // Exact with this resampler: ceil(48000 * 1 / 3).
        Assert.Equal(16_000, total);
    }

    [Fact]
    public void ResamplesStereo44kToMono16k()
    {
        var input = Tone(44_100, 2, 44_100 * 2, 440, 0.25);
        var samples = Stream(new MonoResampler(44_100, 2), input, 441, 1024, 4410);
        Assert.True(Math.Abs(samples.Length - 32_000) <= 16);
        var peak = samples.Max(Math.Abs);
        Assert.True(peak > 0.2 && peak < 0.3, $"peak {peak}");
    }

    [Theory]
    [InlineData(48_000, 2)]
    [InlineData(44_100, 2)]
    [InlineData(44_100, 1)]
    [InlineData(96_000, 2)]
    [InlineData(32_000, 1)]
    [InlineData(24_000, 1)]
    [InlineData(22_050, 1)]
    [InlineData(11_025, 1)]   // 640 phases, tabled
    [InlineData(8_000, 1)]    // upsampling
    [InlineData(44_056, 6)]   // 2000 phases, computed per sample; 5.1 downmix
    public void StreamingMatchesTheDirectFormula(int rate, int channels)
    {
        var frames = rate / 16 + 37;
        var input = Noise(frames * channels, seed: rate + channels);
        var reference = Reference(input, rate, channels);
        var output = Stream(new MonoResampler(rate, channels), input, 480, 1, 1031, 7);
        Assert.Equal(reference.Length, output.Length);
        for (var index = 0; index < output.Length; index++)
        {
            Assert.True(Math.Abs(reference[index] - output[index]) < 1e-5,
                $"sample {index}: reference {reference[index]}, got {output[index]}");
        }
    }

    [Fact]
    public void ChunkBoundariesDoNotChangeTheOutput()
    {
        var input = Noise(48_000 * 2, seed: 7);
        var whole = Stream(new MonoResampler(48_000, 2), input, 48_000);
        var pieces = Stream(new MonoResampler(48_000, 2), input, 1, 2, 3, 480, 511, 4096);
        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void ConstantLevelIsKeptExactlyAwayFromTheEdges()
    {
        var resampler = new MonoResampler(44_100, 2);
        var output = Stream(resampler, Enumerable.Repeat(0.5f, 44_100 * 2).ToArray(), 4410);
        Assert.Equal(16_000, output.Length);
        // The first and last outputs see zeros before and after the input.
        var margin = resampler.TapsPerSide * 16_000 / 44_100 + 1;
        for (var index = margin; index < output.Length - margin; index++)
        {
            Assert.True(Math.Abs(output[index] - 0.5f) < 1e-6, $"sample {index}: {output[index]}");
        }
    }

    [Fact]
    public void PassbandToneKeepsAmplitudeAndPhase()
    {
        // 1 kHz at 48 kHz: the output must be the same sine sampled at 16 kHz
        // (no delay, no level change).
        var output = Stream(new MonoResampler(48_000, 1), Tone(48_000, 1, 48_000, 1000, 0.5), 480);
        Assert.Equal(16_000, output.Length);
        for (var n = 200; n < output.Length - 200; n++)
        {
            var expected = 0.5 * Math.Sin(2 * Math.PI * 1000 * n / 16_000);
            Assert.True(Math.Abs(output[n] - expected) < 1e-3, $"sample {n}: expected {expected}, got {output[n]}");
        }
    }

    [Fact]
    public void ToneAboveTheOutputNyquistIsRemoved()
    {
        // 10 kHz cannot exist at 16 kHz; aliased it would land at 6 kHz.
        var output = Stream(new MonoResampler(48_000, 1), Tone(48_000, 1, 48_000, 10_000, 0.5), 480);
        var level = RmsDB(output.AsSpan(200, output.Length - 400));
        Assert.True(level < -60 - 9, $"level {level} dBFS"); // input is -9 dBFS
    }

    [Fact]
    public void UpsamplingKeepsTheTone()
    {
        var output = Stream(new MonoResampler(8_000, 1), Tone(8_000, 1, 8_000, 440, 0.5), 160);
        Assert.Equal(16_000, output.Length);
        for (var n = 200; n < output.Length - 200; n++)
        {
            var expected = 0.5 * Math.Sin(2 * Math.PI * 440 * n / 16_000);
            Assert.True(Math.Abs(output[n] - expected) < 1e-3, $"sample {n}");
        }
    }

    [Fact]
    public void StereoIsTheMeanOfTheChannels()
    {
        // Same rate: no filter, so the output is exactly the mean.
        var output = Stream(new MonoResampler(16_000, 2), [1f, 0f, 0.5f, -0.5f, -1f, 0f], 1, 2);
        Assert.Equal([0.5f, 0f, -0.5f], output);
    }

    [Fact]
    public void RejectsUnusableFormats()
    {
        Assert.Equal(AudioConversionErrorKind.UnsupportedFormat,
            Assert.Throws<AudioConversionException>(() => new MonoResampler(0, 2)).Kind);
        Assert.Equal(AudioConversionErrorKind.UnsupportedFormat,
            Assert.Throws<AudioConversionException>(() => new MonoResampler(48_000, 0)).Kind);
        var resampler = new MonoResampler(48_000, 2);
        Assert.Equal(AudioConversionErrorKind.ConversionFailed,
            Assert.Throws<AudioConversionException>(() => resampler.Convert([0.1f, 0.2f, 0.3f])).Kind);
    }
}
