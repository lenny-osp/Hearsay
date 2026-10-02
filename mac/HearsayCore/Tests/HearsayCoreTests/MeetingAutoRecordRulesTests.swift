import Foundation
import Testing
@testable import HearsayCore

/// `MeetingAutoRecordRules` driven by a real `MeetingDetector` and a stepped
/// clock, with a fake controller: the Swift port of MeetingAutoRecordTests.cs
/// (the Windows tests run the coordinator on a real RecordingController; here
/// the decisions are pure, so a small shell stands in for it).
private final class Shell {
    typealias Action = MeetingAutoRecordRules.Action

    var rules: MeetingAutoRecordRules
    var detector = MeetingDetector()
    var now = Date(timeIntervalSince1970: 1_790_000_000)

    /// The fake controller.
    var session: MeetingSessionState = .idle
    var failNextStart = false
    var startCalls: [LanguageChoice?] = []
    var stopCalls = 0
    var notifications: [MeetingNotification] = []
    var notice = false
    var watching = false
    var openPrompt: Int?
    var promptsShown: [(id: Int, preselected: LanguageChoice)] = []
    var promptsClosed: [Int] = []
    var allActions: [Action] = []

    private static let teams = [
        CaptureProcess(pid: 4242, bundleID: "com.microsoft.teams2", executablePath: nil, isRunningInput: true),
    ]

    init(enabled: Bool = true, asks: Bool = false, language: LanguageChoice = .auto) {
        rules = MeetingAutoRecordRules(enabled: enabled, asksLanguage: asks, languageChoice: language)
        send(.begin)
    }

    func send(_ input: MeetingAutoRecordRules.Input) {
        perform(rules.handle(input))
    }

    private func perform(_ actions: [Action]) {
        for action in actions {
            allActions.append(action)
            switch action {
            case .startWatching:
                watching = true
                detector = MeetingDetector()
            case .stopWatching:
                watching = false
                detector = MeetingDetector()
            case .start(let language):
                startCalls.append(language)
                if let language { send(.languageChoice(language)) }
                session = failNextStart ? .failed : .starting
                send(.controller(session))
            case .stop:
                stopCalls += 1
                session = .idle
                send(.controller(.idle))
            case .showPrompt(let id, let preselected):
                openPrompt = id
                promptsShown.append((id, preselected))
            case .closePrompt(let id):
                openPrompt = nil
                promptsClosed.append(id)
            case .notify(let notification):
                notifications.append(notification)
            case .setAutomaticNotice:
                notice = true
            case .clearAutomaticNotice:
                notice = false
            }
        }
    }

    // MARK: Probe steps

    /// One observation `seconds` after the previous one, with or without a Teams call.
    func poll(teams: Bool, after seconds: Double = 1) {
        now = now.addingTimeInterval(seconds)
        // As the app does: only while watching.
        guard watching else { return }
        switch detector.observe(teams ? Self.teams : [], now: now) {
        case .started: send(.meetingStarted)
        case .ended: send(.meetingEnded)
        case nil: break
        }
    }

    func meetingStarts() {
        poll(teams: true)
        poll(teams: true, after: MeetingDetector.startDelay)
    }

    func meetingEnds() {
        poll(teams: false)
        poll(teams: false, after: MeetingDetector.endGrace)
    }

    // MARK: Controller steps

    /// The controller's phase moves (the user, the engine).
    func controller(_ state: MeetingSessionState) {
        session = state
        send(.controller(state))
    }

    /// The automatic session finishes preparing.
    func reachRecording() { controller(.recording) }
}

struct MeetingAutoRecordRulesTests {
    @Test func withTheSettingOffAMeetingNeverStartsARecording() {
        let shell = Shell(enabled: false)
        #expect(!shell.rules.isWatching)
        #expect(!shell.watching)
        shell.meetingStarts()
        shell.poll(teams: true)
        // Even if a stray event arrives, nothing happens while not watching.
        shell.send(.meetingStarted)
        #expect(shell.startCalls.isEmpty)
        #expect(shell.session == .idle)
        #expect(shell.notifications.isEmpty)
        #expect(!shell.notice)
    }

