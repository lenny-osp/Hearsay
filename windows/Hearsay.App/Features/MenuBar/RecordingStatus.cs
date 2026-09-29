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
                RecordingPhase.Transcribing => Strings.StateTranscribing((int)Math.Round((TranscriptionProgress ?? 0) * 100, MidpointRounding.AwayFromZero)),
                _ => Strings.StateIdle,
            };
        }
    }

    /// <summary>Routes the tray and hotkey commands to the recording controller.</summary>
    public void Connect(Action toggleStartStop, Action togglePause)
    {
        startStop = toggleStartStop;
        pause = togglePause;
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
