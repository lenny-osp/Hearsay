import AppKit
import HearsayCore
import Observation
import SwiftUI

/// Opens or brings forward the single main window, optionally on a given
/// tab. SwiftUI only hands out `OpenWindowAction` inside views, so every
/// scene's root view (and the Settings… command) registers it here and
/// AppKit code (the app delegate) can use it later.
@MainActor
@Observable
final class MainWindowOpener {
    static let mainWindowID = "main"

    /// The main window's selected tab, shared with `MainView`.
    let tabs: MainTabSelection

    @ObservationIgnored private var openWindow: OpenWindowAction?

    init(tabs: MainTabSelection) {
        self.tabs = tabs
    }

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

    /// Brings the main window forward (opening it if needed) on `tab`.
    func show(tab: MainTab) {
        tabs.tab = tab
        show()
    }

    /// ⌘, and every other "open settings" path: the main window's Settings
    /// tab. Works in every window mode, including menu bar only.
    func showSettings() {
        show(tab: .settings)
    }
}

/// "Settings…" (⌘,) in the app menu, replacing the removed Settings scene.
/// Registers the menu's `openWindow` so the main window can be reopened even
/// when no scene root view has appeared yet.
struct SettingsCommand: View {
    let opener: MainWindowOpener
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("Settings…") {
            opener.register(openWindow)
            opener.showSettings()
        }
        .keyboardShortcut(",", modifiers: .command)
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
/// the main window so the user is never left without an entry point. Also
/// owns the app-level recording session and its global hotkeys, and asks
/// before quitting while a recording is active.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    /// A throwaway copy when a debug entry point runs (see `DebugDefaults`).
    let settings = AppSettings(defaults: DebugDefaults.defaults)
    let aiProviderStore = AIProviderStore()
    lazy var modelStore = ModelStore(settings: settings)
    let tabSelection = MainTabSelection()
    lazy var windowOpener = MainWindowOpener(tabs: tabSelection)
    let whisperEngine = WhisperEngine()
    lazy var recordingController = RecordingController(
        settings: settings, modelStore: modelStore, engine: whisperEngine
    )
    lazy var hotkeyManager = HotkeyManager(settings: settings) { [weak self] action in
        guard let recording = self?.recordingController else { return }
        switch action {
        case .startStop: recording.toggleStartStop()
        case .pause: recording.togglePause()
        }
    }

    private var appliedMode: WindowMode?
    private var isStoppingForQuit = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Debug only: HEARSAY_TRANSCRIBE_FILE + HEARSAY_MODEL_DIR transcribe
        // one file, print the SRT path, and quit (see FileViewModel).
        if FileViewModel.runDebugTranscriptionIfRequested(
            settings: settings, modelStore: modelStore, engine: whisperEngine
        ) {
            return
        }
        // Debug only: HEARSAY_RECORD_SECONDS + HEARSAY_RECORD_DEVICE record
        // from one device, print diagnostics, and quit (see RecordingDebug).
        if RecordingDebug.runIfRequested() {
            return
        }
        // Debug only: HEARSAY_REPLAY_FILE + HEARSAY_MODEL_DIR replay a WAV
        // through the recording pipeline and log live-job timings (see
        // RecordingReplay).
        if RecordingReplay.runIfRequested(engine: whisperEngine) {
            return
        }
        applyWindowMode(settings.windowMode)
        observeWindowMode()
        recordingController.activate()
        hotkeyManager.start()
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

    // MARK: - Quit while recording

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if recordingController.isTranscribing {
            // No alert: the pass is cancelled at the next 30 s window, the
            // live preview (if any) is saved as the SRT, and the WAV is kept.
            guard !isStoppingForQuit else { return .terminateLater }
            isStoppingForQuit = true
            Task { @MainActor in
                await recordingController.cancelTranscriptionForQuit()
                NSApp.reply(toApplicationShouldTerminate: true)
            }
            return .terminateLater
        }
        guard recordingController.isSessionActive else { return .terminateNow }
        // A second Quit while the recording is being saved just waits.
        guard !isStoppingForQuit else { return .terminateLater }

        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Stop recording and quit?"
        alert.informativeText = "The recording is saved before Hearsay quits."
        alert.addButton(withTitle: "Stop & Quit")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return .terminateCancel }

        isStoppingForQuit = true
        Task { @MainActor in
            await recordingController.stop()
            // Stopping starts the final pass; do not wait for it.
            await recordingController.cancelTranscriptionForQuit()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
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
        // window the user is working in (normally the main window's Settings
        // tab) in front.
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

/// Debug entry points (`HEARSAY_TRANSCRIBE_FILE`, `HEARSAY_REPLAY_FILE`,
/// `HEARSAY_RECORD_SECONDS`) run on a throwaway defaults suite, so they never
/// write the user's settings, not even the one-time language migration in
/// `AppSettings.init`. The copy starts with the user's values for the keys
/// that shape a transcription, read without writing anything back.
@MainActor
enum DebugDefaults {
    static let debugVariables = ["HEARSAY_TRANSCRIBE_FILE", "HEARSAY_REPLAY_FILE", "HEARSAY_RECORD_SECONDS"]
    static let copiedKeys = [
        AppSettings.Key.outputFolderBookmark,
        AppSettings.Key.defaultLanguageCode,
        AppSettings.Key.languageChoice,
        AppSettings.Key.preferredLanguage,
        AppSettings.Key.activeModelRepo,
        AppSettings.Key.chineseScript,
    ]

    /// The suite name when a debug entry point was requested.
    private static var suiteName: String?

    /// `.standard` for a normal launch, a fresh suite for a debug run.
    @MainActor
    static var defaults: UserDefaults {
        let environment = ProcessInfo.processInfo.environment
        guard debugVariables.contains(where: { !(environment[$0] ?? "").isEmpty }) else { return .standard }
        let name = suiteName ?? "tw.og1o.hearsay.debug-\(UUID().uuidString)"
        guard let suite = UserDefaults(suiteName: name) else { return .standard }
        if suiteName == nil {
            suiteName = name
            for key in copiedKeys {
                if let value = UserDefaults.standard.object(forKey: key) {
                    suite.set(value, forKey: key)
                }
            }
        }
        return suite
    }

    /// Removes the throwaway suite (call before a debug run exits).
    @MainActor
    static func removeSuite() {
        guard let suiteName else { return }
        UserDefaults.standard.removePersistentDomain(forName: suiteName)
    }
}
