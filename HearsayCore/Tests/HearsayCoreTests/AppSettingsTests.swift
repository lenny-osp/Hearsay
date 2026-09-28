import Foundation
import Testing
@testable import HearsayCore

@MainActor
struct AppSettingsTests {
    private static func freshDefaults() -> (UserDefaults, String) {
        let suite = "tw.og1o.hearsay.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        return (defaults, suite)
    }

    @Test func defaultsWhenEmpty() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        #expect(settings.windowMode == .menuBarAndDock)
        #expect(settings.outputFolderBookmark == nil)
    }

    @Test(arguments: WindowMode.allCases)
    func windowModeRoundTrips(mode: WindowMode) {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        settings.windowMode = mode
        #expect(defaults.string(forKey: AppSettings.Key.windowMode) == mode.rawValue)
        #expect(AppSettings(defaults: defaults).windowMode == mode)
    }

    @Test func unknownWindowModeFallsBackToDefault() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("floating", forKey: AppSettings.Key.windowMode)
        #expect(AppSettings(defaults: defaults).windowMode == .menuBarAndDock)
    }

    @Test func bookmarkRoundTripsAndClears() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        let data = Data([0x01, 0x02, 0x03])
        settings.outputFolderBookmark = data
        #expect(AppSettings(defaults: defaults).outputFolderBookmark == data)
        settings.outputFolderBookmark = nil
        #expect(defaults.data(forKey: AppSettings.Key.outputFolderBookmark) == nil)
        #expect(AppSettings(defaults: defaults).outputFolderBookmark == nil)
    }

    @Test func keepRecordingDefaultsOnAndRoundTrips() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        #expect(settings.keepRecording)
        settings.keepRecording = false
        #expect(AppSettings(defaults: defaults).keepRecording == false)
    }

    @Test func chineseScriptDefaultsTraditionalAndRoundTrips() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        #expect(settings.chineseScript == .traditional)
        settings.chineseScript = .simplified
        #expect(AppSettings(defaults: defaults).chineseScript == .simplified)
        settings.chineseScript = .asIs
        #expect(settings.chineseScript == .traditional)
        #expect(AppSettings(defaults: defaults).chineseScript == .traditional)
        defaults.set("asIs", forKey: AppSettings.Key.chineseScript)
        #expect(AppSettings(defaults: defaults).chineseScript == .traditional)
        defaults.set("bogus", forKey: AppSettings.Key.chineseScript)
        #expect(AppSettings(defaults: defaults).chineseScript == .traditional)
    }

    @Test func hotkeysDefaultToControlOptionCommand() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        #expect(settings.startStopHotkey == .defaultStartStop)
        #expect(settings.pauseHotkey == .defaultPause)
        #expect(settings.startStopHotkey.displayString == "\u{2303}\u{2325}\u{2318}R")
        #expect(settings.pauseHotkey.displayString == "\u{2303}\u{2325}\u{2318}P")
    }

    @Test func hotkeysRoundTrip() {
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
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
        let (defaults, suite) = Self.freshDefaults()
        defer { defaults.removePersistentDomain(forName: suite) }
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
