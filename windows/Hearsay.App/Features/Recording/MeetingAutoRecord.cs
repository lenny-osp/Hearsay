using System.ComponentModel;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// Records Microsoft Teams meetings automatically (Settings > General >
/// Meetings, <see cref="AppSettings.AutoRecordTeamsMeetings"/>). Windows only
/// for now; there is no Swift counterpart yet. The decision when a meeting
/// starts and ends is <see cref="MeetingDetector"/>'s; this class acts on it.
/// </summary>
/// <remarks>
/// <para>Off: no timer runs and nothing is probed. On: every two seconds
/// the probe (<see cref="MeetingAudioProbe.CaptureSessions"/>) lists the
/// capture sessions on the thread pool, and the detector decides on the UI
/// thread (the <see cref="SynchronizationContext"/> captured at
/// construction, as <see cref="AudioDeviceListObservation"/> does). A probe
/// failure is logged once and probing goes on.</para>
/// <para>Started with the controller idle: <see cref="RecordingController.Start()"/>,
/// the session is remembered as automatic, the Record tab shows
/// <see cref="RecordingController.AutomaticStartNotice"/>, and a balloon says
/// "Recording started" once the session records. With
/// <see cref="AppSettings.AutoRecordAsksLanguage"/> on, the prompt asks first:
/// a language sets <see cref="RecordingController.LanguageChoice"/> (it
/// persists, as on the Record tab) and starts the session; no answer records
/// nothing until the next meeting; Ended while it is open cancels it. Only
/// one prompt is open at a time.</para>
/// <para>Started while a session is active (the user recorded first): nothing;
/// that session stays manual and the meeting's end does not stop it.</para>
/// <para>Ended while the automatic session runs (also paused): Stop, as the
/// user's Stop would, and a "Recording stopped" balloon.</para>
/// <para>The automatic flag drops as soon as the controller has no session
/// (the user's Stop, a failure, quit), so nothing restarts until the detector
/// reports Ended and then Started again. Stop &amp; Start Next has no gap in
/// <see cref="RecordingController.IsSessionActive"/>, so the next session
/// stays automatic.</para>
/// <para>A start that fails shows the controller's usual failure on the Record
/// tab plus a "Recording not started" balloon, and is not retried (the
/// detector reports Started once per meeting).</para>
/// <para>Turning the setting off stops watching and leaves a running session
/// alone (the user stops it). Debug runs never start this coordinator.</para>
/// Use from the UI thread.
/// </remarks>
internal sealed class MeetingAutoRecord : IDisposable
{
    /// <summary>How often the capture sessions are listed while the setting is on.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly AppSettings settings;
    private readonly RecordingController controller;
    private readonly Func<IReadOnlyList<CaptureSession>> probe;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string, string> notify;
    private readonly Func<CancellationToken, Task<LanguageChoice?>> prompt;
    private readonly TimeSpan interval;
    private readonly SynchronizationContext? context;
    private MeetingDetector detector = new();
    private CancellationTokenSource? watching;
    private CancellationTokenSource? prompting;
    private bool started;
    private bool disposed;
    private bool automatic;
    private bool awaitingStart;
    private bool stoppingForMeetingEnd;
    private int probeFailureLogged;

    /// <param name="settings">The two settings.</param>
    /// <param name="controller">The one recording session.</param>
    /// <param name="probe">Lists the capture sessions; called on the thread pool.</param>
    /// <param name="clock">The time of a probe.</param>
    /// <param name="notify">A tray balloon: title, message.</param>
    /// <param name="prompt">Asks whether and in which language to record; null is "don't record". Cancelling hides it.</param>
    /// <param name="pollInterval">Tests only: a shorter interval than <see cref="PollInterval"/>.</param>
    public MeetingAutoRecord(
        AppSettings settings, RecordingController controller, Func<IReadOnlyList<CaptureSession>> probe,
        Func<DateTimeOffset> clock, Action<string, string> notify, Func<CancellationToken, Task<LanguageChoice?>> prompt,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(prompt);
        this.settings = settings;
        this.controller = controller;
        this.probe = probe;
        this.clock = clock;
        this.notify = notify;
        this.prompt = prompt;
        interval = pollInterval ?? PollInterval;
        context = SynchronizationContext.Current;
        controller.PropertyChanged += OnControllerChanged;
    }

