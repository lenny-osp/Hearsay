import Foundation
import HearsayCore
import os
import Security

/// Why a downloaded update was not installed. Every case carries enough for
/// the alert's informative text.
enum UpdatePackageError: Error, LocalizedError, Equatable {
    /// `SHA256SUMS.txt` has no line for the DMG.
    case checksumMissing(String)
    case checksumMismatch(String)
    case mountFailed(String)
    /// The disk image holds no app, or more than one.
    case appNotFound(Int)
    case signatureInvalid(String)
    case wrongIdentifier(String?)
    case wrongVersion(found: String?, expected: String)
    case downloadFailed(String)

    var errorDescription: String? {
        switch self {
        case .checksumMissing(let name):
            String(localized: "The release's checksum list has no entry for \(name).",
                   comment: "Update verification error. %@ is a file name.")
        case .checksumMismatch(let name):
            String(localized: "The checksum of \(name) does not match the release's checksum list.",
                   comment: "Update verification error. %@ is a file name.")
        case .mountFailed(let detail):
            String(localized: "The disk image could not be opened: \(detail)",
                   comment: "Update verification error. %@ is the hdiutil error.")
        case .appNotFound(let count):
            String(localized: "The disk image should contain one app but contains \(count).",
                   comment: "Update verification error. %lld is a number of apps.")
        case .signatureInvalid(let detail):
            String(localized: "The code signature of the new version is not valid: \(detail)",
                   comment: "Update verification error. %@ is the Security framework's message.")
        case .wrongIdentifier(let found):
            String(localized: "The new app has the bundle identifier \(found ?? "none"), not tw.og1o.hearsay.",
                   comment: "Update verification error. %@ is a bundle identifier.")
        case .wrongVersion(let found, let expected):
            String(localized: "The new app is version \(found ?? "unknown"), not \(expected).",
                   comment: "Update verification error. %1$@ is the version found, %2$@ the release version.")
        case .downloadFailed(let detail):
            String(localized: "The download failed: \(detail)",
                   comment: "Update download error. %@ is the underlying error.")
        }
    }
}

