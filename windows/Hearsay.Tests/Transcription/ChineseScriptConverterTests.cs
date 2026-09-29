using System.Text;
using Hearsay.Core.Transcription;
using Xunit.Abstractions;

namespace Hearsay.Tests.Transcription;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/ChineseScriptTests.swift,
/// plus the zh-30s fixture check from windows/Spike/TextSpike/REPORT.md.
/// </summary>
public class ChineseScriptConverterTests(ITestOutputHelper output)
{
    private const string Simplified = "那天我来到了中国最冷的城市";
    private const string Traditional = "那天我來到了中國最冷的城市";

    [Fact]
    public void SimplifiedToTraditional() =>
        Assert.Equal(Traditional, ChineseScriptConverter.Convert(Simplified, ChineseScript.Traditional));

    [Fact]
    public void TraditionalToSimplified() =>
        Assert.Equal(Simplified, ChineseScriptConverter.Convert(Traditional, ChineseScript.Simplified));

    [Fact]
    public void NullLeavesTextUnchanged()
    {
        Assert.Equal(Simplified, ChineseScriptConverter.Convert(Simplified, null));
        Assert.Equal(Traditional, ChineseScriptConverter.Convert(Traditional, null));
    }

    [Fact]
    public void OnlyTwoScripts() =>
        Assert.Equal([ChineseScript.Traditional, ChineseScript.Simplified], Enum.GetValues<ChineseScript>());

    [Fact]
    public void AsciiAndEnglishUnchanged()
    {
        foreach (var text in new[] { "", "Hello, world. 123 -> ok!", "The quick brown fox jumps over the lazy dog." })
        {
            foreach (var script in Enum.GetValues<ChineseScript>())
            {
                Assert.Equal(text, ChineseScriptConverter.Convert(text, script));
            }
        }
    }

    [Fact]
    public void MixedTextKeepsEnglish()
    {
        Assert.Equal("我們用 Swift 和 MLX 開發軟件", ChineseScriptConverter.Convert("我们用 Swift 和 MLX 开发软件", ChineseScript.Traditional));
        Assert.Equal("我们用 Swift 和 MLX 开发软件", ChineseScriptConverter.Convert("我們用 Swift 和 MLX 開發軟件", ChineseScript.Simplified));
    }

    [Fact]
    public void ChineseVariantsPickTheirScript()
    {
        TranscriptSegment[] segments = [new(0, 1, Simplified)];
        Assert.Equal(
            [new TranscriptSegment(0, 1, Traditional)],
            ChineseScriptConverter.Convert(segments, TranscriptLanguage.ChineseTaiwan.ChineseScript()));
        TranscriptSegment[] traditional = [new(0, 1, Traditional)];
        Assert.Equal(segments, ChineseScriptConverter.Convert(traditional, TranscriptLanguage.ChineseMainland.ChineseScript()));
    }

    [Fact]
    public void NonChineseSessionsAreUntouched()
    {
        TranscriptSegment[] segments = [new(0, 1, Simplified)];
        foreach (var language in new[] { TranscriptLanguage.English, TranscriptLanguage.German, TranscriptLanguage.Spanish })
        {
            Assert.Null(language.ChineseScript());
            Assert.Equal(segments, ChineseScriptConverter.Convert(segments, language.ChineseScript()));
        }
    }

    [Fact]
    public void SegmentsKeepTimings()
    {
        TranscriptSegment[] segments = [new(0, 1.5, Simplified), new(1.5, 3, "OK")];
        Assert.Equal(
            [new TranscriptSegment(0, 1.5, Traditional), new TranscriptSegment(1.5, 3, "OK")],
            ChineseScriptConverter.Convert(segments, ChineseScript.Traditional));
        Assert.Equal(segments, ChineseScriptConverter.Convert(segments, null));
    }

    // Windows additions

    /// <summary>
    /// The raw model output in zh-30s.expected.srt (mostly Simplified)
    /// converted to Traditional is what the Mac writes for ZH-TW: the truth
    /// file with 臘七→臘漆 and 裏→里 (PLAN.md 18.8; spike report A.1).
    /// </summary>
    [Fact]
    public void ZhFixtureToTraditionalMatchesTheMac()
    {
        var expected = Srt.Parse(SharedFiles.ReadText("fixtures", "zh-30s.expected.srt"));
        var truth = Srt.Parse(SharedFiles.ReadText("fixtures", "zh-30s.truth.srt"));
        var mac = truth
            .Select(s => s with { Text = s.Text.Replace("臘七", "臘漆", StringComparison.Ordinal).Replace('裏', '里') })
            .ToArray();
        var converted = ChineseScriptConverter.Convert(expected, ChineseScript.Traditional);
        Assert.Equal(mac.Select(s => s.Text), converted.Select(s => s.Text));
        Assert.Equal(mac, converted);
        string rendered = Srt.Render(converted);
        Assert.Equal(494, Encoding.UTF8.GetByteCount(rendered));
        Assert.Equal(Srt.Render(mac), rendered);
    }

    [Fact]
    public void ZhFixtureToSimplifiedConvertsTheOneTraditionalCharacter()
    {
        var expected = Srt.Parse(SharedFiles.ReadText("fixtures", "zh-30s.expected.srt"));
        var converted = ChineseScriptConverter.Convert(expected, ChineseScript.Simplified);
        Assert.Equal("按照北京的老规矩", converted[0].Text);
        Assert.Equal(expected.Skip(1), converted.Skip(1));
    }

    [Fact]
    public void SmallestBufferStillConverts()
    {
        var text = string.Concat(Enumerable.Repeat(Simplified + " OK ", 50));
        Assert.Equal(
            ChineseScriptConverter.Convert(text, ChineseScript.Traditional),
            ChineseScriptConverter.Convert(text, ChineseScript.Traditional, initialCapacity: text.Length));
        Assert.Equal(Traditional, ChineseScriptConverter.Convert(Simplified, ChineseScript.Traditional, initialCapacity: 1));
    }

    [Fact]
    public async Task ConcurrentCallsAgree()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            ChineseScriptConverter.Convert(i % 2 == 0 ? Simplified : Traditional,
                i % 2 == 0 ? ChineseScript.Traditional : ChineseScript.Simplified))));
        for (int i = 0; i < results.Length; i++)
        {
            Assert.Equal(i % 2 == 0 ? Traditional : Simplified, results[i]);
        }
    }

    [Fact]
    public void IcuVersionIsReported()
    {
        var version = ChineseScriptConverter.IcuVersion();
        output.WriteLine($"ICU version (u_getVersion): {version}");
        var parts = version.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.True(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) >= 64, version);
    }
}
