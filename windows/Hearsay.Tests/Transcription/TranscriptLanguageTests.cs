using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

public class TranscriptLanguageTests
{
    [Fact]
    public void CodesRoundTrip()
    {
        foreach (var language in TranscriptLanguages.All)
        {
            Assert.Equal(language, TranscriptLanguages.FromCode(language.Code()));
        }
        Assert.Null(TranscriptLanguages.FromCode("fr"));
        Assert.Null(TranscriptLanguages.FromCode("zh"));
    }

    [Fact]
    public void WhisperCodesFollowPickerOrder()
    {
        Assert.Equal(["en", "zh", "de", "es"], TranscriptLanguages.WhisperCodes);
        Assert.Equal("zh", TranscriptLanguage.ChineseTaiwan.WhisperCode());
        Assert.Equal("zh", TranscriptLanguage.ChineseMainland.WhisperCode());
    }

    [Fact]
    public void DetectedChineseFollowsThePreferredVariant()
    {
        Assert.Equal(TranscriptLanguage.ChineseMainland,
            TranscriptLanguages.FromWhisperCode("zh", TranscriptLanguage.ChineseMainland));
        Assert.Equal(TranscriptLanguage.ChineseTaiwan,
            TranscriptLanguages.FromWhisperCode("zh", TranscriptLanguage.English));
        Assert.Equal(TranscriptLanguage.German,
            TranscriptLanguages.FromWhisperCode("de", TranscriptLanguage.ChineseMainland));
        Assert.Null(TranscriptLanguages.FromWhisperCode("fr", TranscriptLanguage.English));
    }

    [Fact]
    public void LegacyChineseMigratesByTheOldOutputSetting()
    {
        Assert.Equal(TranscriptLanguage.ChineseMainland, TranscriptLanguages.FromStoredValue("zh", "simplified"));
        Assert.Equal(TranscriptLanguage.ChineseTaiwan, TranscriptLanguages.FromStoredValue("zh", "traditional"));
        Assert.Equal(TranscriptLanguage.ChineseTaiwan, TranscriptLanguages.FromStoredValue("zh", "asIs"));
        Assert.Equal(TranscriptLanguage.ChineseTaiwan, TranscriptLanguages.FromStoredValue("zh", null));
        Assert.Equal(TranscriptLanguage.German, TranscriptLanguages.FromStoredValue("de", "simplified"));
        Assert.Null(TranscriptLanguages.FromStoredValue("xx", null));
    }

    [Fact]
    public void LanguageChoiceStorage()
    {
        Assert.Equal("auto", LanguageChoice.Auto.StorageValue);
        Assert.Equal("zh-TW", LanguageChoice.Fixed(TranscriptLanguage.ChineseTaiwan).StorageValue);
        Assert.Equal(LanguageChoice.Auto, LanguageChoice.FromStorageValue("auto"));
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.Spanish), LanguageChoice.FromStorageValue("es"));
        Assert.Null(LanguageChoice.FromStorageValue("zh"));
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseTaiwan), LanguageChoice.FromDebugValue("zh"));
        Assert.Equal(6, LanguageChoice.All.Count);
        Assert.True(LanguageChoice.All[0].IsAuto);
    }

    [Fact]
    public void ChineseScriptPerLanguage()
    {
        Assert.Equal(ChineseScript.Traditional, TranscriptLanguage.ChineseTaiwan.ChineseScript());
        Assert.Equal(ChineseScript.Simplified, TranscriptLanguage.ChineseMainland.ChineseScript());
        Assert.Null(TranscriptLanguage.English.ChineseScript());
    }
}
