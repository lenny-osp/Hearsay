import Dispatch
import Foundation
import HearsayCore
import Observation
import os
import UserNotifications

/// Automatic recording of Microsoft Teams meetings (PLAN.md 4.10): the thin
/// shell around `MeetingAutoRecordRules` (every decision is there, with its
/// tests) and `MeetingDetector`. It feeds the rules the detector's events,
/// the recording controller's phase, the two settings and the Record tab's
/// language choice, and performs what the rules answer: start and stop the
/// controller, set the Record tab notice, post notifications, and open the
/// language question.
///
/// Watching uses `MeetingAudioObserver` (CoreAudio listeners, macOS 14.2 and
/// later; before that nothing is watched). The observer only says that
/// something changed: the shell re-reads the process list and feeds the
/// detector. The detector needs the clock too (a Teams stream must last 2 s,
/// and 15 s without one end the meeting), so after every observation a
/// one-shot task re-observes at `detector.nextDeadline`. No timer runs while
/// nothing is pending.
///
/// Created by `AppDelegate` after the debug entry points had their chance, so
/// a debug run never auto-records.
@MainActor
final class MeetingAutoRecord {
    private static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "meetings")
    /// Where the CoreAudio listeners call back.
    private static let probeQueue = DispatchQueue(label: "tw.og1o.hearsay.meeting-audio")

    private let settings: AppSettings
    private let controller: RecordingController
    private let prompt: MeetingPromptModel
    private let notifier: MeetingNotifier
    /// Brings the main window forward on the Record tab.
    private let showRecordTab: @MainActor () -> Void

    private var rules: MeetingAutoRecordRules
    private var detector = MeetingDetector()
    private var watcher: (any MeetingWatcher)?
    /// Bumped whenever watching starts or stops; a late callback of an
    /// older watch is ignored.
    private var watchGeneration = 0
    private var deadlineTask: Task<Void, Never>?
    private var probeFailureLogged = false
    private var begun = false
    private var seenEnabled: Bool
    private var seenAsksLanguage: Bool
    private var seenLanguageChoice: LanguageChoice

    init(
        settings: AppSettings,
        controller: RecordingController,
        prompt: MeetingPromptModel,
        notifier: MeetingNotifier = MeetingNotifier(),
        showRecordTab: @escaping @MainActor () -> Void
    ) {
        self.settings = settings
        self.controller = controller
        self.prompt = prompt
        self.notifier = notifier
        self.showRecordTab = showRecordTab
        seenEnabled = settings.autoRecordTeamsMeetings
        seenAsksLanguage = settings.autoRecordAsksLanguage
        seenLanguageChoice = settings.languageChoice
        rules = MeetingAutoRecordRules(
            enabled: seenEnabled, asksLanguage: seenAsksLanguage, languageChoice: seenLanguageChoice
        )
    }

    /// The app is up: follow the controller and the settings, and watch when
    /// the setting is on. Safe to call once.
    func begin() {
        guard !begun else { return }
        begun = true
        controller.phaseObserver = { [weak self] _ in self?.sendControllerState() }
        prompt.onAnswer = { [weak self] id, choice in
            self?.send(.promptAnswered(id: id, choice: choice))
        }
        sendControllerState()
        send(.begin)
        // Ask for the notification permission at launch only when the setting
        // is already on and the question has never been asked.
        if settings.autoRecordTeamsMeetings {
            let notifier = notifier
            Task { await notifier.requestAuthorizationIfUndecided() }
        }
        observeSettings()
    }

    // MARK: - Inputs

    private func send(_ input: MeetingAutoRecordRules.Input) {
        for action in rules.handle(input) { perform(action) }
    }

    /// The controller's phase as the rules read it: recording, paused and
    /// stopping are one state, so Stop & Start Next (stopping, then starting)
    /// never shows an idle gap.
    private func sendControllerState() {
        let state: MeetingSessionState
        switch controller.phase {
        case .idle: state = .idle
        case .starting: state = .starting
        case .recording, .paused, .stopping: state = .recording
        case .failed: state = .failed
        }
        send(.controller(state))
    }

    /// Settings and the language choice, re-armed after every change. The
    /// tracking callback fires before the new value is stored, so the values
    /// are read on the next main-actor turn.
    private func observeSettings() {
        withObservationTracking {
            _ = settings.autoRecordTeamsMeetings
            _ = settings.autoRecordAsksLanguage
            _ = settings.languageChoice
        } onChange: { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.settingsChanged()
                self.observeSettings()
            }
        }
    }

    private func settingsChanged() {
        let choice = settings.languageChoice
        if choice != seenLanguageChoice {
            seenLanguageChoice = choice
            send(.languageChoice(choice))
        }
        let asks = settings.autoRecordAsksLanguage
        if asks != seenAsksLanguage {
            seenAsksLanguage = asks
            send(.asksLanguage(asks))
        }
        let enabled = settings.autoRecordTeamsMeetings
        if enabled != seenEnabled {
            seenEnabled = enabled
            if enabled {
                // The user just asked for notifications about meetings.
                let notifier = notifier
                Task { await notifier.requestAuthorization() }
            }
            send(.settingEnabled(enabled))
        }
    }

    // MARK: - Actions

    private func perform(_ action: MeetingAutoRecordRules.Action) {
        switch action {
        case .startWatching:
            startWatching()
        case .stopWatching:
            stopWatching()
        case .start(let language):
            // The choice persists, as if picked on the Record tab.
            if let language { controller.languageChoice = language }
            controller.start()
            // A start that did nothing (quitting) changes no phase: tell the rules.
            sendControllerState()
        case .stop:
            let controller = controller
            Task { await controller.stop() }
        case .showPrompt(let id, let preselected):
            prompt.show(id: id, preselected: preselected)
            showRecordTab()
        case .closePrompt(let id):
            prompt.close(id: id)
        case .notify(let notification):
            notifier.post(notification)
        case .setAutomaticNotice:
            controller.automaticStartNotice = String(
                localized: "Recording started automatically for a Microsoft Teams meeting.",
                comment: "Record tab: notice line while a recording that Hearsay started for a Microsoft Teams meeting runs. Keep \"Microsoft Teams\" as is.")
        case .clearAutomaticNotice:
            controller.automaticStartNotice = nil
        }
    }

    // MARK: - Watching

    private func startWatching() {
        detector = MeetingDetector()
        probeFailureLogged = false
        guard #available(macOS 14.2, *) else {
            Self.logger.notice("Meeting detection needs macOS 14.2; nothing is watched")
            return
        }
        watchGeneration += 1
        let generation = watchGeneration
        let observer = MeetingAudioObserver(queue: Self.probeQueue) { [weak self] in
            Task { @MainActor in self?.observe(generation: generation) }
        }
        do {
            try observer.start()
        } catch {
            Self.logger.error("Could not watch the audio processes: \(String(describing: error), privacy: .public)")
            return
        }
        watcher = AudioProcessWatcher(observer: observer)
        // A meeting may already be running (the setting was just turned on,
        // or Hearsay opened during a call).
        observe(generation: generation)
    }

    private func stopWatching() {
        watchGeneration += 1
        watcher?.stop()
        watcher = nil
        deadlineTask?.cancel()
        deadlineTask = nil
        detector = MeetingDetector()
    }

    /// Reads the audio processes and feeds the detector. Ignored when the
    /// watch this callback belongs to has ended.
    private func observe(generation: Int) {
        guard generation == watchGeneration, watcher != nil else { return }
        var processes: [CaptureProcess] = []
        if #available(macOS 14.2, *) {
            do {
                processes = try MeetingAudioProbe.captureProcesses()
                probeFailureLogged = false
            } catch {
                // Logged once until a probe succeeds again; an unreadable
                // list counts as no Teams stream, as on Windows.
                if !probeFailureLogged {
                    Self.logger.error("Could not list the audio processes: \(String(describing: error), privacy: .public)")
                }
                probeFailureLogged = true
            }
        }
        if let event = detector.observe(processes, now: Date()) {
            send(event == .started ? .meetingStarted : .meetingEnded)
        }
        scheduleDeadline(generation: generation)
    }

    /// One task that re-observes when a pending start or end falls due,
    /// replacing the previous one.
    private func scheduleDeadline(generation: Int) {
        deadlineTask?.cancel()
        deadlineTask = nil
        guard generation == watchGeneration, watcher != nil, let deadline = detector.nextDeadline else { return }
        // A little past the deadline: the clocks differ by a hair.
        let delay = max(0, deadline.timeIntervalSinceNow) + 0.05
        deadlineTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            self?.observe(generation: generation)
        }
    }
}

