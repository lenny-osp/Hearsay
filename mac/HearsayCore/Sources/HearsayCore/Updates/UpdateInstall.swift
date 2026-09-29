import CryptoKit
import Foundation

/// The file-level steps of installing an update inside the app (PLAN.md
/// 4.6, "Install"): reading `SHA256SUMS.txt`, hashing the DMG, checking that
/// the running bundle can be replaced, swapping the bundle, and clearing the
/// quarantine attribute from the staged copy. Downloading, mounting, and the
/// code-signature check live in the app (`UpdateInstaller`).
public enum UpdateInstall {
    /// Why the running copy of Hearsay cannot replace itself.
    public enum InstallLocationProblem: Error, Equatable, LocalizedError {
        /// macOS runs the app from a randomized read-only location
        /// (App Translocation), because it was opened where it was downloaded.
        case translocated
        /// The app runs from a mounted disk image.
        case onDiskImage
        /// The running code is not inside a `.app` folder.
        case notAnAppBundle
        /// The folder holding Hearsay.app cannot be written.
        case parentNotWritable(URL)

        public var errorDescription: String? {
            switch self {
            case .translocated:
                String(localized: "macOS is running Hearsay from a temporary location. Move Hearsay to the Applications folder, open it from there, then check for updates again.",
                       bundle: .module, comment: "Update install error: the app is translocated by Gatekeeper")
            case .onDiskImage:
                String(localized: "Hearsay is running from the disk image. Drag Hearsay to the Applications folder, open it from there, then check for updates again.",
                       bundle: .module, comment: "Update install error: the app runs from the mounted DMG")
            case .notAnAppBundle:
                String(localized: "This copy of Hearsay is not an app bundle, so it cannot update itself. Download the new version from the release page.",
                       bundle: .module, comment: "Update install error")
            case .parentNotWritable(let folder):
                String(localized: "Hearsay cannot write to the folder \(folder.path). Move Hearsay to the Applications folder or another folder you can write to, open it from there, then check for updates again.",
                       bundle: .module, comment: "Update install error. %@ is a folder path.")
            }
        }
    }

    // MARK: - Checksums

    /// Parses `shasum -a 256` output: `<hex>  <file name>` (text mode) or
    /// `<hex> *<file name>` (binary mode). Returns file name to lowercase
    /// hex. Blank lines and lines without a 64-digit hex digest and a name
    /// are ignored; a later line for the same name wins.
    public static func parseChecksums(_ text: String) -> [String: String] {
        var result: [String: String] = [:]
        for rawLine in text.split(omittingEmptySubsequences: true, whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard let space = line.firstIndex(where: { $0 == " " || $0 == "\t" }) else { continue }
            let digest = line[..<space].lowercased()
            guard digest.count == 64, digest.allSatisfy(\.isHexDigit) else { continue }
            var name = line[space...].drop { $0 == " " || $0 == "\t" }
            if name.first == "*" { name = name.dropFirst() }
            guard !name.isEmpty else { continue }
            result[String(name)] = digest
        }
        return result
    }

    /// The SHA-256 of the file at `url` as lowercase hex, read in 1 MiB
    /// chunks so a large DMG is never held in memory.
    public static func sha256Hex(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Install location

    /// Throws the first `InstallLocationProblem` for the running bundle at
    /// `bundleURL`: translocated, on a disk image (`/Volumes/`), not a
    /// `.app` folder, or a parent folder that cannot be written.
    public static func checkInstallLocation(bundleURL: URL, fileManager: FileManager = .default) throws {
        let path = bundleURL.standardizedFileURL.path
        if path.contains("/AppTranslocation/") { throw InstallLocationProblem.translocated }
        if path.hasPrefix("/Volumes/") { throw InstallLocationProblem.onDiskImage }
        var isDirectory: ObjCBool = false
        guard bundleURL.pathExtension.lowercased() == "app",
              fileManager.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw InstallLocationProblem.notAnAppBundle
        }
        let parent = bundleURL.standardizedFileURL.deletingLastPathComponent()
        guard fileManager.isWritableFile(atPath: parent.path) else {
            throw InstallLocationProblem.parentNotWritable(parent)
        }
    }

    // MARK: - Replacing the bundle

    /// The hidden name the new app is staged under, next to the running one.
    public static func stagedBundleName(version: String) -> String {
        ".Hearsay-update-\(version).app"
    }

    /// Replaces the bundle at `current` with `staged`, a copy of the new app
    /// on the same volume (normally `<parent>/.Hearsay-update-<v>.app`).
    /// `FileManager.replaceItemAt` swaps the two in one step, so on failure
    /// the original bundle stays in place and usable. `staged` is gone
    /// afterwards.
    public static func replaceBundle(at current: URL, with staged: URL, fileManager: FileManager = .default) throws {
        _ = try fileManager.replaceItemAt(current, withItemAt: staged, backupItemName: nil, options: [])
    }

    // MARK: - Quarantine

    /// Clears the quarantine attribute from `url` and every item below it.
    /// Items without the attribute, and symbolic links, are left alone.
    public static func removeQuarantine(from url: URL) throws {
        let keys: [URLResourceKey] = [.quarantinePropertiesKey, .isSymbolicLinkKey]
        var items = [url]
        if let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: keys) {
            for case let item as URL in enumerator { items.append(item) }
        }
        for var item in items {
            let values = try item.resourceValues(forKeys: Set(keys))
            if values.isSymbolicLink == true { continue }
            guard values.quarantineProperties != nil else { continue }
            var cleared = URLResourceValues()
            cleared.quarantineProperties = nil
            try item.setResourceValues(cleared)
        }
    }
}