    @Test func watchingFollowsTheSetting() {
        let shell = Shell(enabled: true)
        #expect(shell.watching)
        #expect(shell.allActions == [.startWatching])
        shell.send(.settingEnabled(true))
        #expect(shell.allActions == [.startWatching])  // Already watching: no second start.

        shell.send(.settingEnabled(false))
        #expect(!shell.watching)
        #expect(shell.allActions == [.startWatching, .stopWatching])
        shell.send(.settingEnabled(false))
        #expect(shell.allActions == [.startWatching, .stopWatching])

        shell.send(.settingEnabled(true))
        #expect(shell.watching)
        #expect(shell.allActions.last == .startWatching)
        shell.meetingStarts()
        #expect(shell.session == .starting)
    }

    @Test func theSettingTurnedOnBeforeBeginStartsNothingUntilBegin() {
        var rules = MeetingAutoRecordRules(enabled: false, asksLanguage: false)
        #expect(rules.handle(.settingEnabled(true)).isEmpty)
        #expect(rules.handle(.begin) == [.startWatching])
    }

    @Test func aMeetingStartsARecordingWhenTheControllerIsIdle() {
        let shell = Shell()
        shell.poll(teams: true)
        #expect(shell.startCalls.isEmpty, "the start delay has not passed yet")
        shell.poll(teams: true, after: MeetingDetector.startDelay)
        #expect(shell.startCalls.count == 1)
        #expect(shell.startCalls.first == .some(nil), "no language: the Record tab's choice")
        #expect(shell.rules.isAutomaticSession)
        #expect(shell.notice)
        // "Recording started" waits for the phase to reach recording.
        #expect(shell.notifications.isEmpty)
        shell.reachRecording()
        #expect(shell.notifications == [.started])

        // Further observations during the meeting change nothing.
        shell.poll(teams: true)
        shell.poll(teams: true)
        #expect(shell.startCalls.count == 1)
        #expect(shell.notifications == [.started])

        // Quit or a user's stop ends the session: the notice goes.
        shell.controller(.idle)
        #expect(!shell.notice)
    }

    @Test func aMeetingDuringAManualRecordingLeavesItAlone() {
        let shell = Shell()
        shell.controller(.recording)
        shell.meetingStarts()
        #expect(!shell.rules.isAutomaticSession)
        #expect(!shell.notice)
        #expect(shell.notifications.isEmpty)
        #expect(shell.startCalls.isEmpty)

        shell.meetingEnds()
        #expect(shell.stopCalls == 0)
        #expect(shell.session == .recording)
        #expect(shell.notifications.isEmpty)
    }

    @Test func theMeetingsEndStopsTheAutomaticRecording() {
        let shell = Shell()
        shell.meetingStarts()
        shell.reachRecording()
        // A short gap (a device switch) does not end the meeting.
        shell.poll(teams: false)
        shell.poll(teams: false, after: 5)
        shell.poll(teams: true)
        #expect(shell.stopCalls == 0)
        #expect(shell.session == .recording)

        // Paused counts as a session too: the controller reports it as recording.
        shell.meetingEnds()
        #expect(shell.stopCalls == 1)
        #expect(shell.session == .idle)
        #expect(shell.notifications == [.started, .stopped])
        #expect(!shell.notice)
        #expect(!shell.rules.isAutomaticSession)
    }

    @Test func theMeetingsEndStopsASessionStillStarting() {
        let shell = Shell()
        shell.meetingStarts()
        #expect(shell.session == .starting)
        shell.meetingEnds()
        #expect(shell.stopCalls == 1)
        #expect(shell.notifications == [.stopped])
    }

    @Test func afterTheUserStopsNothingRestartsUntilTheNextMeeting() {
        let shell = Shell()
        shell.meetingStarts()
        shell.reachRecording()
        shell.controller(.idle)  // The user's Stop.
        #expect(!shell.rules.isAutomaticSession)
        #expect(!shell.notice)

        for _ in 0..<5 { shell.poll(teams: true, after: 2) }
        #expect(shell.startCalls.count == 1)
        #expect(shell.session == .idle)

        shell.meetingEnds()
        #expect(shell.notifications == [.started])
        #expect(shell.stopCalls == 0)
        shell.meetingStarts()
        #expect(shell.startCalls.count == 2)
        shell.reachRecording()
        #expect(shell.rules.isAutomaticSession)
        #expect(shell.notifications == [.started, .started])
    }

