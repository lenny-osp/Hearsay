namespace Hearsay.Core.Audio;

/// <summary>What <see cref="MeetingDetector.Observe"/> reports when the meeting state changes.</summary>
public enum MeetingEvent
{
    Started,
    Ended,
}

/// <summary>
/// Decides from successive <see cref="MeetingAudioProbe.CaptureSessions"/>
/// lists when a Microsoft Teams meeting starts and ends. Pure: no timers, no
/// COM; the caller polls and passes the time. Windows only for now; there is
/// no Swift counterpart yet (the Mac port is planned one for one, with these
/// tests).
/// </summary>
/// <remarks>
/// <para>
/// The signal: when Teams joins a meeting or call it opens a capture stream
/// on its communications microphone and keeps it running until the call
/// ends, even while muted (Teams mutes in software; the Windows microphone
/// indicator stays on). So a meeting is a capture session in state
/// <see cref="CaptureSessionState.Active"/> owned by Teams. Inactive and
/// Expired sessions (a stream opened and stopped, or closed) do not count.
/// The probe lists sessions on every active capture endpoint, not only the
/// default one, because Teams records from the default communications
/// device, which can differ from the console default Hearsay records from.
/// </para>
/// <para>
/// Teams is <c>ms-teams</c> (the new MSIX Teams) or <c>teams</c> (classic
/// Teams), compared case-insensitively with the image name minus any
/// trailing <c>.exe</c>. A session also counts when its parent process is
/// Teams: a hedge in case Teams' media stack ever opens the stream from a
/// helper process.
/// </para>
/// <para>
/// <see cref="MeetingEvent.Started"/> is reported once a Teams Active session
/// has been present at every observation for <see cref="StartDelay"/> (the
/// first observation that sees one starts the clock; an observation without
/// one resets it). Two seconds filter out a short-lived stream, such as a
/// device test or a ring that is not answered, without delaying a real
/// meeting's recording noticeably.
/// </para>
/// <para>
/// <see cref="MeetingEvent.Ended"/> is reported once no Teams Active session
/// has been seen for <see cref="EndGrace"/>, measured from the last
/// observation that saw one. Teams closes and reopens its stream when the
/// user switches microphone or headset mid-meeting; a gap shorter than
/// fifteen seconds is that, not the meeting's end, and must not stop the
/// recording. After Ended a new Started needs the full start delay again.
/// </para>
/// <para>Not thread-safe: call <see cref="Observe"/> from one thread (the UI thread in the app).</para>
/// </remarks>
public sealed class MeetingDetector
{
    /// <summary>How long a Teams capture session must be seen before the meeting counts as started.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long no Teams capture session may be seen before the meeting counts as ended.</summary>
    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(15);

    private static readonly string[] TeamsNames = ["ms-teams", "teams"];

    private DateTimeOffset? presentSince;
    private DateTimeOffset lastSeen;

    /// <summary>True between a reported <see cref="MeetingEvent.Started"/> and the next <see cref="MeetingEvent.Ended"/>.</summary>
    public bool IsInMeeting { get; private set; }

    /// <summary>True when the session's own process, or else its parent, is Teams.</summary>
    public static bool IsTeams(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return IsTeamsName(session.ProcessName) || IsTeamsName(session.ParentProcessName);
    }

    /// <summary>True for <c>ms-teams</c> or <c>teams</c>, any case, with or without <c>.exe</c>.</summary>
    internal static bool IsTeamsName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        foreach (var teams in TeamsNames)
        {
            if (string.Equals(name, teams, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Takes one probe result at <paramref name="now"/> and returns the
    /// meeting event it completes, or null when the state did not change.
    /// </summary>
    public MeetingEvent? Observe(IReadOnlyList<CaptureSession> sessions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        bool present = false;
        foreach (var session in sessions)
        {
            if (session.State == CaptureSessionState.Active && IsTeams(session))
            {
                present = true;
                break;
            }
        }

        if (!IsInMeeting)
        {
            if (!present)
            {
                presentSince = null;
                return null;
            }
            presentSince ??= now;
            if (now - presentSince.Value < StartDelay)
            {
                return null;
            }
            presentSince = null;
            lastSeen = now;
            IsInMeeting = true;
            return MeetingEvent.Started;
        }

        if (present)
        {
            lastSeen = now;
            return null;
        }
        if (now - lastSeen < EndGrace)
        {
            return null;
        }
        IsInMeeting = false;
        return MeetingEvent.Ended;
    }
}
