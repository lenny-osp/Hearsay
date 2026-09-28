import HearsayCore
import SwiftUI

/// The entry point: applies the interface language (`AppleLanguages`)
/// before SwiftUI, AppKit, or any localized string loads, then runs the app.
@main
enum HearsayMain {
    @MainActor
    static func main() {
        InterfaceLanguageLaunch.applyAtLaunch()
        HearsayApp.main()
    }
}

struct HearsayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    /// Mirrors `AppSettings.windowMode` through the same UserDefaults key so
    /// the scene re-evaluates `isInserted` as soon as the mode changes.
    @AppStorage(AppSettings.Key.windowMode)
    private var windowModeRaw: String = WindowMode.menuBarAndDock.rawValue

    var body: some Scene {
        // A single-window scene: Hearsay never has more than one main window,
        // so there is no File > New Window item.
        Window(Text(verbatim: "Hearsay"), id: MainWindowOpener.mainWindowID) {
            MainView()
                .environment(\.locale, InterfaceLanguageLaunch.applied.locale)
                .environment(appDelegate.relauncher)
                .environment(appDelegate.settings)
                .environment(appDelegate.modelStore)
                .environment(appDelegate.aiProviderStore)
                .environment(appDelegate.recordingController)
                .environment(\.whisperEngine, appDelegate.whisperEngine)
                .environment(appDelegate.hotkeyManager)
                .environment(appDelegate.tabSelection)
                .environment(appDelegate.updateService)
                .environment(appDelegate.permissionMonitor)
                .registeringMainWindowOpener(appDelegate.windowOpener)
        }
        .defaultSize(width: 720, height: 480)
        .commands {
            // Settings live in the main window's Settings tab; there is no
            // separate Settings scene.
            // Check for Updates… below About Hearsay (PLAN.md 4.6).
            CommandGroup(after: .appInfo) {
                CheckForUpdatesCommand(updates: appDelegate.updateService)
            }
            CommandGroup(replacing: .appSettings) {
                SettingsCommand(opener: appDelegate.windowOpener)
            }
            // No File > New items: one main window only.
            CommandGroup(replacing: .newItem) {}
            // Help > Hearsay Help opens the in-app help window.
            CommandGroup(replacing: .help) {
                HelpCommand()
            }
        }

        // One help window; opening it again brings it forward.
        Window(HelpWindow.title, id: HelpWindow.id) {
            HelpView()
                .environment(\.locale, InterfaceLanguageLaunch.applied.locale)
                .environment(appDelegate.windowOpener)
                .registeringMainWindowOpener(appDelegate.windowOpener)
        }
        .defaultSize(width: 760, height: 640)

        MenuBarExtra(isInserted: menuBarItemInserted) {
            MenuBarView()
                .environment(\.locale, InterfaceLanguageLaunch.applied.locale)
                .environment(appDelegate.settings)
                .environment(appDelegate.modelStore)
                .environment(appDelegate.aiProviderStore)
                .environment(appDelegate.recordingController)
                .environment(\.whisperEngine, appDelegate.whisperEngine)
                .environment(appDelegate.windowOpener)
                .registeringMainWindowOpener(appDelegate.windowOpener)
        } label: {
            MenuBarLabel(recording: appDelegate.recordingController)
                .environment(\.locale, InterfaceLanguageLaunch.applied.locale)
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
