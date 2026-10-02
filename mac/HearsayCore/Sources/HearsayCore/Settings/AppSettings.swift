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
        case .menuBarAndDock:
            String(localized: "Menu bar and Dock", bundle: .module,
                   comment: "Settings > Window: show Hearsay in the menu bar and the Dock")
        case .menuBarOnly:
            String(localized: "Menu bar only", bundle: .module,
                   comment: "Settings > Window: show Hearsay only in the menu bar")
        case .dockOnly:
            String(localized: "Dock only", bundle: .module,
                   comment: "Settings > Window: show Hearsay only in the Dock")
        }
    }

    /// Whether the `MenuBarExtra` is inserted in this mode.
    public var showsMenuBarItem: Bool { self != .dockOnly }

    /// Whether the app uses the `.regular` activation policy (Dock icon).
    public var showsDockIcon: Bool { self != .menuBarOnly }
}

/// Whether recordings get a live preview (PLAN.md 4.12, key
/// `livePreviewMode`, shared with Windows). The raw values are the stored
/// values. On the Mac `automatic` and `on` behave the same (Apple Silicon
/// needs no speed probe); only Windows tells them apart (PLAN.md 18.9).
public enum LivePreviewMode: String, CaseIterable, Codable, Sendable {
    /// The default: the preview runs (on Windows, when the speed probe allows).
    case automatic
    /// The preview always runs (Windows ignores the speed probe).
    case on
    /// No live preview: the transcript is made only after Stop.
    case off

    /// The session transcribes live chunks (the Mac's reading of the mode).
    public var showsPreview: Bool { self != .off }

    /// The mode the Mac's on/off toggle stores: on is `automatic`, off is
    /// `off`.
    public init(toggleOn: Bool) {
        self = toggleOn ? .automatic : .off
    }
}

/// User preferences backed by `UserDefaults`. Every write is persisted
/// immediately; SwiftUI observes changes through the Observation framework.
@Observable
@MainActor
public final class AppSettings {
    public enum Key {
        public static let windowMode = "windowMode"
        public static let menuBarShowsStatus = "menuBarShowsStatus"
        public static let outputFolderBookmark = "outputFolderBookmark"
        /// Legacy key ("en" or "zh"), read once to migrate into `languageChoice`.
        public static let defaultLanguageCode = "defaultLanguageCode"
        public static let languageChoice = "languageChoice"
        public static let preferredLanguage = "preferredLanguage"
        public static let activeModelRepo = "activeModelRepo"
        public static let captureSystemAudio = "captureSystemAudio"
        public static let startStopHotkey = "startStopHotkey"
        public static let pauseHotkey = "pauseHotkey"
        public static let stopStartNextHotkey = "stopStartNextHotkey"
        public static let finalPassTiming = "finalPassTiming"
        public static let livePreviewMode = "livePreviewMode"
        public static let keepRecording = "keepRecording"
        /// Legacy "Chinese output" setting ("traditional" or "simplified"),
        /// read only to migrate a stored "zh" into ZH-TW or ZH-CN.
        public static let chineseScript = "chineseScript"
        public static let interfaceLanguage = "interfaceLanguage"
        public static let automaticUpdateChecks = "automaticUpdateChecks"
        public static let autoRecordTeamsMeetings = "autoRecordTeamsMeetings"
        public static let autoRecordAsksLanguage = "autoRecordAsksLanguage"
        public static let lastUpdateCheck = "lastUpdateCheck"
        public static let screenAudioGrantedCodeHash = "screenAudioGrantedCodeHash"
        public static let screenAudioResetCodeHash = "screenAudioResetCodeHash"
        public static let microphoneGrantedCodeHash = "microphoneGrantedCodeHash"
    }

    @ObservationIgnored private let defaults: UserDefaults

    public var windowMode: WindowMode {
        didSet { defaults.set(windowMode.rawValue, forKey: Key.windowMode) }
    }

