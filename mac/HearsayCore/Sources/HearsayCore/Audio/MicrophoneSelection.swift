import Foundation

/// The Record tab's Microphone choice: an input device, or no microphone at
/// all so only system audio is recorded (PLAN.md 4.13).
public enum MicrophoneChoice: Hashable, Sendable {
    /// The CoreAudio input device with this UID.
    case device(uid: String)
    /// "No microphone (system audio only)".
    case noMicrophone
}

/// The Microphone picker's state and how it follows the device list
/// (PLAN.md 4.13). It is never persisted: each launch starts here, with the
/// default microphone once the first device list arrives, so a forgotten
/// "no microphone" never silently drops the user's own voice.
///
/// Rules:
/// - A device that is still connected stays chosen.
/// - A chosen device that went away falls back to the default input device,
///   else the first one.
/// - No input device at all selects "no microphone" by itself
///   (`isAutomatic`). When a device appears later, that automatic choice
///   switches back to the default device; a "no microphone" the user picked
///   stays.
/// - The caller never updates the choice while a session runs; Stop & Start
///   Next keeps "no microphone" (`keepingNoMicrophone`).
public struct MicrophoneSelection: Equatable, Sendable {
    public private(set) var choice: MicrophoneChoice
    /// "No microphone" was selected by Hearsay because no input device
    /// existed, not by the user.
    public private(set) var isAutomatic: Bool

    /// Before the first device list: the first `update` picks the default.
    public init() {
        choice = .noMicrophone
        isAutomatic = true
    }

    /// The user picked `choice` in the Microphone picker.
    public mutating func select(_ choice: MicrophoneChoice) {
        self.choice = choice
        isAutomatic = false
    }

    /// Follows a new device list. `defaultUID` is the system's default input
    /// device. With `keepingNoMicrophone` a "no microphone" choice stays
    /// whoever made it (Stop & Start Next keeps the session's sources).
    public mutating func update(deviceUIDs: [String], defaultUID: String?, keepingNoMicrophone: Bool = false) {
        switch choice {
        case .device(let uid) where deviceUIDs.contains(uid):
            return
        case .noMicrophone where !isAutomatic || keepingNoMicrophone:
            return
        case .device, .noMicrophone:
            break
        }
        if let defaultUID, deviceUIDs.contains(defaultUID) {
            choice = .device(uid: defaultUID)
            isAutomatic = false
        } else if let first = deviceUIDs.first {
            choice = .device(uid: first)
            isAutomatic = false
        } else {
            choice = .noMicrophone
            isAutomatic = true
        }
    }

    /// The UID of the chosen device; nil for "no microphone".
    public var deviceUID: String? {
        if case .device(let uid) = choice { return uid }
        return nil
    }

    /// A microphone is part of the recording.
    public var recordsMicrophone: Bool {
        choice != .noMicrophone
    }

    /// Whether system audio is captured: always without a microphone,
    /// otherwise the stored "Also capture system audio" setting, which "no
    /// microphone" never changes.
    public func capturesSystemAudio(stored: Bool) -> Bool {
        recordsMicrophone ? stored : true
    }
}
