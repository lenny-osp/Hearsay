import Foundation

/// A notification the app shows for an automatic recording (PLAN.md 4.10).
public enum MeetingNotification: Equatable, Sendable {
    /// "Recording started": the automatic session reached recording.
    case started
    /// "Recording stopped": the meeting ended and the session was stopped.
    case stopped
    /// "Recording not started": the automatic start failed. Not retried.
    case notStarted
}

/// The recording controller's state as the rules need it. The app maps its
/// own phases onto these four.
public enum MeetingSessionState: Equatable, Sendable {
    /// No session and no failure showing.
    case idle
    /// A session is active but has not reached recording yet (preparing).
    case starting
    /// A session is recording or paused.
    case recording
    /// The start failed; no session.
    case failed

    /// True while a session exists (starting, recording or paused).
    public var isSessionActive: Bool { self == .starting || self == .recording }
}

/// The decision core of the automatic recording of Microsoft Teams meetings
/// (PLAN.md 4.10; port of the decisions in MeetingAutoRecord.cs). A pure
/// state machine: the app coordinator feeds it `Input`s, performs the
/// returned `Action`s, and holds no rules of its own.
///
/// The shell's duties:
/// - Watching: on `.startWatching` start `MeetingAudioObserver` and feed the
///   detector; on `.stopWatching` stop it and discard the detector (a new
///   `MeetingDetector()` next time). Feed `.meetingStarted` / `.meetingEnded`
///   from the detector's events.
/// - Controller: send `.controller(state)` on every phase change, and also
///   right after performing `.start` (even when unchanged), so a start that
///   did nothing is noticed. `.starting`/`.recording` must stay session-active
///   across Stop & Start Next (never report `.idle` between the two sessions):
///   that is how the next session stays automatic.
/// - Prompt: `.showPrompt(id:preselected:)` opens the question; answer with
///   `.promptAnswered(id:choice:)` (nil choice = Don't record, also for a
///   dismissed window). `.closePrompt` hides it; a late answer for a closed
///   prompt is ignored.
/// - `.start(language:)`: with a language, set the Record tab's language
///   choice first (it persists), then start the session as the Start button
///   does; with nil start with the current choice.
/// - Language: send `.languageChoice(_)` when the Record tab's choice
///   changes, so the prompt is preselected with the current one.
///
/// Rules, in terms of this type:
/// - Meeting started, setting on, nothing else going on: start (session
///   marked automatic), or ask first when `asksLanguage`.
/// - Meeting started while a session is active (the user recorded first): nothing;
///   that session stays manual and the meeting's end does not stop it.
/// - Never two prompts; no prompt while a session is active; a meeting that
///   ends closes the open prompt.
/// - Meeting ended while the automatic session is active (also paused): stop
///   and notify `.stopped`.
/// - The automatic flag drops as soon as the controller has no session (the
///   user's Stop, a failure, quit). Nothing restarts until the detector
///   reports ended and then started again.
/// - A start that fails: notify `.notStarted`, no retry.
/// - `.started` is notified when the phase reaches recording, not at the
///   start call.
/// - Setting off: stop watching, close a prompt, drop the flag; a running
///   session is left alone (the user stops it).
public struct MeetingAutoRecordRules: Sendable {
    /// What the shell tells the rules.
    public enum Input: Equatable, Sendable {
        /// The shell is up: begin watching if the setting is on.
        case begin
        /// The detector reported a meeting start / end.
        case meetingStarted
        case meetingEnded
        /// "Record Microsoft Teams meetings automatically" changed.
        case settingEnabled(Bool)
        /// "Ask which language to use" changed.
        case asksLanguage(Bool)
        /// The Record tab's language choice changed.
        case languageChoice(LanguageChoice)
        /// The recording controller's state (see the type's notes).
        case controller(MeetingSessionState)
        /// The user answered the open prompt; nil choice is Don't record.
        case promptAnswered(id: Int, choice: LanguageChoice?)
    }

    /// What the shell performs, in order.
    public enum Action: Equatable, Sendable {
        /// Start observing audio processes and feed the detector.
        case startWatching
        /// Stop observing and discard the detector.
        case stopWatching
        /// Start a session as the Start button does; a language is set as
        /// the Record tab's choice first. The session is automatic.
        case start(language: LanguageChoice?)
        /// Stop the session as the user's Stop does.
        case stop
        /// Bring the window forward and ask "Record this meeting?".
        case showPrompt(id: Int, preselected: LanguageChoice)
        /// Hide the prompt with this id without an answer.
        case closePrompt(id: Int)
        case notify(MeetingNotification)
        /// Show the notice line on the Record tab ("Recording started
        /// automatically for a Microsoft Teams meeting."); idempotent.
        case setAutomaticNotice
        case clearAutomaticNotice
    }

    public private(set) var isEnabled: Bool
    public private(set) var asksLanguage: Bool
    /// The probe should run (the setting is on and watching began).
    public private(set) var isWatching = false
    /// The session running or starting now was started for a meeting.
    public private(set) var isAutomaticSession = false
    /// True between the detector's started and ended (cleared when watching stops).
    public private(set) var isMeetingActive = false
    /// The id of the open prompt, or nil.
    public private(set) var openPromptID: Int?
    public var isPrompting: Bool { openPromptID != nil }

