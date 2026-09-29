using System.Globalization;

namespace Hearsay.Core.Audio;

/// <summary>What went wrong converting audio to Hearsay's 16 kHz mono format.</summary>
public enum AudioConversionErrorKind
{
    UnsupportedFormat,
    ConversionFailed,
}

/// <summary>
/// Errors raised while converting audio to Hearsay's 16 kHz mono format. Port
/// of <c>AudioConversionError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MonoResampler.swift. The messages
/// are the English catalog keys; the detail is technical English.
/// </summary>
public sealed class AudioConversionException : Exception, ILocalizedError
{
    public AudioConversionException()
        : this(AudioConversionErrorKind.ConversionFailed, "unknown error")
    {
    }

    public AudioConversionException(string message)
        : base(message)
    {
        Detail = message;
    }

    public AudioConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Detail = message;
    }

    public AudioConversionException(AudioConversionErrorKind kind, string detail)
        : base(kind == AudioConversionErrorKind.UnsupportedFormat
            ? $"Unsupported audio format: {detail}"
            : $"Audio conversion failed: {detail}")
    {
        Kind = kind;
        Detail = detail;
        LocalizedMessage = new LocalizedMessage(
            kind == AudioConversionErrorKind.UnsupportedFormat ? "Unsupported audio format: %@" : "Audio conversion failed: %@", detail);
    }

    public AudioConversionErrorKind Kind { get; }

    public string Detail { get; } = "";

    /// <summary>The message as its catalog key and detail; null when made from a bare message.</summary>
    public ILocalizedMessage? LocalizedMessage { get; }
}

/// <summary>
/// Streaming converter that turns interleaved Float32 frames of any sample
/// rate and channel count into 16 kHz mono Float32 samples. Keeps its
/// resampling state across calls, so consecutive buffers join without
/// clicks. Not thread-safe: use one instance from one thread at a time.
/// Port of <c>MonoResampler</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MonoResampler.swift.
/// </summary>
/// <remarks>
/// <para>The Swift class wraps <c>AVAudioConverter</c> (with <c>downmix = true</c>),
/// whose sample-rate converter is Apple's and not published. This port
/// implements the same job with published math, so the output matches the
/// Mac in count, level and timing but not bit for bit:</para>
/// <list type="bullet">
/// <item>Downmix: the mean of the channels of each frame (a stereo sine of
/// amplitude a stays at amplitude a, as <c>AVAudioConverter</c> does).</item>
/// <item>Resampling: a band-limited windowed-sinc interpolator. With
/// <c>L/M = 16000/rate</c> reduced by their gcd, output sample <c>n</c> sits at
/// input position <c>t = n*M/L</c>. Its value is
/// <c>sum x[k] h(k - t) / sum h(k - t)</c> over the <c>2K</c> input samples
/// <c>k = floor(t)-K+1 ... floor(t)+K</c>, with
/// <c>h(d) = 2fc sinc(2fc d) I0(beta sqrt(1-(d/W)^2)) / I0(beta)</c> for
/// <c>|d| &lt; W</c>, cutoff <c>fc = 0.5 * min(1, L/M) * Rolloff</c> cycles per input
/// sample, half width <c>W = ZeroCrossings / (2fc)</c>, <c>K = ceil(W)</c>.
/// Samples before the start and after the end of the input count as 0. The
/// filter is centered, so the output has no delay: sample <c>n</c> is the
/// input at time <c>n / 16000</c> s.</item>
/// <item>At 16 kHz input there is no filter: the output is the downmix.</item>
/// <item>Output count: <c>ceil(frames * L / M)</c> in total once
/// <see cref="Flush"/> has run; <see cref="Convert"/> holds back the last
/// <c>K</c> input samples' outputs until later input or the flush.</item>
/// </list>
/// <para>The input is the plain interface W3 feeds from NAudio: interleaved
/// Float32 frames (WASAPI's shared-mode mix format), given as a span.</para>
/// </remarks>
public sealed class MonoResampler
{
    public const int SampleRate = 16_000;

