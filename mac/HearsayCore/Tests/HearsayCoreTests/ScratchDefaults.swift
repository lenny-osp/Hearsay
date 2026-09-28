import Foundation

/// Hands out throwaway `UserDefaults` suites and, when released, removes
/// their persistent domains and files, so tests leave nothing in
/// `~/Library/Preferences` (PLAN.md section 17). Hold one per test as a
/// stored property of a class suite: Swift Testing creates a new instance
/// per test and releases it afterwards.
///
/// Each suite name is an absolute path inside a private temporary directory,
/// so cfprefsd writes the plist there instead of `~/Library/Preferences`.
/// Deleting a plist from `~/Library/Preferences` is not enough: cfprefsd
/// writes an empty one back asynchronously, after the delete.
final class ScratchDefaults: @unchecked Sendable {
    private let lock = NSLock()
    private var suiteNames: [String] = []
    private let prefix: String
    private let directory: URL

    init(prefix: String = "tw.og1o.hearsay.tests") {
        self.prefix = prefix
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(prefix).\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    /// A new, empty suite.
    func make() -> UserDefaults {
        let name = directory.appendingPathComponent("\(prefix).\(UUID().uuidString)").path
        lock.withLock { suiteNames.append(name) }
        let defaults = UserDefaults(suiteName: name) ?? .standard
        defaults.removePersistentDomain(forName: name)
        return defaults
    }

    deinit {
        for name in suiteNames {
            UserDefaults(suiteName: name)?.removePersistentDomain(forName: name)
            CFPreferencesAppSynchronize(name as CFString)
        }
        try? FileManager.default.removeItem(at: directory)
    }
}