    @Test func stopAndStartNextKeepsTheNextSessionAutomatic() {
        let shell = Shell()
        shell.meetingStarts()
        shell.reachRecording()
        // Stop & Start Next: the session never goes inactive.
        shell.controller(.starting)
        shell.reachRecording()
        #expect(shell.rules.isAutomaticSession)
        #expect(shell.notice)
        #expect(shell.startCalls.count == 1)
        #expect(shell.notifications == [.started], "no second notification for the next session")

        shell.meetingEnds()
        #expect(shell.stopCalls == 1)
        #expect(shell.session == .idle)
        #expect(shell.notifications == [.started, .stopped])
    }

    @Test func aStartThatFailsNotifiesAndIsNotRetried() {
        let shell = Shell()
        shell.failNextStart = true
        shell.meetingStarts()
        #expect(shell.session == .failed)
        #expect(shell.notifications == [.notStarted])
        #expect(!shell.rules.isAutomaticSession)
        #expect(!shell.notice)
        #expect(shell.startCalls.count == 1)

        for _ in 0..<5 { shell.poll(teams: true, after: 2) }
        shell.meetingEnds()
        #expect(shell.startCalls.count == 1)
        #expect(shell.stopCalls == 0)
        #expect(shell.notifications == [.notStarted])
    }

    @Test func aStartThatLeavesTheControllerIdleDropsTheFlagQuietly() {
        var rules = MeetingAutoRecordRules(enabled: true, asksLanguage: false)
        _ = rules.handle(.begin)
        #expect(rules.handle(.meetingStarted) == [.start(language: nil)])
        #expect(rules.handle(.controller(.idle)).isEmpty)
        #expect(!rules.isAutomaticSession)
    }

    @Test func aFailureDuringTheMeetingAfterRecordingDropsTheFlag() {
        let shell = Shell()
        shell.meetingStarts()
        shell.reachRecording()
        shell.controller(.failed)
        #expect(!shell.rules.isAutomaticSession)
        #expect(!shell.notice)
        shell.meetingEnds()
        #expect(shell.stopCalls == 0)
        #expect(shell.notifications == [.started])
    }

    @Test func turningTheSettingOffLeavesTheRecordingRunning() {
        let shell = Shell()
        shell.meetingStarts()
        shell.reachRecording()

        shell.send(.settingEnabled(false))
        #expect(!shell.watching)
        #expect(!shell.rules.isAutomaticSession)
        shell.meetingEnds()
        #expect(shell.stopCalls == 0)
        #expect(shell.session == .recording)
        #expect(shell.notifications == [.started])

        // On again mid-recording: the running session is not taken over.
        shell.send(.settingEnabled(true))
        #expect(shell.watching)
        shell.meetingStarts()
        shell.meetingEnds()
        #expect(shell.stopCalls == 0)
        #expect(shell.session == .recording)
        #expect(shell.startCalls.count == 1)
    }

    // MARK: - Prompt

    @Test func withAskOnTheAnsweredLanguageIsUsedAndKept() throws {
        let shell = Shell(asks: true, language: .auto)
        shell.meetingStarts()
        #expect(shell.promptsShown.count == 1)
        #expect(shell.promptsShown.first?.preselected == .auto)
        #expect(shell.rules.isPrompting)
        #expect(shell.session == .idle, "nothing starts before the answer")

        let id = try #require(shell.openPrompt)
        let german = LanguageChoice.fixed(.german)
        shell.send(.promptAnswered(id: id, choice: german))
        #expect(!shell.rules.isPrompting)
        #expect(shell.startCalls == [german])
        #expect(shell.session == .starting)
        #expect(shell.notice)
        #expect(shell.notifications.isEmpty)
        shell.reachRecording()
        #expect(shell.notifications == [.started])

        shell.meetingEnds()
        #expect(shell.session == .idle)
        #expect(shell.notifications == [.started, .stopped])
    }