    /// <summary>Zero crossings of the sinc on each side of the center, at the cutoff.</summary>
    public const int ZeroCrossings = 16;

    /// <summary>Cutoff as a fraction of the lower Nyquist frequency (7.2 kHz for 16 kHz output).</summary>
    public const double Rolloff = 0.9;

    /// <summary>Kaiser window shape (about 80 dB stopband attenuation).</summary>
    public const double KaiserBeta = 8.0;

    /// <summary>Above this many phases the coefficients are computed per sample instead of tabled.</summary>
    private const int MaxTabledPhases = 1024;

    private readonly long up;
    private readonly long down;
    private readonly double cutoff;
    private readonly double halfWidth;
    private readonly int taps;
    private readonly double[][]? table;

    /// <summary>Downmixed input not yet needed by any output; <c>history[0]</c> is input index <see cref="historyStart"/>.</summary>
    private readonly List<float> history = [];
    private long historyStart;
    private long received;
    private long nextOutput;

    public MonoResampler(int inputSampleRate, int channels)
    {
        if (inputSampleRate <= 0 || channels <= 0)
        {
            throw new AudioConversionException(AudioConversionErrorKind.UnsupportedFormat,
                string.Create(CultureInfo.InvariantCulture, $"{channels} ch, {inputSampleRate} Hz"));
        }
        InputSampleRate = inputSampleRate;
        Channels = channels;
        var divisor = Gcd(inputSampleRate, SampleRate);
        up = SampleRate / divisor;
        down = inputSampleRate / divisor;
        cutoff = 0.5 * Math.Min(1.0, (double)up / down) * Rolloff;
        halfWidth = ZeroCrossings / (2 * cutoff);
        taps = (int)Math.Ceiling(halfWidth);
        if (up <= MaxTabledPhases)
        {
            table = new double[up][];
            for (var phase = 0; phase < up; phase++)
            {
                table[phase] = Coefficients((double)phase / up);
            }
        }
    }

    public int InputSampleRate { get; }

    public int Channels { get; }

    /// <summary>Taps on each side of the center (<c>K</c>).</summary>
    public int TapsPerSide => taps;

