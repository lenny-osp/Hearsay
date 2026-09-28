import Foundation
import Observation

/// How Hearsay presents itself: menu bar item, Dock icon, or both (PLAN.md 4.4).
public enum WindowMode: String, CaseIterable, Codable, Sendable {
    case menuBarAndDock
    case menuBarOnly
    case dockOnly

    /// Human-readable label for the Settings picker.
    public var displayName: String {
        switch self {
        case .menuBarAndDock: "Menu bar and Dock"
        case .menuBarOnly: "Menu bar only"
        case .dockOnly: "Dock only"
        }
    }

    /// Whether the `MenuBarExtra` is inserted in this mode.
    public var showsMenuBarItem: Bool { self != .dockOnly }

    /// Whether the app uses the `.regular` activation policy (Dock icon).
    public var showsDockIcon: Bool { self != .menuBarOnly }
}

/// User preferences backed by `UserDefaults`. Every write is persisted
/// immediately; SwiftUI observes changes through the Observation framework.
@Observable
@MainActor
public final class AppSettings {
    public enum Key {
        public static let windowMode = "windowMode"
        public static let outputFolderBookmark = "outputFolderBookmark"
    }

    @ObservationIgnored private let defaults: UserDefaults

    public var windowMode: WindowMode {
        didSet { defaults.set(windowMode.rawValue, forKey: Key.windowMode) }
    }

    /// Security-scoped bookmark for the user-chosen output folder, or nil
    /// for the default folder (see `OutputLocation`).
    public var outputFolderBookmark: Data? {
        didSet {
            if let outputFolderBookmark {
                defaults.set(outputFolderBookmark, forKey: Key.outputFolderBookmark)
            } else {
                defaults.removeObject(forKey: Key.outputFolderBookmark)
            }
        }
    }

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let rawMode = defaults.string(forKey: Key.windowMode) ?? ""
        self.windowMode = WindowMode(rawValue: rawMode) ?? .menuBarAndDock
        self.outputFolderBookmark = defaults.data(forKey: Key.outputFolderBookmark)
    }
}
