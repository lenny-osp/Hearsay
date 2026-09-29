using Hearsay.Core.Settings;

namespace Hearsay.Tests.Settings;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/InterfaceLanguageTests.swift
/// (Settings > General > Interface language). The Mac's
/// <c>writesAppleLanguagesIntoTheGivenDomain</c> has no Windows counterpart
/// in the core (the app sets its resource language at launch); the launch
/// rule it serves is tested by <see cref="AppliesAtNextLaunch"/>.
/// </summary>
public sealed class InterfaceLanguageTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    [Fact]
    public void FreshInstallIsEnglish()
    {
        Assert.Equal(InterfaceLanguage.English, new AppSettings(scratch.Make()).InterfaceLanguage);
    }

    [Theory]
    [InlineData(InterfaceLanguage.English)]
    [InlineData(InterfaceLanguage.German)]
    [InlineData(InterfaceLanguage.Spanish)]
    [InlineData(InterfaceLanguage.TraditionalChinese)]
    [InlineData(InterfaceLanguage.SimplifiedChinese)]
    public void RoundTripsAsItsCode(InterfaceLanguage language)
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        settings.InterfaceLanguage = language == InterfaceLanguage.English ? InterfaceLanguage.German : InterfaceLanguage.English;
        settings.InterfaceLanguage = language;
        Assert.Equal(language.Code(), ScratchSettings.Raw(folder).GetString(AppSettings.Key.InterfaceLanguage));
        Assert.Equal(language, new AppSettings(folder).InterfaceLanguage);
    }

    [Fact]
    public void CodesMatchTheLocalizations()
    {
        Assert.Equal(["en", "de", "es", "zh-Hant", "zh-Hans"], InterfaceLanguages.All.Select(l => l.Code()));
        foreach (var language in InterfaceLanguages.All)
        {
            Assert.Equal(language, InterfaceLanguages.FromCode(language.Code()));
        }
    }

    [Fact]
    public void CodesMatchTheSharedHelpAndTranslations()
    {
        foreach (var language in InterfaceLanguages.All)
        {
            Assert.True(File.Exists(SharedFiles.Path("help", language.Code(), "Help.html")), language.Code());
            if (language != InterfaceLanguage.English)
            {
                Assert.True(File.Exists(SharedFiles.Path("localization", language.Code() + ".json")), language.Code());
            }
        }
    }

    [Fact]
    public void AutonymsAreNeverTranslated()
    {
        Assert.Equal(["English", "Deutsch", "Español", "繁體中文", "简体中文"], InterfaceLanguages.All.Select(l => l.Autonym()));
    }

    [Fact]
    public void CulturesFollowTheCode()
    {
        Assert.Equal(["en", "de", "es", "zh-Hant", "zh-Hans"], InterfaceLanguages.All.Select(l => l.Culture().Name));
    }

    [Fact]
    public void UnknownStoredValueFallsBackToEnglish()
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).SetString(AppSettings.Key.InterfaceLanguage, "fr");
        Assert.Equal(InterfaceLanguage.English, new AppSettings(folder).InterfaceLanguage);
    }

    [Fact]
    public void EnvironmentOverride()
    {
        Assert.Equal(InterfaceLanguage.German, InterfaceLanguages.Override(new Dictionary<string, string> { ["HEARSAY_UI_LANGUAGE"] = "de" }));
        Assert.Equal(InterfaceLanguage.SimplifiedChinese, InterfaceLanguages.Override(new Dictionary<string, string> { ["HEARSAY_UI_LANGUAGE"] = "zh-Hans" }));
        Assert.Null(InterfaceLanguages.Override(new Dictionary<string, string> { ["HEARSAY_UI_LANGUAGE"] = "fr" }));
        Assert.Null(InterfaceLanguages.Override(new Dictionary<string, string>()));
    }

    /// <summary>
    /// A change applies at the next launch: the running language is fixed at
    /// launch, the override wins without being stored, and a stored change
    /// only asks for a restart.
    /// </summary>
    [Fact]
    public void AppliesAtNextLaunch()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        var none = new Dictionary<string, string>();
        var running = InterfaceLanguages.ResolveAtLaunch(settings.InterfaceLanguage, none);
        Assert.Equal(InterfaceLanguage.English, running);
        Assert.False(InterfaceLanguages.NeedsRestart(running, settings.InterfaceLanguage));

        settings.InterfaceLanguage = InterfaceLanguage.TraditionalChinese;
        Assert.True(InterfaceLanguages.NeedsRestart(running, settings.InterfaceLanguage));
        var next = InterfaceLanguages.ResolveAtLaunch(new AppSettings(folder).InterfaceLanguage, none);
        Assert.Equal(InterfaceLanguage.TraditionalChinese, next);

        var debug = new Dictionary<string, string> { [InterfaceLanguages.OverrideVariable] = "es" };
        Assert.Equal(InterfaceLanguage.Spanish, InterfaceLanguages.ResolveAtLaunch(settings.InterfaceLanguage, debug));
        Assert.Equal(InterfaceLanguage.TraditionalChinese, new AppSettings(folder).InterfaceLanguage);
    }
}
