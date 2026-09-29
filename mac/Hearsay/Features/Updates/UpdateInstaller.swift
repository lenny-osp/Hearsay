import AppKit
import HearsayCore
import Observation
import os
import SwiftUI

/// Downloads, verifies, and installs a new release inside the app, then
/// relaunches (PLAN.md 4.6, "Install"). Owned by `UpdateService`.
///
/// 1. The running bundle must be replaceable (`checkInstallLocation`).
/// 2. `SHA256SUMS.txt` and the DMG go to
///    `~/Library/Caches/tw.og1o.hearsay/Updates/<version>/`; a DMG already
///    there whose checksum matches is reused.
/// 3. The DMG's SHA-256 must match its line in `SHA256SUMS.txt`.
/// 4. It is mounted read-only; the one app on it must pass
///    `UpdatePackage.verifySignature`.
/// 5. The app is copied next to the running one as
///    `.Hearsay-update-<version>.app`, quarantine cleared, DMG detached.
/// 6. "Install and Relaunch" swaps the bundles and relaunches; "Later"
///    deletes the staged copy and keeps the verified DMG.
@MainActor
@Observable
final class UpdateInstaller {
    enum Phase: Equatable {
        case idle
        case downloading(fraction: Double?, received: Int64, total: Int64?)
        case verifying
        case readyToInstall(String)
        case installing
        case failed(String)
    }

    private(set) var phase: Phase = .idle
    /// The version being installed while `phase` is not idle.
    private(set) var version: String?