/// The steps of an update install that do not touch the UI (PLAN.md 4.6):
/// download, checksum, mount, signature check, staging. Shared by
/// `UpdateInstaller` and the `HEARSAY_INSTALL_UPDATE` debug entry point.
/// Everything here runs off the main actor.
enum UpdatePackage {
    static let bundleIdentifier = "tw.og1o.hearsay"
    private static let hdiutil = "/usr/bin/hdiutil"
    private static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "updates")

    // MARK: - Download

    /// Streams `url` into `destination` (via `<destination>.part`), calling
    /// `progress(received, total)` about every 256 KB. Cancelling the task
    /// stops the transfer and removes the partial file.
    nonisolated static func download(
        _ url: URL, to destination: URL, session: URLSession = .shared,
        progress: @escaping @Sendable (Int64, Int64?) -> Void
    ) async throws {
        let fileManager = FileManager.default
        let partial = destination.appendingPathExtension("part")
        try? fileManager.removeItem(at: partial)
        try? fileManager.removeItem(at: destination)
        guard fileManager.createFile(atPath: partial.path, contents: nil) else {
            throw UpdatePackageError.downloadFailed(partial.path)
        }
        do {
            let handle = try FileHandle(forWritingTo: partial)
            defer { try? handle.close() }
            let (bytes, response) = try await session.bytes(from: url)
            if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                throw UpdatePackageError.downloadFailed("HTTP \(http.statusCode)")
            }
            let total: Int64? = response.expectedContentLength > 0 ? response.expectedContentLength : nil
            var received: Int64 = 0
            var reported: Int64 = 0
            var buffer = Data()
            buffer.reserveCapacity(1 << 16)
            progress(0, total)
            for try await byte in bytes {
                buffer.append(byte)
                if buffer.count >= 1 << 16 {
                    try handle.write(contentsOf: buffer)
                    received += Int64(buffer.count)
                    buffer.removeAll(keepingCapacity: true)
                    if received - reported >= 1 << 18 {
                        reported = received
                        progress(received, total)
                        try Task.checkCancellation()
                    }
                }
            }
            try handle.write(contentsOf: buffer)
            received += Int64(buffer.count)
            progress(received, total)
            try Task.checkCancellation()
            try handle.close()
            try fileManager.moveItem(at: partial, to: destination)
        } catch {
            try? fileManager.removeItem(at: partial)
            if error is CancellationError || (error as? URLError)?.code == .cancelled {
                throw CancellationError()
            }
            if let packageError = error as? UpdatePackageError { throw packageError }
            throw UpdatePackageError.downloadFailed(error.localizedDescription)
        }
    }

    // MARK: - Checksum

    /// The DMG's SHA-256 equals its entry in the checksum file. Returns the
    /// digest.
    @discardableResult
    nonisolated static func verifyChecksum(of dmg: URL, checksumFile: URL) throws -> String {
        let text = try String(contentsOf: checksumFile, encoding: .utf8)
        let name = dmg.lastPathComponent
        guard let expected = UpdateInstall.parseChecksums(text)[name] else {
            throw UpdatePackageError.checksumMissing(name)
        }
        let actual = try UpdateInstall.sha256Hex(of: dmg)
        guard actual == expected else { throw UpdatePackageError.checksumMismatch(name) }
        return actual
    }

    // MARK: - Disk image

    /// `hdiutil attach -nobrowse -readonly -noverify -mountpoint <mountPoint>`;
    /// returns the single `.app` at the mount root. Detaches again when no
    /// single app is found.
    nonisolated static func mount(_ dmg: URL, at mountPoint: URL) async throws -> URL {
        let fileManager = FileManager.default
        // A mount left over from an interrupted run.
        if fileManager.fileExists(atPath: mountPoint.path) {
            _ = await detach(mountPoint)
        }
        try fileManager.createDirectory(at: mountPoint, withIntermediateDirectories: true)
        let result: CLIRunResult
        do {
            result = try await CLIProcessRunner.run(
                [hdiutil, "attach", "-nobrowse", "-readonly", "-noverify", "-mountpoint", mountPoint.path, dmg.path],
                mountPoint.deletingLastPathComponent(), ProcessInfo.processInfo.environment, 120
            )
        } catch {
            throw UpdatePackageError.mountFailed(error.localizedDescription)
        }
        guard result.exitCode == 0, !result.timedOut else {
            let detail = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            throw UpdatePackageError.mountFailed(detail.isEmpty ? "hdiutil status \(result.exitCode)" : detail)
        }
        let apps = ((try? fileManager.contentsOfDirectory(at: mountPoint, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == "app" }
        guard apps.count == 1, let app = apps.first else {
            _ = await detach(mountPoint)
            throw UpdatePackageError.appNotFound(apps.count)
        }
        return app
    }

    /// `hdiutil detach`, forced when a plain detach fails. Returns an error
    /// description, or nil when the image is detached.
    @discardableResult
    nonisolated static func detach(_ mountPoint: URL) async -> String? {
        var detail = ""
        for arguments in [["detach", mountPoint.path], ["detach", "-force", mountPoint.path]] {
            do {
                let result = try await CLIProcessRunner.run(
                    [hdiutil] + arguments, URL(fileURLWithPath: "/"), ProcessInfo.processInfo.environment, 60
                )
                if result.exitCode == 0 {
                    try? FileManager.default.removeItem(at: mountPoint)
                    return nil
                }
                detail = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            } catch {
                detail = error.localizedDescription
            }
        }
        logger.error("hdiutil detach failed: \(detail, privacy: .public)")
        return detail.isEmpty ? "hdiutil detach failed" : detail
    }

    // MARK: - Code signature

    /// Checks the new app (PLAN.md 4.6): when the running app's designated
    /// requirement names a certificate, the new app must satisfy that
    /// requirement; when the running build is ad-hoc (a `cdhash`
    /// requirement), it must only carry a valid signature. In both cases
    /// the bundle identifier must be `tw.og1o.hearsay` and, when
    /// `expectedVersion` is given, `CFBundleShortVersionString` must equal
    /// it. Returns a line describing what was checked.
    nonisolated static func verifySignature(of app: URL, expectedVersion: String?) throws -> String {
        var newCode: SecStaticCode?
        var status = SecStaticCodeCreateWithPath(app as CFURL, SecCSFlags(), &newCode)
        guard status == errSecSuccess, let newCode else { throw signatureError(status) }

        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSCheckNestedCode)
        let running = try runningRequirement()
        let summary: String
        if let running, running.text.contains("certificate") {
            status = SecStaticCodeCheckValidity(newCode, flags, running.requirement)
            guard status == errSecSuccess else { throw signatureError(status) }
            summary = "signature satisfies the running requirement: \(running.text)"
        } else {
            status = SecStaticCodeCheckValidity(newCode, flags, nil)
            guard status == errSecSuccess else { throw signatureError(status) }
            summary = "running build is ad-hoc (\(running?.text ?? "no requirement")); "
                + "certificate check skipped, signature valid"
            logger.notice("update: running build is ad-hoc; certificate check skipped")
        }

        let info = infoDictionary(of: app)
        let identifier = info["CFBundleIdentifier"] as? String
        guard identifier == bundleIdentifier else { throw UpdatePackageError.wrongIdentifier(identifier) }
        let version = info["CFBundleShortVersionString"] as? String
        if let expectedVersion, version != expectedVersion {
            throw UpdatePackageError.wrongVersion(found: version, expected: expectedVersion)
        }
        return summary + "; \(identifier ?? "") \(version ?? "unknown")"
    }

    /// The running app's designated requirement and its text; nil when the
    /// running code has none.
    private nonisolated static func runningRequirement() throws -> (requirement: SecRequirement, text: String)? {
        var code: SecCode?
        var status = SecCodeCopySelf(SecCSFlags(), &code)
        guard status == errSecSuccess, let code else { throw signatureError(status) }
        var staticCode: SecStaticCode?
        status = SecCodeCopyStaticCode(code, SecCSFlags(), &staticCode)
        guard status == errSecSuccess, let staticCode else { throw signatureError(status) }
        var requirement: SecRequirement?
        guard SecCodeCopyDesignatedRequirement(staticCode, SecCSFlags(), &requirement) == errSecSuccess,
              let requirement else { return nil }
        var text: CFString?
        guard SecRequirementCopyString(requirement, SecCSFlags(), &text) == errSecSuccess, let text else {
            return nil
        }
        return (requirement, text as String)
    }

    private nonisolated static func signatureError(_ status: OSStatus) -> UpdatePackageError {
        let message = SecCopyErrorMessageString(status, nil) as String? ?? "OSStatus \(status)"
        return .signatureInvalid(message)
    }

    /// `Contents/Info.plist`, read directly (`Bundle` caches by path).
    nonisolated static func infoDictionary(of app: URL) -> [String: Any] {
        let url = app.appendingPathComponent("Contents/Info.plist")
        guard let data = try? Data(contentsOf: url),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil),
              let dictionary = plist as? [String: Any] else { return [:] }
        return dictionary
    }

    // MARK: - Staging

    /// The staged copy's location: `<parent of target>/.Hearsay-update-<v>.app`.
    nonisolated static func stagedURL(nextTo target: URL, version: String) -> URL {
        target.deletingLastPathComponent()
            .appendingPathComponent(UpdateInstall.stagedBundleName(version: version), isDirectory: true)
    }

    /// Copies `app` next to `target` under the hidden staging name
    /// (replacing a stale copy), clears quarantine, and checks the copy's
    /// signature again. Returns the staged URL.
    nonisolated static func stage(_ app: URL, nextTo target: URL, version: String, expectedVersion: String?) throws -> URL {
        let fileManager = FileManager.default
        let staged = stagedURL(nextTo: target, version: version)
        try? fileManager.removeItem(at: staged)
        do {
            try fileManager.copyItem(at: app, to: staged)
            try UpdateInstall.removeQuarantine(from: staged)
            _ = try verifySignature(of: staged, expectedVersion: expectedVersion)
        } catch {
            try? fileManager.removeItem(at: staged)
            throw error
        }
        return staged
    }
}
