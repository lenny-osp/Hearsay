import Foundation
import Testing
@testable import HearsayCore

/// Tests for `MeetingDetector` (PLAN.md 4.10), ported one for one from
/// MeetingDetectorTests.cs: hand-made process lists and a stepped clock.
struct MeetingDetectorTests {
    private static let t0 = Date(timeIntervalSince1970: 1_790_000_000)

    private static func at(_ seconds: Double) -> Date { t0.addingTimeInterval(seconds) }

    private static func proc(
        _ bundleID: String?, running: Bool = true, path: String? = nil
    ) -> CaptureProcess {
        CaptureProcess(pid: 4242, bundleID: bundleID, executablePath: path, isRunningInput: running)
    }

    private static let teams = [proc("com.microsoft.teams2")]
    private static let nothing: [CaptureProcess] = []

    /// Starts a meeting at t=0..2 s and returns the detector.
    private static func inMeeting() -> MeetingDetector {
        var detector = MeetingDetector()
        #expect(detector.observe(teams, now: at(0)) == nil)
        #expect(detector.observe(teams, now: at(2)) == .started)
        return detector
    }

    @Test func theDelaysAreTwoAndFifteenSeconds() {
        #expect(MeetingDetector.startDelay == 2)
        #expect(MeetingDetector.endGrace == 15)
    }

    @Test func startsOnceAfterTheStartDelay() {
        var detector = MeetingDetector()
        #expect(detector.observe(Self.teams, now: Self.at(0)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(1)) == nil)
        #expect(!detector.isInMeeting)
        #expect(detector.observe(Self.teams, now: Self.at(2)) == .started)
        #expect(detector.isInMeeting)
        #expect(detector.observe(Self.teams, now: Self.at(4)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(60)) == nil)
        #expect(detector.isInMeeting)
    }

    @Test func aStreamThatDisappearsBeforeTheDelayResetsTheClock() {
        var detector = MeetingDetector()
        #expect(detector.observe(Self.teams, now: Self.at(0)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(1)) == nil)
        #expect(detector.observe(Self.nothing, now: Self.at(1.5)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(2)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(3)) == nil)
        #expect(!detector.isInMeeting)
        #expect(detector.observe(Self.teams, now: Self.at(4)) == .started)
    }

    @Test func aTeamsProcessNotRunningInputDoesNotCount() {
        var detector = MeetingDetector()
        let idle = [Self.proc("com.microsoft.teams2", running: false)]
        for second in stride(from: 0, through: 30, by: 2) {
            #expect(detector.observe(idle, now: Self.at(Double(second))) == nil)
        }
        #expect(!detector.isInMeeting)
    }

