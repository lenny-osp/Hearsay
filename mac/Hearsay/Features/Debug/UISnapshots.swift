import AppKit
import HearsayCore
import SwiftUI

/// Debug only. When the app is launched with `HEARSAY_UI_SNAPSHOTS=<dir>`,
/// renders the app's own views (never a screen capture) into PNGs in
/// `<dir>`: every main-window tab, every Settings section, the confirm,
/// naming, onboarding, and unfinished-recording sheets with sample data, the
/// menu bar panel, and the help page (top and the Meeting notes section).
/// Then it quits with status 0 (1 when a file could not be written).
///
/// The views run in the interface language of `HEARSAY_UI_LANGUAGE` (en, de,
/// es, zh-Hant, zh-Hans; default the stored choice), applied at launch
/// without writing any setting (`InterfaceLanguageLaunch`). Settings and the
/// AI provider store live in the throwaway `DebugDefaults` suite (tokens in
/// memory), and sample files in a
/// temporary folder that is removed afterwards, so the user's settings,
/// Keychain, spool, models, and output folder are never touched.
///
///     HEARSAY_UI_SNAPSHOTS=/tmp/hearsay-ui HEARSAY_UI_LANGUAGE=de \
///     .build/derived/Build/Products/Release/Hearsay.app/Contents/MacOS/Hearsay
@MainActor
enum UISnapshots {
    static let variable = "HEARSAY_UI_SNAPSHOTS"

    /// A snapshot run was requested (the main window then skips the spool
    /// check for unfinished recordings).
    static var isRunning: Bool {
        !(ProcessInfo.processInfo.environment[variable] ?? "").isEmpty
    }

    /// Returns false (and does nothing) when the variable is not set.
    static func runIfRequested(delegate: AppDelegate) -> Bool {
        guard let path = ProcessInfo.processInfo.environment[variable], !path.isEmpty else { return false }
        Task { @MainActor in
            let status = await run(directory: URL(fileURLWithPath: path, isDirectory: true), delegate: delegate)
            DebugDefaults.removeSuite()
            exit(status)
        }
        return true
    }

