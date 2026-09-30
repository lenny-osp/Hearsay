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
        Button(String(localized: "Settings…", comment: "App menu command that opens the Settings tab (⌘,)")) {
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
    let relauncher = AppRelauncher()
    lazy var permissionMonitor = PermissionMonitor(settings: settings)
    lazy var updateService = UpdateService(settings: settings)
    /// Finished recordings waiting for or running their final pass (PLAN.md 4.9).
    lazy var transcriptionQueue = TranscriptionQueue(
        settings: settings, modelStore: modelStore, engine: whisperEngine
    )
    lazy var recordingController = RecordingController(
        settings: settings, modelStore: modelStore, engine: whisperEngine, queue: transcriptionQueue
    )
    lazy var hotkeyManager = HotkeyManager(settings: settings) { [weak self] action in
        guard let recording = self?.recordingController else { return }
        switch action {
        case .startStop: recording.toggleStartStop()
        case .pause: recording.togglePause()
        case .stopStartNext: recording.stopAndStartNext()
        }
    }

    private var appliedMode: WindowMode?
    private var isStoppingForQuit = false
    /// "Restart Now" after an interface-language change: once the quit is
    /// allowed (and a recording saved), a new instance is opened.
    private var relaunchAfterQuit = false

    func applicationWillFinishLaunching(_ notification: Notification) {
        relauncher.handler = { [weak self] in self?.restart() }
        // Install Update (PLAN.md 4.6): waits for recordings and
        // transcriptions, then relaunches through the same path.
        updateService.installer.relaunch = { [weak self] in self?.restart() }
        updateService.installer.installBlocker = { [weak self] in await self?.updateInstallBlocker() }
        // One main window, never tabbed: removes View > Show Tab Bar and
        // Show All Tabs. Set before any window exists.
        NSWindow.allowsAutomaticWindowTabbing = false
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Debug only: HEARSAY_UI_SNAPSHOTS=<dir> renders every tab, Settings
        // section, and sheet into PNGs and quits (see UISnapshots).
        if UISnapshots.runIfRequested(delegate: self) {
            return
        }
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
        // Debug only: HEARSAY_INSTALL_UPDATE + HEARSAY_INSTALL_TARGET verify a
        // DMG and install it over a scratch app bundle (see UpdateInstallDebug).
        if UpdateInstallDebug.runIfRequested() {
            return
        }
        applyWindowMode(settings.windowMode)
        observeWindowMode()
        recordingController.activate()
        // Recordings a previous run did not transcribe continue (PLAN.md 4.9).
        transcriptionQueue.restore()
        // Microphone and system audio status, and the re-approval sheet for a
        // grant an update made stale (PLAN.md section 9).
        permissionMonitor.onStaleGrantDetected = { [weak self] in
            self?.windowOpener.show(tab: .record)
        }
        permissionMonitor.start()
        Task { @MainActor in
            // After "Restart Now" the previous instance may still hold the
            // global shortcuts for a moment.
            await AppRelaunch.waitForPreviousInstance()
            hotkeyManager.start()
        }
        // GitHub release check, 10 s after launch and daily (PLAN.md 4.6).
        updateService.startAutomaticChecks()
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
        // A second Quit while the recording is being saved just waits.
        guard !isStoppingForQuit else { return .terminateLater }
        let queue = transcriptionQueue
        if recordingController.isSessionActive {
            NSApp.activate()
            let alert = NSAlert()
            alert.messageText = String(localized: "Stop recording and quit?",
                                       comment: "Alert when quitting (or restarting) while a recording is active")
            alert.informativeText = String(localized: "The recording is saved before Hearsay quits.",
                                           comment: "Alert when quitting while a recording is active")
            alert.addButton(withTitle: String(localized: "Stop & Quit", comment: "Alert button: stop the recording, then quit"))
            alert.addButton(withTitle: String(localized: "Cancel", comment: "Alert button"))
            guard alert.runModal() == .alertFirstButtonReturn else {
                relaunchAfterQuit = false
                return .terminateCancel
            }
            isStoppingForQuit = true
            Task { @MainActor in
                // The recording becomes a queue job; the next launch
                // transcribes it (PLAN.md 4.9).
                await recordingController.stopForQuit()
                await queue.prepareForQuit()
                await replyToTerminate()
            }
            return .terminateLater
        }
        let pending = queue.pendingCount
        if pending > 0 {
            NSApp.activate()
            let alert = NSAlert()
            alert.messageText = String(localized: "Recordings not transcribed yet: \(pending)",
                                       comment: "Alert when quitting while the transcription queue has recordings. %lld is their number.")
            alert.informativeText = String(localized: "Hearsay continues with them the next time it opens.",
                                           comment: "Alert when quitting while the transcription queue has recordings")
            alert.addButton(withTitle: String(localized: "Quit", comment: "Alert button: quit Hearsay"))
            alert.addButton(withTitle: String(localized: "Cancel", comment: "Alert button"))
            guard alert.runModal() == .alertFirstButtonReturn else {
                relaunchAfterQuit = false
                return .terminateCancel
            }
        }
        if pending > 0 || queue.hasWorkInFlight {
            // No alert for a re-run in progress: it stops at the next 30 s
            // window and the previous SRT stays. The spool WAVs of pending
            // jobs stay for the next launch.
            isStoppingForQuit = true
            Task { @MainActor in
                // No session is active; this only keeps one from starting.
                await recordingController.stopForQuit()
                await queue.prepareForQuit()
                await replyToTerminate()
            }
            return .terminateLater
        }
        guard relaunchAfterQuit else { return .terminateNow }
        Task { @MainActor in await replyToTerminate() }
        return .terminateLater
    }

    // MARK: - Update install

    /// Why Install and Relaunch must wait, or nil: a recording session, a
    /// recording still waiting for its transcription, or any Whisper job
    /// (File mode, a re-run) still running.
    private func updateInstallBlocker() async -> String? {
        if transcriptionQueue.blocksUpdateInstall {
            return String(localized: "Wait until the transcriptions are finished.",
                          comment: "Alert when Install and Relaunch is chosen while recordings wait for their transcription")
        }
        let recordingBusy = recordingController.isSessionActive
        let engineBusy = await whisperEngine.isBusy
        guard recordingBusy || engineBusy else { return nil }
        return String(localized: "Finish the recording first.",
                      comment: "Alert when Install and Relaunch is chosen while recording or transcribing")
    }

    // MARK: - Restart

    /// "Restart Now": quits through the usual quit path (which asks before
    /// stopping a recording) and opens a new instance once quitting is
    /// allowed.
    ///
    /// The terminate call is deferred to the run loop. `applicationShouldTerminate`
    /// answers `.terminateLater` and replies from a main-actor Task, and
    /// AppKit waits for that reply in a nested run loop. When `restart()` runs
    /// inside a main-actor job (Install and Relaunch is called from the
    /// installer's Task), that nested run loop cannot run other main-actor
    /// jobs, so the reply never came and Hearsay hung on "Installing"
    /// (fixed 2026-09-29). A run-loop block is not a main-actor job, so the
    /// reply Task runs.
    private func restart() {
        relaunchAfterQuit = true
        RunLoop.main.perform {
            NSApp.terminate(nil)
        }
    }

    /// Allows the pending quit, opening the new instance first when a
    /// restart was asked for. If that launch fails, Hearsay keeps running
    /// and says so.
    private func replyToTerminate() async {
        guard relaunchAfterQuit else {
            NSApp.reply(toApplicationShouldTerminate: true)
            return
        }
        relaunchAfterQuit = false
        if await AppRelaunch.launchNewInstance() {
            NSApp.reply(toApplicationShouldTerminate: true)
            return
        }
        isStoppingForQuit = false
        NSApp.reply(toApplicationShouldTerminate: false)
        recordingController.quitCancelled()
        transcriptionQueue.quitCancelled()
        let alert = NSAlert()
        alert.messageText = String(localized: "Hearsay could not restart.",
                                   comment: "Alert when the automatic restart (language change or update) failed")
        alert.informativeText = String(localized: "Quit Hearsay and open it again.",
                                       comment: "Alert when the automatic restart (language change or update) failed")
        alert.runModal()
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
/// `HEARSAY_RECORD_SECONDS`, `HEARSAY_UI_SNAPSHOTS`, `HEARSAY_INSTALL_UPDATE`) run on a throwaway
/// defaults suite and never write `AppleLanguages`, so they never
/// write the user's settings, not even the one-time language migration in
/// `AppSettings.init`. The copy starts with the user's values for the keys
/// that shape a transcription, read without writing anything back.
@MainActor
enum DebugDefaults {
    static let debugVariables = [
        "HEARSAY_TRANSCRIBE_FILE", "HEARSAY_REPLAY_FILE", "HEARSAY_RECORD_SECONDS", UISnapshots.variable,
        UpdateInstallDebug.variable,
    ]
    static let copiedKeys = [
        AppSettings.Key.interfaceLanguage,
        AppSettings.Key.outputFolderBookmark,
        AppSettings.Key.defaultLanguageCode,
        AppSettings.Key.languageChoice,
        AppSettings.Key.preferredLanguage,
        AppSettings.Key.activeModelRepo,
        AppSettings.Key.chineseScript,
    ]

    /// The suite name when a debug entry point was requested.
    private static var suiteName: String?

    /// A debug entry point was requested through the environment.
    static var isDebugRun: Bool {
        let environment = ProcessInfo.processInfo.environment
        return debugVariables.contains { !(environment[$0] ?? "").isEmpty }
    }

    /// `.standard` for a normal launch, a fresh suite for a debug run.
    @MainActor
    static var defaults: UserDefaults {
        let environment = ProcessInfo.processInfo.environment
        guard isDebugRun else { return .standard }
        let name = suiteName ?? "tw.og1o.hearsay.debug-\(UUID().uuidString)"
        guard let suite = UserDefaults(suiteName: name) else { return .standard }
        if suiteName == nil {
            suiteName = name
            for key in copiedKeys {
                if let value = UserDefaults.standard.object(forKey: key) {
                    suite.set(value, forKey: key)
                }
            }
            // HEARSAY_UI_LANGUAGE renders a debug run in another language.
            if let language = InterfaceLanguage.override(in: environment) {
                suite.set(language.rawValue, forKey: AppSettings.Key.interfaceLanguage)
            }
        }
        return suite
    }

    /// Removes the throwaway suite (call before a debug run exits).
    @MainActor
    static func removeSuite() {
        guard let suiteName else { return }
        removeDomain(named: suiteName)
    }

    /// Removes a throwaway defaults domain and its plist. Flushes first:
    /// cfprefsd otherwise writes an empty plist after the file is deleted.
    static func removeDomain(named name: String) {
        UserDefaults.standard.removePersistentDomain(forName: name)
        CFPreferencesAppSynchronize(name as CFString)
        let plist = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Preferences/\(name).plist")
        try? FileManager.default.removeItem(at: plist)
    }
}
