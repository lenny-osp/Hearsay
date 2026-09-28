import Foundation

/// A global keyboard shortcut (PLAN.md 4.4): a virtual key code plus
/// modifiers, stored in `AppSettings` and registered with Carbon's
/// `RegisterEventHotKey` by the app.
///
/// Both values use Carbon's encoding so they pass straight through:
/// `keyCode` is a `kVK_*` virtual key code and `modifiers` holds the
/// `cmdKey` / `shiftKey` / `optionKey` / `controlKey` masks.
public struct HotkeyBinding: Codable, Equatable, Hashable, Sendable {
    public struct Modifiers: OptionSet, Codable, Hashable, Sendable {
        public let rawValue: UInt32

        public init(rawValue: UInt32) {
            self.rawValue = rawValue
        }

        // Carbon `Events.h` masks.
        public static let command = Modifiers(rawValue: 1 << 8)
        public static let shift = Modifiers(rawValue: 1 << 9)
        public static let option = Modifiers(rawValue: 1 << 11)
        public static let control = Modifiers(rawValue: 1 << 12)

        /// Symbols in the order macOS menus show them: ⌃⌥⇧⌘.
        public var symbols: String {
            var text = ""
            if contains(.control) { text += "\u{2303}" }
            if contains(.option) { text += "\u{2325}" }
            if contains(.shift) { text += "\u{21E7}" }
            if contains(.command) { text += "\u{2318}" }
            return text
        }
    }

    public var keyCode: UInt32
    public var modifiers: Modifiers

    public init(keyCode: UInt32, modifiers: Modifiers) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    /// kVK_ANSI_R and kVK_ANSI_P.
    public static let keyCodeR: UInt32 = 0x0F
    public static let keyCodeP: UInt32 = 0x23

    /// ⌃⌥⌘R toggles Start / Stop.
    public static let defaultStartStop = HotkeyBinding(
        keyCode: keyCodeR, modifiers: [.control, .option, .command]
    )
    /// ⌃⌥⌘P toggles Pause / Resume.
    public static let defaultPause = HotkeyBinding(
        keyCode: keyCodeP, modifiers: [.control, .option, .command]
    )

    /// A global shortcut needs at least one of ⌃ ⌥ ⌘; Shift alone would
    /// steal ordinary typing.
    public var isValidGlobalShortcut: Bool {
        !modifiers.intersection([.control, .option, .command]).isEmpty
    }

    /// The shortcut as menus show it, for example "⌃⌥⌘R".
    public var displayString: String {
        modifiers.symbols + Self.keyName(for: keyCode)
    }

    /// Name of a key on the ANSI (US) layout. Carbon hotkeys follow the
    /// physical key, so this is the label printed on a US keyboard.
    public static func keyName(for keyCode: UInt32) -> String {
        if let name = keyNames[keyCode] { return name }
        return String(localized: "Key \(keyCode)", bundle: .module,
                      comment: "Shortcut display for a key with no printed name. The placeholder is the numeric key code.")
    }

    private static let keyNames: [UInt32: String] = [
        0x00: "A", 0x01: "S", 0x02: "D", 0x03: "F", 0x04: "H", 0x05: "G",
        0x06: "Z", 0x07: "X", 0x08: "C", 0x09: "V", 0x0B: "B", 0x0C: "Q",
        0x0D: "W", 0x0E: "E", 0x0F: "R", 0x10: "Y", 0x11: "T", 0x12: "1",
        0x13: "2", 0x14: "3", 0x15: "4", 0x16: "6", 0x17: "5", 0x18: "=",
        0x19: "9", 0x1A: "7", 0x1B: "-", 0x1C: "8", 0x1D: "0", 0x1E: "]",
        0x1F: "O", 0x20: "U", 0x21: "[", 0x22: "I", 0x23: "P", 0x25: "L",
        0x26: "J", 0x27: "'", 0x28: "K", 0x29: ";", 0x2A: "\\", 0x2B: ",",
        0x2C: "/", 0x2D: "N", 0x2E: "M", 0x2F: ".", 0x32: "`",
        0x24: "\u{21A9}", 0x30: "\u{21E5}", 0x31: String(localized: "Space", bundle: .module, comment: "Shortcut display: the space bar"), 0x33: "\u{232B}",
        0x35: "\u{238B}", 0x75: "\u{2326}", 0x73: "\u{2196}", 0x77: "\u{2198}",
        0x74: "\u{21DE}", 0x79: "\u{21DF}", 0x7B: "\u{2190}", 0x7C: "\u{2192}",
        0x7D: "\u{2193}", 0x7E: "\u{2191}",
        0x7A: "F1", 0x78: "F2", 0x63: "F3", 0x76: "F4", 0x60: "F5", 0x61: "F6",
        0x62: "F7", 0x64: "F8", 0x65: "F9", 0x6D: "F10", 0x67: "F11", 0x6F: "F12",
        0x69: "F13", 0x6B: "F14", 0x71: "F15", 0x6A: "F16", 0x40: "F17",
        0x4F: "F18", 0x50: "F19", 0x5A: "F20",
    ]
}