    @Test(arguments: [
        "us.zoom.xos", "com.microsoft.edgemac", "com.microsoft.teamsy", "com.microsoft.teams2x",
        "com.microsoft.teamsx.helper", "com.apple.Safari", "", "com.microsoft",
    ])
    func otherAppsNeverStart(bundleID: String) {
        var detector = MeetingDetector()
        let chrome = Self.proc(
            "com.google.Chrome", path: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")
        let processes = [Self.proc(bundleID), chrome]
        for second in stride(from: 0, through: 30, by: 2) {
            #expect(detector.observe(processes, now: Self.at(Double(second))) == nil)
        }
        #expect(!detector.isInMeeting)
    }

    @Test func aProcessWithNoBundleIDOrPathNeverStarts() {
        var detector = MeetingDetector()
        let processes = [Self.proc(nil)]
        for second in stride(from: 0, through: 30, by: 2) {
            #expect(detector.observe(processes, now: Self.at(Double(second))) == nil)
        }
        #expect(!detector.isInMeeting)
    }

    @Test(arguments: [
        ("com.microsoft.teams2", true),
        ("com.microsoft.teams", true),
        ("COM.Microsoft.Teams2", true),
        ("Com.Microsoft.Teams", true),
        ("com.microsoft.teams2.modulehost", true),
        ("com.microsoft.teams.helper.renderer", true),
        ("us.zoom.xos", false),
        ("com.microsoft.edgemac", false),
        ("com.microsoft.teamsy", false),
        ("", false),
    ])
    func teamsBundleIDsMatchCaseInsensitivelyIncludingHelpers(bundleID: String, matches: Bool) {
        #expect(MeetingDetector.isTeams(Self.proc(bundleID)) == matches)
    }

    @Test func bothBundleIDsStartAMeeting() {
        for bundleID in ["com.microsoft.teams", "com.microsoft.teams2"] {
            var detector = MeetingDetector()
            let processes = [Self.proc(bundleID)]
            #expect(detector.observe(processes, now: Self.at(0)) == nil)
            #expect(detector.observe(processes, now: Self.at(2)) == .started)
        }
    }

    @Test func aHelperBundleIDOrAPathMatchCounts() {
        // The Mac rule replacing the Windows parent-process hedge.
        #expect(MeetingDetector.isTeams(Self.proc("com.microsoft.teams2.modulehost")))
        let helperPath = "/Applications/Microsoft Teams.app/Contents/Helpers/"
            + "Microsoft Teams ModuleHost.app/Contents/MacOS/Microsoft Teams ModuleHost"
        #expect(MeetingDetector.isTeams(Self.proc(nil, path: helperPath)))
        #expect(MeetingDetector.isTeams(Self.proc("org.something.else", path: helperPath)))
        #expect(MeetingDetector.isTeams(Self.proc(
            nil, path: "/Applications/Microsoft Teams (work or school).app/Contents/MacOS/MSTeams")))
        #expect(MeetingDetector.isTeams(Self.proc(
            nil, path: "/applications/microsoft teams classic.app/contents/macos/teams")))
        #expect(!MeetingDetector.isTeams(Self.proc(nil, path: "/Applications/Zoom.app/Contents/MacOS/zoom.us")))
        #expect(!MeetingDetector.isTeams(Self.proc(nil, path: "/Users/me/Microsoft Teams notes/tool")))
        #expect(!MeetingDetector.isTeams(Self.proc(
            nil, path: "/Applications/Microsoft Word.app/Contents/MacOS/Microsoft Word")))
        #expect(!MeetingDetector.isTeams(Self.proc(nil, path: "")))

        var detector = MeetingDetector()
        let processes = [Self.proc(nil, path: helperPath)]
        #expect(detector.observe(processes, now: Self.at(0)) == nil)
        #expect(detector.observe(processes, now: Self.at(2)) == .started)
    }

    @Test func aTeamsStreamAmongOthersCounts() {
        var detector = MeetingDetector()
        let processes = [
            Self.proc("us.zoom.xos"),
            Self.proc("com.microsoft.teams2", running: false),
            Self.proc("com.microsoft.teams"),
        ]
        #expect(detector.observe(processes, now: Self.at(0)) == nil)
        #expect(detector.observe(processes, now: Self.at(2)) == .started)
    }

    @Test func aShortGapDoesNotEndTheMeeting() {
        var detector = Self.inMeeting()
        // A device switch: Teams closes its stream and reopens it 5 s later.
        #expect(detector.observe(Self.nothing, now: Self.at(4)) == nil)
        #expect(detector.observe(Self.nothing, now: Self.at(7)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(9)) == nil)
        #expect(detector.isInMeeting)
        // The grace is measured from the last observation that saw Teams.
        #expect(detector.observe(Self.nothing, now: Self.at(11)) == nil)
        #expect(detector.observe(Self.nothing, now: Self.at(23)) == nil)
        #expect(detector.isInMeeting)
        #expect(detector.observe(Self.nothing, now: Self.at(24)) == .ended)
    }

    @Test func endsOnceAfterTheEndGrace() {
        var detector = Self.inMeeting()
        #expect(detector.observe(Self.teams, now: Self.at(10)) == nil)
        #expect(detector.observe(Self.nothing, now: Self.at(12)) == nil)
        #expect(detector.observe(Self.nothing, now: Self.at(24)) == nil)
        #expect(detector.isInMeeting)
        #expect(detector.observe(Self.nothing, now: Self.at(25)) == .ended)
        #expect(!detector.isInMeeting)
        #expect(detector.observe(Self.nothing, now: Self.at(27)) == nil)
        #expect(detector.observe(Self.nothing, now: Self.at(60)) == nil)
        #expect(!detector.isInMeeting)
    }

    @Test func aStoppedStreamCountsAsGoneForTheEndGrace() {
        var detector = Self.inMeeting()
        let stopped = [Self.proc("com.microsoft.teams2", running: false)]
        #expect(detector.observe(stopped, now: Self.at(4)) == nil)
        #expect(detector.observe(stopped, now: Self.at(16)) == nil)
        #expect(detector.observe(stopped, now: Self.at(17)) == .ended)
    }

    @Test func afterEndedANewStartNeedsTheFullDelay() {
        var detector = Self.inMeeting()
        #expect(detector.observe(Self.nothing, now: Self.at(17)) == .ended)
        #expect(detector.observe(Self.teams, now: Self.at(19)) == nil)
        #expect(detector.observe(Self.teams, now: Self.at(20)) == nil)
        #expect(!detector.isInMeeting)
        #expect(detector.observe(Self.teams, now: Self.at(21)) == .started)
        #expect(detector.isInMeeting)
        #expect(detector.observe(Self.nothing, now: Self.at(36)) == .ended)
        #expect(!detector.isInMeeting)
    }

    @Test func isInMeetingTracksTheEvents() {
        var detector = MeetingDetector()
        var events: [MeetingEvent] = []
        var states: [Bool] = []
        let script: [[CaptureProcess]] = [
            Self.nothing, Self.teams, Self.teams, Self.teams, Self.nothing, Self.teams,
        ] + Array(repeating: Self.nothing, count: 14)
        for (step, processes) in script.enumerated() {
            if let event = detector.observe(processes, now: Self.at(Double(step * 2))) {
                events.append(event)
                #expect(detector.isInMeeting == (event == .started))
            }
            states.append(detector.isInMeeting)
        }
        #expect(events == [.started, .ended])
        // Started at step 2 (t=4 s, seen since t=2 s); last seen at step 5 (t=10 s); ended at t=26 s (step 13).
        #expect(states.firstIndex(of: true) == 2)
        #expect((states.lastIndex(of: true) ?? -1) + 1 == 13)
    }

    // MARK: - nextDeadline

    @Test func nextDeadlineIsNilWhenNothingIsPending() {
        var detector = MeetingDetector()
        #expect(detector.nextDeadline == nil)
        _ = detector.observe(Self.nothing, now: Self.at(0))
        #expect(detector.nextDeadline == nil)
        // In a meeting with the stream still there: nothing is pending.
        var running = Self.inMeeting()
        #expect(running.nextDeadline == nil)
        _ = running.observe(Self.teams, now: Self.at(30))
        #expect(running.nextDeadline == nil)
    }

    @Test func nextDeadlineForAPendingStartIsTheFirstSightingPlusTheDelay() {
        var detector = MeetingDetector()
        _ = detector.observe(Self.teams, now: Self.at(5))
        #expect(detector.nextDeadline == Self.at(7))
        // Later sightings do not move it.
        _ = detector.observe(Self.teams, now: Self.at(6))
        #expect(detector.nextDeadline == Self.at(7))
        // Re-observing at the deadline fires it and clears it.
        #expect(detector.observe(Self.teams, now: Self.at(7)) == .started)
        #expect(detector.nextDeadline == nil)
        // A gap resets it.
        var other = MeetingDetector()
        _ = other.observe(Self.teams, now: Self.at(0))
        _ = other.observe(Self.nothing, now: Self.at(1))
        #expect(other.nextDeadline == nil)
    }

    @Test func nextDeadlineForAPendingEndIsTheLastSightingPlusTheGrace() {
        var detector = Self.inMeeting()
        _ = detector.observe(Self.teams, now: Self.at(10))
        #expect(detector.nextDeadline == nil)
        _ = detector.observe(Self.nothing, now: Self.at(12))
        #expect(detector.nextDeadline == Self.at(25))
        _ = detector.observe(Self.nothing, now: Self.at(20))
        #expect(detector.nextDeadline == Self.at(25))
        // The stream returns: the end is no longer pending.
        _ = detector.observe(Self.teams, now: Self.at(21))
        #expect(detector.nextDeadline == nil)
        _ = detector.observe(Self.nothing, now: Self.at(22))
        #expect(detector.nextDeadline == Self.at(36))
        #expect(detector.observe(Self.nothing, now: Self.at(36)) == .ended)
        #expect(detector.nextDeadline == nil)
    }
}
