using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Hearsay.Core.Audio;

/// <summary>
/// Why system audio capture could not start or stopped on its own. Port of
/// the cases of <c>SystemAudioRecorderError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/SystemAudioRecorder.swift.
/// </summary>
/// <remarks>
/// WASAPI loopback needs no permission, so there is no <c>permissionDenied</c>;
/// <c>noDisplay</c> becomes <see cref="NoOutputDevice"/> because Windows
/// captures the default output device, not a display.
/// </remarks>
public enum SystemAudioRecorderErrorKind
{
    NoOutputDevice,
    StartFailed,

    /// <summary>Loopback capture stopped and could not be reopened.</summary>
    StreamStopped,
    InvalidState,
}

/// <summary>
/// A <see cref="SystemAudioRecorderErrorKind"/> with its detail; the message
/// is the English catalog text (W7 wires the translations).
/// </summary>
public sealed class SystemAudioRecorderException : Exception
{
    public SystemAudioRecorderException()
        : this(SystemAudioRecorderErrorKind.InvalidState, "unknown error")
    {
    }

    public SystemAudioRecorderException(string message)
        : this(SystemAudioRecorderErrorKind.InvalidState, message)
    {
    }

    public SystemAudioRecorderException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = SystemAudioRecorderErrorKind.InvalidState;
        Detail = message;
    }

    public SystemAudioRecorderException(SystemAudioRecorderErrorKind kind, string detail = "")
        : base(Text(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public SystemAudioRecorderErrorKind Kind { get; }

    public string Detail { get; } = "";

    private static string Text(SystemAudioRecorderErrorKind kind, string detail) => kind switch
    {
        // Windows-only text (the Mac says "No display is available ...").
        SystemAudioRecorderErrorKind.NoOutputDevice => "No output device is available to capture system audio from.",
        // "from Windows" where the Mac says "from macOS" in the translator comment only.
        SystemAudioRecorderErrorKind.StartFailed => $"System audio capture could not start: {detail}",
        SystemAudioRecorderErrorKind.StreamStopped => $"System audio capture stopped: {detail}",
        _ => detail,
    };
}

/// <summary>
/// Records the audio Windows plays (other apps, calls) through WASAPI
/// loopback of the default output device and delivers 16 kHz mono Float32
/// chunks (PLAN.md 4.1, step 2). Port of <c>SystemAudioRecorder</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/SystemAudioRecorder.swift.
/// </summary>
/// <remarks>
/// <para>Same output contract as <see cref="MicrophoneRecorder"/>: a
/// <see cref="ChunkFanout"/> with an untimed and a host-timed stream, thread-safe
/// public members. One recorder makes one recording. Pause keeps capture
/// running and drops the buffers, so resuming is instant, as on the Mac.</para>
/// <para>Differences from the Mac (PLAN.md 18.4):</para>
/// <list type="bullet">
/// <item>Loopback delivers no packets while nothing plays; ScreenCaptureKit
/// delivers continuous buffers. The session makes up the missing frames as
/// silence (<see cref="SilenceGapFiller"/>: after a 0.2 s gap, checked every
/// 0.1 s) so the mixer's system lane and its meter stay continuous.</item>
/// <item>Loopback includes Hearsay's own output; the Mac excludes it
/// (<c>excludesCurrentProcessAudio</c>). Hearsay plays no audio while
/// recording.</item>
/// <item>When the default output device changes (headset plugged in) the
/// capture moves to the new default, and when capture fails (the device was
/// removed or its format changed) it is reopened on the current default, at
/// most <see cref="ConfigurationRecovery.MaxRecoveries"/> times per
/// <see cref="ConfigurationRecovery.WindowSeconds"/>. The Mac's stream is tied
/// to the main display and simply ends with <c>streamStopped</c> when
/// ScreenCaptureKit stops it; here the recording ends with
/// <see cref="SystemAudioRecorderErrorKind.StreamStopped"/> only when there is
/// no output device left or the device keeps changing.</item>
/// </list>
/// </remarks>
public sealed class SystemAudioRecorder
{
    private readonly Lock gate = new();
    private readonly Func<CaptureReceiver, ICaptureSession> openSession;
    private readonly DiagnosticsBox diagnosticsBox = new();
    private readonly ConfigurationRecovery recovery = new();
    private ICaptureSession? session;

    /// <summary>A recorder on WASAPI loopback.</summary>
    [SupportedOSPlatform("windows")]
    public SystemAudioRecorder()
        : this(WasapiCaptureSession.OpenLoopback)
    {
    }

    /// <summary>A recorder whose capture session comes from <paramref name="openSession"/> (tests).</summary>
    internal SystemAudioRecorder(Func<CaptureReceiver, ICaptureSession> openSession)
    {
        this.openSession = openSession;
    }

    public ChannelReader<float[]> Samples => Output.Samples;

    /// <summary>The same chunks stamped with host time (see <see cref="TimedChunk"/>).</summary>
    public ChannelReader<TimedChunk> TimedSamples => Output.TimedSamples;

    public RecorderState State { get; private set; } = RecorderState.Idle;

    /// <summary>Set when capture stopped on its own.</summary>
    public SystemAudioRecorderException? Failure { get; private set; }

    /// <summary>Times capture moved to another output device or was reopened after an error.</summary>
    public int ConfigurationRecoveries { get; private set; }

    /// <summary>What capture has seen so far (Windows only; the Mac keeps none for system audio).</summary>
    public MicrophoneDiagnostics Diagnostics => diagnosticsBox.Value;

    /// <summary>The output device being captured (the last one once stopped), or null before start.</summary>
    public string? DeviceName
    {
        get
        {
            lock (gate)
            {
                return session?.DeviceName ?? diagnosticsBox.Value.DeviceName;
            }
        }
    }

    internal ChunkFanout Output { get; } = new();

    /// <exception cref="SystemAudioRecorderException">There is no output device, or capture cannot start.</exception>
    public void Start()
    {
        lock (gate)
        {
            if (State != RecorderState.Idle)
            {
                throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.InvalidState,
                    "This recorder has already been started.");
            }
            var receiver = new CaptureReceiver(Output, diagnosticsBox);
            ICaptureSession opened;
            try
            {
                opened = openSession(receiver);
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            {
                throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.StartFailed,
                    CaptureErrorText.Describe(error));
            }
            try
            {
                opened.Start();
            }
            catch (Exception error) when (IsCaptureFailure(error))
            {
                opened.Dispose();
                throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.StartFailed,
                    CaptureErrorText.Describe(error));
            }
            catch (SystemAudioRecorderException)
            {
                opened.Dispose();
                throw;
            }
            var name = opened.DeviceName;
            var deviceFormat = opened.DeviceFormat ?? opened.MixFormat;
            diagnosticsBox.Update(value => value with
            {
                DeviceName = name,
                DeviceNominalSampleRate = deviceFormat?.SampleRate,
                RequestedFormat = "endpoint mix format, shared-mode loopback",
                DeviceFormat = deviceFormat is CaptureFormat format ? MicrophoneDiagnostics.Describe(format) : null,
            });
            session = opened;
            opened.RuntimeError += OnRuntimeError;
            opened.DefaultDeviceChanged += OnDefaultDeviceChanged;
            State = RecorderState.Recording;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (State != RecorderState.Recording)
            {
                return;
            }
            Output.Pause(HostClock.NowSeconds());
            State = RecorderState.Paused;
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (State != RecorderState.Paused)
            {
                return;
            }
            Output.Resume(HostClock.NowSeconds());
            State = RecorderState.Recording;
        }
    }

    /// <summary>Ends the recording. Buffers already captured are still delivered, then the streams complete.</summary>
    public void Stop()
    {
        lock (gate)
        {
            Finish(null);
        }
    }

    /// <summary>
    /// Moves capture to the current default output: reopens it when there is
    /// one and recovery is allowed, else ends the recording with
    /// <see cref="SystemAudioRecorderErrorKind.StreamStopped"/> and
    /// <paramref name="reason"/>. Returns whether capture goes on.
    /// </summary>
    internal bool HandleDeviceChange(string reason, double? now = null)
    {
        lock (gate)
        {
            if (State is not (RecorderState.Recording or RecorderState.Paused) || session is not ICaptureSession current)
            {
                return false;
            }
            if (!current.IsDeviceActive())
            {
                Finish(new SystemAudioRecorderException(SystemAudioRecorderErrorKind.StreamStopped, reason));
                return false;
            }
            if (!recovery.AllowRecovery(now ?? HostClock.NowSeconds()))
            {
                Finish(new SystemAudioRecorderException(SystemAudioRecorderErrorKind.StreamStopped,
                    "the output device kept changing"));
                return false;
            }
            try
            {
                // Pause keeps capture running, so it restarts in either state.
                current.Reinstall();
                current.Start();
                var name = current.DeviceName;
                diagnosticsBox.Update(value => value with { DeviceName = name });
            }
            catch (Exception error)
            {
                Finish(new SystemAudioRecorderException(SystemAudioRecorderErrorKind.StreamStopped,
                    error is SystemAudioRecorderException known ? known.Message : CaptureErrorText.Describe(error)));
                return false;
            }
            ConfigurationRecoveries++;
            return true;
        }
    }

    /// <summary>What WASAPI and NAudio throw when capture cannot start (E_ACCESSDENIED can surface as UnauthorizedAccessException).</summary>
    private static bool IsCaptureFailure(Exception error) =>
        error is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private void OnRuntimeError(string message)
    {
        diagnosticsBox.Update(value => value with
        {
            RuntimeErrors = value.RuntimeErrors + 1,
            LastRuntimeError = message,
        });
        HandleDeviceChange(message);
    }

    private void OnDefaultDeviceChanged() => HandleDeviceChange("the output device changed");

    /// <summary>Call with <see cref="gate"/> held.</summary>
    private void Finish(SystemAudioRecorderException? failure)
    {
        switch (State)
        {
            case RecorderState.Stopped:
                return;
            case RecorderState.Idle:
                State = RecorderState.Stopped;
                Output.Finish();
                return;
            default:
                break;
        }
        Failure = failure;
        State = RecorderState.Stopped;
        if (session is ICaptureSession current)
        {
            current.RuntimeError -= OnRuntimeError;
            current.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            session = null;
            var output = Output;
            current.StopAndDrain(() =>
            {
                output.Finish();
                current.Dispose();
            });
        }
        else
        {
            Output.Finish();
        }
    }
}
