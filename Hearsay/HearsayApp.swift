import HearsayCore
import SwiftUI

@main
struct HearsayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    /// Mirrors `AppSettings.windowMode` through the same UserDefaults key so
    /// the scene re-evaluates `isInserted` as soon as the mode changes.
    @AppStorage(AppSettings.Key.windowMode)
    private var windowModeRaw: String = WindowMode.menuBarAndDock.rawValue

    var body: some Scene {
        WindowGroup("Hearsay", id: MainWindowOpener.mainWindowID) {
            MainView()
                .environment(appDelegate.settings)
                .environment(appDelegate.modelStore)
                .environment(appDelegate.aiProviderStore)
                .registeringMainWindowOpener(appDelegate.windowOpener)
        }
        .defaultSize(width: 720, height: 480)

        Settings {
            SettingsView()
                .environment(appDelegate.settings)
                .environment(appDelegate.modelStore)
                .environment(appDelegate.aiProviderStore)
                .registeringMainWindowOpener(appDelegate.windowOpener)
        }

        MenuBarExtra("Hearsay", systemImage: "waveform", isInserted: menuBarItemInserted) {
            MenuBarView()
                .environment(appDelegate.settings)
                .environment(appDelegate.modelStore)
                .environment(appDelegate.aiProviderStore)
                .environment(appDelegate.windowOpener)
                .registeringMainWindowOpener(appDelegate.windowOpener)
        }
        .menuBarExtraStyle(.window)
    }

    private var menuBarItemInserted: Binding<Bool> {
        let mode = WindowMode(rawValue: windowModeRaw) ?? .menuBarAndDock
        // The user can also cmd-drag the item out of the menu bar; the
        // setting stays the source of truth, so writes are ignored.
        return Binding(get: { mode.showsMenuBarItem }, set: { _ in })
    }
}
