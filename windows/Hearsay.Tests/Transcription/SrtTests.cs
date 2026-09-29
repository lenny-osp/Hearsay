using System.Text;
using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/SRTTests.swift, plus a
/// byte round trip of the English fixture.
/// </summary>
public class SrtTests
{
    // clean_srt_text assertion in test_srt_cleanup_and_prompt_language
    [Fact]
    public void CleanTextStripsIndicesAndTimestamps()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n";
        Assert.Equal("Hello\nWorld\n", Srt.CleanText(srt));
    }

    [Fact]
    public void CleanTextKeepsOriginalLineEndings()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld";
        Assert.Equal("Hello\r\nWorld", Srt.CleanText(srt));
        Assert.Equal("", Srt.CleanText(""));
        Assert.Equal("", Srt.CleanText("1\n\n"));
        // Only a line that is all digits is an index; "12 apples" is speech.
        Assert.Equal("12 apples\n", Srt.CleanText("12 apples\n"));
    }

    [Fact]
    public void CleanTextSplitsLikePython()
    {
        // str.splitlines also breaks at U+2028 and form feed, but only CR and
        // LF are stripped before the checks, so "1" + U+2028 is not an index.
        Assert.Equal("a\u20281\u2028b\u000C", Srt.CleanText("a\u20281\u2028b\u000C00:00:01 x\r"));
        // A lone CR ends a line.
        Assert.Equal("Hi\r", Srt.CleanText("1\rHi\r"));
    }

    [Fact]
    public void RenderProducesStandardSrt()
    {
        TranscriptSegment[] segments =
        [
            new(1, 2, "Hello"),
            new(3661.5, 3662.25, "World"),
        ];
        Assert.Equal(
            "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n01:01:01,500 --> 01:01:02,250\nWorld\n\n",
            Srt.Render(segments));
        Assert.Equal("", Srt.Render([]));
    }

    [Fact]
    public void TimestampRoundsAndClamps()
    {
        Assert.Equal("00:00:00,000", Srt.Timestamp(-1));
        Assert.Equal("00:00:00,000", Srt.Timestamp(double.NaN));
        Assert.Equal("00:00:00,000", Srt.Timestamp(double.PositiveInfinity));
        Assert.Equal("00:00:01,000", Srt.Timestamp(0.9996));
        Assert.Equal("100:00:00,000", Srt.Timestamp(360000));
    }

    [Fact]
    public void RenderThenParseRoundTrips()
    {
        TranscriptSegment[] segments =
        [
            new(0, 1.5, "First line"),
            new(1.5, 3.25, "Two\nlines"),
            new(3661.001, 3700.999, "Late"),
        ];
        Assert.Equal(segments, Srt.Parse(Srt.Render(segments)));
    }

    [Fact]
    public void ParseIsLenient()
    {
        const string text = "\uFEFF1\r\n00:00:01.000 --> 00:00:02,5\r\nHello\r\n\r\n00:00:03,000 --> 00:00:04,000\r\nWorld";
        Assert.Equal(
            [new TranscriptSegment(1, 2.5, "Hello"), new TranscriptSegment(3, 4, "World")],
            Srt.Parse(text));
        Assert.Empty(Srt.Parse(""));
        Assert.Empty(Srt.Parse("just some text\n"));
    }

    [Fact]
    public void CleanTextOfRenderedSegments()
    {
        TranscriptSegment[] segments =
        [
            new(1, 2, "Hello"),
            new(3, 4, "World"),
        ];
        Assert.Equal("Hello\nWorld\n", Srt.CleanText(Srt.Render(segments)));
    }

    /// <summary>The English reference SRT parses and re-renders to the identical bytes.</summary>
    [Fact]
    public void EnglishFixtureRoundTripsByteForByte()
    {
        var bytes = File.ReadAllBytes(SharedFiles.Path("fixtures", "en-30s.expected.srt"));
        var text = SharedFiles.ReadText("fixtures", "en-30s.expected.srt");
        var segments = Srt.Parse(text);
        Assert.NotEmpty(segments);
        Assert.Equal(bytes, new UTF8Encoding(false).GetBytes(Srt.Render(segments)));
    }
}
