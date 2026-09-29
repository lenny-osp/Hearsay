namespace Hearsay.Core.Transcription;

/// <summary>
/// A transcription language Hearsay supports (PLAN.md section 1, "Languages").
/// Chinese comes in two variants that share the Whisper language "zh" and
/// differ in the characters the transcript is written in.
/// Port of mac/HearsayCore/Sources/HearsayCore/Transcription/TranscriptLanguage.swift.
/// </summary>
public enum TranscriptLanguage
{
    English,
    ChineseTaiwan,
    ChineseMainland,
    German,
    Spanish,
}

/// <summary>The characters a Chinese transcript is converted to.</summary>
public enum ChineseScript
{
    Traditional,
    Simplified,
}

public static class TranscriptLanguages
{
    /// <summary>Every language in picker order.</summary>
    public static readonly IReadOnlyList<TranscriptLanguage> All =
    [
        TranscriptLanguage.English,
        TranscriptLanguage.ChineseTaiwan,
        TranscriptLanguage.ChineseMainland,
        TranscriptLanguage.German,
        TranscriptLanguage.Spanish,
    ];

    /// <summary>The Whisper languages detection compares, in picker order. Both Chinese variants are "zh".</summary>
    public static readonly IReadOnlyList<string> WhisperCodes = ["en", "zh", "de", "es"];

    /// <summary>The legacy storage value of Chinese before the two variants.</summary>
    public const string LegacyChineseValue = "zh";

