using Hearsay.Core.Audio;

namespace Hearsay.Core.Transcription;

/// <summary>
/// A half-open range of sample offsets, <c>Start..&lt;End</c> (Swift's
/// <c>Range&lt;Int&gt;</c> in LiveChunker.swift).
/// </summary>
public readonly record struct SampleRange(int Start, int End)
{
    public int Count => End - Start;

    public bool IsEmpty => End <= Start;

    public override string ToString() => $"{Start}..<{End}";
}

/// <summary>
/// Decides where the live preview cuts the recording into chunks (PLAN.md 4.1
/// step 4): a chunk closes at 30 s, or earlier once it holds at least 10 s and
/// the last 2 s were below the silence threshold.
/// Port of mac/HearsayCore/Sources/HearsayCore/Transcription/LiveChunker.swift.
/// </summary>
/// <remarks>
/// The caller feeds the running number of 16 kHz samples recorded so far
/// together with the RMS level of the samples added since the previous call
/// (normally one 0.1 s window, see <see cref="WindowSamples"/>). Closed chunks
/// come back as sample offsets into the recording; together with
/// <see cref="Flush"/> they cover the recording contiguously from sample 0,
/// with no gaps or overlaps.
/// No clock: time is the sample count, so tests are exact.
/// </remarks>
public sealed class LiveChunker
{
    public const int SampleRate = 16_000;

    /// <summary>Hard cut: a chunk never holds more than this.</summary>
    public const double MaxChunkSeconds = 30;

    /// <summary>A silence cut needs at least this much audio in the chunk.</summary>
    public const double MinChunkSeconds = 10;

    /// <summary>Trailing silence that allows an early cut.</summary>
    public const double SilenceCutSeconds = 2;

    /// <summary>Same threshold as the level meter's silence warning (-55 dBFS).</summary>
    public const double DefaultSilenceThresholdDB = LevelMeter.SilenceThresholdDB;

    /// <summary>Samples per level window the caller should measure (0.1 s).</summary>
    public const int WindowSamples = SampleRate / 10;

    /// <summary>Where the current run of silent windows began, null after sound.</summary>
    private int? silentSince;

    public LiveChunker(
        int sampleRate = SampleRate,
        double maxChunkSeconds = MaxChunkSeconds,
        double minChunkSeconds = MinChunkSeconds,
        double silenceCutSeconds = SilenceCutSeconds,
        double silenceThresholdDB = DefaultSilenceThresholdDB)
    {
        MaxChunkSamples = ToSamples(maxChunkSeconds, sampleRate);
        MinChunkSamples = ToSamples(minChunkSeconds, sampleRate);
        SilenceCutSamples = ToSamples(silenceCutSeconds, sampleRate);
        SilenceThresholdDB = silenceThresholdDB;
    }

    public int MaxChunkSamples { get; }

    public int MinChunkSamples { get; }

    public int SilenceCutSamples { get; }

    public double SilenceThresholdDB { get; }

    /// <summary>Start of the chunk that is still open.</summary>
    public int ChunkStart { get; private set; }

    /// <summary>Samples observed so far.</summary>
    public int SampleCount { get; private set; }

    /// <summary>
    /// Records that the recording now holds <paramref name="totalSamples"/>
    /// samples and that the samples added since the last call measured
    /// <paramref name="rmsDB"/> (null or negative infinity count as silence, as
    /// in <c>LevelMeter</c>). Returns the chunks that closed, oldest first;
    /// usually none. A count that does not grow is ignored.
    /// </summary>
    public IReadOnlyList<SampleRange> Observe(int totalSamples, double? rmsDB)
    {
        if (totalSamples <= SampleCount) return [];
        int windowStart = SampleCount;
        SampleCount = totalSamples;

        bool isSound = rmsDB is { } db && db > SilenceThresholdDB;
        if (isSound)
        {
            silentSince = null;
        }
        else if (silentSince is null)
        {
            silentSince = windowStart;
        }

        var closed = new List<SampleRange>();
        // Hard cuts at exactly 30 s, several if the caller jumped far ahead.
        while (SampleCount - ChunkStart >= MaxChunkSamples)
        {
            closed.Add(Cut(ChunkStart + MaxChunkSamples));
        }
        // Early cut at the end of a long enough silence.
        if (silentSince is { } since
            && SampleCount - ChunkStart >= MinChunkSamples
            && SampleCount - Math.Max(since, ChunkStart) >= SilenceCutSamples)
        {
            closed.Add(Cut(SampleCount));
        }
        return closed;
    }

    /// <summary>Closes the open chunk on Stop. Null when it is empty.</summary>
    public SampleRange? Flush()
    {
        if (SampleCount <= ChunkStart) return null;
        return Cut(SampleCount);
    }

    private SampleRange Cut(int end)
    {
        var range = new SampleRange(ChunkStart, end);
        ChunkStart = end;
        if (silentSince is { } since)
        {
            // Silence before the new chunk does not count toward its cut;
            // right after a silence cut the run starts over.
            silentSince = end >= SampleCount ? null : Math.Max(since, end);
        }
        return range;
    }

    // Swift's rounded() rounds half away from zero.
    private static int ToSamples(double seconds, int sampleRate) =>
        (int)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);
}
