import Foundation

/// Hands out throwaway `UserDefaults` suites and, when released, removes
/// their persistent domains and plist files, so tests leave nothing in
/// `~/Library/Preferences` (PLAN.md section 17). Hold one per test as a
/// stored property of a class suite: Swift Testing creates a new instance
/// per test and releases it afterwards.
final class ScratchDefaults: @unchecked Sendable {
    private let lock = NSLock()
    private var suiteNames: [String] = []
    private let prefix: String

    init(prefix: String = "tw.og1o.hearsay.tests") {
        self.prefix = prefix
    }

    /// A new, empty suite.
    func make() -> UserDefaults {
        let name = "\(prefix).\(UUID().uuidString)"
        lock.withLock { suiteNames.append(name) }
        let defaults = UserDefaults(suiteName: name) ?? .standard
        defaults.removePersistentDomain(forName: name)
        return defaults
    }

    deinit {
        let preferences = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Preferences", isDirectory: true)
        for name in suiteNames {
            UserDefaults(suiteName: name)?.removePersistentDomain(forName: name)
            // Flush first: cfprefsd otherwise writes an empty plist after the
            // file is deleted.
            CFPreferencesAppSynchronize(name as CFString)
            try? FileManager.default.removeItem(at: preferences.appendingPathComponent("\(name).plist"))
        }
    }
}
