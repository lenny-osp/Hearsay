import Foundation
import Testing
@testable import HearsayCore

/// Settings > General > Interface language (work item I18N-1).
@MainActor
final class InterfaceLanguageTests {
    private let scratch = ScratchDefaults()

    @Test func freshInstallIsEnglish() {
        let settings = AppSettings(defaults: scratch.make())
        #expect(settings.interfaceLanguage == .english)
    }

    @Test(arguments: InterfaceLanguage.allCases)
    func roundTripsAsItsCode(language: InterfaceLanguage) {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        settings.interfaceLanguage = language
        #expect(defaults.string(forKey: AppSettings.Key.interfaceLanguage) == language.code)
        #expect(AppSettings(defaults: defaults).interfaceLanguage == language)
    }

    @Test func codesMatchTheLocalizations() {
        #expect(InterfaceLanguage.allCases.map(\.code) == ["en", "de", "es", "zh-Hant", "zh-Hans"])
    }

    @Test func autonymsAreNeverTranslated() {
        #expect(InterfaceLanguage.allCases.map(\.autonym)
                == ["English", "Deutsch", "Español", "繁體中文", "简体中文"])
    }

    @Test func unknownStoredValueFallsBackToEnglish() {
        let defaults = scratch.make()
        defaults.set("fr", forKey: AppSettings.Key.interfaceLanguage)
        #expect(AppSettings(defaults: defaults).interfaceLanguage == .english)
    }

    @Test func writesAppleLanguagesIntoTheGivenDomain() {
        let defaults = scratch.make()
        InterfaceLanguage.traditionalChinese.writePreferredLanguages(to: defaults)
        #expect(defaults.stringArray(forKey: InterfaceLanguage.appleLanguagesKey) == ["zh-Hant"])
        InterfaceLanguage.english.writePreferredLanguages(to: defaults)
        #expect(defaults.stringArray(forKey: InterfaceLanguage.appleLanguagesKey) == ["en"])
    }

    @Test func environmentOverride() {
        #expect(InterfaceLanguage.override(in: ["HEARSAY_UI_LANGUAGE": "de"]) == .german)
        #expect(InterfaceLanguage.override(in: ["HEARSAY_UI_LANGUAGE": "zh-Hans"]) == .simplifiedChinese)
        #expect(InterfaceLanguage.override(in: ["HEARSAY_UI_LANGUAGE": "fr"]) == nil)
        #expect(InterfaceLanguage.override(in: [:]) == nil)
    }
}
