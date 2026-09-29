import AppKit
import HearsayCore

/// Debug only. When the app is launched with
/// `HEARSAY_INSTALL_UPDATE=<dmg>` and `HEARSAY_INSTALL_TARGET=<app bundle>`,
/// runs the update install (PLAN.md 4.6) against `<app bundle>` instead of
/// the running app: the install-location check, the checksum (against a
/// `SHA256SUMS.txt` next to the DMG, when there is one), the mount, the
/// signature check (against the running app's own requirement), staging next
/// to the target, and the bundle replacement. Prints each step to stdout,
/// never relaunches, and quits with status 0, or 1 at the first failure.
///
/// The expected version comes from the DMG name (`Hearsay-<version>.dmg`),
/// or `HEARSAY_INSTALL_VERSION` when set; without either the version check
/// is skipped. Point it only at a scratch copy, never at the running app.
///
///     HEARSAY_INSTALL_UPDATE=/tmp/x/Hearsay-0.2.0.dmg \
///     HEARSAY_INSTALL_TARGET=/tmp/x/Hearsay.app \
///     .build/derived/Build/Products/Release/Hearsay.app/Contents/MacOS/Hearsay
///
/// Returns false (and does nothing) when the variables are not set.
@MainActor
enum UpdateInstallDebug {
    static let variable = "HEARSAY_INSTALL_UPDATE"

    static func runIfRequested() -> Bool {
        let environment = ProcessInfo.processInfo.environment
        guard let dmgPath = environment[variable], !dmgPath.isEmpty else { return false }
        let targetPath = environment["HEARSAY_INSTALL_TARGET"] ?? ""
        let versionOverride = environment["HEARSAY_INSTALL_VERSION"].flatMap { $0.isEmpty ? nil : $0 }
        Task { @MainActor in
            let status = await run(
                dmg: URL(fileURLWithPath: dmgPath), targetPath: targetPath, versionOverride: versionOverride
            )
            DebugDefaults.removeSuite()
            exit(status)
        }
        return true
    }

    private static func run(dmg: URL, targetPath: String, versionOverride: String?) async -> Int32 {
        guard !targetPath.isEmpty else {
            say("HEARSAY_INSTALL_TARGET is not set")
            return 1
        }
        let target = URL(fileURLWithPath: targetPath, isDirectory: true).standardizedFileURL
        if target.resolvingSymlinksInPath().path == Bundle.main.bundleURL.resolvingSymlinksInPath().path {
            say("refusing to replace the running app \(target.path)")
            return 1
        }
        guard FileManager.default.fileExists(atPath: dmg.path) else {
            say("no disk image at \(dmg.path)")
            return 1
        }
        let expectedVersion = versionOverride ?? version(fromDMGName: dmg.lastPathComponent)
        let stagingVersion = expectedVersion ?? "debug"
        say("dmg \(dmg.path)")
        say("target \(target.path) (version \(UpdatePackage.infoDictionary(of: target)["CFBundleShortVersionString"] as? String ?? "unknown"))")
        say("expected version \(expectedVersion ?? "unknown; version check skipped")")

        do {
            try UpdateInstall.checkInstallLocation(bundleURL: target)
            say("install location ok")
        } catch {
            say("install location: \(error.localizedDescription)")
            return 1
        }

        let checksums = dmg.deletingLastPathComponent().appendingPathComponent(ReleaseInfo.checksumsFileName)
        if FileManager.default.fileExists(atPath: checksums.path) {
            do {
                let digest = try await detached { try UpdatePackage.verifyChecksum(of: dmg, checksumFile: checksums) }
                say("checksum ok \(digest)")
            } catch {
                say("checksum: \(error.localizedDescription)")
                return 1
            }
        } else {
            say("checksum skipped (no \(ReleaseInfo.checksumsFileName) next to the DMG)")
        }

        let work = FileManager.default.temporaryDirectory
            .appendingPathComponent("hearsay-update-debug-\(UUID().uuidString)", isDirectory: true)
        let mountPoint = work.appendingPathComponent("mnt", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: work) }
        let app: URL
        do {
            app = try await UpdatePackage.mount(dmg, at: mountPoint)
            say("mounted at \(mountPoint.path), app \(app.lastPathComponent)")
        } catch {
            say("mount: \(error.localizedDescription)")
            return 1
        }

        let staged: URL
        do {
            let signature = try await detached {
                try UpdatePackage.verifySignature(of: app, expectedVersion: expectedVersion)
            }
            say("signature ok: \(signature)")
            staged = try await detached {
                try UpdatePackage.stage(app, nextTo: target, version: stagingVersion, expectedVersion: expectedVersion)
            }
            say("staged \(staged.path) (quarantine cleared, signature checked again)")
        } catch {
            say("verify or stage: \(error.localizedDescription)")
            await detachAndReport(mountPoint)
            return 1
        }
        await detachAndReport(mountPoint)

        do {
            try UpdateInstall.replaceBundle(at: target, with: staged)
        } catch {
            try? FileManager.default.removeItem(at: staged)
            say("replace: \(error.localizedDescription)")
            return 1
        }
        let installed = UpdatePackage.infoDictionary(of: target)["CFBundleShortVersionString"] as? String
        say("replaced \(target.path); now version \(installed ?? "unknown"); staged copy gone: "
            + "\(!FileManager.default.fileExists(atPath: staged.path))")
        say("done (no relaunch in a debug run)")
        return 0
    }

    private static func detachAndReport(_ mountPoint: URL) async {
        if let problem = await UpdatePackage.detach(mountPoint) {
            say("detach failed (ignored): \(problem)")
        } else {
            say("detached")
        }
    }

    /// "0.2.0" from "Hearsay-0.2.0.dmg".
    private static func version(fromDMGName name: String) -> String? {
        guard name.hasPrefix("Hearsay-"), name.hasSuffix(".dmg") else { return nil }
        let version = name.dropFirst("Hearsay-".count).dropLast(".dmg".count)
        return version.isEmpty ? nil : String(version)
    }

    private static func detached<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await Task.detached(priority: .userInitiated) { try work() }.value
    }

    private static func say(_ line: String) {
        print("hearsay install update: " + line)
        fflush(stdout)
    }
}
