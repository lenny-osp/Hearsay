using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/TranscriptTextLanguageTests.swift,
/// plus the fixture checks from windows/Spike/TextSpike/REPORT.md (B.2).
/// Tests assert the language and whether it is confident, never the number.
/// </summary>
public class TranscriptTextLanguageTests
{
    private static (TranscriptLanguage Language, double Probability)? ConfidentResult(string text) =>
        TranscriptTextLanguage.Detect(text) is { } result && result.Probability >= TranscriptTextLanguage.ConfidenceThreshold
            ? result
            : null;

    [Fact]
    public void Thresholds()
    {
        Assert.Equal(0.6, TranscriptTextLanguage.ConfidenceThreshold);
        Assert.Equal(4_000, TranscriptTextLanguage.SampleLength);
        Assert.Equal(12, TranscriptTextLanguage.MinimumLetters);
    }

    [Fact]
    public void English() => Assert.Equal(
        TranscriptLanguage.English,
        ConfidentResult("Let's start with the budget review. We need to finish the report before Friday "
            + "and send it to the whole team.")?.Language);

    [Fact]
    public void German() => Assert.Equal(
        TranscriptLanguage.German,
        ConfidentResult("Wir fangen mit dem Budget an. Der Bericht muss bis Freitag fertig sein, "
            + "dann schicken wir ihn an das ganze Team.")?.Language);

    [Fact]
    public void Spanish() => Assert.Equal(
        TranscriptLanguage.Spanish,
        ConfidentResult("Empezamos con el presupuesto. Tenemos que terminar el informe antes del viernes "
            + "y enviarlo a todo el equipo.")?.Language);

    [Fact]
    public void TraditionalChinese() => Assert.Equal(
        TranscriptLanguage.ChineseTaiwan,
        ConfidentResult("我們先從預算開始討論。報告必須在星期五之前完成，然後寄給整個團隊。")?.Language);

    [Fact]
    public void SimplifiedChinese() => Assert.Equal(
        TranscriptLanguage.ChineseMainland,
        ConfidentResult("我们先从预算开始讨论。报告必须在星期五之前完成，然后发给整个团队。")?.Language);

    [Fact]
    public void NoLettersIsNull()
    {
        Assert.Null(TranscriptTextLanguage.Detect(""));
        Assert.Null(TranscriptTextLanguage.Detect("12 34 !?"));
        Assert.Null(TranscriptTextLanguage.Confident(""));
    }

    [Fact]
    public void TooShortOrMixedIsNotConfident()
    {
        // Too few letters to judge: detection returns null, so the caller
        // falls back to the assumed language.
        Assert.Null(TranscriptTextLanguage.Detect("ok"));
        Assert.Null(TranscriptTextLanguage.Detect("OK 好"));
        Assert.Null(ConfidentResult("ok"));
        Assert.Null(ConfidentResult("OK 好"));
        Assert.Null(TranscriptTextLanguage.Confident("OK 好"));
        Assert.Null(TranscriptTextLanguage.Detect(string.Concat(Enumerable.Repeat("a ", TranscriptTextLanguage.MinimumLetters - 1))));
        Assert.NotNull(TranscriptTextLanguage.Detect("Guten Morgen zusammen, willkommen"));
    }

