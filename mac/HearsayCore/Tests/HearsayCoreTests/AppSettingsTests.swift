import Foundation
import Testing
@testable import HearsayCore

@MainActor
final class AppSettingsTests {
    private let scratch = ScratchDefaults()

    @Test func defaultsWhenEmpty() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.windowMode == .menuBarAndDock)
        #expect(settings.outputFolderBookmark == nil)
    }

    @Test(arguments: WindowMode.allCases)
    func windowModeRoundTrips(mode: WindowMode) {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        settings.windowMode = mode
        #expect(defaults.string(forKey: AppSettings.Key.windowMode) == mode.rawValue)
        #expect(AppSettings(defaults: defaults).windowMode == mode)
    }

    @Test func unknownWindowModeFallsBackToDefault() {
        let defaults = scratch.make()
        defaults.set("floating", forKey: AppSettings.Key.windowMode)
        #expect(AppSettings(defaults: defaults).windowMode == .menuBarAndDock)
    }

    @Test func bookmarkRoundTripsAndClears() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        let data = Data([0x01, 0x02, 0x03])
        settings.outputFolderBookmark = data
        #expect(AppSettings(defaults: defaults).outputFolderBookmark == data)
        settings.outputFolderBookmark = nil
        #expect(defaults.data(forKey: AppSettings.Key.outputFolderBookmark) == nil)
        #expect(AppSettings(defaults: defaults).outputFolderBookmark == nil)
    }

    @Test func keepRecordingDefaultsOnAndRoundTrips() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.keepRecording)
        settings.keepRecording = false
        #expect(AppSettings(defaults: defaults).keepRecording == false)
    }

    @Test func menuBarShowsStatusDefaultOnAndRoundTrips() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.menuBarShowsStatus)
        settings.menuBarShowsStatus = false
        #expect(AppSettings(defaults: defaults).menuBarShowsStatus == false)
        settings.menuBarShowsStatus = true
        #expect(AppSettings(defaults: defaults).menuBarShowsStatus)
    }

    @Test func automaticUpdateChecksDefaultOnAndRoundTrips() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.automaticUpdateChecks)
        settings.automaticUpdateChecks = false
        #expect(AppSettings(defaults: defaults).automaticUpdateChecks == false)
    }

    @Test func lastUpdateCheckRoundTripsAndClears() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.lastUpdateCheck == nil)
        let date = Date(timeIntervalSince1970: 1_790_000_000)
        settings.lastUpdateCheck = date
        #expect(AppSettings(defaults: defaults).lastUpdateCheck == date)
        settings.lastUpdateCheck = nil
        #expect(defaults.object(forKey: AppSettings.Key.lastUpdateCheck) == nil)
        #expect(AppSettings(defaults: defaults).lastUpdateCheck == nil)
    }

    @Test func permissionCodeHashesRoundTripAndClear() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.screenAudioGrantedCodeHash == nil)
        #expect(settings.screenAudioResetCodeHash == nil)
        #expect(settings.microphoneGrantedCodeHash == nil)
        settings.screenAudioGrantedCodeHash = "cdhash H\"a\""
        settings.screenAudioResetCodeHash = "cdhash H\"b\""
        settings.microphoneGrantedCodeHash = "cdhash H\"c\""
        let reloaded = AppSettings(defaults: defaults)
        #expect(reloaded.screenAudioGrantedCodeHash == "cdhash H\"a\"")
        #expect(reloaded.screenAudioResetCodeHash == "cdhash H\"b\"")
        #expect(reloaded.microphoneGrantedCodeHash == "cdhash H\"c\"")
        settings.screenAudioGrantedCodeHash = nil
        settings.screenAudioResetCodeHash = nil
        settings.microphoneGrantedCodeHash = nil
        #expect(defaults.object(forKey: AppSettings.Key.screenAudioGrantedCodeHash) == nil)
        #expect(defaults.object(forKey: AppSettings.Key.screenAudioResetCodeHash) == nil)
        #expect(defaults.object(forKey: AppSettings.Key.microphoneGrantedCodeHash) == nil)
        let cleared = AppSettings(defaults: defaults)
        #expect(cleared.screenAudioGrantedCodeHash == nil)
        #expect(cleared.screenAudioResetCodeHash == nil)
        #expect(cleared.microphoneGrantedCodeHash == nil)
    }

    @Test func permissionCodeHashesUseSeparateKeys() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        settings.screenAudioGrantedCodeHash = "granted"
        settings.screenAudioResetCodeHash = "reset"
        settings.microphoneGrantedCodeHash = "mic"
        #expect(defaults.string(forKey: AppSettings.Key.screenAudioGrantedCodeHash) == "granted")
        #expect(defaults.string(forKey: AppSettings.Key.screenAudioResetCodeHash) == "reset")
        #expect(defaults.string(forKey: AppSettings.Key.microphoneGrantedCodeHash) == "mic")
    }

    @Test func languageDefaultsForFreshInstall() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.languageChoice == .auto)
        #expect(settings.preferredLanguage == .english)
        #expect(settings.defaultLanguageCode == "en")
    }

    @Test(arguments: LanguageChoice.allCases)
    func languageChoiceRoundTrips(choice: LanguageChoice) {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        settings.languageChoice = choice
        #expect(defaults.string(forKey: AppSettings.Key.languageChoice) == choice.storageValue)
        #expect(AppSettings(defaults: defaults).languageChoice == choice)
    }

    @Test(arguments: TranscriptLanguage.allCases)
    func preferredLanguageRoundTrips(language: TranscriptLanguage) {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        settings.preferredLanguage = language
        #expect(defaults.string(forKey: AppSettings.Key.preferredLanguage) == language.rawValue)
        #expect(AppSettings(defaults: defaults).preferredLanguage == language)
    }

    @Test func unknownStoredLanguageValuesFallBack() {
        let defaults = scratch.make()
        defaults.set("fr", forKey: AppSettings.Key.languageChoice)
        defaults.set("fr", forKey: AppSettings.Key.preferredLanguage)
        let settings = AppSettings(defaults: defaults)
        #expect(settings.languageChoice == .auto)
        #expect(settings.preferredLanguage == .english)
    }

    @Test(arguments: [("en", TranscriptLanguage.english), ("zh", .chineseTaiwan)])
    func legacyLanguageCodeMigratesToFixed(code: String, language: TranscriptLanguage) {
        let defaults = scratch.make()
        defaults.set(code, forKey: AppSettings.Key.defaultLanguageCode)
        let settings = AppSettings(defaults: defaults)
        #expect(settings.languageChoice == .fixed(language))
        #expect(settings.preferredLanguage == .english)
        #expect(settings.defaultLanguageCode == language.rawValue)
        #expect(defaults.string(forKey: AppSettings.Key.languageChoice) == language.rawValue)
    }

    @Test func storedChoiceWinsOverLegacyCode() {
        let defaults = scratch.make()
        defaults.set("zh", forKey: AppSettings.Key.defaultLanguageCode)
        defaults.set("auto", forKey: AppSettings.Key.languageChoice)
        #expect(AppSettings(defaults: defaults).languageChoice == .auto)
    }

    @Test func unknownLegacyCodeMigratesToAuto() {
        let defaults = scratch.make()
        defaults.set("fr", forKey: AppSettings.Key.defaultLanguageCode)
        #expect(AppSettings(defaults: defaults).languageChoice == .auto)
    }

    @Test func settingLanguageChoiceNeverChangesPreferredLanguage() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        for preferred in TranscriptLanguage.allCases {
            settings.preferredLanguage = preferred
            for choice in LanguageChoice.allCases {
                settings.languageChoice = choice
                #expect(settings.preferredLanguage == preferred)
                #expect(AppSettings(defaults: defaults).preferredLanguage == preferred)
            }
            for code in ["en", "zh", "zh-TW", "zh-CN", "de", "es", "fr"] {
                settings.defaultLanguageCode = code
                #expect(settings.preferredLanguage == preferred)
            }
        }
    }

    @Test func deprecatedLanguageCodeReadsChoiceOrPreferred() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        settings.preferredLanguage = .spanish
        settings.languageChoice = .auto
        #expect(settings.defaultLanguageCode == "es")
        settings.languageChoice = .fixed(.german)
        #expect(settings.defaultLanguageCode == "de")
        settings.defaultLanguageCode = "zh"
        #expect(settings.languageChoice == .fixed(.chineseTaiwan))
        settings.defaultLanguageCode = "zh-CN"
        #expect(settings.languageChoice == .fixed(.chineseMainland))
        settings.defaultLanguageCode = "fr"
        #expect(settings.languageChoice == .fixed(.chineseMainland))
    }

    @Test func hotkeysDefaultToControlOptionCommand() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        #expect(settings.startStopHotkey == .defaultStartStop)
        #expect(settings.pauseHotkey == .defaultPause)
        #expect(settings.startStopHotkey.displayString == "\u{2303}\u{2325}\u{2318}R")
        #expect(settings.pauseHotkey.displayString == "\u{2303}\u{2325}\u{2318}P")
    }

    @Test func hotkeysRoundTrip() {
        let defaults = scratch.make()
        let settings = AppSettings(defaults: defaults)
        let startStop = HotkeyBinding(keyCode: 0x01, modifiers: [.command, .shift])
        let pause = HotkeyBinding(keyCode: 0x7A, modifiers: [.option])
        settings.startStopHotkey = startStop
        settings.pauseHotkey = pause
        let reloaded = AppSettings(defaults: defaults)
        #expect(reloaded.startStopHotkey == startStop)
        #expect(reloaded.pauseHotkey == pause)
        #expect(reloaded.startStopHotkey.displayString == "\u{21E7}\u{2318}S")
        #expect(reloaded.pauseHotkey.displayString == "\u{2325}F1")
    }

    @Test func corruptHotkeyFallsBackToDefault() {
        let defaults = scratch.make()
        defaults.set(Data([0x7B]), forKey: AppSettings.Key.startStopHotkey)
        #expect(AppSettings(defaults: defaults).startStopHotkey == .defaultStartStop)
    }

    @Test func globalShortcutNeedsControlOptionOrCommand() {
        #expect(HotkeyBinding.defaultStartStop.isValidGlobalShortcut)
        #expect(!HotkeyBinding(keyCode: 0x0F, modifiers: [.shift]).isValidGlobalShortcut)
        #expect(!HotkeyBinding(keyCode: 0x0F, modifiers: []).isValidGlobalShortcut)
        #expect(HotkeyBinding(keyCode: 0x0F, modifiers: [.control]).isValidGlobalShortcut)
    }

    @Test func windowModeFlags() {
        #expect(WindowMode.menuBarAndDock.showsMenuBarItem && WindowMode.menuBarAndDock.showsDockIcon)
        #expect(WindowMode.menuBarOnly.showsMenuBarItem && !WindowMode.menuBarOnly.showsDockIcon)
        #expect(!WindowMode.dockOnly.showsMenuBarItem && WindowMode.dockOnly.showsDockIcon)
    }
}