/// What `MeetingAutoRecord` holds while it watches. A protocol, so the
/// property needs no availability annotation (the observer is macOS 14.2).
@MainActor
private protocol MeetingWatcher {
    func stop()
}

@available(macOS 14.2, *)
@MainActor
private final class AudioProcessWatcher: MeetingWatcher {
    private let observer: MeetingAudioObserver

    init(observer: MeetingAudioObserver) {
        self.observer = observer
    }

    func stop() {
        observer.stop()
    }
}

// MARK: - Notifications

/// The notifications of an automatic recording (PLAN.md 4.10) through
/// `UNUserNotificationCenter`. The permission (alert, no sound) is asked when
/// the user turns the setting on, and at launch when it is already on and
/// has never been asked. Without it nothing shows except the Record tab's
/// notice. Failures are logged, never shown.
@MainActor
final class MeetingNotifier {
    private static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "meetings")
    /// Shows banners while Hearsay is frontmost (the system hides them
    /// otherwise). The center keeps only a weak reference.
    private static let delegate = BannerDelegate()

    private var center: UNUserNotificationCenter {
        let center = UNUserNotificationCenter.current()
        center.delegate = Self.delegate
        return center
    }

    /// Asks for permission when it has never been asked.
    func requestAuthorizationIfUndecided() async {
        let settings = await center.notificationSettings()
        guard settings.authorizationStatus == .notDetermined else { return }
        await requestAuthorization()
    }

    /// Asks for permission; the system shows its question only the first
    /// time and answers at once afterwards.
    func requestAuthorization() async {
        do {
            let granted = try await center.requestAuthorization(options: [.alert])
            Self.logger.notice("Notification permission \(granted ? "granted" : "denied", privacy: .public)")
        } catch {
            Self.logger.error("Notification permission request failed: \(String(describing: error), privacy: .public)")
        }
    }

    func post(_ notification: MeetingNotification) {
        let content = UNMutableNotificationContent()
        switch notification {
        case .started:
            content.title = String(
                localized: "Recording started",
                comment: "Notification title when Hearsay starts recording a Microsoft Teams meeting by itself.")
            content.body = String(
                localized: "Microsoft Teams meeting",
                comment: "Notification text under \"Recording started\". Keep \"Microsoft Teams\" as is.")
        case .stopped:
            content.title = String(
                localized: "Recording stopped",
                comment: "Notification title when Hearsay stops an automatic recording because the Microsoft Teams meeting ended.")
            content.body = String(
                localized: "The Microsoft Teams meeting ended.",
                comment: "Notification text under \"Recording stopped\". Keep \"Microsoft Teams\" as is.")
        case .notStarted:
            content.title = String(
                localized: "Recording not started",
                comment: "Notification title when an automatic recording for a Microsoft Teams meeting could not start.")
            content.body = String(
                localized: "Hearsay could not start recording for the Microsoft Teams meeting.",
                comment: "Notification text under \"Recording not started\"; the Record tab shows why. Keep \"Microsoft Teams\" as is.")
        }
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        let center = center
        Task {
            do {
                try await center.add(request)
            } catch {
                Self.logger.error("Could not post a notification: \(String(describing: error), privacy: .public)")
            }
        }
    }

    private final class BannerDelegate: NSObject, UNUserNotificationCenterDelegate {
        func userNotificationCenter(
            _ center: UNUserNotificationCenter, willPresent notification: UNNotification
        ) async -> UNNotificationPresentationOptions {
            [.banner]
        }
    }
}
