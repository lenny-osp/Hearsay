using System.Globalization;
using Hearsay.Core.Audio;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// Debug only. <c>HEARSAY_WATCH_MEETINGS=&lt;seconds&gt;</c> lists the
/// capture sessions every second for that many seconds and prints what
/// <see cref="Recording.MeetingAutoRecord"/> would act on: every session once
/// at the start (endpoint name, process id, process name, parent name when
/// resolved, state), then one line per change in that list, and
/// <c>meeting started</c> / <c>meeting ended</c> from a
/// <see cref="MeetingDetector"/>. It only prints: nothing is recorded. Exit
/// status 0, or 1 for a bad value. The Mac counterpart is
/// <c>mac/Hearsay/Features/Debug/MeetingWatchDebug.swift</c>.
/// </summary>
internal static class MeetingWatchDebug
{
    public const string Variable = "HEARSAY_WATCH_MEETINGS";

    /// <summary>Returns false (and does nothing) when the variable is not set.</summary>
    public static bool RunIfRequested(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (!DebugEnvironment.Environment.TryGetValue(Variable, out var text) || text.Length == 0) return false;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            Say($"{Variable} needs a positive number of seconds");
            shell.FinishDebugRun(1);
            return true;
        }
        _ = RunAsync(shell, seconds);
        return true;
    }

    private static async Task RunAsync(AppShell shell, double seconds)
    {
        var status = 0;
        try
        {
            await WatchAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Say($"failed: {error}");
            status = 1;
        }
        shell.FinishDebugRun(status);
    }

    private static async Task WatchAsync(TimeSpan duration)
    {
        var detector = new MeetingDetector();
        var start = DateTimeOffset.Now;
        Dictionary<(string Endpoint, int Pid), CaptureSession>? previous = null;
        string? lastFailure = null;
        Say(string.Create(CultureInfo.InvariantCulture,
            $"watching for {duration.TotalSeconds:0.#} s (start delay {MeetingDetector.StartDelay.TotalSeconds:0} s, end grace {MeetingDetector.EndGrace.TotalSeconds:0} s)"));
        while (true)
        {
            IReadOnlyList<CaptureSession> sessions;
            try
            {
                // The probe runs on the thread pool, as in the app.
                sessions = await Task.Run(MeetingAudioProbe.CaptureSessions).ConfigureAwait(true);
                lastFailure = null;
            }
            catch (System.Runtime.InteropServices.COMException error)
            {
                if (error.Message != lastFailure) Say($"cannot list the capture sessions: {error.Message}");
                lastFailure = error.Message;
                sessions = [];
            }
            var now = DateTimeOffset.Now;
            var current = new Dictionary<(string Endpoint, int Pid), CaptureSession>();
            foreach (var session in sessions) current[(session.EndpointId, session.ProcessId)] = session;
            if (previous is null)
            {
                Say(string.Create(CultureInfo.InvariantCulture, $"{sessions.Count} capture session(s)"));
                foreach (var session in sessions) Say("  " + Describe(session));
            }
            else
            {
                foreach (var (key, session) in current)
                {
                    if (!previous.TryGetValue(key, out var before)) Say("added " + Describe(session));
                    else if (before != session) Say("changed " + Describe(session));
                }
                foreach (var (key, session) in previous)
                {
                    if (!current.ContainsKey(key)) Say("removed " + Describe(session));
                }
            }
            previous = current;
            switch (detector.Observe(sessions, now))
            {
                case MeetingEvent.Started:
                    Say("meeting started");
                    break;
                case MeetingEvent.Ended:
                    Say("meeting ended");
                    break;
            }
            if (now - start >= duration) break;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }
        Say(detector.IsInMeeting ? "done (in a meeting)" : "done");
    }

    private static string Describe(CaptureSession session)
    {
        var name = session.ProcessName ?? "(exited)";
        var parent = session.ParentProcessName is { } parentName ? $", parent {parentName}" : "";
        var teams = MeetingDetector.IsTeams(session) ? ", Teams" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"\"{session.EndpointName}\" pid {session.ProcessId} {name}{parent}: {session.State}{teams}");
    }

    private static void Say(string line)
    {
        DebugOutput.Out.WriteLine("hearsay meeting watch: " + line);
        DebugOutput.Out.Flush();
    }
}
