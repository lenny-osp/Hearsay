using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>
/// PLAN.md 4.1: cut at 30 s, or earlier at >= 2 s of silence (&lt; -55 dB)
/// once the chunk holds >= 10 s. Chunks are contiguous sample ranges.
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/LiveChunkerTests.swift.
/// </summary>
public class LiveChunkerTests
{
    private const int Rate = LiveChunker.SampleRate;
    private const int Window = LiveChunker.WindowSamples;
    private const double Speech = -20.0;
    private const double Silence = -70.0;

    private static SampleRange R(int start, int end) => new(start, end);

    /// <summary>Feeds <paramref name="seconds"/> of 0.1 s windows at <paramref name="db"/> and collects closed chunks.</summary>
    private static List<SampleRange> Feed(LiveChunker chunker, double seconds, double? db)
    {
        var closed = new List<SampleRange>();
        int windows = (int)Math.Round(seconds * 10, MidpointRounding.AwayFromZero);
        for (int i = 0; i < windows; i++)
        {
            closed.AddRange(chunker.Observe(chunker.SampleCount + Window, db));
        }
        return closed;
    }

    [Fact]
    public void CutsAtThirtySecondsOfSpeech()
    {
        var chunker = new LiveChunker();
        Assert.Empty(Feed(chunker, 29.9, Speech));
        Assert.Equal([R(0, 30 * Rate)], Feed(chunker, 0.1, Speech));
        Assert.Equal([R(30 * Rate, 60 * Rate)], Feed(chunker, 30, Speech));
    }

    [Fact]
    public void CutsEarlyAtTwoSecondsOfSilenceAfterTenSeconds()
    {
        var chunker = new LiveChunker();
        Assert.Empty(Feed(chunker, 12, Speech));
        Assert.Empty(Feed(chunker, 1.9, Silence));
        Assert.Equal([R(0, 14 * Rate)], Feed(chunker, 0.1, Silence));
    }

    [Fact]
    public void DigitalSilenceAndMissingLevelCountAsSilence()
    {
        var chunker = new LiveChunker();
        Feed(chunker, 10, Speech);
        Assert.Empty(Feed(chunker, 1, double.NegativeInfinity));
        Assert.Equal([R(0, 12 * Rate)], Feed(chunker, 1, null));
    }

    [Fact]
    public void ThresholdIsExclusiveLikeTheLevelMeter()
    {
        // -55 dB exactly is not above the threshold, so it is silence.
        Assert.Equal(-55.0, LiveChunker.DefaultSilenceThresholdDB);
        var chunker = new LiveChunker();
        Feed(chunker, 10, Speech);
        Assert.Equal([R(0, 12 * Rate)], Feed(chunker, 2, -55));
    }

    [Fact]
    public void SoundInterruptsTheSilenceRun()
    {
        var chunker = new LiveChunker();
        Feed(chunker, 10, Speech);
        Assert.Empty(Feed(chunker, 1.5, Silence));
        Assert.Empty(Feed(chunker, 0.1, Speech));
        Assert.Empty(Feed(chunker, 1.9, Silence));
        Assert.Equal([R(0, 136 * Rate / 10)], Feed(chunker, 0.1, Silence));
    }

    [Fact]
    public void NoCutBeforeTenSecondsEvenInSilence()
    {
        var chunker = new LiveChunker();
        Assert.Empty(Feed(chunker, 9.9, Silence));
        // Silence since the start: the cut happens the moment the chunk
        // reaches 10 s.
        Assert.Equal([R(0, 10 * Rate)], Feed(chunker, 0.1, Silence));

        var speechThenSilence = new LiveChunker();
        Assert.Empty(Feed(speechThenSilence, 3, Speech));
        Assert.Empty(Feed(speechThenSilence, 6.9, Silence));
        Assert.Equal([R(0, 10 * Rate)], Feed(speechThenSilence, 0.1, Silence));
    }

    [Fact]
    public void SilenceBeforeACutDoesNotCountForTheNextChunk()
    {
        var chunker = new LiveChunker();
        Feed(chunker, 10, Speech);
        Assert.Equal([R(0, 12 * Rate)], Feed(chunker, 2, Silence));
        // Continuing silence needs a fresh 10 s chunk before the next cut.
        Assert.Empty(Feed(chunker, 9.9, Silence));
        Assert.Equal([R(12 * Rate, 22 * Rate)], Feed(chunker, 0.1, Silence));
    }

    [Fact]
    public void FlushReturnsTheRemainderOnce()
    {
        var chunker = new LiveChunker();
        Feed(chunker, 35, Speech);
        Assert.Equal(R(30 * Rate, 35 * Rate), chunker.Flush());
        Assert.Null(chunker.Flush());

        var empty = new LiveChunker();
        Assert.Null(empty.Flush());
    }

    [Fact]
    public void OffsetsAreContiguous()
    {
        var chunker = new LiveChunker();
        var closed = new List<SampleRange>();
        // Irregular input: speech, pauses, and uneven block sizes.
        (int Count, double Db)[] pattern =
        [
            (8_000, Speech), (1_600, Silence), (32_000, Silence), (240_000, Speech),
            (3_200, Silence), (40_000, Silence), (500_000, Speech), (1_600, Silence), (777, Speech),
        ];
        for (int i = 0; i < 5; i++)
        {
            foreach (var (count, db) in pattern)
            {
                closed.AddRange(chunker.Observe(chunker.SampleCount + count, db));
            }
        }
        if (chunker.Flush() is { } tail) closed.Add(tail);
        Assert.NotEmpty(closed);
        Assert.Equal(0, closed[0].Start);
        Assert.Equal(chunker.SampleCount, closed[^1].End);
        for (int i = 1; i < closed.Count; i++)
        {
            Assert.Equal(closed[i - 1].End, closed[i].Start);
        }
        Assert.All(closed, range => Assert.True(!range.IsEmpty && range.Count <= 30 * Rate, range.ToString()));
    }

    [Fact]
    public void LargeJumpProducesSeveralHardCuts()
    {
        var chunker = new LiveChunker();
        Assert.Equal(
            [R(0, 30 * Rate), R(30 * Rate, 60 * Rate)],
            chunker.Observe(65 * Rate, Speech));
        Assert.Equal(R(60 * Rate, 65 * Rate), chunker.Flush());
    }

    [Fact]
    public void NonGrowingCountIsIgnored()
    {
        var chunker = new LiveChunker();
        chunker.Observe(100, Speech);
        Assert.Empty(chunker.Observe(100, Silence));
        Assert.Equal(100, chunker.SampleCount);
    }
}
