import Foundation

/// The language of Hearsay's own interface (Settings > General > Interface
/// language). Independent of the transcription language and of the Mac's
/// language: a fresh install starts in English. The raw value is the
/// localization code used for `AppleLanguages` and the `.lproj` folders.
public enum InterfaceLanguage: String, CaseIterable, Codable, Sendable {
    case english = "en"
    case german = "de"
    case spanish = "es"
    case traditionalChinese = "zh-Hant"
    case simplifiedChinese = "zh-Hans"

    /// The language's name in that language. Never translated: the picker
    /// shows every entry in its own language, whatever the interface is in.
    public var autonym: String {
        switch self {
        case .english: "English"
        case .german: "Deutsch"
        case .spanish: "Español"
        case .traditionalChinese: "繁體中文"
        case .simplifiedChinese: "简体中文"
        }
    }

    /// The localization code ("en", "de", "es", "zh-Hant", "zh-Hans").
    public var code: String { rawValue }

    /// The locale for SwiftUI's `\.locale`, so dates and numbers follow the
    /// interface language.
    public var locale: Locale { Locale(identifier: rawValue) }

    /// The `UserDefaults` key Foundation reads the preferred localizations
    /// from.
    public static let appleLanguagesKey = "AppleLanguages"

    /// The environment variable a debug entry point reads to render the
    /// interface in another language without writing any setting.
    public static let overrideVariable = "HEARSAY_UI_LANGUAGE"

    /// Writes `AppleLanguages = [code]` into `defaults`. The app passes its
    /// own domain (`UserDefaults.standard`), never the global domain, so only
    /// Hearsay changes language. Takes effect for bundles that have not
    /// resolved their localization yet, which is why the app calls this
    /// before any UI loads and asks for a restart after a change.
    public func writePreferredLanguages(to defaults: UserDefaults) {
        defaults.set([code], forKey: Self.appleLanguagesKey)
    }

    /// `HEARSAY_UI_LANGUAGE` from `environment`, when it names a supported
    /// language; nil when unset or unknown.
    public static func override(in environment: [String: String]) -> InterfaceLanguage? {
        environment[overrideVariable].flatMap(InterfaceLanguage.init(rawValue:))
    }
}