    /// <summary>The storage value ("en", "zh-TW", "zh-CN", "de", "es"); also the code in shared/prompts/languages.json.</summary>
    public static string Code(this TranscriptLanguage language) => language switch
    {
        TranscriptLanguage.English => "en",
        TranscriptLanguage.ChineseTaiwan => "zh-TW",
        TranscriptLanguage.ChineseMainland => "zh-CN",
        TranscriptLanguage.German => "de",
        TranscriptLanguage.Spanish => "es",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>Short label for compact pickers. Never translated.</summary>
    public static string ShortLabel(this TranscriptLanguage language) => language switch
    {
        TranscriptLanguage.English => "EN",
        TranscriptLanguage.ChineseTaiwan => "ZH-TW",
        TranscriptLanguage.ChineseMainland => "ZH-CN",
        TranscriptLanguage.German => "DE",
        TranscriptLanguage.Spanish => "ES",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>The language's own name, for menus and Settings. Never translated.</summary>
    public static string DisplayName(this TranscriptLanguage language) => language switch
    {
        TranscriptLanguage.English => "English",
        TranscriptLanguage.ChineseTaiwan => "繁體中文",
        TranscriptLanguage.ChineseMainland => "简体中文",
        TranscriptLanguage.German => "Deutsch",
        TranscriptLanguage.Spanish => "Español",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>The Whisper language code ("en", "zh", "de", "es").</summary>
    public static string WhisperCode(this TranscriptLanguage language) => language switch
    {
        TranscriptLanguage.English => "en",
        TranscriptLanguage.ChineseTaiwan or TranscriptLanguage.ChineseMainland => "zh",
        TranscriptLanguage.German => "de",
        TranscriptLanguage.Spanish => "es",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>
    /// The characters the transcript is converted to: Traditional for ZH-TW,
    /// Simplified for ZH-CN, null (unchanged) for every other language.
    /// </summary>
    public static ChineseScript? ChineseScript(this TranscriptLanguage language) => language switch
    {
        TranscriptLanguage.ChineseTaiwan => Transcription.ChineseScript.Traditional,
        TranscriptLanguage.ChineseMainland => Transcription.ChineseScript.Simplified,
        _ => null,
    };

    /// <summary>Parses a storage value ("en", "zh-TW", ...); null for anything else.</summary>
    public static TranscriptLanguage? FromCode(string code) => code switch
    {
        "en" => TranscriptLanguage.English,
        "zh-TW" => TranscriptLanguage.ChineseTaiwan,
        "zh-CN" => TranscriptLanguage.ChineseMainland,
        "de" => TranscriptLanguage.German,
        "es" => TranscriptLanguage.Spanish,
        _ => null,
    };

    /// <summary>
    /// The language for a Whisper code: the Chinese variant for "zh" is
    /// <paramref name="preferred"/> when that is a Chinese variant, otherwise
    /// ZH-TW. Null for a code outside <see cref="WhisperCodes"/>.
    /// </summary>
    public static TranscriptLanguage? FromWhisperCode(string whisperCode, TranscriptLanguage preferred) => whisperCode switch
    {
        "en" => TranscriptLanguage.English,
        "zh" => preferred.ChineseScript() is null ? TranscriptLanguage.ChineseTaiwan : preferred,
        "de" => TranscriptLanguage.German,
        "es" => TranscriptLanguage.Spanish,
        _ => null,
    };

    /// <summary>
    /// Parses a stored value, migrating the legacy "zh" by the legacy "Chinese
    /// output" setting (<paramref name="legacyChineseScript"/>, the stored
    /// script value): "simplified" gives ZH-CN, anything else (traditional,
    /// asIs, missing) ZH-TW. Null for an unknown value.
    /// </summary>
    public static TranscriptLanguage? FromStoredValue(string storedValue, string? legacyChineseScript)
    {
        if (storedValue == LegacyChineseValue)
        {
            return legacyChineseScript == "simplified"
                ? TranscriptLanguage.ChineseMainland
                : TranscriptLanguage.ChineseTaiwan;
        }
        return FromCode(storedValue);
    }
}

/// <summary>
/// What the user picked on the Record or File tab: automatic detection, or one
/// fixed language. Stored as a single string: "auto" or the language's code.
/// Port of <c>LanguageChoice</c> in TranscriptLanguage.swift. The localized
/// "Auto" label is a UI concern and lives in the app, not here.
/// </summary>
public readonly record struct LanguageChoice
{
    public const string AutoStorageValue = "auto";

    /// <summary>The fixed language, or null for auto.</summary>
    public TranscriptLanguage? FixedLanguage { get; }

    private LanguageChoice(TranscriptLanguage? fixedLanguage) => FixedLanguage = fixedLanguage;

    public static LanguageChoice Auto { get; } = new(null);

    public static LanguageChoice Fixed(TranscriptLanguage language) => new(language);

    public bool IsAuto => FixedLanguage is null;

    /// <summary>Every choice in picker order: Auto, then the languages.</summary>
    public static readonly IReadOnlyList<LanguageChoice> All =
        [Auto, .. TranscriptLanguages.All.Select(Fixed)];

    /// <summary>"auto" or the language's code.</summary>
    public string StorageValue => FixedLanguage is { } language ? language.Code() : AutoStorageValue;

    /// <summary>Short label for pickers: the language's short label; "Auto" for auto (localize in the app).</summary>
    public string ShortLabel => FixedLanguage?.ShortLabel() ?? "Auto";

    /// <summary>Parses <see cref="StorageValue"/>; null for anything else.</summary>
    public static LanguageChoice? FromStorageValue(string value)
    {
        if (value == AutoStorageValue) return Auto;
        return TranscriptLanguages.FromCode(value) is { } language ? Fixed(language) : null;
    }

    /// <summary>Parses a stored value like <see cref="FromStorageValue"/>, migrating the legacy "zh".</summary>
    public static LanguageChoice? FromStoredValue(string value, string? legacyChineseScript)
    {
        if (value == AutoStorageValue) return Auto;
        return TranscriptLanguages.FromStoredValue(value, legacyChineseScript) is { } language ? Fixed(language) : null;
    }

    /// <summary>Parses a debug HEARSAY_LANGUAGE value: the storage value, with "zh" accepted as an alias for ZH-TW.</summary>
    public static LanguageChoice? FromDebugValue(string value) => FromStoredValue(value, null);

    public override string ToString() => StorageValue;
}