    /// <summary>
    /// Converts interleaved frames. May return fewer (or zero) samples than the
    /// ratio suggests; the remainder comes out with later buffers or <see cref="Flush"/>.
    /// </summary>
    public float[] Convert(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length % Channels != 0)
        {
            throw new AudioConversionException(AudioConversionErrorKind.ConversionFailed,
                string.Create(CultureInfo.InvariantCulture,
                    $"{interleaved.Length} samples is not a whole number of {Channels}-channel frames"));
        }
        var frames = interleaved.Length / Channels;
        history.EnsureCapacity(history.Count + frames);
        for (var frame = 0; frame < frames; frame++)
        {
            history.Add(Downmix(interleaved.Slice(frame * Channels, Channels)));
        }
        received += frames;
        if (up == down)
        {
            // Already 16 kHz: downmix only, like AVAudioConverter with equal rates.
            var mono = history.ToArray();
            history.Clear();
            historyStart = received;
            nextOutput = received;
            return mono;
        }
        return Produce(endOfStream: false);
    }

    /// <summary>Drains what the converter still holds at the end of the input.</summary>
    public float[] Flush() => Produce(endOfStream: true);

    private static float Downmix(ReadOnlySpan<float> frame)
    {
        if (frame.Length == 1)
        {
            return frame[0];
        }
        var sum = 0f;
        foreach (var value in frame)
        {
            sum += value;
        }
        return sum / frame.Length;
    }

    private float[] Produce(bool endOfStream)
    {
        // Non-final: every tap must be inside the input received so far.
        // Final: every output whose position lies before the end of the input.
        var limit = endOfStream ? (received * up + down - 1) / down : CountReady();
        if (limit <= nextOutput)
        {
            Trim();
            return [];
        }
        var output = new float[limit - nextOutput];
        for (var index = 0; index < output.Length; index++, nextOutput++)
        {
            var position = nextOutput * down;
            var center = position / up;
            var phase = position % up;
            var weights = table?[phase] ?? Coefficients((double)phase / up);
            var sum = 0.0;
            var first = center - taps + 1;
            for (var tap = 0; tap < weights.Length; tap++)
            {
                var k = first + tap - historyStart;
                if (k >= 0 && k < history.Count)
                {
                    sum += history[(int)k] * weights[tap];
                }
            }
            output[index] = (float)sum;
        }
        Trim();
        return output;
    }

    /// <summary>Outputs whose last tap, <c>floor(n*M/L) + K</c>, is below <see cref="received"/>.</summary>
    private long CountReady()
    {
        // floor(n*M/L) <= received - 1 - K  <=>  n*M < (received - K) * L.
        var bound = (received - taps) * up;
        return bound <= 0 ? 0 : (bound + down - 1) / down;
    }

    /// <summary>Drops input no later output reaches.</summary>
    private void Trim()
    {
        var firstNeeded = nextOutput * down / up - taps + 1;
        var drop = Math.Min(history.Count, firstNeeded - historyStart);
        if (drop > 4096 && drop >= history.Count / 2)
        {
            history.RemoveRange(0, (int)drop);
            historyStart += drop;
        }
    }

    /// <summary>
    /// The <c>2K</c> normalized weights for an output at fractional offset
    /// <paramref name="fraction"/> past input sample <c>floor(t)</c>; weight
    /// <c>j</c> belongs to input <c>floor(t) - K + 1 + j</c>.
    /// </summary>
    private double[] Coefficients(double fraction)
    {
        var weights = new double[2 * taps];
        var total = 0.0;
        for (var j = 0; j < weights.Length; j++)
        {
            var distance = j - taps + 1 - fraction;
            var weight = Kernel(distance, cutoff, halfWidth);
            weights[j] = weight;
            total += weight;
        }
        if (total != 0)
        {
            for (var j = 0; j < weights.Length; j++)
            {
                weights[j] /= total;
            }
        }
        return weights;
    }

    /// <summary>Kaiser-windowed sinc low-pass at <paramref name="distance"/> input samples from the output position.</summary>
    public static double Kernel(double distance, double cutoff, double halfWidth)
    {
        if (Math.Abs(distance) >= halfWidth)
        {
            return 0;
        }
        var x = 2 * cutoff * distance;
        var sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
        var ratio = distance / halfWidth;
        var window = BesselI0(KaiserBeta * Math.Sqrt(1 - ratio * ratio)) / BesselI0(KaiserBeta);
        return 2 * cutoff * sinc * window;
    }

    /// <summary>Modified Bessel function of the first kind, order 0 (power series).</summary>
    public static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var quarter = x * x / 4;
        for (var k = 1; k < 500; k++)
        {
            term *= quarter / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }
        return sum;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}

/// <summary>
/// Turns interleaved Float32 capture buffers into 16 kHz mono Float32,
/// copying directly when they already are, resampling otherwise. Port of
/// <c>PCMSampleBufferConverter</c> in MonoResampler.swift, with an
/// interleaved span in place of a <c>CMSampleBuffer</c>. Not thread-safe:
/// use one instance from one thread.
/// </summary>
public sealed class PcmBufferConverter
{
    private readonly MonoResampler? resampler;

    public PcmBufferConverter(int sampleRate, int channels)
    {
        SampleRateIn = sampleRate;
        Channels = channels;
        var isTarget = sampleRate == MonoResampler.SampleRate && channels == 1;
        resampler = isTarget ? null : new MonoResampler(sampleRate, channels);
    }

    public int SampleRateIn { get; }

    public int Channels { get; }

    public float[] Convert(ReadOnlySpan<float> interleaved) =>
        resampler is null ? interleaved.ToArray() : resampler.Convert(interleaved);

    public float[] Flush() => resampler?.Flush() ?? [];
}