    private static func run(directory: URL, delegate: AppDelegate) async -> Int32 {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("hearsay-ui-snapshots-\(UUID().uuidString)")
        defer { try? fileManager.removeItem(at: root) }
        // The SwiftUI main window opens on its own at launch; only the
        // windows below are rendered.
        for window in delegate.windowOpener.mainWindows { window.close() }

        let samples: Samples
        let aiStore: AIProviderStore
        do {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            samples = try Samples(root: root)
            // The throwaway debug suite holds no AI keys (they are not
            // copied), so the store starts from a fresh configuration.
            aiStore = AIProviderStore(
                defaults: DebugDefaults.defaults, secrets: InMemorySecretStore(), installedCLI: { nil }
            )
        } catch {
            say("cannot prepare the sample data: \(error)")
            return 1
        }

        let settings = delegate.settings
        do {
            settings.outputFolderBookmark = try OutputLocation.makeBookmark(for: samples.output)
        } catch {
            say("cannot use \(samples.output.path): \(error)")
            return 1
        }
        settings.languageChoice = .auto
        let modelStore = ModelStore(settings: settings, rootURL: root.appendingPathComponent("models"))
        let controller = RecordingController(
            settings: settings, modelStore: modelStore, engine: delegate.whisperEngine,
            spool: RecordingSpool(root: root.appendingPathComponent("spool"))
        )
        let tabs = MainTabSelection()
        let opener = MainWindowOpener(tabs: tabs)
        let hotkeys = HotkeyManager(settings: settings) { _ in }
        let context = Context(
            settings: settings, modelStore: modelStore, aiStore: aiStore, controller: controller,
            engine: delegate.whisperEngine, hotkeys: hotkeys, tabs: tabs, opener: opener,
            relauncher: delegate.relauncher, updates: delegate.updateService
        )
        let language = InterfaceLanguageLaunch.applied
        say("language \(language.code), bundle localization \(Bundle.main.preferredLocalizations.first ?? "none")")

        var failed = false
        func render<V: View>(_ name: String, width: CGFloat, height: CGFloat? = nil, _ view: V) async {
            let url = directory.appendingPathComponent("\(name).png")
            if await snapshot(context.apply(to: view), width: width, height: height, to: url) {
                say("wrote \(url.path)")
            } else {
                say("could not write \(url.path)")
                failed = true
            }
        }

        let mainTabs: [(String, MainTab)] = [
            ("01-record", .record), ("02-file", .file), ("03-models", .models),
            ("04-history", .history), ("05-settings", .settings),
        ]
        for (name, tab) in mainTabs {
            tabs.tab = tab
            await render(name, width: 720, height: 560, MainView())
        }
        let panes: [(String, SettingsView.Pane)] = [
            ("06-settings-general", .general), ("07-settings-window", .window),
            ("08-settings-output", .output), ("09-settings-ai", .ai),
        ]
        for (name, pane) in panes {
            await render(name, width: 720, height: 1000, SettingsView(initialPane: pane))
        }
        aiStore.selectPreset(.claudeCodeCLI)
        await render("10-settings-ai-cli", width: 720, height: 1000, SettingsView(initialPane: .ai))
        aiStore.selectPreset(.custom)
        await render("11-settings-ai-custom", width: 720, height: 1000, SettingsView(initialPane: .ai))

        let confirm = NotesFlowViewModel(store: aiStore)
        confirm.run(srtURL: samples.meetingSRT, language: .english)
        confirm.notesLanguage = .german
        await render("12-sheet-confirm", width: 440, ConfirmSendSheet(model: confirm))
        await render("13-sheet-naming", width: 420,
                     NamingSheet(suggestion: "quarterly-planning", onSave: { _ in }, onCancel: {}))
        await render("14-sheet-naming-manual", width: 420,
                     NamingSheet(suggestion: nil, onSave: { _ in }, onCancel: {}))
        await render("15-sheet-naming-regenerate", width: 420, NamingSheet(
            suggestion: "genhe-road-trip", currentName: "trip-to-genhe", replacesNotes: true,
            onSave: { _ in }, onCancel: {}
        ))
        await render("16-sheet-onboarding", width: 460, OnboardingModelSheet())
        let queue = UnfinishedRecordingQueue(
            recordings: [samples.unfinishedWAV, samples.unfinishedWAV],
            spool: RecordingSpool(root: root.appendingPathComponent("spool"))
        )
        await render("17-sheet-unfinished-recording", width: 460,
                     UnfinishedRecordingSheet(queue: queue, recording: samples.unfinishedWAV, onTranscribe: { _ in }))
        await render("18-menu-bar", width: 260, MenuBarView())
        say("help file \(HelpWindow.contentURL?.path ?? "missing")")
        for (name, fragment) in [("19-help-top", nil), ("20-help-meeting-notes", "meeting-notes")] as [(String, String?)] {
            let url = directory.appendingPathComponent("\(name).png")
            if await snapshotHelp(fragment: fragment, to: url) {
                say("wrote \(url.path)")
            } else {
                say("could not write \(url.path)")
                failed = true
            }
        }
        return failed ? 1 : 0
    }