struct OutputLocationTests {
    private func tempDir() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("hearsay-tests-\(UUID().uuidString)", isDirectory: true)
    }

    @Test func defaultFolderEndsInDocumentsHearsay() {
        let url = OutputLocation.defaultFolder()
        #expect(url.lastPathComponent == "Hearsay")
        #expect(url.deletingLastPathComponent().lastPathComponent == "Documents")
    }

    @Test func noBookmarkCreatesFallback() throws {
        let root = tempDir()
        defer { try? FileManager.default.removeItem(at: root) }
        let fallback = root.appendingPathComponent("Hearsay", isDirectory: true)
        let resolved = try OutputLocation.resolve(bookmark: nil, fallback: fallback)
        #expect(resolved.url == fallback)
        #expect(!resolved.isSecurityScoped)
        var isDir: ObjCBool = false
        #expect(FileManager.default.fileExists(atPath: fallback.path, isDirectory: &isDir) && isDir.boolValue)
    }

    @Test func garbageBookmarkFallsBack() throws {
        let root = tempDir()
        defer { try? FileManager.default.removeItem(at: root) }
        let resolved = try OutputLocation.resolve(bookmark: Data([0xde, 0xad]), fallback: root)
        #expect(resolved.url == root)
    }

    @Test func bookmarkResolvesToChosenFolder() throws {
        let chosen = tempDir()
        let fallback = tempDir()
        defer {
            try? FileManager.default.removeItem(at: chosen)
            try? FileManager.default.removeItem(at: fallback)
        }
        try FileManager.default.createDirectory(at: chosen, withIntermediateDirectories: true)
        let bookmark = try OutputLocation.makeBookmark(for: chosen)
        let resolved = try OutputLocation.resolve(bookmark: bookmark, fallback: fallback)
        defer { resolved.stopAccessing() }
        #expect(resolved.url.standardizedFileURL.resolvingSymlinksInPath()
            == chosen.standardizedFileURL.resolvingSymlinksInPath())
        #expect(!FileManager.default.fileExists(atPath: fallback.path))
    }

    @Test func deletedFolderBookmarkFallsBack() throws {
        let chosen = tempDir()
        let fallback = tempDir()
        defer { try? FileManager.default.removeItem(at: fallback) }
        try FileManager.default.createDirectory(at: chosen, withIntermediateDirectories: true)
        let bookmark = try OutputLocation.makeBookmark(for: chosen)
        try FileManager.default.removeItem(at: chosen)
        let resolved = try OutputLocation.resolve(bookmark: bookmark, fallback: fallback)
        #expect(resolved.url == fallback)
    }
}