    /// Why an install must wait (a recording or transcription is running),
    /// or nil when it may go ahead. Installed by the app delegate.
    @ObservationIgnored var installBlocker: () async -> String? = { nil }
    /// Quits and opens the (new) bundle. Installed by the app delegate.
    @ObservationIgnored var relaunch: () -> Void = {}

    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var progressPanel: NSPanel?
    @ObservationIgnored private let session: URLSession
    private static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "updates")

    init(session: URLSession = .shared) {
        self.session = session
    }

    /// Downloading or verifying: the progress window is up.
    var isWorking: Bool {
        switch phase {
        case .downloading, .verifying, .installing, .readyToInstall: true
        case .idle, .failed: false
        }
    }

    /// One line for Settings > General > Software updates, or nil when idle.
    var statusLine: String? {
        guard let version else { return nil }
        switch phase {
        case .idle: return nil
        case .downloading(_, let received, let total):
            return Self.downloadingTitle(version) + " " + Self.bytesLine(received: received, total: total)
        case .verifying: return Self.verifyingTitle(version)
        case .readyToInstall: return Self.readyTitle(version)
        case .installing: return Self.installingTitle(version)
        case .failed(let message): return message
        }
    }

    /// The failure message is shown once; a new check clears it.
    func clearFailure() {
        if case .failed = phase { phase = .idle }
    }

    // MARK: - Cache

    /// `~/Library/Caches/tw.og1o.hearsay/Updates`.
    static var cacheRoot: URL {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches")
        return caches.appendingPathComponent("tw.og1o.hearsay/Updates", isDirectory: true)
    }

    // MARK: - Install

    /// Runs the whole install for `release`. Does nothing while one runs.
    func install(release: ReleaseInfo) {
        guard !isWorking else {
            progressPanel?.makeKeyAndOrderFront(nil)
            return
        }
        do {
            try UpdateInstall.checkInstallLocation(bundleURL: Bundle.main.bundleURL)
        } catch {
            presentFailure(title: String(localized: "Hearsay cannot install the update here.",
                                         comment: "Alert title: the running app cannot replace itself"),
                           message: error.localizedDescription, release: release)
            return
        }
        guard let dmgAsset = release.dmgAsset(forVersion: release.version),
              let sumsAsset = release.checksumsAsset else {
            presentFailure(title: String(localized: "Hearsay could not download the update.",
                                         comment: "Alert title: the update download failed"),
                           message: String(localized: "The release has no disk image or checksum list.",
                                           comment: "Update error"),
                           release: release)
            return
        }
        version = release.version
        phase = .downloading(fraction: nil, received: 0, total: dmgAsset.size > 0 ? dmgAsset.size : nil)
        showProgressPanel()
        task = Task { @MainActor [weak self] in
            await self?.run(release: release, dmgAsset: dmgAsset, sumsAsset: sumsAsset)
        }
    }

    /// Cancel in the progress window: stops the download or verification and
    /// removes partial files.
    func cancel() {
        task?.cancel()
    }

    private func run(release: ReleaseInfo, dmgAsset: ReleaseAsset, sumsAsset: ReleaseAsset) async {
        let version = release.version
        let folder = Self.cacheRoot.appendingPathComponent(version, isDirectory: true)
        let dmg = folder.appendingPathComponent(dmgAsset.name)
        let sums = folder.appendingPathComponent(ReleaseInfo.checksumsFileName)
        let mountPoint = folder.appendingPathComponent("mnt", isDirectory: true)
        let target = Bundle.main.bundleURL
        let session = self.session
        var mounted = false

        do {
            // Download.
            try Self.prepareCacheFolder(folder)
            try await UpdatePackage.download(sumsAsset.downloadURL, to: sums, session: session) { _, _ in }
            let cached = (try? await Self.offMain { try UpdatePackage.verifyChecksum(of: dmg, checksumFile: sums) }) != nil
            if cached {
                Self.logger.notice("update \(version, privacy: .public): verified DMG in the cache, no download")
            } else {
                try await UpdatePackage.download(dmgAsset.downloadURL, to: dmg, session: session) { received, total in
                    Task { @MainActor [weak self] in self?.updateProgress(received: received, total: total) }
                }
            }
            try Task.checkCancellation()

            // Verify, mount, check the signature, stage.
            phase = .verifying
            do {
                try await Self.offMain { try UpdatePackage.verifyChecksum(of: dmg, checksumFile: sums) }
                try Task.checkCancellation()
                let app = try await UpdatePackage.mount(dmg, at: mountPoint)
                mounted = true
                let signature = try await Self.offMain {
                    try UpdatePackage.verifySignature(of: app, expectedVersion: version)
                }
                Self.logger.notice("update \(version, privacy: .public): \(signature, privacy: .public)")
                try Task.checkCancellation()
                let staged = try await Self.offMain {
                    try UpdatePackage.stage(app, nextTo: target, version: version, expectedVersion: version)
                }
                await UpdatePackage.detach(mountPoint)
                mounted = false
                phase = .readyToInstall(version)
                closeProgressPanel()
                await offerInstall(staged: staged, cacheFolder: folder, release: release)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                if mounted { await UpdatePackage.detach(mountPoint) }
                mounted = false
                if let packageError = error as? UpdatePackageError {
                    // Checksum, disk image, or signature: the download is not trusted.
                    try? FileManager.default.removeItem(at: folder)
                    throw StepFailure(verification: true, message: packageError.localizedDescription)
                }
                throw StepFailure(verification: false, message: error.localizedDescription)
            }
        } catch {
            if mounted { await UpdatePackage.detach(mountPoint) }
            closeProgressPanel()
            if error is CancellationError {
                // A partial download is already gone; a complete DMG stays
                // and is checked again next time.
                try? FileManager.default.removeItem(at: mountPoint)
                Self.logger.notice("update \(version, privacy: .public): cancelled")
                phase = .idle
                self.version = nil
                return
            }
            let title: String
            let message: String
            switch error {
            case let failure as StepFailure where failure.verification:
                title = String(localized: "Hearsay could not verify the downloaded update.",
                               comment: "Alert title: checksum or code signature check of an update failed")
                message = failure.message
            case let failure as StepFailure:
                title = Self.installFailedTitle
                message = failure.message
            default:
                title = String(localized: "Hearsay could not download the update.",
                               comment: "Alert title: the update download failed")
                message = error.localizedDescription
            }
            Self.logger.error("update \(version, privacy: .public) failed: \(message, privacy: .public)")
            phase = .failed(message)
            presentFailure(title: title, message: message, release: release)
        }
    }

    /// A failure after the download: `verification` for the checksum, the
    /// disk image, or the signature; otherwise staging.
    private struct StepFailure: Error {
        var verification: Bool
        var message: String
    }

    private static var installFailedTitle: String {
        String(localized: "Hearsay could not install the update.",
               comment: "Alert title: staging or replacing the app bundle failed")
    }

    /// Creates `folder` and removes the other versions' folders.
    private static func prepareCacheFolder(_ folder: URL) throws {
        let fileManager = FileManager.default
        let root = folder.deletingLastPathComponent()
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        for other in (try? fileManager.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        where other.lastPathComponent != folder.lastPathComponent {
            try? fileManager.removeItem(at: other)
        }
    }

    /// Runs blocking file work (hashing, copying) off the main actor.
    private static func offMain<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await Task.detached(priority: .userInitiated) { try work() }.value
    }

    private func updateProgress(received: Int64, total: Int64?) {
        guard case .downloading = phase else { return }
        let fraction = total.map { $0 > 0 ? min(1, Double(received) / Double($0)) : 0 }
        phase = .downloading(fraction: fraction, received: received, total: total)
    }

    // MARK: - Ready to install

    private func offerInstall(staged: URL, cacheFolder: URL, release: ReleaseInfo) async {
        let version = release.version
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = Self.readyTitle(version)
        alert.informativeText = String(localized: "Hearsay will quit and open again as the new version.",
                                       comment: "Update ready alert")
        alert.addButton(withTitle: String(localized: "Install and Relaunch",
                                          comment: "Update ready alert button: replace the app and relaunch"))
        alert.addButton(withTitle: String(localized: "Later",
                                          comment: "Update available alert button: do nothing now"))
        guard alert.runModal() == .alertFirstButtonReturn else {
            discardStaged(staged)
            return
        }
        if let blocker = await installBlocker() {
            let busy = NSAlert()
            busy.messageText = blocker
            busy.addButton(withTitle: String(localized: "OK", comment: "Alert button"))
            busy.runModal()
            discardStaged(staged)
            return
        }
        phase = .installing
        do {
            try UpdateInstall.replaceBundle(at: Bundle.main.bundleURL, with: staged)
        } catch {
            discardStaged(staged)
            let message = error.localizedDescription
            Self.logger.error("update \(version, privacy: .public): replace failed: \(message, privacy: .public)")
            phase = .failed(message)
            presentFailure(title: Self.installFailedTitle, message: message, release: release)
            return
        }
        Self.logger.notice("update \(version, privacy: .public): installed, relaunching")
        try? FileManager.default.removeItem(at: cacheFolder)
        relaunch()
    }

    /// "Later" (or a blocked install): the staged copy goes, the verified
    /// DMG stays in the cache so the next install skips the download.
    private func discardStaged(_ staged: URL) {
        try? FileManager.default.removeItem(at: staged)
        phase = .idle
        version = nil
    }

    // MARK: - Alerts

    private func presentFailure(title: String, message: String, release: ReleaseInfo) {
        NSApp.activate()
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: Self.viewOnGitHubTitle)
        alert.addButton(withTitle: String(localized: "Cancel", comment: "Alert button"))
        if alert.runModal() == .alertFirstButtonReturn {
            NSWorkspace.shared.open(release.htmlURL)
        }
    }

    static var viewOnGitHubTitle: String {
        String(localized: "View on GitHub", comment: "Update alert button: open the release page in the browser")
    }

    static func downloadingTitle(_ version: String) -> String {
        String(localized: "Downloading Hearsay \(version)…", comment: "Update progress. %@ is the new version.")
    }

    static func verifyingTitle(_ version: String) -> String {
        String(localized: "Verifying Hearsay \(version)…", comment: "Update progress. %@ is the new version.")
    }

    static func readyTitle(_ version: String) -> String {
        String(localized: "Hearsay \(version) is ready to install.",
               comment: "Update ready alert title and Settings line. %@ is the new version.")
    }

    static func installingTitle(_ version: String) -> String {
        String(localized: "Installing Hearsay \(version)…", comment: "Update progress. %@ is the new version.")
    }

    /// "12.3 MB of 48 MB", or "12.3 MB" when the size is unknown.
    static func bytesLine(received: Int64, total: Int64?) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        let done = formatter.string(fromByteCount: received)
        guard let total else { return done }
        let all = formatter.string(fromByteCount: total)
        return String(localized: "\(done) of \(all)",
                      comment: "Update download progress. %1$@ is the amount received, %2$@ the total size.")
    }

    // MARK: - Progress window

    private func showProgressPanel() {
        if let progressPanel {
            progressPanel.makeKeyAndOrderFront(nil)
            return
        }
        let hosting = NSHostingController(rootView: UpdateProgressWindowContent(installer: self)
            .environment(\.locale, InterfaceLanguageLaunch.applied.locale))
        let panel = NSPanel(contentViewController: hosting)
        panel.styleMask = [.titled]
        panel.title = String(localized: "Software Update", comment: "Update progress window title")
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.level = .floating
        panel.center()
        NSApp.activate()
        panel.makeKeyAndOrderFront(nil)
        progressPanel = panel
    }

    private func closeProgressPanel() {
        progressPanel?.close()
        progressPanel = nil
    }
}

