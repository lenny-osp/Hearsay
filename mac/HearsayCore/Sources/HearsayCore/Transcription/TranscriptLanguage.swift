import Foundation

/// A transcription language Hearsay supports (PLAN.md section 1, "Languages").
/// Chinese comes in two variants that share the Whisper language "zh" and
/// differ in the characters the transcript is written in. The raw value is
/// the storage value ("en", "zh-TW", "zh-CN", "de", "es"); Whisper gets
/// `whisperCode`.
public enum TranscriptLanguage: String, Codable, CaseIterable, Sendable {
    case english = "en"
    case chineseTaiwan = "zh-TW"
    case chineseMainland = "zh-CN"
    case german = "de"
    case spanish = "es"

    /// The Whisper languages detection compares, in picker order. Both
    /// Chinese variants are "zh".
    public static let whisperCodes = ["en", "zh", "de", "es"]

    /// The legacy storage value of Chinese before the two variants.
    public static let legacyChineseValue = "zh"

    /// Short label for compact pickers. Never translated.
    public var shortLabel: String {
        switch self {
        case .english: "EN"
        case .chineseTaiwan: "ZH-TW"
        case .chineseMainland: "ZH-CN"
        case .german: "DE"
        case .spanish: "ES"
        }
    }

    /// The language's own name, for menus and Settings. Never translated.
    public var displayName: String {
        switch self {
        case .english: "English"
        case .chineseTaiwan: "繁體中文"
        case .chineseMainland: "简体中文"
        case .german: "Deutsch"
        case .spanish: "Español"
        }
    }

    /// The Whisper language code ("en", "zh", "de", "es").
    public var whisperCode: String {
        switch self {
        case .english: "en"
        case .chineseTaiwan, .chineseMainland: "zh"
        case .german: "de"
        case .spanish: "es"
        }
    }

    /// The characters the transcript is converted to: traditional for
    /// ZH-TW, simplified for ZH-CN, nil (unchanged) for every other language.
    public var chineseScript: ChineseScript? {
        switch self {
        case .chineseTaiwan: .traditional
        case .chineseMainland: .simplified
        case .english, .german, .spanish: nil
        }
    }

    /// The language for a Whisper code: the Chinese variant for "zh" is
    /// `preferred` when that is a Chinese variant, otherwise ZH-TW. Nil for
    /// a code outside `whisperCodes`.
    public init?(whisperCode: String, preferred: TranscriptLanguage) {
        switch whisperCode {
        case "en": self = .english
        case "zh": self = preferred.chineseScript == nil ? .chineseTaiwan : preferred
        case "de": self = .german
        case "es": self = .spanish
        default: return nil
        }
    }

    /// Parses a stored value, migrating the legacy "zh" by the legacy
    /// "Chinese output" setting (`legacyChineseScript`, the stored
    /// `ChineseScript` raw value): "simplified" gives ZH-CN, anything else
    /// (traditional, asIs, missing) ZH-TW. Nil for an unknown value.
    public init?(storedValue: String, legacyChineseScript: String?) {
        if storedValue == Self.legacyChineseValue {
            self = legacyChineseScript == ChineseScript.simplified.rawValue ? .chineseMainland : .chineseTaiwan
        } else if let language = TranscriptLanguage(rawValue: storedValue) {
            self = language
        } else {
            return nil
        }
    }
}

/// What the user picked on the Record or File tab: automatic detection, or
/// one fixed language. Stored as a single string: "auto" or the language's
/// raw value.
public enum LanguageChoice: Codable, Equatable, Hashable, Sendable {
    case auto
    case fixed(TranscriptLanguage)

    public static let autoStorageValue = "auto"

    /// Every choice in picker order: Auto, then the languages.
    public static let allCases: [LanguageChoice] = [.auto] + TranscriptLanguage.allCases.map { .fixed($0) }

    /// "auto" or the language's raw value.
    public var storageValue: String {
        switch self {
        case .auto: Self.autoStorageValue
        case .fixed(let language): language.rawValue
        }
    }

    /// Parses `storageValue`; nil for anything else.
    public init?(storageValue: String) {
        if storageValue == Self.autoStorageValue {
            self = .auto
        } else if let language = TranscriptLanguage(rawValue: storageValue) {
            self = .fixed(language)
        } else {
            return nil
        }
    }

    /// Parses a stored value like `init(storageValue:)`, migrating the
    /// legacy "zh" (see `TranscriptLanguage.init(storedValue:legacyChineseScript:)`).
    public init?(storedValue: String, legacyChineseScript: String?) {
        if storedValue == Self.autoStorageValue {
            self = .auto
        } else if let language = TranscriptLanguage(
            storedValue: storedValue, legacyChineseScript: legacyChineseScript) {
            self = .fixed(language)
        } else {
            return nil
        }
    }

    /// Parses a debug `HEARSAY_LANGUAGE` value: `storageValue`, with "zh"
    /// accepted as an alias for ZH-TW.
    public init?(debugValue: String) {
        self.init(storedValue: debugValue, legacyChineseScript: nil)
    }

    /// Short label for pickers: "Auto" or the language's short label.
    public var shortLabel: String {
        switch self {
        case .auto:
            String(localized: "Auto", bundle: .module,
                   comment: "Language picker segment: detect the spoken language automatically. Keep it short.")
        case .fixed(let language): language.shortLabel
        }
    }

    /// The fixed language, or nil for auto.
    public var fixedLanguage: TranscriptLanguage? {
        if case .fixed(let language) = self { return language }
        return nil
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = try container.decode(String.self)
        guard let choice = LanguageChoice(storageValue: raw) else {
            throw DecodingError.dataCorruptedError(
                in: container, debugDescription: "unknown language choice: \(raw)")
        }
        self = choice
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(storageValue)
    }
}
