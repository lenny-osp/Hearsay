namespace Hearsay.Core.Transcription;

/// <summary>
/// How long the app remembers a speed probe result (PLAN.md 4.12, "As built
/// (Windows, 2026-10-03)", and 18.4 "Speed"). A fast result stays for the
/// process; a "too slow" result is remembered for <see cref="TooSlowMemory"/>
/// only, because a busy moment (agents building on the machine, the first
/// recording right after launch) must not turn the live preview off until
/// relaunch. Windows only: the Mac has no speed probe.
/// </summary>
public static class SpeedProbeMemory
{
    /// <summary>How long a "too slow" result is remembered.</summary>
    public static readonly TimeSpan TooSlowMemory = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether a result measured at <paramref name="measuredAt"/> still
    /// stands at <paramref name="now"/>: always when the preview is feasible,
    /// and for ten minutes (inclusive of the tenth) when it is not. A clock
    /// that went backwards counts as not expired.
    /// </summary>
    public static bool IsRemembered(bool livePreviewFeasible, DateTimeOffset measuredAt, DateTimeOffset now) =>
        livePreviewFeasible || now - measuredAt <= TooSlowMemory;
}