    private var session: MeetingSessionState = .idle
    private var language: LanguageChoice
    private var begun = false
    private var awaitingStart = false
    private var noticeShown = false
    private var nextPromptID = 1

    /// - Parameters:
    ///   - enabled: the setting's current value.
    ///   - asksLanguage: the second setting's current value.
    ///   - languageChoice: the Record tab's current choice.
    public init(enabled: Bool, asksLanguage: Bool, languageChoice: LanguageChoice = .auto) {
        self.isEnabled = enabled
        self.asksLanguage = asksLanguage
        self.language = languageChoice
    }

    /// Applies one input and returns the actions to perform, in order.
    public mutating func handle(_ input: Input) -> [Action] {
        var actions: [Action] = []
        switch input {
        case .begin:
            begun = true
            if isEnabled { startWatching(&actions) }
        case .meetingStarted:
            guard isWatching else { break }
            meetingStarted(&actions)
        case .meetingEnded:
            guard isWatching else { break }
            meetingEnded(&actions)
        case .settingEnabled(let enabled):
            isEnabled = enabled
            if enabled {
                if begun { startWatching(&actions) }
            } else {
                settingOff(&actions)
            }
        case .asksLanguage(let asks):
            asksLanguage = asks
        case .languageChoice(let choice):
            language = choice
        case .controller(let state):
            controllerChanged(state, &actions)
        case .promptAnswered(let id, let choice):
            promptAnswered(id: id, choice: choice, &actions)
        }
        return actions
    }

    // MARK: - Decisions

    private mutating func meetingStarted(_ actions: inout [Action]) {
        isMeetingActive = true
        guard canStart else { return }  // The user records: it stays manual.
        guard openPromptID == nil else { return }  // Never two prompts.
        if asksLanguage {
            let id = nextPromptID
            nextPromptID += 1
            openPromptID = id
            actions.append(.showPrompt(id: id, preselected: language))
            return
        }
        startAutomaticSession(language: nil, &actions)
    }

    private mutating func meetingEnded(_ actions: inout [Action]) {
        isMeetingActive = false
        closePrompt(&actions)
        if isAutomaticSession && session.isSessionActive {
            actions.append(.notify(.stopped))
            actions.append(.stop)
        }
    }

    private mutating func promptAnswered(id: Int, choice: LanguageChoice?, _ actions: inout [Action]) {
        guard openPromptID == id else { return }  // Closed meanwhile: a late answer does nothing.
        openPromptID = nil
        guard let choice else { return }  // Don't record: nothing until the next meeting.
        guard isEnabled, isWatching, isMeetingActive else { return }
        guard canStart else { return }  // A recording started while the prompt was open: it stays manual.
        language = choice
        startAutomaticSession(language: choice, &actions)
    }

    private mutating func startAutomaticSession(language: LanguageChoice?, _ actions: inout [Action]) {
        isAutomaticSession = true
        awaitingStart = true
        actions.append(.start(language: language))
    }

    private mutating func controllerChanged(_ state: MeetingSessionState, _ actions: inout [Action]) {
        let previous = session
        session = state
        guard isAutomaticSession else { return }
        if awaitingStart {
            settleStart(state, &actions)
        } else if !state.isSessionActive {
            dropAutomatic(&actions)
        }
        if isAutomaticSession, state.isSessionActive, previous != state {
            noticeShown = true
            actions.append(.setAutomaticNotice)
        }
    }

    /// The automatic start reached recording (notify), failed (notify, no
    /// retry), or ended without a session.
    private mutating func settleStart(_ state: MeetingSessionState, _ actions: inout [Action]) {
        switch state {
        case .recording:
            awaitingStart = false
            actions.append(.notify(.started))
        case .failed:
            awaitingStart = false
            actions.append(.notify(.notStarted))
            dropAutomatic(&actions)
        case .idle:
            awaitingStart = false
            dropAutomatic(&actions)
        case .starting:
            break
        }
    }

    private mutating func dropAutomatic(_ actions: inout [Action]) {
        isAutomaticSession = false
        awaitingStart = false
        clearNotice(&actions)
    }

    // MARK: - Watching

    private mutating func startWatching(_ actions: inout [Action]) {
        guard !isWatching else { return }
        isWatching = true
        isMeetingActive = false
        actions.append(.startWatching)
    }

    private mutating func settingOff(_ actions: inout [Action]) {
        guard isWatching || isAutomaticSession || openPromptID != nil else { return }
        if isWatching {
            isWatching = false
            actions.append(.stopWatching)
        }
        closePrompt(&actions)
        // A running automatic session goes on; the user stops it.
        dropAutomatic(&actions)
        isMeetingActive = false
    }

    private mutating func closePrompt(_ actions: inout [Action]) {
        guard let id = openPromptID else { return }
        openPromptID = nil
        actions.append(.closePrompt(id: id))
    }

    private mutating func clearNotice(_ actions: inout [Action]) {
        guard noticeShown else { return }
        noticeShown = false
        actions.append(.clearAutomaticNotice)
    }

    /// The controller can start a session: none is active (idle or failed).
    private var canStart: Bool { !session.isSessionActive }
}