/// A stored "zh" (before ZH-TW and ZH-CN) migrates by the legacy
/// "Chinese output" setting: traditional or missing gives ZH-TW, simplified
/// ZH-CN. Auto and the other languages are unchanged.
@MainActor
final class ChineseVariantMigrationTests {
    private let scratch = ScratchDefaults()

    private func defaults(choice: String?, preferred: String?, script: String?) -> UserDefaults {
        let defaults = scratch.make()
        if let choice { defaults.set(choice, forKey: AppSettings.Key.languageChoice) }
        if let preferred { defaults.set(preferred, forKey: AppSettings.Key.preferredLanguage) }
        if let script { defaults.set(script, forKey: AppSettings.Key.chineseScript) }
        return defaults
    }

    @Test(arguments: [
        ("traditional" as String?, TranscriptLanguage.chineseTaiwan),
        (nil, .chineseTaiwan),
        ("asIs", .chineseTaiwan),
        ("simplified", .chineseMainland),
    ])
    func fixedZhChoiceMigrates(script: String?, expected: TranscriptLanguage) {
        let defaults = defaults(choice: "zh", preferred: nil, script: script)
        let settings = AppSettings(defaults: defaults)
        #expect(settings.languageChoice == .fixed(expected))
        #expect(settings.preferredLanguage == .english)
        #expect(defaults.string(forKey: AppSettings.Key.languageChoice) == expected.rawValue)
        #expect(AppSettings(defaults: defaults).languageChoice == .fixed(expected))
    }

