import CryptoKit
import Foundation
import Testing
@testable import HearsayCore

/// The file-level steps of the in-app update install (PLAN.md 4.6).
struct UpdateInstallTests {
    /// A fresh folder under the temporary directory, removed by `cleanUp`.
    private func makeTemporaryFolder() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("hearsay-update-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url.resolvingSymlinksInPath()
    }

    private func cleanUp(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }

    /// A fake app bundle: `<name>.app/Contents/Info.txt` holding `marker`.
    private func makeFakeApp(named name: String, in folder: URL, marker: String) throws -> URL {
        let app = folder.appendingPathComponent(name, isDirectory: true)
        let contents = app.appendingPathComponent("Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)
        try Data(marker.utf8).write(to: contents.appendingPathComponent("Info.txt"))
        return app
    }

    private func marker(of app: URL) throws -> String {
        try String(contentsOf: app.appendingPathComponent("Contents/Info.txt"), encoding: .utf8)
    }

    // MARK: - Checksums

    @Test func parsesTextAndBinaryModeLines() {
        let dmgDigest = String(repeating: "a", count: 64)
        let zipDigest = "5891B5B522D5DF086D0FF0B110FBD9D21BB4FC7163AF34D08286A2E846F6BE03"
        let text = """
            \(dmgDigest)  Hearsay-0.3.0.dmg
            \(zipDigest) *Hearsay 0.3.0.zip\r

            """
        let sums = UpdateInstall.parseChecksums(text)
        #expect(sums == [
            "Hearsay-0.3.0.dmg": dmgDigest,
            "Hearsay 0.3.0.zip": zipDigest.lowercased(),
        ])
    }

    @Test func ignoresMalformedLines() {
        let good = String(repeating: "0", count: 64)
        let text = """
            not a checksum line
            abc123  short.dmg
            \(String(repeating: "g", count: 64))  nothex.dmg
            \(good)
            \(good)  *
            \(good)  Hearsay-0.3.0.dmg
            """
        #expect(UpdateInstall.parseChecksums(text) == ["Hearsay-0.3.0.dmg": good])
        #expect(UpdateInstall.parseChecksums("").isEmpty)
    }

    @Test func sha256OfAKnownFile() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let file = folder.appendingPathComponent("hello.txt")
        try Data("hello\n".utf8).write(to: file)
        #expect(try UpdateInstall.sha256Hex(of: file)
            == "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03")
    }

    @Test func sha256OfAFileLargerThanOneChunk() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let file = folder.appendingPathComponent("big.bin")
        // 2.5 MiB of zeros: three reads.
        try Data(count: 5 << 19).write(to: file)
        // Compared with CryptoKit on the whole data at once.
        let whole = try Data(contentsOf: file)
        let expected = SHA256Reference.hex(whole)
        #expect(try UpdateInstall.sha256Hex(of: file) == expected)
    }

    @Test func sha256OfAMissingFileThrows() {
        #expect(throws: (any Error).self) {
            try UpdateInstall.sha256Hex(of: URL(fileURLWithPath: "/nonexistent/\(UUID().uuidString)"))
        }
    }

    // MARK: - Install location

    @Test func writableAppFolderPasses() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let app = try makeFakeApp(named: "Hearsay.app", in: folder, marker: "old")
        try UpdateInstall.checkInstallLocation(bundleURL: app)
    }

    @Test func translocatedAppIsRejected() {
        let app = URL(fileURLWithPath: "/private/var/folders/xy/T/AppTranslocation/1234-ABCD/d/Hearsay.app")
        #expect(throws: UpdateInstall.InstallLocationProblem.translocated) {
            try UpdateInstall.checkInstallLocation(bundleURL: app)
        }
    }

    @Test func appOnADiskImageIsRejected() {
        let app = URL(fileURLWithPath: "/Volumes/Hearsay 0.2.0/Hearsay.app")
        #expect(throws: UpdateInstall.InstallLocationProblem.onDiskImage) {
            try UpdateInstall.checkInstallLocation(bundleURL: app)
        }
    }

    @Test func notAnAppBundleIsRejected() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        // A plain folder, a missing .app, and a file named .app.
        let plain = folder.appendingPathComponent("Hearsay", isDirectory: true)
        try FileManager.default.createDirectory(at: plain, withIntermediateDirectories: true)
        let file = folder.appendingPathComponent("File.app")
        try Data().write(to: file)
        for url in [plain, folder.appendingPathComponent("Missing.app"), file] {
            #expect(throws: UpdateInstall.InstallLocationProblem.notAnAppBundle) {
                try UpdateInstall.checkInstallLocation(bundleURL: url)
            }
        }
    }

    @Test func readOnlyParentIsRejected() throws {
        guard getuid() != 0 else { return } // root can write anywhere
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let parent = folder.appendingPathComponent("ReadOnly", isDirectory: true)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        let app = try makeFakeApp(named: "Hearsay.app", in: parent, marker: "old")
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: parent.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: parent.path) }
        #expect(throws: UpdateInstall.InstallLocationProblem.parentNotWritable(parent)) {
            try UpdateInstall.checkInstallLocation(bundleURL: app)
        }
    }

    @Test func problemsTellTheUserWhatToDo() {
        let problems: [UpdateInstall.InstallLocationProblem] = [
            .translocated, .onDiskImage, .parentNotWritable(URL(fileURLWithPath: "/Applications")),
        ]
        for problem in problems {
            #expect(problem.localizedDescription.contains("Applications folder"))
        }
        #expect(UpdateInstall.InstallLocationProblem.notAnAppBundle.localizedDescription.contains("release page"))
    }

    // MARK: - Asset selection

    private func asset(_ name: String) -> ReleaseAsset {
        ReleaseAsset(
            name: name,
            downloadURL: URL(fileURLWithPath: "/").appendingPathComponent(name),
            size: 1
        )
    }

    private func release(_ names: [String]) -> ReleaseInfo {
        ReleaseInfo(
            version: "0.3.0", tagName: "v0.3.0",
            htmlURL: URL(fileURLWithPath: "/release"), publishedAt: nil, notes: nil,
            assets: names.map(asset)
        )
    }

    @Test func exactDMGNameWins() {
        let info = release(["Hearsay-0.2.0.dmg", "Hearsay-0.3.0.dmg", "SHA256SUMS.txt"])
        #expect(info.dmgAsset(forVersion: "0.3.0")?.name == "Hearsay-0.3.0.dmg")
        #expect(info.checksumsAsset?.name == "SHA256SUMS.txt")
    }

    @Test func singleDMGIsTheFallback() {
        let info = release(["Hearsay.dmg", "SHA256SUMS.txt"])
        #expect(info.dmgAsset(forVersion: "0.3.0")?.name == "Hearsay.dmg")
    }

    @Test func noOrSeveralUnnamedDMGsGiveNone() {
        #expect(release(["notes.txt"]).dmgAsset(forVersion: "0.3.0") == nil)
        #expect(release(["A.dmg", "B.dmg"]).dmgAsset(forVersion: "0.3.0") == nil)
        #expect(release([]).dmgAsset(forVersion: "0.3.0") == nil)
        #expect(release(["sha256sums.txt"]).checksumsAsset == nil)
    }

    // MARK: - Replacing the bundle

    @Test func replaceBundleSwapsTheContents() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let current = try makeFakeApp(named: "Hearsay.app", in: folder, marker: "old")
        try Data("only in old".utf8).write(to: current.appendingPathComponent("Contents/Old.txt"))
        let staged = try makeFakeApp(
            named: UpdateInstall.stagedBundleName(version: "0.3.0"), in: folder, marker: "new"
        )
        #expect(staged.lastPathComponent == ".Hearsay-update-0.3.0.app")

        try UpdateInstall.replaceBundle(at: current, with: staged)

        #expect(try marker(of: current) == "new")
        #expect(!FileManager.default.fileExists(atPath: current.appendingPathComponent("Contents/Old.txt").path))
        #expect(!FileManager.default.fileExists(atPath: staged.path))
        let left = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        #expect(left == ["Hearsay.app"])
    }

    @Test func failedReplacementLeavesTheOriginal() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let current = try makeFakeApp(named: "Hearsay.app", in: folder, marker: "old")
        let missing = folder.appendingPathComponent(".Hearsay-update-0.3.0.app", isDirectory: true)

        #expect(throws: (any Error).self) {
            try UpdateInstall.replaceBundle(at: current, with: missing)
        }
        #expect(try marker(of: current) == "old")
        let left = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        #expect(left == ["Hearsay.app"])
    }

    // MARK: - Quarantine

    @Test func removesQuarantineFromTheWholeTree() throws {
        let folder = try makeTemporaryFolder()
        defer { cleanUp(folder) }
        let app = try makeFakeApp(named: "Hearsay.app", in: folder, marker: "new")
        let file = app.appendingPathComponent("Contents/Info.txt")
        let plain = app.appendingPathComponent("Contents/Plain.txt")
        try Data().write(to: plain)
        let quarantine: [String: Any] = [
            "LSQuarantineAgentName": "HearsayTests",
            "LSQuarantineType": "LSQuarantineTypeWebDownload",
        ]
        for var url in [app, file] {
            var values = URLResourceValues()
            values.quarantineProperties = quarantine
            try url.setResourceValues(values)
        }
        #expect(try quarantineProperties(file) != nil)

        try UpdateInstall.removeQuarantine(from: app)

        for url in [app, file, plain] {
            #expect(try quarantineProperties(url) == nil)
        }
    }

    /// Reads the attribute through a fresh URL, bypassing cached values.
    private func quarantineProperties(_ url: URL) throws -> [String: Any]? {
        try URL(fileURLWithPath: url.path).resourceValues(forKeys: [.quarantinePropertiesKey]).quarantineProperties
    }
}

/// The digest of a whole `Data` value, to compare the streaming hash with.
private enum SHA256Reference {
    static func hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
