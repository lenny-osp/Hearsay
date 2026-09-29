import AppKit
import HearsayCore
import Observation
import SwiftUI

/// The update check (PLAN.md 4.6): asks GitHub for the latest release of
/// the repository in Info.plist `HearsayUpdateRepository` and, when it is
/// newer than this build, offers to install it (`UpdateInstaller`) or to
/// open the release page. Nothing is downloaded or installed without the
/// user's click, and nothing but the requests themselves is sent.
///
/// - Automatic: when "Automatically check for updates" is on, 10 s after
///   launch and then hourly, a check runs if the last successful one is at
///   least 24 h old. Errors are ignored; an alert appears only when a newer
///   version exists.
/// - Manual (Check for Updates…, Settings > General > Check Now): an alert
///   every time, with the result or the error.
@MainActor
@Observable
final class UpdateService {
    enum Outcome: Equatable {
        case available(ReleaseInfo)
        case upToDate
        case failed(String)
    }

    /// Info.plist key holding the "owner/name" slug.
    static let repositoryKey = "HearsayUpdateRepository"

    let currentVersion: String
    let buildNumber: String
    private(set) var isChecking = false
    /// The last result of this session, shown in Settings.
    private(set) var lastOutcome: Outcome?
    /// Downloads, verifies, and installs a release (Install Update).
    let installer: UpdateInstaller

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let checker: UpdateChecker?
    @ObservationIgnored private var scheduler: Task<Void, Never>?

    init(settings: AppSettings, bundle: Bundle = .main, session: URLSession = .shared) {
        self.settings = settings
        currentVersion = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        buildNumber = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0"
        let repository = (bundle.object(forInfoDictionaryKey: Self.repositoryKey) as? String ?? "")
            .trimmingCharacters(in: .whitespaces)
        checker = repository.isEmpty ? nil : UpdateChecker(repository: repository, session: session)
        installer = UpdateInstaller(session: session)
    }

    /// "Version 0.1.0 (1)".
    var versionLine: String {
        String(localized: "Version \(currentVersion) (\(buildNumber))",
               comment: "Settings > General > Software updates. %1$@ is the version, %2$@ the build number.")
    }

    /// One line for Settings describing `lastOutcome`.
    var resultLine: String? {
        if let line = installer.statusLine { return line }
        switch lastOutcome {
        case nil: return nil
        case .available(let release): return Self.availableTitle(release.version)
        case .upToDate: return upToDateTitle
        case .failed(let message): return message
        }
    }

    private var upToDateTitle: String {
        String(localized: "You're up to date (\(currentVersion)).",
               comment: "Update check result. %@ is the installed version.")
    }

    private static func availableTitle(_ version: String) -> String {
        String(localized: "Hearsay \(version) is available.",
               comment: "Update check result. %@ is the new version.")
    }

    // MARK: - Automatic checks

