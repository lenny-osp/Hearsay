import Foundation

/// A transcription language Hearsay supports (PLAN.md section 1, "Languages").
/// The raw value is the Whisper language code.
public enum TranscriptLanguage: String, Codable, CaseIterable, Sendable {
    case english = "en"
    case chinese = "zh"
    case german = "de"
    case spanish = "es"

    /// Short label for compact pickers.
    public var shortLabel: String {
        switch self {
        case .english: "EN"
        case .chinese: "ZH"
        case .german: "DE"
        case .spanish: "ES"
        }
    }

    /// The language's own name, for menus and Settings.
    public var displayName: String {
        switch self {
        case .english: "English"
        case .chinese: "中文"
        case .german: "Deutsch"
        case .spanish: "Español"
        }
    }

    /// The Whisper language code ("en", "zh", "de", "es").
    public var code: String { rawValue }
}

/// What the user picked on the Record or File tab: automatic detection, or
/// one fixed language. Stored as a single string: "auto" or the language code.
public enum LanguageChoice: Codable, Equatable, Hashable, Sendable {
    case auto
    case fixed(TranscriptLanguage)

    public static let autoStorageValue = "auto"

    /// Every choice in picker order: Auto, then the languages.
    public static let allCases: [LanguageChoice] = [.auto] + TranscriptLanguage.allCases.map { .fixed($0) }

    /// "auto" or the language code.
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

    /// Short label for pickers: "Auto" or the language's short label.
    public var shortLabel: String {
        switch self {
        case .auto: "Auto"
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