    /// "Show recording status in the menu bar" (Settings > Window, added
    /// 2026-09-29, owner request). On (the default): the menu bar icon
    /// turns into the red record symbol with the elapsed time while
    /// recording, a pause symbol while paused, and the transcription
    /// percentage. Off: the icon stays the plain waveform in every state.
    public var menuBarShowsStatus: Bool {
        didSet { defaults.set(menuBarShowsStatus, forKey: Key.menuBarShowsStatus) }
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
    /// fixed language, stored as "auto" or the language's raw value. A fresh
    /// install starts at Auto; an install that stored the legacy
    /// `defaultLanguageCode` keeps that language as a fixed choice. A stored
    /// "zh" becomes ZH-TW or ZH-CN by the legacy `Key.chineseScript`.
    public var languageChoice: LanguageChoice {
        didSet { defaults.set(languageChoice.storageValue, forKey: Key.languageChoice) }
    }

    /// The language Auto falls back to when detection is not confident
    /// (Settings > General). Default English. Only the user changes it in
    /// Settings; nothing else in the app writes it. A stored "zh" migrates
    /// like `languageChoice`.
    public var preferredLanguage: TranscriptLanguage {
        didSet { defaults.set(preferredLanguage.rawValue, forKey: Key.preferredLanguage) }
    }

    /// Deprecated: use `languageChoice` and `preferredLanguage`. Kept for
    /// callers not yet moved to the new API. Reads the fixed language's raw
    /// value, or the preferred language's for Auto. Writing a supported raw
    /// value (or the legacy "zh", as ZH-TW) sets `languageChoice` to that
    /// fixed language (never `preferredLanguage`); other values are ignored.
    public var defaultLanguageCode: String {
        get {
            switch languageChoice {
            case .auto: preferredLanguage.rawValue
            case .fixed(let language): language.rawValue
            }
        }
        set {
            guard let language = TranscriptLanguage(storedValue: newValue, legacyChineseScript: nil) else { return }
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

    /// Global shortcut for Stop & Start Next (PLAN.md 4.9 item 2). Default ⌃⌥⌘N.
    public var stopStartNextHotkey: HotkeyBinding {
        didSet { Self.store(stopStartNextHotkey, forKey: Key.stopStartNextHotkey, in: defaults) }
    }

    /// When a queued recording gets its final pass (Settings > General >
    /// Transcription, PLAN.md 4.9 item 3). Default `.immediate` on the Mac;
    /// stored as the raw value, an unknown stored value reads as the default.
    public var finalPassTiming: FinalPassTiming {
        didSet { defaults.set(finalPassTiming.rawValue, forKey: Key.finalPassTiming) }
    }

    /// "Show the live preview while recording" (Settings > General >
    /// Transcription, PLAN.md 4.12). Default `.automatic`; stored as the raw
    /// value, an unknown stored value reads as the default. Read once at
    /// each session's Start.
    public var livePreviewMode: LivePreviewMode {
        didSet { defaults.set(livePreviewMode.rawValue, forKey: Key.livePreviewMode) }
    }

    /// Keep the recording (WAV) in the output folder after a successful
    /// transcription (PLAN.md section 8). Default on, like the Python tool.
    /// Off deletes it; a failed transcription always keeps it.
    public var keepRecording: Bool {
        didSet { defaults.set(keepRecording, forKey: Key.keepRecording) }
    }

    /// The language of Hearsay's own interface (Settings > General). Default
    /// English on a fresh install, whatever the Mac's language. The app
    /// applies it through `AppleLanguages` at launch; a change needs a
    /// restart.
    public var interfaceLanguage: InterfaceLanguage {
        didSet { defaults.set(interfaceLanguage.rawValue, forKey: Key.interfaceLanguage) }
    }

    /// "Automatically check for updates" (Settings > General > Software
    /// updates, PLAN.md 4.6). Default on: at most once a day Hearsay asks
    /// GitHub for the latest release.
    public var automaticUpdateChecks: Bool {
        didSet { defaults.set(automaticUpdateChecks, forKey: Key.automaticUpdateChecks) }
    }

    /// "Record Microsoft Teams meetings automatically" (PLAN.md 4.10).
    /// Default off: nothing is probed.
    public var autoRecordTeamsMeetings: Bool {
        didSet { defaults.set(autoRecordTeamsMeetings, forKey: Key.autoRecordTeamsMeetings) }
    }

    /// "Ask which language to use before each automatic recording"
    /// (PLAN.md 4.10). Default off; only meaningful while
    /// `autoRecordTeamsMeetings` is on.
    public var autoRecordAsksLanguage: Bool {
        didSet { defaults.set(autoRecordAsksLanguage, forKey: Key.autoRecordAsksLanguage) }
    }

    /// When the last update check succeeded, or nil when none has.
    public var lastUpdateCheck: Date? {
        didSet {
            if let lastUpdateCheck {
                defaults.set(lastUpdateCheck, forKey: Key.lastUpdateCheck)
            } else {
                defaults.removeObject(forKey: Key.lastUpdateCheck)
            }
        }
    }

    /// The code identity (signing requirement) of the build that last saw
    /// Screen & System Audio Recording granted, or nil (PLAN.md section 9,
    /// `StaleGrantDetector`).
    public var screenAudioGrantedCodeHash: String? {
        didSet { Self.store(screenAudioGrantedCodeHash, forKey: Key.screenAudioGrantedCodeHash, in: defaults) }
    }

    /// The code identity for which Hearsay last removed a stale Screen &
    /// System Audio Recording entry with `tccutil`, or nil. At most one
    /// reset per identity.
    public var screenAudioResetCodeHash: String? {
        didSet { Self.store(screenAudioResetCodeHash, forKey: Key.screenAudioResetCodeHash, in: defaults) }
    }

    /// The code identity of the build that last saw Microphone access
    /// granted, or nil.
    public var microphoneGrantedCodeHash: String? {
        didSet { Self.store(microphoneGrantedCodeHash, forKey: Key.microphoneGrantedCodeHash, in: defaults) }
    }

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let rawMode = defaults.string(forKey: Key.windowMode) ?? ""
        self.windowMode = WindowMode(rawValue: rawMode) ?? .menuBarAndDock
        self.menuBarShowsStatus = defaults.object(forKey: Key.menuBarShowsStatus) as? Bool ?? true
        self.outputFolderBookmark = defaults.data(forKey: Key.outputFolderBookmark)
        self.languageChoice = Self.loadLanguageChoice(from: defaults)
        self.preferredLanguage = Self.loadPreferredLanguage(from: defaults)
        self.activeModelRepo = defaults.string(forKey: Key.activeModelRepo)
        self.captureSystemAudio = defaults.object(forKey: Key.captureSystemAudio) as? Bool ?? true
        self.startStopHotkey = Self.loadHotkey(forKey: Key.startStopHotkey, from: defaults)
            ?? .defaultStartStop
        self.pauseHotkey = Self.loadHotkey(forKey: Key.pauseHotkey, from: defaults) ?? .defaultPause
        self.stopStartNextHotkey = Self.loadHotkey(forKey: Key.stopStartNextHotkey, from: defaults)
            ?? .defaultStopStartNext
        self.finalPassTiming = FinalPassTiming(
            rawValue: defaults.string(forKey: Key.finalPassTiming) ?? "") ?? .immediate
        self.livePreviewMode = LivePreviewMode(
            rawValue: defaults.string(forKey: Key.livePreviewMode) ?? "") ?? .automatic
        self.keepRecording = defaults.object(forKey: Key.keepRecording) as? Bool ?? true
        self.interfaceLanguage = InterfaceLanguage(
            rawValue: defaults.string(forKey: Key.interfaceLanguage) ?? "") ?? .english
        self.automaticUpdateChecks = defaults.object(forKey: Key.automaticUpdateChecks) as? Bool ?? true
        self.autoRecordTeamsMeetings = defaults.object(forKey: Key.autoRecordTeamsMeetings) as? Bool ?? false
        self.autoRecordAsksLanguage = defaults.object(forKey: Key.autoRecordAsksLanguage) as? Bool ?? false
        self.lastUpdateCheck = defaults.object(forKey: Key.lastUpdateCheck) as? Date
        self.screenAudioGrantedCodeHash = defaults.string(forKey: Key.screenAudioGrantedCodeHash)
        self.screenAudioResetCodeHash = defaults.string(forKey: Key.screenAudioResetCodeHash)
        self.microphoneGrantedCodeHash = defaults.string(forKey: Key.microphoneGrantedCodeHash)
    }

    /// The stored choice (a legacy "zh" migrated and persisted); otherwise
    /// the legacy code as a fixed language (persisted under the new key);
    /// otherwise Auto.
    private static func loadLanguageChoice(from defaults: UserDefaults) -> LanguageChoice {
        let script = defaults.string(forKey: Key.chineseScript)
        if let stored = defaults.string(forKey: Key.languageChoice),
           let choice = LanguageChoice(storedValue: stored, legacyChineseScript: script) {
            if choice.storageValue != stored {
                defaults.set(choice.storageValue, forKey: Key.languageChoice)
            }
            return choice
        }
        if let legacy = defaults.string(forKey: Key.defaultLanguageCode),
           let language = TranscriptLanguage(storedValue: legacy, legacyChineseScript: script) {
            let choice = LanguageChoice.fixed(language)
            defaults.set(choice.storageValue, forKey: Key.languageChoice)
            return choice
        }
        return .auto
    }

    /// The stored preferred language (a legacy "zh" migrated and
    /// persisted); otherwise English.
    private static func loadPreferredLanguage(from defaults: UserDefaults) -> TranscriptLanguage {
        guard let stored = defaults.string(forKey: Key.preferredLanguage),
              let language = TranscriptLanguage(
                storedValue: stored, legacyChineseScript: defaults.string(forKey: Key.chineseScript))
        else { return .english }
        if language.rawValue != stored {
            defaults.set(language.rawValue, forKey: Key.preferredLanguage)
        }
        return language
    }

    private static func loadHotkey(forKey key: String, from defaults: UserDefaults) -> HotkeyBinding? {
        guard let data = defaults.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(HotkeyBinding.self, from: data)
    }

    private static func store(_ value: String?, forKey key: String, in defaults: UserDefaults) {
        if let value {
            defaults.set(value, forKey: key)
        } else {
            defaults.removeObject(forKey: key)
        }
    }

    private static func store(_ binding: HotkeyBinding, forKey key: String, in defaults: UserDefaults) {
        guard let data = try? JSONEncoder().encode(binding) else { return }
        defaults.set(data, forKey: key)
    }
}
