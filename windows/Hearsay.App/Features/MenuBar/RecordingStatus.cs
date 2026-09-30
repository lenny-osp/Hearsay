using System.ComponentModel;
using System.Globalization;
using Hearsay.Core.Audio;

namespace Hearsay.App.Features.MenuBar;

/// <summary>The recording phases the tray reflects (the Mac's <c>RecordingController.Phase</c> as <c>MenuBarView</c> reads it).</summary>
internal enum RecordingPhase
{
    Idle,
    Recording,
    Paused,
    Starting,
    Stopping,
    Transcribing,
}

/// <summary>
/// What the tray icon, its menu, the global hotkeys and History read from the
/// recording (the Mac's <c>MenuBarView</c> and <c>MenuBarLabel</c> read
/// mac/Hearsay/Features/Recording/RecordingController.swift directly). The
/// app's <c>RecordingController</c> pushes its state here with
/// <see cref="Update"/> and <see cref="SetBusyFiles"/>; Start / Stop and
/// Pause / Resume from the tray and the hotkeys go to the controller through
/// <see cref="Connect"/>. Use from the UI thread.
/// </summary>
internal sealed class RecordingStatus : INotifyPropertyChanged
{
    private Action? startStop;
    private Action? pause;
    private Action? stopStartNext;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecordingPhase Phase { get; private set; } = RecordingPhase.Idle;

    /// <summary>Recorded time, paused time excluded.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>The final pass's progress, 0...1, while transcribing.</summary>
    public double? TranscriptionProgress { get; private set; }

    /// <summary>Recording or paused: Stop and Pause / Resume apply.</summary>
    public bool IsCapturing => Phase is RecordingPhase.Recording or RecordingPhase.Paused;

    /// <summary>
    /// The recording's WAV and SRT while it is recorded or transcribed (the
    /// Mac's <c>finishedRecording</c> and <c>finishedTranscript</c> while
    /// <c>isSessionActive || isTranscribing</c>): History turns Rename off
    /// for them (PLAN.md 4.8).
    /// </summary>
    public IReadOnlyList<string> BusyFiles { get; private set; } = [];

    /// <summary>The files of the recording being captured or transcribed; empty when done.</summary>
    public void SetBusyFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.SequenceEqual(BusyFiles, StringComparer.OrdinalIgnoreCase)) return;
        BusyFiles = files;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BusyFiles)));
    }

    /// <summary>Recordings not transcribed yet (the queue's <c>PendingCount</c>).</summary>
    public int QueuePending { get; private set; }

    /// <summary>The running job's progress, 0...1, whether or not a session runs; null when no job runs.</summary>
    public double? QueueProgress { get; private set; }

    /// <summary>The queue waits for the recording to stop (timing "when no recording is running").</summary>
    public bool QueueHeld { get; private set; }

    /// <summary>Starting, recording, paused or stopping.</summary>
    public bool IsSessionActive => Phase is RecordingPhase.Starting or RecordingPhase.Recording or RecordingPhase.Paused or RecordingPhase.Stopping;

    /// <summary>Stop &amp; Start Next applies (the tray item is enabled): recording or paused.</summary>
    public bool CanStopAndStartNext => IsCapturing;

    /// <summary>
    /// The queue's state, pushed by the shell. Raises <see cref="QueueLine"/>
    /// when the line of the tray menu changed.
    /// </summary>
    public void SetQueue(int pending, double? activeProgress, bool held)
    {
        var before = QueueLine;
        QueuePending = pending;
        QueueProgress = activeProgress;
        QueueHeld = held;
        if (QueueLine != before) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueLine)));
    }

    /// <summary>
    /// One line for the queue in the tray menu (the Mac panel's
    /// <c>queueLine</c>): how many recordings are not transcribed yet and how
    /// far the current one is. Null when the state line already says it all
    /// (one job, no session) or the queue is empty.
    /// </summary>
    public string? QueueLine
    {
        get
        {
            if (QueuePending <= 0) return null;
            if (!IsSessionActive && QueueProgress is not null)
            {
                // The state line already shows the percentage.
                return QueuePending > 1 ? Strings.QueueLineCount(QueuePending) : null;
            }
            if (QueueHeld) return Strings.QueueLinePaused(QueuePending);
            if (QueueProgress is { } progress) return Strings.QueueLineTranscribing(Percent(progress), QueuePending);
            return Strings.QueueLineWaiting(QueuePending);
        }
    }

    private static int Percent(double fraction) => (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);

    /// <summary>The state line of the tray menu, the Mac's <c>MenuBarView.stateText</c>.</summary>
    public string StateText
    {
        get
        {
            var elapsed = LevelMeter.FormatElapsed(Elapsed.TotalSeconds);
            return Phase switch
            {
                RecordingPhase.Recording => Strings.StateRecording(elapsed),
                RecordingPhase.Paused => Strings.StatePaused(elapsed),
                RecordingPhase.Starting => Strings.StartingState,
                RecordingPhase.Stopping => Strings.Saving,
                RecordingPhase.Transcribing => Strings.StateTranscribing(Percent(TranscriptionProgress ?? 0)),
                _ => Strings.StateIdle,
            };
        }
    }

    /// <summary>Routes the tray and hotkey commands to the recording controller.</summary>
    public void Connect(Action toggleStartStop, Action togglePause, Action? stopAndStartNext = null)
    {
        startStop = toggleStartStop;
        pause = togglePause;
        stopStartNext = stopAndStartNext;
    }

    /// <summary>The Stop &amp; Start Next hotkey and menu command (<c>stopAndStartNext()</c>); the controller ignores it unless recording.</summary>
    public void StopAndStartNext()
    {
        AppLog.Write($"recording: stop and start next command in {Phase}");
        stopStartNext?.Invoke();
    }

    /// <summary>The Start / Stop hotkey and menu command (<c>toggleStartStop()</c>).</summary>
    public void ToggleStartStop()
    {
        AppLog.Write($"recording: start/stop command in {Phase}");
        startStop?.Invoke();
    }

    /// <summary>The Pause / Resume hotkey and menu command (<c>togglePause()</c>); nothing while idle.</summary>
    public void TogglePause()
    {
        AppLog.Write($"recording: pause/resume command in {Phase}");
        pause?.Invoke();
    }

    /// <summary>
    /// The controller's state. Raises <see cref="Phase"/> when the phase
    /// changed, else <see cref="Elapsed"/> when the shown second or percent
    /// changed, else nothing.
    /// </summary>
    public void Update(RecordingPhase phase, TimeSpan elapsed, double? progress)
    {
        var before = StateText;
        var phaseChanged = phase != Phase;
        Phase = phase;
        Elapsed = elapsed;
        TranscriptionProgress = progress;
        if (phaseChanged)
        {
            AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"recording: state {phase}"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Phase)));
        }
        else if (StateText != before)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Elapsed)));
        }
    }

    /// <summary>Called once a second by the shell so the tooltip stays current while capturing.</summary>
    public void Tick()
    {
        if (IsCapturing) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Elapsed)));
    }
}
