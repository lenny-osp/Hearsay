using System.ComponentModel;
using System.Diagnostics;
using Hearsay.Core.Audio;

namespace Hearsay.App.Features.MenuBar;

/// <summary>The recording phases the tray reflects (a subset of the Mac's <c>RecordingController.Phase</c>).</summary>
internal enum RecordingPhase
{
    Idle,
    Recording,
    Paused,
}

/// <summary>
/// A stand-in for the Mac's <c>RecordingController</c>
/// (mac/Hearsay/Features/Recording/RecordingController.swift) until W5 wires
/// the real recorder: Start / Stop and Pause / Resume from the tray menu and
/// the global hotkeys only move this state and its elapsed time, and log the
/// command. The tray icon, tooltip and menu read it the way the Mac's
/// <c>MenuBarView</c> and <c>MenuBarLabel</c> read the controller.
/// Use from the UI thread.
/// </summary>
internal sealed class RecordingStatus : INotifyPropertyChanged
{
    private readonly Stopwatch clock = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecordingPhase Phase { get; private set; } = RecordingPhase.Idle;

    /// <summary>Recorded time, paused time excluded.</summary>
    public TimeSpan Elapsed => clock.Elapsed;

    /// <summary>Recording or paused: Stop and Pause / Resume apply.</summary>
    public bool IsCapturing => Phase != RecordingPhase.Idle;

    /// <summary>
    /// The recording's WAV and SRT while it is recorded or transcribed (the
    /// Mac's <c>finishedRecording</c> and <c>finishedTranscript</c> while
    /// <c>isSessionActive || isTranscribing</c>): History turns Rename off
    /// for them (PLAN.md 4.8). Empty until W5 sets it with <see cref="SetBusyFiles"/>.
    /// </summary>
    public IReadOnlyList<string> BusyFiles { get; private set; } = [];

    /// <summary>W5: the files of the recording being captured or transcribed; empty when done.</summary>
    public void SetBusyFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
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
                _ => Strings.StateIdle,
            };
        }
    }

    /// <summary>The Start / Stop hotkey and menu command (<c>toggleStartStop()</c>).</summary>
    public void ToggleStartStop()
    {
        if (IsCapturing)
        {
            clock.Reset();
            SetPhase(RecordingPhase.Idle, "stop");
        }
        else
        {
            clock.Restart();
            SetPhase(RecordingPhase.Recording, "start");
        }
    }

    /// <summary>The Pause / Resume hotkey and menu command (<c>togglePause()</c>); nothing while idle.</summary>
    public void TogglePause()
    {
        switch (Phase)
        {
            case RecordingPhase.Recording:
                clock.Stop();
                SetPhase(RecordingPhase.Paused, "pause");
                break;
            case RecordingPhase.Paused:
                clock.Start();
                SetPhase(RecordingPhase.Recording, "resume");
                break;
            default:
                AppLog.Write("recording: pause ignored while idle (recording arrives in W5)");
                break;
        }
    }

    /// <summary>Called once a second while capturing so the elapsed time refreshes.</summary>
    public void Tick()
    {
        if (IsCapturing) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Elapsed)));
    }

    private void SetPhase(RecordingPhase phase, string command)
    {
        Phase = phase;
        AppLog.Write($"recording: {command} (stand-in until W5), state {phase}");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Phase)));
    }
}