    @Test func thePromptIsPreselectedWithTheCurrentChoice() {
        let shell = Shell(asks: true, language: .auto)
        shell.send(.languageChoice(.fixed(.spanish)))
        shell.meetingStarts()
        #expect(shell.promptsShown.first?.preselected == .fixed(.spanish))
    }

    @Test func withAskOnDontRecordWaitsForTheNextMeeting() throws {
        let shell = Shell(asks: true)
        shell.meetingStarts()
        shell.send(.promptAnswered(id: try #require(shell.openPrompt), choice: nil))
        #expect(shell.session == .idle)
        #expect(!shell.rules.isPrompting)

        for _ in 0..<5 { shell.poll(teams: true, after: 2) }
        #expect(shell.promptsShown.count == 1)
        shell.meetingEnds()
        #expect(shell.promptsClosed.isEmpty)
        shell.meetingStarts()
        #expect(shell.promptsShown.count == 2)
        shell.send(.promptAnswered(id: try #require(shell.openPrompt), choice: nil))
        #expect(shell.startCalls.isEmpty)
        #expect(shell.notifications.isEmpty)
    }

    @Test func withAskOnTheMeetingsEndClosesAnOpenPrompt() throws {
        let shell = Shell(asks: true)
        shell.meetingStarts()
        let id = try #require(shell.openPrompt)
        shell.meetingEnds()
        #expect(shell.promptsClosed == [id])
        #expect(!shell.rules.isPrompting)
        // A late answer after the close starts nothing either.
        shell.send(.promptAnswered(id: id, choice: .fixed(.spanish)))
        #expect(shell.session == .idle)
        #expect(shell.startCalls.isEmpty)
        #expect(shell.notifications.isEmpty)
    }

    @Test func withAskOnAManualRecordingIsNeverPrompted() {
        let shell = Shell(asks: true)
        shell.controller(.recording)
        shell.meetingStarts()
        #expect(shell.promptsShown.isEmpty)
        #expect(!shell.rules.isPrompting)
        shell.meetingEnds()
        #expect(shell.session == .recording)
        #expect(shell.stopCalls == 0)
    }

    @Test func neverTwoPromptsAtOnce() {
        var rules = MeetingAutoRecordRules(enabled: true, asksLanguage: true)
        _ = rules.handle(.begin)
        let first = rules.handle(.meetingStarted)
        #expect(first == [.showPrompt(id: 1, preselected: .auto)])
        #expect(rules.handle(.meetingStarted).isEmpty)
        #expect(rules.openPromptID == 1)
    }

    @Test func aLateAnswerForAnOlderPromptIsIgnored() throws {
        let shell = Shell(asks: true)
        shell.meetingStarts()
        let oldID = try #require(shell.openPrompt)
        shell.meetingEnds()
        shell.meetingStarts()
        let newID = try #require(shell.openPrompt)
        #expect(newID != oldID)
        shell.send(.promptAnswered(id: oldID, choice: .fixed(.german)))
        #expect(shell.startCalls.isEmpty)
        #expect(shell.rules.openPromptID == newID)
    }

    @Test func aRecordingStartedWhileThePromptIsOpenStaysManual() throws {
        let shell = Shell(asks: true)
        shell.meetingStarts()
        let id = try #require(shell.openPrompt)
        shell.controller(.recording)
        shell.send(.promptAnswered(id: id, choice: .fixed(.german)))
        #expect(shell.startCalls.isEmpty)
        #expect(!shell.rules.isAutomaticSession)
        shell.meetingEnds()
        #expect(shell.stopCalls == 0)
    }

    @Test func turningTheSettingOffClosesAnOpenPrompt() throws {
        let shell = Shell(asks: true)
        shell.meetingStarts()
        let id = try #require(shell.openPrompt)
        shell.send(.settingEnabled(false))
        #expect(shell.promptsClosed == [id])
        #expect(!shell.watching)
        shell.send(.promptAnswered(id: id, choice: .auto))
        #expect(shell.startCalls.isEmpty)
    }

    @Test func theAsksSettingIsReadWhenAMeetingStarts() {
        let shell = Shell(asks: false)
        shell.send(.asksLanguage(true))
        shell.meetingStarts()
        #expect(shell.promptsShown.count == 1)
        #expect(shell.startCalls.isEmpty)
    }
}
