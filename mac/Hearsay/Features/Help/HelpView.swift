import AppKit
import SwiftUI
import WebKit

/// The "Hearsay Help" window (Help > Hearsay Help, ⌘?): one instance of the
/// bundled `Help.html` for the interface language, shown in a web view.
enum HelpWindow {
    static let id = "help"

    /// The Help menu item and the window's title.
    static var title: String {
        String(localized: "Hearsay Help", comment: "Help menu item and the title of the help window")
    }

    /// `Help.html` in the `.lproj` folder of the interface language, which
    /// Foundation picks from `AppleLanguages` (set at launch).
    static var contentURL: URL? {
        Bundle.main.url(forResource: "Help", withExtension: "html")
    }
}

/// Help > Hearsay Help. Opening the `Window` scene again brings the existing
/// window forward.
struct HelpCommand: View {
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button(HelpWindow.title) {
            NSApp.activate()
            openWindow(id: HelpWindow.id)
        }
        .keyboardShortcut("?", modifiers: .command)
    }
}

/// A link in the help page, mapped to what Hearsay does with it.
enum HelpNavigation: Equatable {
    /// The help page itself, including `#anchor` jumps inside it.
    case load
    /// A web link, opened in the default browser.
    case openInBrowser(URL)
    /// `hearsay://open/<destination>`: a main-window tab.
    case openInApp(HelpDestination)
    /// Anything else is ignored.
    case ignore

    static func decide(_ url: URL, helpFile: URL) -> HelpNavigation {
        switch url.scheme?.lowercased() {
        case "file":
            // Only the help file itself (with or without a fragment).
            return url.standardizedFileURL.path == helpFile.standardizedFileURL.path ? .load : .ignore
        case "about":
            return url.absoluteString == "about:blank" ? .load : .ignore
        case "https", "http", "mailto":
            return .openInBrowser(url)
        case "hearsay":
            guard url.host?.lowercased() == "open" else { return .ignore }
            let name = url.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")).lowercased()
            guard let destination = HelpDestination(rawValue: name) else { return .ignore }
            return .openInApp(destination)
        default:
            return .ignore
        }
    }
}

/// The `<destination>` of `hearsay://open/<destination>`.
enum HelpDestination: String, CaseIterable {
    case record, file, models, history, settings
    case settingsGeneral = "settings-general"
    case settingsWindow = "settings-window"
    case settingsOutput = "settings-output"
    case settingsAI = "settings-ai"

    var tab: MainTab {
        switch self {
        case .record: .record
        case .file: .file
        case .models: .models
        case .history: .history
        case .settings, .settingsGeneral, .settingsWindow, .settingsOutput, .settingsAI: .settings
        }
    }

    /// The Settings section to show, nil to leave it as it is.
    var settingsPane: SettingsView.Pane? {
        switch self {
        case .record, .file, .models, .history, .settings: nil
        case .settingsGeneral: .general
        case .settingsWindow: .window
        case .settingsOutput: .output
        case .settingsAI: .ai
        }
    }
}

/// The help window's content.
struct HelpView: View {
    @Environment(MainWindowOpener.self) private var opener

    var body: some View {
        Group {
            if let url = HelpWindow.contentURL {
                HelpWebView(url: url) { destination in
                    if let pane = destination.settingsPane {
                        opener.tabs.settingsPane = pane
                    }
                    opener.show(tab: destination.tab)
                }
            } else {
                Text(String(localized: "(\("Help.html") is missing from the app bundle.)",
                            comment: "Licenses sheet, in place of a missing file. %@ is a file name."))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(minWidth: 480, minHeight: 360)
    }
}

/// A `WKWebView` showing one local help file, JavaScript off. Anchor links
/// stay in the view, web links open in the default browser, and
/// `hearsay://open/...` links call `onOpen`.
struct HelpWebView: NSViewRepresentable {
    let url: URL
    let onOpen: @MainActor (HelpDestination) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(helpFile: url, onOpen: onOpen)
    }

    func makeNSView(context: Context) -> WKWebView {
        let webView = Self.makeWebView()
        webView.navigationDelegate = context.coordinator
        Self.load(url, in: webView)
        return webView
    }

    func updateNSView(_ webView: WKWebView, context: Context) {
        context.coordinator.onOpen = onOpen
    }

    /// Also used by the UI snapshots.
    static func makeWebView() -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.defaultWebpagePreferences.allowsContentJavaScript = false
        configuration.websiteDataStore = .nonPersistent()
        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.allowsBackForwardNavigationGestures = false
        webView.allowsMagnification = true
        webView.underPageBackgroundColor = .textBackgroundColor
        return webView
    }

    /// Loads the file (a `#fragment` jumps to that section) with read access
    /// to its `.lproj` folder only.
    static func load(_ url: URL, in webView: WKWebView) {
        let file = URL(fileURLWithPath: url.path)
        webView.loadFileURL(url, allowingReadAccessTo: file.deletingLastPathComponent())
    }

    @MainActor
    final class Coordinator: NSObject, WKNavigationDelegate {
        let helpFile: URL
        var onOpen: @MainActor (HelpDestination) -> Void

        init(helpFile: URL, onOpen: @escaping @MainActor (HelpDestination) -> Void) {
            self.helpFile = helpFile
            self.onOpen = onOpen
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping @MainActor @Sendable (WKNavigationActionPolicy) -> Void
        ) {
            guard let url = navigationAction.request.url else {
                decisionHandler(.cancel)
                return
            }
            switch HelpNavigation.decide(url, helpFile: helpFile) {
            case .load:
                decisionHandler(.allow)
            case .openInBrowser(let link):
                decisionHandler(.cancel)
                NSWorkspace.shared.open(link)
            case .openInApp(let destination):
                decisionHandler(.cancel)
                onOpen(destination)
            case .ignore:
                decisionHandler(.cancel)
            }
        }
    }
}