    @Test(arguments: [
        ("traditional" as String?, TranscriptLanguage.chineseTaiwan),
        (nil, .chineseTaiwan),
        ("simplified", .chineseMainland),
    ])
    func preferredZhMigrates(script: String?, expected: TranscriptLanguage) {
        let defaults = defaults(choice: "auto", preferred: "zh", script: script)
        let settings = AppSettings(defaults: defaults)
        #expect(settings.preferredLanguage == expected)
        #expect(settings.languageChoice == .auto)
        #expect(defaults.string(forKey: AppSettings.Key.preferredLanguage) == expected.rawValue)
        #expect(AppSettings(defaults: defaults).preferredLanguage == expected)
    }

    @Test func legacyDefaultLanguageCodeZhMigratesByScript() {
        let defaults = scratch.make()
        defaults.set("zh", forKey: AppSettings.Key.defaultLanguageCode)
        defaults.set("simplified", forKey: AppSettings.Key.chineseScript)
        #expect(AppSettings(defaults: defaults).languageChoice == .fixed(.chineseMainland))
        #expect(defaults.string(forKey: AppSettings.Key.languageChoice) == "zh-CN")
    }

    @Test(arguments: ["auto", "en", "de", "es", "zh-TW", "zh-CN"])
    func otherChoicesUnchanged(stored: String) {
        for script in ["traditional", "simplified", nil] as [String?] {
            let defaults = defaults(choice: stored, preferred: nil, script: script)
            #expect(AppSettings(defaults: defaults).languageChoice.storageValue == stored)
            #expect(defaults.string(forKey: AppSettings.Key.languageChoice) == stored)
        }
    }

    @Test(arguments: ["en", "de", "es", "zh-TW", "zh-CN"])
    func otherPreferredUnchanged(stored: String) {
        for script in ["traditional", "simplified", nil] as [String?] {
            let defaults = defaults(choice: nil, preferred: stored, script: script)
            #expect(AppSettings(defaults: defaults).preferredLanguage.rawValue == stored)
            #expect(defaults.string(forKey: AppSettings.Key.preferredLanguage) == stored)
        }
    }

    @Test func bothZhMigrateTogether() {
        let defaults = defaults(choice: "zh", preferred: "zh", script: "simplified")
        let settings = AppSettings(defaults: defaults)
        #expect(settings.languageChoice == .fixed(.chineseMainland))
        #expect(settings.preferredLanguage == .chineseMainland)
    }
}