    /// Renders the help page at the help window's default size, scrolled to
    /// `fragment` when given. A web view draws out of process, so it is
    /// captured with `takeSnapshot`, not `cacheDisplay`.
    private static func snapshotHelp(fragment: String?, to url: URL) async -> Bool {
        guard let file = HelpWindow.contentURL else { return false }
        var target = file
        if let fragment, var components = URLComponents(url: file, resolvingAgainstBaseURL: false) {
            components.fragment = fragment
            target = components.url ?? file
        }
        let size = NSSize(width: 760, height: 640)
        let webView = HelpWebView.makeWebView()
        let window = NSWindow(
            contentRect: NSRect(origin: NSPoint(x: 40, y: 40), size: size),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = webView
        webView.frame = NSRect(origin: .zero, size: size)
        window.orderFrontRegardless()
        defer { window.close() }
        HelpWebView.load(target, in: webView)
        try? await Task.sleep(for: .milliseconds(300))
        for _ in 0..<100 where webView.isLoading {
            try? await Task.sleep(for: .milliseconds(100))
        }
        try? await Task.sleep(for: .milliseconds(700))
        guard let image = try? await webView.takeSnapshot(configuration: nil),
              let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let data = rep.representation(using: .png, properties: [:]) else { return false }
        do {
            try data.write(to: url, options: .atomic)
            return true
        } catch {
            return false
        }
    }

    /// Hosts `view` in a borderless window (never constrained to the screen),
    /// lets SwiftUI lay it out and run its `onAppear` work, and writes the
    /// window's own content as a PNG. A nil `height` uses the view's fitting
    /// height.
    private static func snapshot(_ view: some View, width: CGFloat, height: CGFloat?, to url: URL) async -> Bool {
        // An opaque background: the PNG otherwise keeps the window's
        // transparency and light text disappears on a white viewer.
        let hosting = NSHostingView(rootView: view.background(Color(nsColor: .windowBackgroundColor)))
        let fitting = hosting.fittingSize
        let size = NSSize(width: width, height: height ?? max(fitting.height, 40))
        let window = NSWindow(
            contentRect: NSRect(origin: NSPoint(x: 40, y: 40), size: size),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        // Light appearance: controls drawn with materials (the tab bar)
        // come out white in a cached display, which hides light text.
        window.appearance = NSAppearance(named: .aqua)
        window.backgroundColor = .windowBackgroundColor
        window.contentView = hosting
        hosting.frame = NSRect(origin: .zero, size: size)
        window.orderFrontRegardless()
        defer { window.close() }
        try? await Task.sleep(for: .milliseconds(1500))
        hosting.layoutSubtreeIfNeeded()
        guard let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return false }
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        guard let data = rep.representation(using: .png, properties: [:]) else { return false }
        do {
            try data.write(to: url, options: .atomic)
            return true
        } catch {
            return false
        }
    }

    private static func say(_ line: String) {
        FileHandle.standardError.write(Data(("hearsay ui snapshots: " + line + "\n").utf8))
    }

    /// Everything the views read from the environment.
    @MainActor
    private struct Context {
        let settings: AppSettings
        let modelStore: ModelStore
        let aiStore: AIProviderStore
        let controller: RecordingController
        let engine: WhisperEngine
        let hotkeys: HotkeyManager
        let tabs: MainTabSelection
        let opener: MainWindowOpener
        let relauncher: AppRelauncher
        let updates: UpdateService

        func apply(to view: some View) -> some View {
            view
                .environment(\.locale, InterfaceLanguageLaunch.applied.locale)
                .environment(settings)
                .environment(modelStore)
                .environment(aiStore)
                .environment(controller)
                .environment(\.whisperEngine, engine)
                .environment(hotkeys)
                .environment(tabs)
                .environment(opener)
                .environment(relauncher)
                .environment(updates)
        }
    }

    /// Sample files in a temporary folder: two meetings in the output folder
    /// (one with notes) and a WAV for the unfinished-recording sheet.
    private struct Samples {
        let output: URL
        let meetingSRT: URL
        let unfinishedWAV: URL

        init(root: URL) throws {
            let fileManager = FileManager.default
            output = root.appendingPathComponent("out", isDirectory: true)
            let spool = root.appendingPathComponent("recordings", isDirectory: true)
            try fileManager.createDirectory(at: output, withIntermediateDirectories: true)
            try fileManager.createDirectory(at: spool, withIntermediateDirectories: true)
            let srt = """
                1
                00:00:00,000 --> 00:00:04,000
                Good morning, everyone. Let's start with the quarterly numbers.

                2
                00:00:04,000 --> 00:00:09,500
                Revenue is up, and the launch moves to the second week of October.

                """
            meetingSRT = output.appendingPathComponent("2026-09-21_10-00-00_quarterly-planning.srt")
            try Data(srt.utf8).write(to: meetingSRT)
            try Data("# Quarterly planning\n".utf8)
                .write(to: output.appendingPathComponent("2026-09-21_10-00-00_quarterly-planning.md"))
            try Data("# Quarterly planning transcript\n".utf8)
                .write(to: output.appendingPathComponent("2026-09-21_10-00-00_quarterly-planning_transcript.md"))
            try Data(srt.utf8).write(to: output.appendingPathComponent("2026-09-25_14-30-00.srt"))
            unfinishedWAV = spool.appendingPathComponent("2026-09-27_09-15-00.wav")
            let writer = try WavWriter(url: unfinishedWAV)
            try writer.append([Float](repeating: 0, count: WavWriter.sampleRate * 83))
            try writer.close()
        }
    }
}
