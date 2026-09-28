import AppKit
import HearsayCore
import SwiftUI

/// A small shortcut recorder: click it, press a combination, and it shows
/// the symbols (for example ⌃⌥⌘R). Escape cancels. The combination needs at
/// least one of ⌃ ⌥ ⌘ so it cannot steal ordinary typing.
struct HotkeyRecorderView: View {
    @Binding var binding: HotkeyBinding
    @Environment(HotkeyManager.self) private var hotkeys
    @State private var isListening = false
    @State private var monitor: Any?
    @State private var hint: String?

    var body: some View {
        VStack(alignment: .trailing, spacing: 2) {
            Button(action: toggleListening) {
                Text(isListening ? "Type shortcut…" : binding.displayString)
                    .monospaced()
                    .frame(minWidth: 110)
            }
            .help(isListening ? "Press a combination with ⌃, ⌥, or ⌘. Escape cancels."
                : "Click, then press a new shortcut.")
            .accessibilityLabel("Shortcut")
            .accessibilityValue(binding.displayString)
            if let hint {
                Text(hint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .onDisappear(perform: stopListening)
    }

    private func toggleListening() {
        if isListening {
            stopListening()
        } else {
            startListening()
        }
    }

    private func startListening() {
        hint = nil
        isListening = true
        hotkeys.suspend()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            handle(event)
            // Swallow every key press while listening.
            return nil
        }
    }

    private func stopListening() {
        guard isListening else { return }
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }
        monitor = nil
        isListening = false
        hotkeys.resume()
    }

    private func handle(_ event: NSEvent) {
        let keyCode = UInt32(event.keyCode)
        let modifiers = Self.carbonModifiers(event.modifierFlags)
        if keyCode == 0x35, modifiers.isEmpty {  // kVK_Escape
            stopListening()
            return
        }
        let candidate = HotkeyBinding(keyCode: keyCode, modifiers: modifiers)
        guard candidate.isValidGlobalShortcut else {
            hint = "Include ⌃, ⌥, or ⌘."
            return
        }
        hint = nil
        binding = candidate
        stopListening()
    }

    static func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> HotkeyBinding.Modifiers {
        var modifiers: HotkeyBinding.Modifiers = []
        if flags.contains(.command) { modifiers.insert(.command) }
        if flags.contains(.shift) { modifiers.insert(.shift) }
        if flags.contains(.option) { modifiers.insert(.option) }
        if flags.contains(.control) { modifiers.insert(.control) }
        return modifiers
    }
}

/// The "Shortcuts" section of the General settings tab.
struct HotkeySettingsSection: View {
    @Environment(AppSettings.self) private var settings
    @Environment(HotkeyManager.self) private var hotkeys

    var body: some View {
        @Bindable var settings = settings
        Section("Shortcuts") {
            LabeledContent("Start / Stop recording:") {
                HotkeyRecorderView(binding: $settings.startStopHotkey)
            }
            LabeledContent("Pause / Resume:") {
                HotkeyRecorderView(binding: $settings.pauseHotkey)
            }
            HStack {
                Text("Work in any app, even with the window closed.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Reset") {
                    settings.startStopHotkey = .defaultStartStop
                    settings.pauseHotkey = .defaultPause
                }
                .disabled(settings.startStopHotkey == .defaultStartStop
                    && settings.pauseHotkey == .defaultPause)
            }
            if let error = hotkeys.registrationError {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
    }
}