    /// <summary>The probe runs now (the setting is on and <see cref="Start"/> was called).</summary>
    public bool IsWatching => watching is not null;

    /// <summary>The session running now was started for a meeting.</summary>
    internal bool IsAutomaticSession => automatic;

    /// <summary>The language prompt is open.</summary>
    internal bool IsPrompting => prompting is not null;

    /// <summary>The last stop or prompt-and-start this coordinator began, for the tests to await.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Begins watching when the setting is on, and follows the setting from now on.</summary>
    public void Start()
    {
        if (started || disposed) return;
        started = true;
        settings.PropertyChanged += OnSettingsChanged;
        if (settings.AutoRecordTeamsMeetings) StartWatching();
    }

    /// <summary>
    /// One probe result at <paramref name="now"/>: the timer's decision step,
    /// which the tests drive directly. Does nothing while the setting is off.
    /// </summary>
    internal void Apply(IReadOnlyList<CaptureSession> sessions, DateTimeOffset now)
    {
        if (disposed || !settings.AutoRecordTeamsMeetings) return;
        switch (detector.Observe(sessions, now))
        {
            case MeetingEvent.Started:
                MeetingStarted();
                break;
            case MeetingEvent.Ended:
                MeetingEnded();
                break;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopWatching();
        CancelPrompt();
        controller.PropertyChanged -= OnControllerChanged;
        if (started) settings.PropertyChanged -= OnSettingsChanged;
    }

    // MARK: - Decisions

    private void MeetingStarted()
    {
        if (!controller.CanStart)
        {
            AppLog.Write("teams: meeting started while a recording runs; it stays manual");
            return;
        }
        if (prompting is not null)
        {
            AppLog.Write("teams: meeting started while the prompt is open");
            return;
        }
        if (settings.AutoRecordAsksLanguage)
        {
            AppLog.Write("teams: meeting started; asking which language to use");
            Pending = AskThenStartAsync();
            return;
        }
        AppLog.Write("teams: meeting started; starting a recording");
        StartAutomaticSession();
    }

    private void MeetingEnded()
    {
        if (prompting is not null)
        {
            AppLog.Write("teams: meeting ended before the prompt was answered; nothing recorded");
            CancelPrompt();
        }
        if (automatic && controller.IsSessionActive)
        {
            AppLog.Write("teams: meeting ended; stopping the automatic recording");
            stoppingForMeetingEnd = true;
            notify(Strings.MeetingBalloonStoppedTitle, Strings.MeetingBalloonStoppedText);
            Pending = controller.StopAsync();
            return;
        }
        AppLog.Write("teams: meeting ended");
    }

    private async Task AskThenStartAsync()
    {
        var cancel = new CancellationTokenSource();
        prompting = cancel;
        LanguageChoice? choice = null;
        try
        {
            choice = await prompt(cancel.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            choice = null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"teams: the prompt failed: {error.Message}");
            choice = null;
        }
        var cancelled = cancel.IsCancellationRequested;
        if (ReferenceEquals(prompting, cancel)) prompting = null;
        cancel.Dispose();
        if (cancelled || disposed) return;
        if (choice is not { } language)
        {
            AppLog.Write("teams: not recording this meeting (the prompt was declined)");
            return;
        }
        if (!settings.AutoRecordTeamsMeetings || !detector.IsInMeeting)
        {
            AppLog.Write("teams: the prompt was answered after watching stopped; nothing recorded");
            return;
        }
        if (!controller.CanStart)
        {
            AppLog.Write("teams: a recording started while the prompt was open; it stays manual");
            return;
        }
        controller.LanguageChoice = language;
        AppLog.Write($"teams: starting a recording in {language.StorageValue}");
        StartAutomaticSession();
    }

    private void StartAutomaticSession()
    {
        automatic = true;
        awaitingStart = true;
        stoppingForMeetingEnd = false;
        controller.Start();
        if (controller.IsSessionActive) controller.AutomaticStartNotice = Strings.AutomaticRecordingNotice;
        SettleStart();
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!automatic) return;
        if (e.PropertyName is not (nameof(RecordingController.Phase) or null)) return;
        if (awaitingStart)
        {
            SettleStart();
            return;
        }
        if (controller.IsSessionActive) return;
        automatic = false;
        AppLog.Write(stoppingForMeetingEnd
            ? "teams: the automatic recording stopped"
            : "teams: the automatic recording ended (stopped by the user, failed, or quit); waiting for the next meeting");
        stoppingForMeetingEnd = false;
    }

