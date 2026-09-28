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
