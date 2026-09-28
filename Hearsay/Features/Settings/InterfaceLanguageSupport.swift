import AppKit
import HearsayCore
import os
import SwiftUI

/// Applies the interface language (Settings > General > Interface language)
/// the standard macOS way: `AppleLanguages = [code]` in Hearsay's own
/// defaults domain, written from `main` before any UI or localized string
/// loads. Foundation resolves each bundle's localization once, so a change
/// takes effect at the next launch (the picker offers a restart).
///
/// Debug entry points (`DebugDefaults.isDebugRun`) never write it: they use
/// `HEARSAY_UI_LANGUAGE` (or the stored choice) through the volatile
/// argument domain, which lives only in this process.
@MainActor
enum InterfaceLanguageLaunch {
    /// The language this process runs in, fixed at launch. Scenes set
    /// SwiftUI's `\.locale` from it so dates and numbers match the strings.
    private(set) static var applied: InterfaceLanguage = .english

    /// The stored choice, read without creating `AppSettings` (whose init
    /// may migrate other keys).
    static func storedChoice(in defaults: UserDefaults = .standard) -> InterfaceLanguage {
        InterfaceLanguage(rawValue: defaults.string(forKey: AppSettings.Key.interfaceLanguage) ?? "") ?? .english
    }

    /// Called first thing in `main`.
    static func applyAtLaunch() {
        let environment = ProcessInfo.processInfo.environment
        if DebugDefaults.isDebugRun {
            let language = InterfaceLanguage.override(in: environment) ?? storedChoice()
            applied = language
            let standard = UserDefaults.standard
            var arguments = standard.volatileDomain(forName: UserDefaults.argumentDomain)
            arguments[InterfaceLanguage.appleLanguagesKey] = [language.code]
            standard.setVolatileDomain(arguments, forName: UserDefaults.argumentDomain)
            return
        }
        let language = storedChoice()
        applied = language
        language.writePreferredLanguages(to: .standard)
    }

    /// The picker changed: store `AppleLanguages` for the next launch. Not
    /// in debug runs, which must not touch the user's defaults.
    static func prepareNextLaunch(_ language: InterfaceLanguage) {
        guard !DebugDefaults.isDebugRun else { return }
        language.writePreferredLanguages(to: .standard)
    }
}

/// Lets a view ask the app delegate to relaunch Hearsay (the restart after
/// an interface-language change). The delegate installs the handler.
@MainActor
@Observable
final class AppRelauncher {
    @ObservationIgnored var handler: () -> Void = {}

    func restart() {
        handler()
    }
}

/// Starting a second instance of Hearsay and waiting for the first one to go
/// away, for "Restart Now".
@MainActor
enum AppRelaunch {
    /// Passed to the new instance as `-HearsayRelaunchedFromPID <pid>`: the
    /// process id of the instance that launched it and is quitting. An
    /// argument, not an environment variable, so the new instance keeps the
    /// usual launch environment.
    static let previousInstanceArgument = "-HearsayRelaunchedFromPID"
    private nonisolated static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "relaunch")

    /// Opens a new instance of this app bundle. True when it launched.
    static func launchNewInstance() async -> Bool {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        configuration.activates = true
        configuration.arguments = [previousInstanceArgument, String(ProcessInfo.processInfo.processIdentifier)]
        let url = Bundle.main.bundleURL
        return await withCheckedContinuation { continuation in
            NSWorkspace.shared.openApplication(at: url, configuration: configuration) { _, error in
                if let error {
                    logger.error("relaunch failed: \(error.localizedDescription, privacy: .public)")
                }
                continuation.resume(returning: error == nil)
            }
        }
    }

    /// In a relaunched instance, waits (up to `timeout`) until the previous
    /// instance has exited, so it has released its global shortcuts. Returns
    /// at once for a normal launch.
    static func waitForPreviousInstance(timeout: Duration = .seconds(10)) async {
        let arguments = ProcessInfo.processInfo.arguments
        guard let index = arguments.firstIndex(of: previousInstanceArgument), index + 1 < arguments.count,
              let pid = pid_t(arguments[index + 1]), pid > 0 else { return }
        let deadline = ContinuousClock.now + timeout
        while kill(pid, 0) == 0, ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(50))
        }
    }
}

/// "Interface language" in Settings > General. Each language is shown in its
/// own language. A change is stored at once and applies after a restart,
/// which the alert offers.
struct InterfaceLanguagePicker: View {
    @Environment(AppSettings.self) private var settings
    @Environment(AppRelauncher.self) private var relauncher: AppRelauncher?
    @State private var restartAlertShown = false

    var body: some View {
        Picker(selection: Binding(
            get: { settings.interfaceLanguage },
            set: { change(to: $0) }
        )) {
            ForEach(InterfaceLanguage.allCases, id: \.self) { language in
                Text(verbatim: language.autonym).tag(language)
            }
        } label: {
            Text("Interface language", comment: "Settings > General: the language of Hearsay's own menus and windows")
        }
        .alert(
            Text("Hearsay needs to restart to change the language.",
                 comment: "Alert after changing the interface language"),
            isPresented: $restartAlertShown
        ) {
            Button {
                relauncher?.restart()
            } label: {
                Text("Restart Now", comment: "Alert button: quit and reopen Hearsay now")
            }
            .keyboardShortcut(.defaultAction)
            Button(role: .cancel) {} label: {
                Text("Later", comment: "Alert button: keep running; the language changes at the next launch")
            }
        }
    }

    private func change(to language: InterfaceLanguage) {
        guard language != settings.interfaceLanguage else { return }
        settings.interfaceLanguage = language
        InterfaceLanguageLaunch.prepareNextLaunch(language)
        // Switching back to the running language needs no restart.
        restartAlertShown = language != InterfaceLanguageLaunch.applied
    }
}
