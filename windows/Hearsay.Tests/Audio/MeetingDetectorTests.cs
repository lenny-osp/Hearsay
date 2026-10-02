using System.Diagnostics;
using System.Runtime.Versioning;
using Hearsay.Core.Audio;
using Xunit.Abstractions;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Tests for <see cref="MeetingDetector"/> (automatic recording of Microsoft
/// Teams meetings). Mirrored one for one by the Mac's MeetingDetectorTests.swift
/// (bundle ids and paths instead of process names). Hand-made session lists
/// and a stepped clock, no hardware.
/// </summary>
public class MeetingDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static CaptureSession Session(
        string? name,
        CaptureSessionState state = CaptureSessionState.Active,
        string? parent = null) =>
        new("{0.0.1.00000000}.{a}", "Headset Microphone", 4242, name, parent, state);

    private static readonly CaptureSession[] Teams = [Session("ms-teams")];
    private static readonly CaptureSession[] Nothing = [];

    /// <summary>Starts a meeting at t=0..2 s and returns the detector.</summary>
    private static MeetingDetector InMeeting()
    {
        var detector = new MeetingDetector();
        Assert.Null(detector.Observe(Teams, At(0)));
        Assert.Equal(MeetingEvent.Started, detector.Observe(Teams, At(2)));
        return detector;
    }

    [Fact]
    public void TheDelaysAreTwoAndFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MeetingDetector.StartDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), MeetingDetector.EndGrace);
    }

    [Fact]
    public void StartsOnceAfterTheStartDelay()
    {
        var detector = new MeetingDetector();
        Assert.Null(detector.Observe(Teams, At(0)));
        Assert.Null(detector.Observe(Teams, At(1)));
        Assert.False(detector.IsInMeeting);
        Assert.Equal(MeetingEvent.Started, detector.Observe(Teams, At(2)));
        Assert.True(detector.IsInMeeting);
        Assert.Null(detector.Observe(Teams, At(4)));
        Assert.Null(detector.Observe(Teams, At(60)));
        Assert.True(detector.IsInMeeting);
    }

    [Fact]
    public void ASessionThatDisappearsBeforeTheDelayResetsTheClock()
    {
        var detector = new MeetingDetector();
        Assert.Null(detector.Observe(Teams, At(0)));
        Assert.Null(detector.Observe(Teams, At(1)));
        Assert.Null(detector.Observe(Nothing, At(1.5)));
        Assert.Null(detector.Observe(Teams, At(2)));
        Assert.Null(detector.Observe(Teams, At(3)));
        Assert.False(detector.IsInMeeting);
        Assert.Equal(MeetingEvent.Started, detector.Observe(Teams, At(4)));
    }

    [Theory]
    [InlineData(CaptureSessionState.Inactive)]
    [InlineData(CaptureSessionState.Expired)]
    public void InactiveAndExpiredTeamsSessionsDoNotCount(CaptureSessionState state)
    {
        var detector = new MeetingDetector();
        CaptureSession[] sessions = [Session("ms-teams", state)];
        for (int second = 0; second <= 30; second += 2)
        {
            Assert.Null(detector.Observe(sessions, At(second)));
        }
        Assert.False(detector.IsInMeeting);
    }

    [Theory]
    [InlineData("Zoom")]
    [InlineData("msedge")]
    [InlineData("Teamsy")]
    [InlineData("ms-teams-helper")]
    [InlineData(null)]
    public void OtherAppsNeverStart(string? name)
    {
        var detector = new MeetingDetector();
        CaptureSession[] sessions = [Session(name), Session("chrome", parent: "explorer")];
        for (int second = 0; second <= 30; second += 2)
        {
            Assert.Null(detector.Observe(sessions, At(second)));
        }
        Assert.False(detector.IsInMeeting);
    }

    [Theory]
    [InlineData("ms-teams", true)]
    [InlineData("MS-TEAMS.EXE", true)]
    [InlineData("Teams.exe", true)]
    [InlineData("teams", true)]
    [InlineData("Ms-Teams.Exe", true)]
    [InlineData("Zoom", false)]
    [InlineData("msedge", false)]
    [InlineData(".exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TeamsNamesMatchCaseInsensitivelyWithOrWithoutExe(string? name, bool matches) =>
        Assert.Equal(matches, MeetingDetector.IsTeams(Session(name)));

    [Fact]
    public void AParentTeamsProcessCounts()
    {
        Assert.True(MeetingDetector.IsTeams(Session("msedgewebview2", parent: "ms-teams")));
        Assert.True(MeetingDetector.IsTeams(Session("helper", parent: "Teams.exe")));
        Assert.False(MeetingDetector.IsTeams(Session("helper", parent: "explorer")));

        var detector = new MeetingDetector();
        CaptureSession[] sessions = [Session("msedgewebview2", parent: "ms-teams")];
        Assert.Null(detector.Observe(sessions, At(0)));
        Assert.Equal(MeetingEvent.Started, detector.Observe(sessions, At(2)));
    }

    [Fact]
    public void ATeamsSessionAmongOthersCounts()
    {
        var detector = new MeetingDetector();
        CaptureSession[] sessions =
        [
            Session("Zoom"),
            Session("ms-teams", CaptureSessionState.Inactive),
            Session("Teams"),
        ];
        Assert.Null(detector.Observe(sessions, At(0)));
        Assert.Equal(MeetingEvent.Started, detector.Observe(sessions, At(2)));
    }

    [Fact]
    public void AShortGapDoesNotEndTheMeeting()
    {
        var detector = InMeeting();
        // A device switch: Teams closes its stream and reopens it 5 s later.
        Assert.Null(detector.Observe(Nothing, At(4)));
        Assert.Null(detector.Observe(Nothing, At(7)));
        Assert.Null(detector.Observe(Teams, At(9)));
        Assert.True(detector.IsInMeeting);
        // The grace is measured from the last observation that saw Teams.
        Assert.Null(detector.Observe(Nothing, At(11)));
        Assert.Null(detector.Observe(Nothing, At(23)));
        Assert.True(detector.IsInMeeting);
        Assert.Equal(MeetingEvent.Ended, detector.Observe(Nothing, At(24)));
    }

    [Fact]
    public void EndsOnceAfterTheEndGrace()
    {
        var detector = InMeeting();
        Assert.Null(detector.Observe(Teams, At(10)));
        Assert.Null(detector.Observe(Nothing, At(12)));
        Assert.Null(detector.Observe(Nothing, At(24)));
        Assert.True(detector.IsInMeeting);
        Assert.Equal(MeetingEvent.Ended, detector.Observe(Nothing, At(25)));
        Assert.False(detector.IsInMeeting);
        Assert.Null(detector.Observe(Nothing, At(27)));
        Assert.Null(detector.Observe(Nothing, At(60)));
        Assert.False(detector.IsInMeeting);
    }

    [Fact]
    public void AnInactiveSessionCountsAsGoneForTheEndGrace()
    {
        var detector = InMeeting();
        CaptureSession[] stopped = [Session("ms-teams", CaptureSessionState.Inactive)];
        Assert.Null(detector.Observe(stopped, At(4)));
        Assert.Null(detector.Observe(stopped, At(16)));
        Assert.Equal(MeetingEvent.Ended, detector.Observe(stopped, At(17)));
    }

    [Fact]
    public void AfterEndedANewStartNeedsTheFullDelay()
    {
        var detector = InMeeting();
        Assert.Equal(MeetingEvent.Ended, detector.Observe(Nothing, At(17)));
        // Teams is back at the very next poll: the clock starts again from there.
        Assert.Null(detector.Observe(Teams, At(19)));
        Assert.Null(detector.Observe(Teams, At(20)));
        Assert.False(detector.IsInMeeting);
        Assert.Equal(MeetingEvent.Started, detector.Observe(Teams, At(21)));
        Assert.True(detector.IsInMeeting);
        Assert.Equal(MeetingEvent.Ended, detector.Observe(Nothing, At(36)));
        Assert.False(detector.IsInMeeting);
    }

    [Fact]
    public void IsInMeetingTracksTheEvents()
    {
        var detector = new MeetingDetector();
        var events = new List<MeetingEvent>();
        var states = new List<bool>();
        CaptureSession[][] script =
        [
            Nothing, Teams, Teams, Teams, Nothing, Teams, Nothing, Nothing, Nothing, Nothing,
            Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
        ];
        for (int step = 0; step < script.Length; step++)
        {
            // Polls every 2 s, as the app does.
            if (detector.Observe(script[step], At(step * 2)) is MeetingEvent meetingEvent)
            {
                events.Add(meetingEvent);
                Assert.Equal(meetingEvent == MeetingEvent.Started, detector.IsInMeeting);
            }
            states.Add(detector.IsInMeeting);
        }
        Assert.Equal([MeetingEvent.Started, MeetingEvent.Ended], events);
        // Started at step 2 (t=4 s, seen since t=2 s); last seen at step 5 (t=10 s); ended at t=26 s (step 13).
        Assert.Equal(2, states.IndexOf(true));
        Assert.Equal(13, states.LastIndexOf(true) + 1);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => new MeetingDetector().Observe(null!, T0));
        Assert.Throws<ArgumentNullException>(() => MeetingDetector.IsTeams(null!));
    }
}

