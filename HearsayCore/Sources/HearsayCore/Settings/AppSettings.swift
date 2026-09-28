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
        /// Legacy key ("en" or "zh"), read once to migrate into `languageChoice`.
        public static let defaultLanguageCode = "defaultLanguageCode"
        public static let languageChoice = "languageChoice"
        public static let preferredLanguage = "preferredLanguage"
        public static let activeModelRepo = "activeModelRepo"
        public static let captureSystemAudio = "captureSystemAudio"
        public static let startStopHotkey = "startStopHotkey"
        public static let pauseHotkey = "pauseHotkey"
        public static let keepRecording = "keepRecording"
        public static let chineseScript = "chineseScript"
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

    /// Hugging Face repo of the Whisper model used for transcription, or nil
    /// when none is chosen (see `ModelStore`).
    public var activeModelRepo: String? {
        didSet {
            if let activeModelRepo {
                defaults.set(activeModelRepo, forKey: Key.activeModelRepo)
            } else {
                defaults.removeObject(forKey: Key.activeModelRepo)
            }
        }
    }

    /// Transcription language chosen on the Record or File tab: Auto or a
    /// fixed language, stored as "auto" or the code. A fresh install starts
    /// at Auto; an install that stored the legacy `defaultLanguageCode` keeps
    /// that language as a fixed choice.
    public var languageChoice: LanguageChoice {
        didSet { defaults.set(languageChoice.storageValue, forKey: Key.languageChoice) }
    }

    /// The language Auto falls back to when detection is not confident
    /// (Settings > General). Default English. Only the user changes it in
    /// Settings; nothing else in the app writes it.
    public var preferredLanguage: TranscriptLanguage {
        didSet { defaults.set(preferredLanguage.rawValue, forKey: Key.preferredLanguage) }
    }

    /// Deprecated: use `languageChoice` and `preferredLanguage`. Kept for
    /// callers not yet moved to the new API. Reads the fixed language code,
    /// or the preferred language code for Auto. Writing a supported code sets
    /// `languageChoice` to that fixed language (never `preferredLanguage`);
    /// other values are ignored.
    public var defaultLanguageCode: String {
        get {
            switch languageChoice {
            case .auto: preferredLanguage.rawValue
            case .fixed(let language): language.rawValue
            }
        }
        set {
            guard let language = TranscriptLanguage(rawValue: newValue) else { return }
            languageChoice = .fixed(language)
        }
    }

    /// "Also capture system audio" on the Record tab (PLAN.md 4.1). Default on.
    public var captureSystemAudio: Bool {
        didSet { defaults.set(captureSystemAudio, forKey: Key.captureSystemAudio) }
    }

    /// Global shortcut that toggles Start / Stop (PLAN.md 4.4). Default ⌃⌥⌘R.
    public var startStopHotkey: HotkeyBinding {
        didSet { Self.store(startStopHotkey, forKey: Key.startStopHotkey, in: defaults) }
    }

    /// Global shortcut that toggles Pause / Resume (PLAN.md 4.4). Default ⌃⌥⌘P.
    public var pauseHotkey: HotkeyBinding {
        didSet { Self.store(pauseHotkey, forKey: Key.pauseHotkey, in: defaults) }
    }

    /// Keep the recording (WAV) in the output folder after a successful
    /// transcription (PLAN.md section 8). Default on, like the Python tool.
    /// Off deletes it; a failed transcription always keeps it.
    public var keepRecording: Bool {
        didSet { defaults.set(keepRecording, forKey: Key.keepRecording) }
    }

    /// Characters for zh transcripts (Record tab, Settings > General):
    /// traditional or simplified. Default traditional; a stored or assigned
    /// asIs reads as traditional. Other languages are never converted.
    public var chineseScript: ChineseScript {
        didSet {
            if chineseScript == .asIs { chineseScript = .traditional }
            defaults.set(chineseScript.rawValue, forKey: Key.chineseScript)
        }
    }

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let rawMode = defaults.string(forKey: Key.windowMode) ?? ""
        self.windowMode = WindowMode(rawValue: rawMode) ?? .menuBarAndDock
        self.outputFolderBookmark = defaults.data(forKey: Key.outputFolderBookmark)
        self.languageChoice = Self.loadLanguageChoice(from: defaults)
        self.preferredLanguage = TranscriptLanguage(
            rawValue: defaults.string(forKey: Key.preferredLanguage) ?? "") ?? .english
        self.activeModelRepo = defaults.string(forKey: Key.activeModelRepo)
        self.captureSystemAudio = defaults.object(forKey: Key.captureSystemAudio) as? Bool ?? true
        self.startStopHotkey = Self.loadHotkey(forKey: Key.startStopHotkey, from: defaults)
            ?? .defaultStartStop
        self.pauseHotkey = Self.loadHotkey(forKey: Key.pauseHotkey, from: defaults) ?? .defaultPause
        self.keepRecording = defaults.object(forKey: Key.keepRecording) as? Bool ?? true
        self.chineseScript = (ChineseScript(rawValue: defaults.string(forKey: Key.chineseScript) ?? "")
            ?? .traditional).pickerValue
    }

    /// The stored choice; otherwise the legacy code as a fixed language
    /// (persisted under the new key); otherwise Auto.
    private static func loadLanguageChoice(from defaults: UserDefaults) -> LanguageChoice {
        if let stored = defaults.string(forKey: Key.languageChoice),
           let choice = LanguageChoice(storageValue: stored) {
            return choice
        }
        if let legacy = defaults.string(forKey: Key.defaultLanguageCode),
           let language = TranscriptLanguage(rawValue: legacy) {
            let choice = LanguageChoice.fixed(language)
            defaults.set(choice.storageValue, forKey: Key.languageChoice)
            return choice
        }
        return .auto
    }

    private static func loadHotkey(forKey key: String, from defaults: UserDefaults) -> HotkeyBinding? {
        guard let data = defaults.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(HotkeyBinding.self, from: data)
    }

    private static func store(_ binding: HotkeyBinding, forKey key: String, in defaults: UserDefaults) {
        guard let data = try? JSONEncoder().encode(binding) else { return }
        defaults.set(data, forKey: key)
    }
}