    /// <summary>The automatic start reached recording (balloon), failed (balloon, no retry), or ended without a session.</summary>
    private void SettleStart()
    {
        if (!awaitingStart) return;
        switch (controller.Phase)
        {
            case ControllerPhase.Recording or ControllerPhase.Paused:
                awaitingStart = false;
                AppLog.Write("teams: the automatic recording runs");
                notify(Strings.MeetingBalloonStartedTitle, Strings.MeetingBalloonStartedText);
                break;
            case ControllerPhase.Failed failed:
                awaitingStart = false;
                automatic = false;
                AppLog.Write($"teams: the automatic recording could not start: {failed.Message}");
                notify(Strings.MeetingBalloonFailedTitle, Strings.MeetingBalloonFailedText);
                break;
            case ControllerPhase.Idle:
                awaitingStart = false;
                automatic = false;
                AppLog.Write("teams: the automatic recording did not start");
                break;
        }
    }

    // MARK: - Watching

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.AutoRecordTeamsMeetings) or null)) return;
        Post(ApplySetting);
    }

    private void ApplySetting()
    {
        if (disposed) return;
        if (settings.AutoRecordTeamsMeetings)
        {
            StartWatching();
            return;
        }
        if (!IsWatching && !automatic && prompting is null) return;
        StopWatching();
        CancelPrompt();
        // A running automatic session goes on; the user stops it.
        automatic = false;
        awaitingStart = false;
        detector = new MeetingDetector();
        AppLog.Write("teams: watching stopped (setting off)");
    }

    private void StartWatching()
    {
        if (watching is not null || disposed) return;
        var cancel = new CancellationTokenSource();
        watching = cancel;
        Interlocked.Exchange(ref probeFailureLogged, 0);
        AppLog.Write("teams: watching for meetings");
        _ = Task.Run(() => WatchAsync(cancel.Token));
    }

    private void StopWatching()
    {
        if (watching is not { } cancel) return;
        watching = null;
        // Not disposed: the loop may still read its token once more.
        cancel.Cancel();
    }

    private void CancelPrompt()
    {
        if (prompting is not { } cancel) return;
        prompting = null;
        cancel.Cancel();
    }

    /// <summary>The timer loop on the thread pool: probe, then hand the result to the UI thread.</summary>
    private async Task WatchAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                IReadOnlyList<CaptureSession> sessions;
                try
                {
                    sessions = probe();
                    Interlocked.Exchange(ref probeFailureLogged, 0);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (Interlocked.Exchange(ref probeFailureLogged, 1) == 0)
                    {
                        AppLog.Write($"teams: cannot list the capture sessions: {error.Message}");
                    }
                    continue;
                }
                var now = clock();
                Post(() =>
                {
                    if (!token.IsCancellationRequested) Apply(sessions, now);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Watching stopped.
        }
    }

    private void Post(Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }
}