    /// Starts the automatic schedule. Call once after launch (not in debug
    /// runs).
    func startAutomaticChecks() {
        guard scheduler == nil else { return }
        scheduler = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(10))
            while !Task.isCancelled {
                guard let self else { return }
                if UpdateChecker.isAutomaticCheckDue(
                    enabled: self.settings.automaticUpdateChecks, lastCheck: self.settings.lastUpdateCheck
                ) {
                    await self.checkNow(userInitiated: false)
                }
                try? await Task.sleep(for: .seconds(60 * 60))
            }
        }
    }

    // MARK: - Checking

    /// Asks GitHub once. A manual check always shows an alert; an automatic
    /// one only when a newer version exists.
    func checkNow(userInitiated: Bool) async {
        guard !isChecking else { return }
        installer.clearFailure()
        isChecking = true
        defer { isChecking = false }
        let outcome = await fetchOutcome()
        if userInitiated || !Self.isFailure(outcome) {
            lastOutcome = outcome
        }
        switch outcome {
        case .available(let release):
            // An install of this (or any) release is already under way.
            guard !installer.isWorking else {
                // A manual check brings the progress window forward.
                if userInitiated { installer.install(release: release) }
                return
            }
            presentAvailable(release)
        case .upToDate, .failed:
            if userInitiated { presentResult(outcome) }
        }
    }

    private func fetchOutcome() async -> Outcome {
        guard let checker else {
            return .failed(String(localized: "This build of Hearsay has no update repository set.",
                                  comment: "Update check error: Info.plist has no HearsayUpdateRepository"))
        }
        do {
            let release = try await checker.latestRelease()
            settings.lastUpdateCheck = Date()
            return UpdateChecker.isNewer(release.version, than: currentVersion) ? .available(release) : .upToDate
        } catch UpdateCheckError.noRelease {
            // The check itself worked; there is just nothing to offer.
            settings.lastUpdateCheck = Date()
            return .failed(UpdateCheckError.noRelease.localizedDescription)
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    private static func isFailure(_ outcome: Outcome) -> Bool {
        if case .failed = outcome { return true }
        return false
    }

    // MARK: - Alerts

    /// Install Update / View on GitHub / Later, or Download / Later with
    /// the reason when this copy cannot install the release itself.
    private func presentAvailable(_ release: ReleaseInfo) {
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = Self.availableTitle(release.version)
        let installed = String(localized: "You have version \(currentVersion).",
                               comment: "Update available alert. %@ is the installed version.")
        if let reason = installProblem(release) {
            alert.informativeText = installed + " " + reason + " " + String(
                localized: "The download page opens in your browser.",
                comment: "Update available alert, when the app cannot install the update itself")
            alert.addButton(withTitle: String(localized: "Download",
                                              comment: "Button: download. Also used on the Models tab for a model; here it opens the release page of a new version"))
            alert.addButton(withTitle: String(localized: "Later",
                                              comment: "Update available alert button: do nothing now"))
            if alert.runModal() == .alertFirstButtonReturn {
                NSWorkspace.shared.open(release.htmlURL)
            }
            return
        }
        alert.informativeText = installed
        alert.addButton(withTitle: String(localized: "Install Update",
                                          comment: "Update available alert button: download and install the new version"))
        alert.addButton(withTitle: UpdateInstaller.viewOnGitHubTitle)
        alert.addButton(withTitle: String(localized: "Later",
                                          comment: "Update available alert button: do nothing now"))
        switch alert.runModal() {
        case .alertFirstButtonReturn: installer.install(release: release)
        case .alertSecondButtonReturn: NSWorkspace.shared.open(release.htmlURL)
        default: break
        }
    }

    /// Why this copy cannot install `release` itself, or nil when it can.
    private func installProblem(_ release: ReleaseInfo) -> String? {
        guard release.dmgAsset(forVersion: release.version) != nil, release.checksumsAsset != nil else {
            return String(localized: "The release has no disk image or checksum list.", comment: "Update error")
        }
        do {
            try UpdateInstall.checkInstallLocation(bundleURL: Bundle.main.bundleURL)
        } catch {
            return error.localizedDescription
        }
        return nil
    }

    private func presentResult(_ outcome: Outcome) {
        NSApp.activate()
        let alert = NSAlert()
        switch outcome {
        case .upToDate:
            alert.messageText = upToDateTitle
        case .failed(let message):
            alert.alertStyle = .warning
            alert.messageText = String(localized: "Could not check for updates.",
                                       comment: "Update check failed alert title")
            alert.informativeText = message
        case .available:
            return
        }
        alert.runModal()
    }
}

/// "Check for Updates…" in the app menu, below About Hearsay.
struct CheckForUpdatesCommand: View {
    let updates: UpdateService

    var body: some View {
        Button(String(localized: "Check for Updates…", comment: "App menu command")) {
            Task { await updates.checkNow(userInitiated: true) }
        }
        .disabled(updates.isChecking)
    }
}

/// "Software updates" in Settings > General: version, the automatic-check
/// toggle, and Check Now with the last check and its result.
struct SoftwareUpdatesSection: View {
    @Environment(AppSettings.self) private var settings
    @Environment(UpdateService.self) private var updates: UpdateService?

    var body: some View {
        @Bindable var settings = settings
        Section {
            if let updates {
                Text(verbatim: updates.versionLine)
            }
            Toggle(isOn: $settings.automaticUpdateChecks) {
                Text("Automatically check for updates", comment: "Settings > General > Software updates toggle")
            }
            Text("Checks GitHub once a day for a new release. Nothing else is sent.",
                 comment: "Settings > General caption under Automatically check for updates")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Button {
                    guard let updates else { return }
                    Task { await updates.checkNow(userInitiated: true) }
                } label: {
                    Text("Check Now", comment: "Settings > General > Software updates button")
                }
                .disabled(updates == nil || updates?.isChecking == true)
                if updates?.isChecking == true {
                    ProgressView().controlSize(.small)
                }
                Spacer()
                lastCheckText
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if let line = updates?.resultLine {
                Text(verbatim: line)
                    .font(.caption)
                    .textSelection(.enabled)
            }
        } header: {
            Text("Software updates", comment: "Settings > General section header")
        }
    }

    private var lastCheckText: Text {
        if let date = settings.lastUpdateCheck {
            Text("Last checked \(date.formatted(date: .abbreviated, time: .shortened))",
                 comment: "Settings > General > Software updates. %@ is a date and time.")
        } else {
            Text("Not checked yet", comment: "Settings > General > Software updates: no check has succeeded")
        }
    }
}