    [Fact]
    public void SrtTimingIsIgnored()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:04,000\nWir fangen mit dem Budget an und besprechen dann den Bericht.\n\n"
            + "2\n00:00:04,000 --> 00:00:08,000\nDer Bericht muss bis Freitag fertig sein.\n\n";
        Assert.Equal(TranscriptLanguage.German, TranscriptTextLanguage.DetectSrt(srt)?.Language);
    }

    // Windows additions

    [Theory]
    [InlineData("en-30s.expected.srt", TranscriptLanguage.English)]
    [InlineData("de-30s.expected.srt", TranscriptLanguage.German)]
    [InlineData("es-30s.expected.srt", TranscriptLanguage.Spanish)]
    [InlineData("zh-30s.expected.srt", TranscriptLanguage.ChineseMainland)] // raw model output, mostly Simplified
    [InlineData("zh-30s.truth.srt", TranscriptLanguage.ChineseTaiwan)]
    public void FixturesAreConfident(string file, TranscriptLanguage language)
    {
        var srt = SharedFiles.ReadText("fixtures", file);
        Assert.Equal(language, ConfidentResult(Srt.CleanText(srt))?.Language);
        Assert.Equal(language, TranscriptTextLanguage.Confident(Srt.CleanText(srt)));
        Assert.Equal(language, TranscriptTextLanguage.DetectSrt(srt)?.Language);
    }

    [Fact]
    public void MacZhTwRenderingIsConfidentTraditional()
    {
        var text = ChineseScriptConverter.Convert(
            Srt.CleanText(SharedFiles.ReadText("fixtures", "zh-30s.expected.srt")), ChineseScript.Traditional);
        Assert.Equal(TranscriptLanguage.ChineseTaiwan, TranscriptTextLanguage.Confident(text));
    }

    /// <summary>
    /// Every fixture cue: the six Chinese cues under 12 letters are null (as
    /// on the Mac); every other cue names its file's language.
    /// </summary>
    [Fact]
    public void FixtureCues()
    {
        var files = new (string File, TranscriptLanguage Language)[]
        {
            ("en-30s.expected.srt", TranscriptLanguage.English),
            ("de-30s.expected.srt", TranscriptLanguage.German),
            ("es-30s.expected.srt", TranscriptLanguage.Spanish),
            ("zh-30s.expected.srt", TranscriptLanguage.ChineseMainland),
            ("zh-30s.truth.srt", TranscriptLanguage.ChineseTaiwan),
        };
        var nullCues = new List<string>();
        foreach (var (file, language) in files)
        {
            foreach (var segment in Srt.Parse(SharedFiles.ReadText("fixtures", file)))
            {
                var result = TranscriptTextLanguage.Detect(segment.Text);
                if (result is null)
                {
                    nullCues.Add(segment.Text);
                    continue;
                }
                Assert.True(result.Value.Language == language, $"{file}: '{segment.Text}' -> {result.Value.Language}");
            }
        }
        Assert.Equal(
            ["按照北京的老規矩", "腊漆腊八 冻死寒鸭", "这是一年里最冷的时候",
             "按照北京的老規矩", "臘七臘八 凍死寒鴨", "這是一年裏最冷的時候"],
            nullCues);
    }

    [Fact]
    public void OnlyTheFirst4000CharactersCount()
    {
        var german = "Wir fangen mit dem Budget an und besprechen dann den Bericht. ";
        var english = "Let's start with the budget review and send it to the whole team. ";
        var text = string.Concat(Enumerable.Repeat(german, (TranscriptTextLanguage.SampleLength / german.Length) + 1))
            + string.Concat(Enumerable.Repeat(english, 200));
        Assert.Equal(TranscriptLanguage.German, TranscriptTextLanguage.Confident(text));
    }

    [Fact]
    public void ScriptNeutralChineseTiesToTaiwanAndIsNotConfident()
    {
        // No character that only one script uses: ZH-TW and ZH-CN tie, and
        // the tie goes to the earlier language in picker order.
        const string text = "你好，我在北京工作，大家都很好";
        Assert.Equal(TranscriptLanguage.ChineseTaiwan, TranscriptTextLanguage.Detect(text)?.Language);
        Assert.Null(TranscriptTextLanguage.Confident(text));
    }

    [Fact]
    public void DecomposedDiacriticsCountLikeComposed()
    {
        var composed = "müssen über können möchte würde";
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        Assert.Equal(TranscriptTextLanguage.Detect(composed), TranscriptTextLanguage.Detect(decomposed));
    }
}