/// The progress window's content, following the installer.
private struct UpdateProgressWindowContent: View {
    let installer: UpdateInstaller

    var body: some View {
        UpdateProgressView(version: installer.version ?? "", phase: installer.phase) {
            installer.cancel()
        }
    }
}

/// "Downloading Hearsay 0.3.0…" with a progress bar, the byte count, and
/// Cancel. Also rendered by the UI snapshots.
struct UpdateProgressView: View {
    let version: String
    let phase: UpdateInstaller.Phase
    let onCancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(verbatim: title)
                .font(.headline)
            if let fraction {
                ProgressView(value: fraction)
            } else {
                ProgressView().progressViewStyle(.linear)
            }
            HStack {
                Text(verbatim: detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                Spacer()
                Button(role: .cancel, action: onCancel) {
                    Text("Cancel", comment: "Alert button")
                }
                .keyboardShortcut(.cancelAction)
                .disabled(!canCancel)
            }
        }
        .padding(20)
        .frame(width: 380)
    }

    private var title: String {
        switch phase {
        case .downloading: UpdateInstaller.downloadingTitle(version)
        case .installing: UpdateInstaller.installingTitle(version)
        default: UpdateInstaller.verifyingTitle(version)
        }
    }

    private var fraction: Double? {
        if case .downloading(let fraction, _, _) = phase { return fraction }
        return nil
    }

    private var detail: String {
        if case .downloading(_, let received, let total) = phase {
            return UpdateInstaller.bytesLine(received: received, total: total)
        }
        return ""
    }

    private var canCancel: Bool {
        switch phase {
        case .downloading, .verifying: true
        default: false
        }
    }
}
