import AppKit
import HearsayCore
import Observation
import SwiftUI

/// Opens or brings forward the single main window. SwiftUI only hands out
/// `OpenWindowAction` inside views, so every scene's root view registers it
/// here and AppKit code (the app delegate) can use it later.
@MainActor
@Observable
final class MainWindowOpener {
    static let mainWindowID = "main"

    @ObservationIgnored private var openWindow: OpenWindowAction?

    func register(_ action: OpenWindowAction) {
        openWindow = action
    }

    /// Windows that belong to the main `WindowGroup`. SwiftUI derives their
    /// identifiers from the scene id.
    var mainWindows: [NSWindow] {
        NSApp.windows.filter { window in
            window.identifier?.rawValue.hasPrefix(Self.mainWindowID) == true
        }
    }

    var isMainWindowVisible: Bool {
        mainWindows.contains { $0.isVisible && !$0.isMiniaturized }
    }

    /// Brings the existing main window forward, or opens a new one.
    func show() {
        NSApp.unhide(nil)
        NSApp.activate()
        if let window = mainWindows.first {
            if window.isMiniaturized { window.deminiaturize(nil) }
            window.makeKeyAndOrderFront(nil)
        } else if let openWindow {
            openWindow(id: Self.mainWindowID)
        }
    }
}

private struct MainWindowOpenerRegistration: ViewModifier {
    let opener: MainWindowOpener
    @Environment(\.openWindow) private var openWindow

    func body(content: Content) -> some View {
        content.onAppear { opener.register(openWindow) }
    }
}

extension View {
    func registeringMainWindowOpener(_ opener: MainWindowOpener) -> some View {
        modifier(MainWindowOpenerRegistration(opener: opener))
    }
}

/// Applies the window mode (PLAN.md 4.4): activation policy on launch and on
/// every change, hide-instead-of-quit in menu-bar-only mode, and reopening
/// the main window so the user is never left without an entry point.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let settings = AppSettings()
    let aiProviderStore = AIProviderStore()
    lazy var modelStore = ModelStore(settings: settings)
    let windowOpener = MainWindowOpener()

    private var appliedMode: WindowMode?

    func applicationDidFinishLaunching(_ notification: Notification) {
        applyWindowMode(settings.windowMode)
        observeWindowMode()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(windowWillClose(_:)),
            name: NSWindow.willCloseNotification,
            object: nil
        )
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            windowOpener.show()
        }
        return false
    }

    // MARK: - Window mode

    private func observeWindowMode() {
        withObservationTracking {
            _ = settings.windowMode
        } onChange: { [weak self] in
            // onChange fires before the new value is stored; hop to the next
            // main-actor turn to read it and to re-arm tracking.
            Task { @MainActor in
                guard let self else { return }
                self.applyWindowMode(self.settings.windowMode)
                self.observeWindowMode()
            }
        }
    }

    private func applyWindowMode(_ mode: WindowMode) {
        let previous = appliedMode
        appliedMode = mode

        let policy: NSApplication.ActivationPolicy = mode.showsDockIcon ? .regular : .accessory
        if NSApp.activationPolicy() != policy {
            NSApp.setActivationPolicy(policy)
        }

        guard let previous, previous != mode else { return }
        // Changing the policy can push the app to the background; keep the
        // window the user is working in (normally Settings) in front.
        NSApp.activate()
        let involvesDockOnly = previous == .dockOnly || mode == .dockOnly
        if involvesDockOnly && !windowOpener.isMainWindowVisible {
            windowOpener.show()
        }
    }

    // MARK: - Hide instead of quit

    @objc private func windowWillClose(_ notification: Notification) {
        guard settings.windowMode == .menuBarOnly,
              let closing = notification.object as? NSWindow,
              isUserWindow(closing) else { return }
        let othersVisible = NSApp.windows.contains { window in
            window !== closing && window.isVisible && isUserWindow(window)
        }
        if !othersVisible {
            NSApp.hide(nil)
        }
    }

    /// Titled document-style windows, excluding the menu bar extra panel,
    /// status item windows, and other panels.
    private func isUserWindow(_ window: NSWindow) -> Bool {
        !(window is NSPanel) && window.styleMask.contains(.titled) && window.canBecomeMain
    }
}
