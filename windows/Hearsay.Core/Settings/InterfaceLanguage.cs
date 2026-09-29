using System.Globalization;

namespace Hearsay.Core.Settings;

/// <summary>
/// The language of Hearsay's own interface (Settings > General > Interface
/// language). Independent of the transcription language and of the Windows
/// display language: a fresh install starts in English.
/// Port of mac/HearsayCore/Sources/HearsayCore/Settings/InterfaceLanguage.swift.
/// </summary>
public enum InterfaceLanguage
{
    English,
    German,
    Spanish,
    TraditionalChinese,
    SimplifiedChinese,
}

/// <summary>
/// Codes, autonyms and the launch rule for <see cref="InterfaceLanguage"/>.
/// <para>
/// Applies at next launch (PLAN.md 18.3, "Localization"): the app resolves
/// the language once with <see cref="ResolveAtLaunch"/> before any UI loads
/// (the Mac writes <c>AppleLanguages</c> at that point; the Windows app sets
/// its resource language and UI culture, W7) and keeps it for the whole run.
/// A change in Settings is only stored; <see cref="NeedsRestart"/> tells the
/// app to ask for a restart, as the Mac does.
/// </para>
/// </summary>
public static class InterfaceLanguages
{
    /// <summary>Every language in picker order.</summary>
    public static readonly IReadOnlyList<InterfaceLanguage> All =
    [
        InterfaceLanguage.English,
        InterfaceLanguage.German,
        InterfaceLanguage.Spanish,
        InterfaceLanguage.TraditionalChinese,
        InterfaceLanguage.SimplifiedChinese,
    ];

    /// <summary>
    /// The environment variable a debug entry point reads to render the
    /// interface in another language without writing any setting.
    /// </summary>
    public const string OverrideVariable = "HEARSAY_UI_LANGUAGE";

    /// <summary>
    /// The localization code and storage value ("en", "de", "es", "zh-Hant",
    /// "zh-Hans"): the names of the shared/localization and shared/help files,
    /// and valid BCP-47 tags for Windows' resource loader.
    /// </summary>
    public static string Code(this InterfaceLanguage language) => language switch
    {
        InterfaceLanguage.English => "en",
        InterfaceLanguage.German => "de",
        InterfaceLanguage.Spanish => "es",
        InterfaceLanguage.TraditionalChinese => "zh-Hant",
        InterfaceLanguage.SimplifiedChinese => "zh-Hans",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>
    /// The language's name in that language. Never translated: the picker
    /// shows every entry in its own language, whatever the interface is in.
    /// </summary>
    public static string Autonym(this InterfaceLanguage language) => language switch
    {
        InterfaceLanguage.English => "English",
        InterfaceLanguage.German => "Deutsch",
        InterfaceLanguage.Spanish => "Español",
        InterfaceLanguage.TraditionalChinese => "繁體中文",
        InterfaceLanguage.SimplifiedChinese => "简体中文",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>The culture for dates and numbers, the Mac's <c>locale</c>.</summary>
    public static CultureInfo Culture(this InterfaceLanguage language) =>
        CultureInfo.GetCultureInfo(language.Code());

    /// <summary>Parses a code; null for anything else (case-sensitive, like the Mac's raw values).</summary>
    public static InterfaceLanguage? FromCode(string? code) => code switch
    {
        "en" => InterfaceLanguage.English,
        "de" => InterfaceLanguage.German,
        "es" => InterfaceLanguage.Spanish,
        "zh-Hant" => InterfaceLanguage.TraditionalChinese,
        "zh-Hans" => InterfaceLanguage.SimplifiedChinese,
        _ => null,
    };

    /// <summary><c>HEARSAY_UI_LANGUAGE</c> from <paramref name="environment"/> when it names a supported language; null when unset or unknown.</summary>
    public static InterfaceLanguage? Override(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.TryGetValue(OverrideVariable, out var value) ? FromCode(value) : null;
    }

    /// <summary>
    /// The language the interface runs in for this launch: the debug override
    /// when set, else the stored setting.
    /// </summary>
    public static InterfaceLanguage ResolveAtLaunch(InterfaceLanguage stored, IReadOnlyDictionary<string, string> environment) =>
        Override(environment) ?? stored;

    /// <summary>Whether the setting differs from the language this run started in, so the change waits for a restart.</summary>
    public static bool NeedsRestart(InterfaceLanguage running, InterfaceLanguage stored) => running != stored;
}
