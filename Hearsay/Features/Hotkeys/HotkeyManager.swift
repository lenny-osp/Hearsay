import Carbon.HIToolbox
import HearsayCore
import Observation

/// Global hotkeys for Start / Stop and Pause / Resume (PLAN.md 4.4).
///
/// Uses Carbon `RegisterEventHotKey`, which works inside the App Sandbox
/// and needs no Accessibility permission. Bindings come from `AppSettings`
/// and are re-registered whenever either one changes. Carbon delivers hotkey
/// events on the main run loop, so the handler runs on the main actor.
@MainActor
@Observable
final class HotkeyManager {
    enum Action: UInt32, CaseIterable {
        case startStop = 1
        case pause = 2
    }

    /// Shortcuts that could not be registered, for example because another
    /// app already owns the combination. Shown in Settings.
    private(set) var registrationError: String?

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let perform: (Action) -> Void
    @ObservationIgnored private var hotKeys: [Action: EventHotKeyRef] = [:]
    @ObservationIgnored private var handler: EventHandlerRef?
    @ObservationIgnored private var suspended = false

    /// 'Hrsy', the signature Carbon uses to tell our hotkeys apart.
    private static let signature: OSType = 0x4872_7379

    init(settings: AppSettings, perform: @escaping (Action) -> Void) {
        self.settings = settings
        self.perform = perform
    }

    /// Installs the event handler, registers the current bindings, and
    /// watches the settings for changes.
    func start() {
        installHandler()
        register()
        observeBindings()
    }

    /// Unregisters the hotkeys while a shortcut recorder listens, so the
    /// pressed combination reaches the recorder instead of firing.
    func suspend() {
        suspended = true
        unregisterAll()
    }

    func resume() {
        suspended = false
        register()
    }

    // MARK: - Registration

    private func register() {
        unregisterAll()
        guard !suspended else { return }
        var failures: [String] = []
        let bindings: [(Action, HotkeyBinding)] = [
            (.startStop, settings.startStopHotkey),
            (.pause, settings.pauseHotkey),
        ]
        for (action, binding) in bindings {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: Self.signature, id: action.rawValue)
            let status = RegisterEventHotKey(
                binding.keyCode, binding.modifiers.rawValue, id,
                GetApplicationEventTarget(), 0, &ref
            )
            if status == noErr, let ref {
                hotKeys[action] = ref
            } else {
                failures.append("\(binding.displayString) is already used by another app or shortcut.")
            }
        }
        registrationError = failures.isEmpty ? nil : failures.joined(separator: "\n")
    }

    private func unregisterAll() {
        for ref in hotKeys.values {
            UnregisterEventHotKey(ref)
        }
        hotKeys.removeAll()
    }

    private func observeBindings() {
        withObservationTracking {
            _ = settings.startStopHotkey
            _ = settings.pauseHotkey
        } onChange: { [weak self] in
            // onChange fires before the new value is stored; hop to the next
            // main-actor turn to read it and to re-arm tracking.
            Task { @MainActor in
                guard let self else { return }
                self.register()
                self.observeBindings()
            }
        }
    }

    // MARK: - Events

    private func installHandler() {
        guard handler == nil else { return }
        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)
        )
        let context = Unmanaged.passUnretained(self).toOpaque()
        var installed: EventHandlerRef?
        let status = InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, context -> OSStatus in
                guard let event, let context else { return OSStatus(eventNotHandledErr) }
                var hotKeyID = EventHotKeyID()
                let status = GetEventParameter(
                    event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                    nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID
                )
                guard status == noErr else { return status }
                let signature = hotKeyID.signature
                let rawAction = hotKeyID.id
                let pointer = UInt(bitPattern: context)
                return MainActor.assumeIsolated {
                    guard let raw = UnsafeRawPointer(bitPattern: pointer) else {
                        return OSStatus(eventNotHandledErr)
                    }
                    let manager = Unmanaged<HotkeyManager>.fromOpaque(raw).takeUnretainedValue()
                    return manager.handle(signature: signature, rawAction: rawAction)
                }
            },
            1, &eventType, context, &installed
        )
        if status == noErr {
            handler = installed
        } else {
            registrationError = "Global shortcuts are unavailable (error \(status))."
        }
    }

    private func handle(signature: OSType, rawAction: UInt32) -> OSStatus {
        guard signature == Self.signature, let action = Action(rawValue: rawAction) else {
            return OSStatus(eventNotHandledErr)
        }
        perform(action)
        return noErr
    }
}