/// <summary>
/// Smoke test of <see cref="MeetingAudioProbe.CaptureSessions"/> against the
/// real endpoints. The machine may have no capture device or no session, so
/// it only checks that the call succeeds and returns a list.
/// </summary>
[SupportedOSPlatform("windows")]
public class MeetingAudioProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void CaptureSessionsListsWithoutThrowing()
    {
        var sessions = MeetingAudioProbe.CaptureSessions();
        Assert.NotNull(sessions);
        foreach (var session in sessions)
        {
            output.WriteLine(
                $"{session.EndpointName}: pid {session.ProcessId} {session.ProcessName ?? "(exited)"}"
                + $" parent {session.ParentProcessName ?? "-"} {session.State}");
            Assert.False(string.IsNullOrEmpty(session.EndpointId));
            if (session.ProcessName is not null && MeetingDetector.IsTeams(session with { ParentProcessName = null }))
            {
                Assert.Null(session.ParentProcessName);
            }
        }
        // Twice in a row: every COM wrapper was released, nothing is left registered.
        Assert.NotNull(MeetingAudioProbe.CaptureSessions());
    }

    [Fact]
    public void TheProcessSnapshotNamesAChildsParent()
    {
        using var self = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var child = Process.Start(start);
        Assert.NotNull(child);
        try
        {
            var tree = MeetingAudioProbe.ProcessTree.Snapshot();
            Assert.Equal(self.ProcessName, tree.ParentName(child.Id), ignoreCase: true);
            Assert.Null(tree.ParentName(-1));
        }
        finally
        {
            child.Kill();
            child.WaitForExit(5000);
        }
    }
}
