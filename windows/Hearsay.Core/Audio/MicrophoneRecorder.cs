using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Hearsay.Core.Audio;

/// <summary>Where a recorder is in its one recording.</summary>
public enum RecorderState
{
    Idle,
    Recording,
    Paused,
    Stopped,
}

/// <summary>
/// Why a recording could not start or stopped on its own. Port of the cases
/// of <c>MicrophoneRecorderError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MicrophoneRecorder.swift.
/// </summary>
/// <remarks>
/// <c>cannotSelectDevice</c> has no Windows case: WASAPI opens an endpoint by
/// id, and a failure there is <see cref="DeviceNotFound"/> or
/// <see cref="EngineFailed"/>.
/// </remarks>
public enum MicrophoneRecorderErrorKind
{
    PermissionDenied,
    NoInputDevice,

    /// <summary>The chosen endpoint is not present or not active.</summary>
    DeviceNotFound,
    EngineFailed,

    /// <summary>Nothing arrived within <see cref="NoAudioWatchdog.Timeout"/> seconds of recording.</summary>
    NoAudio,

    /// <summary>The input disappeared, could not be restarted after a change, or kept changing.</summary>
    ConfigurationChanged,
    InvalidState,
}

/// <summary>
/// A <see cref="MicrophoneRecorderErrorKind"/> with its details. Thrown by
/// <see cref="MicrophoneRecorder.Start"/> and kept in
/// <see cref="MicrophoneRecorder.Failure"/>. The message is the English
/// catalog text of the Swift case (W7 wires the translations).
/// </summary>
public sealed class MicrophoneRecorderException : Exception
{
    public MicrophoneRecorderException()
        : this(MicrophoneRecorderErrorKind.InvalidState, "unknown error")
    {
    }

    public MicrophoneRecorderException(string message)
        : this(MicrophoneRecorderErrorKind.InvalidState, message)
    {
    }

    public MicrophoneRecorderException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = MicrophoneRecorderErrorKind.InvalidState;
        Detail = message;
    }

    /// <param name="kind">What went wrong.</param>
    /// <param name="detail">The device name for <see cref="MicrophoneRecorderErrorKind.DeviceNotFound"/>
    /// and <see cref="MicrophoneRecorderErrorKind.NoAudio"/>, technical English for
    /// <see cref="MicrophoneRecorderErrorKind.EngineFailed"/> and
    /// <see cref="MicrophoneRecorderErrorKind.InvalidState"/>, else unused.</param>
    public MicrophoneRecorderException(MicrophoneRecorderErrorKind kind, string detail = "")
        : base(Text(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public MicrophoneRecorderErrorKind Kind { get; }

    public string Detail { get; } = "";

    private static string Text(MicrophoneRecorderErrorKind kind, string detail) => kind switch
    {
        // Windows wording for the privacy setting (the Mac names System Settings).
        MicrophoneRecorderErrorKind.PermissionDenied =>
            "Hearsay has no microphone access. Allow it in Settings > Privacy & security > Microphone (\"Let desktop apps access your microphone\").",
        MicrophoneRecorderErrorKind.NoInputDevice => "No input device is available.",
        MicrophoneRecorderErrorKind.DeviceNotFound =>
            $"{detail} is not available for recording. Reconnect it or choose another input.",
        MicrophoneRecorderErrorKind.EngineFailed => $"Audio capture failed: {detail}",
        MicrophoneRecorderErrorKind.NoAudio => string.Create(CultureInfo.InvariantCulture,
            $"No audio from {detail}. Nothing arrived within {(int)NoAudioWatchdog.Timeout} seconds, so the recording was stopped. Check that the device is connected and not muted, or choose another input."),
        MicrophoneRecorderErrorKind.ConfigurationChanged =>
            "The input device changed or was disconnected, so recording stopped.",
        _ => detail,
    };
}

/// <summary>
/// What the capture path saw, for diagnosing a device that records silence.
/// Port of <c>MicrophoneDiagnostics</c> in MicrophoneRecorder.swift; a value
/// type as in Swift.
/// </summary>
public record struct MicrophoneDiagnostics
{
    public MicrophoneDiagnostics()
    {
    }

    public string? DeviceName { get; set; }

    /// <summary>Sample rate of the endpoint's device format when recording started (the CoreAudio nominal rate on the Mac).</summary>
    public double? DeviceNominalSampleRate { get; set; }

    /// <summary>The format capture was asked for.</summary>
    public string? RequestedFormat { get; set; }

    /// <summary>The endpoint's device format when recording started.</summary>
    public string? DeviceFormat { get; set; }

    /// <summary>The format of the first buffer that arrived.</summary>
    public string? DeliveredFormat { get; set; }

    /// <summary>Buffers delivered by the capture callback.</summary>
    public int Callbacks { get; set; }

    public long FramesReceived { get; set; }

    /// <summary>16 kHz samples handed to the sample streams.</summary>
    public long SamplesDelivered { get; set; }

    public int ConversionFailures { get; set; }

    public string? LastConversionError { get; set; }

    public int RuntimeErrors { get; set; }

    public string? LastRuntimeError { get; set; }

    /// <summary>Always 0 on Windows: WASAPI has no interruption notification (see PLAN.md 18.4).</summary>
    public int Interruptions { get; set; }

    /// <summary>
    /// Frames of silence made up at the capture rate because loopback
    /// delivered nothing (system audio only; not in <see cref="Description"/>,
    /// which matches the Mac line for line).
    /// </summary>
    public long SilenceFramesSynthesized { get; set; }

    /// <summary>Host time now minus the stamped host time of the newest chunk's end, in seconds.</summary>
    public double? TimestampLagSeconds { get; set; }

    /// <summary>The multi-line text the Mac prints, line for line.</summary>
    public readonly string Description
    {
        get
        {
            static string Rate(double? value) =>
                value is double rate ? string.Create(CultureInfo.InvariantCulture, $"{rate:F0} Hz") : "unknown";
            var conversion = LastConversionError is string conversionError ? $" (last: {conversionError})" : "";
            var runtime = LastRuntimeError is string runtimeError ? $" (last: {runtimeError})" : "";
            var lag = TimestampLagSeconds is double seconds
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds:F3} s")
                : "n/a";
            return string.Join("\n",
                $"device: {DeviceName ?? "system default"}",
                $"device nominal rate: {Rate(DeviceNominalSampleRate)}",
                $"requested format: {RequestedFormat ?? "none"}",
                $"device format: {DeviceFormat ?? "unknown"}",
                $"delivered format: {DeliveredFormat ?? "none"}",
                string.Create(CultureInfo.InvariantCulture,
                    $"callbacks: {Callbacks}, frames: {FramesReceived}, samples delivered: {SamplesDelivered}"),
                string.Create(CultureInfo.InvariantCulture, $"conversion failures: {ConversionFailures}{conversion}"),
                string.Create(CultureInfo.InvariantCulture,
                    $"runtime errors: {RuntimeErrors}{runtime}, interruptions: {Interruptions}"),
                $"timestamp lag: {lag}");
        }
    }

    public override readonly string ToString() => Description;

    /// <summary>
    /// "2 ch, 48000 Hz, Float32 interleaved". Port of
    /// <c>MicrophoneDiagnostics.describe(_:)</c>; WASAPI shared mode is always
    /// interleaved.
    /// </summary>
    public static string Describe(CaptureFormat format) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{format.Channels} ch, {format.SampleRate} Hz, {(format.IsFloat ? "Float" : "Int")}{format.BitsPerSample} interleaved");
}

/// <summary>
/// Records one input endpoint through WASAPI and delivers 16 kHz mono
/// Float32 chunks (PLAN.md 4.1). Port of <c>MicrophoneRecorder</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MicrophoneRecorder.swift.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> The Swift class is <c>@MainActor</c>. Here every
/// public member is thread-safe (one lock); capture buffers arrive on NAudio's
/// capture thread and only touch the thread-safe <see cref="ChunkFanout"/>,
/// and session events arrive on the thread pool.</para>
/// <para>One recorder makes one recording: the sample streams complete on
/// <see cref="Stop"/> or when the device goes away, and a new recording needs
/// a new recorder. When they complete without <see cref="Stop"/> having been
/// called, <see cref="Failure"/> says why. The recorder reports its failure
/// before completing the stream, as the W2 mixer expects (PLAN.md 18.4).</para>
/// <para><b>Recovery.</b> A capture error (WASAPI's
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c> when the endpoint's format changes or
/// the audio service restarts, or any other failure of the capture thread)
/// reopens the endpoint and starts again, at most
/// <see cref="ConfigurationRecovery.MaxRecoveries"/> times per
/// <see cref="ConfigurationRecovery.WindowSeconds"/>, so a failing device ends
/// the recording instead of looping. The chosen endpoint being removed,
/// disabled or unplugged ends the recording with
/// <see cref="MicrophoneRecorderErrorKind.ConfigurationChanged"/> rather than
/// silently falling back to another microphone. A device that never
/// delivers is caught by <see cref="NoAudioWatchdog"/> in the app, as on the
/// Mac.</para>
/// <para><see cref="TimedSamples"/> carries the chunks stamped with host
/// time, for <see cref="AudioMixer"/>. Read a recording through one of the two
/// streams: the first one accessed claims the chunks.</para>
/// </remarks>
public sealed class MicrophoneRecorder
{
    private readonly Lock gate = new();
    private readonly Func<AudioInputDevice?, CaptureReceiver, ICaptureSession> openSession;
    private readonly DiagnosticsBox diagnosticsBox = new();
    private readonly ConfigurationRecovery recovery = new();
    private ICaptureSession? session;

    /// <summary>A recorder on WASAPI.</summary>
    [SupportedOSPlatform("windows")]
    public MicrophoneRecorder()
        : this(WasapiCaptureSession.OpenMicrophone)
    {
    }

    /// <summary>A recorder whose capture session comes from <paramref name="openSession"/> (tests).</summary>
    internal MicrophoneRecorder(Func<AudioInputDevice?, CaptureReceiver, ICaptureSession> openSession)
    {
        this.openSession = openSession;
    }

    /// <summary>16 kHz mono Float32 chunks, in capture order.</summary>
    public ChannelReader<float[]> Samples => Output.Samples;

    /// <summary>The same chunks, each stamped with the host time of its first sample, paused time removed.</summary>
    public ChannelReader<TimedChunk> TimedSamples => Output.TimedSamples;

    public RecorderState State { get; private set; } = RecorderState.Idle;

    /// <summary>Set when the recorder stopped on its own.</summary>
    public MicrophoneRecorderException? Failure { get; private set; }

    /// <summary>Capture errors survived by reopening the endpoint.</summary>
    public int ConfigurationRecoveries { get; private set; }

    /// <summary>What capture has seen so far.</summary>
    public MicrophoneDiagnostics Diagnostics => diagnosticsBox.Value;

    internal ChunkFanout Output { get; } = new();

    /// <summary>
    /// Starts recording from <paramref name="device"/>, or from the system
    /// default input when null. Returns once capture runs; the first samples
    /// arrive a little later (Bluetooth headsets can take a second to switch
    /// to their microphone profile).
    /// </summary>
    /// <exception cref="MicrophoneRecorderException">The device is missing, access is denied, or capture cannot start.</exception>
    public void Start(AudioInputDevice? device)
    {
        lock (gate)
        {
            if (State != RecorderState.Idle)
            {
                throw new MicrophoneRecorderException(MicrophoneRecorderErrorKind.InvalidState,
                    "This recorder has already been started.");
            }
            var receiver = new CaptureReceiver(Output, diagnosticsBox);
            ICaptureSession opened;
            try
            {
                opened = openSession(device, receiver);
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            {
                throw MapStartError(error);
            }
            try
            {
                opened.Start();
            }
            catch (Exception error) when (IsCaptureFailure(error))
            {
                opened.Dispose();
                throw MapStartError(error);
            }
            var name = opened.DeviceName;
            var deviceFormat = opened.DeviceFormat ?? opened.MixFormat;
            diagnosticsBox.Update(value => value with
            {
                DeviceName = name,
                DeviceNominalSampleRate = deviceFormat?.SampleRate,
                RequestedFormat = WasapiRequestedFormat,
                DeviceFormat = deviceFormat is CaptureFormat format ? MicrophoneDiagnostics.Describe(format) : null,
            });
            Attach(opened);
            State = RecorderState.Recording;
        }
    }

    /// <summary>Stops capture without ending the recording; no samples arrive while paused.</summary>
    public void Pause()
    {
        lock (gate)
        {
            if (State != RecorderState.Recording)
            {
                return;
            }
            session?.Stop();
            Output.Pause(HostClock.NowSeconds());
            State = RecorderState.Paused;
        }
    }

    /// <summary>
    /// Continues a paused recording. A failure to reopen capture is handled
    /// like any capture error (recovery, else the recording ends with
    /// <see cref="MicrophoneRecorderErrorKind.ConfigurationChanged"/>).
    /// </summary>
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
            try
            {
                session?.Start();
            }
            catch (Exception error) when (IsCaptureFailure(error))
            {
                var message = CaptureErrorText.Describe(error);
                ThreadPool.QueueUserWorkItem(_ => OnRuntimeError(message));
            }
        }
    }

    /// <summary>Ends the recording. Chunks already captured are still delivered, then the streams complete.</summary>
    public void Stop()
    {
        lock (gate)
        {
            Finish(null);
        }
    }

    // MARK: - Runtime errors and device changes

    /// <summary>What <see cref="HandleConfigurationChange"/> did.</summary>
    internal enum ConfigurationChangeOutcome
    {
        /// <summary>Not recording (idle or already stopped).</summary>
        Ignored,

        /// <summary>Capture restarted unless paused.</summary>
        Recovered,

        /// <summary>Gave up and finished with <see cref="MicrophoneRecorderErrorKind.ConfigurationChanged"/>.</summary>
        Finished,
    }

    /// <summary>
    /// The recovery state machine, independent of the session so it can be
    /// unit-tested. <paramref name="inputFormatValid"/> is false when the
    /// device is gone. <paramref name="reinstall"/> rebuilds what needs
    /// rebuilding; <paramref name="restart"/> starts capture again and is
    /// skipped while paused. <paramref name="now"/> defaults to the host clock.
    /// </summary>
    internal ConfigurationChangeOutcome HandleConfigurationChange(
        bool inputFormatValid, Action restart, double? now = null, Action? reinstall = null)
    {
        lock (gate)
        {
            if (State is not (RecorderState.Recording or RecorderState.Paused))
            {
                return ConfigurationChangeOutcome.Ignored;
            }
            if (!inputFormatValid || !recovery.AllowRecovery(now ?? HostClock.NowSeconds()))
            {
                Finish(new MicrophoneRecorderException(MicrophoneRecorderErrorKind.ConfigurationChanged));
                return ConfigurationChangeOutcome.Finished;
            }
            try
            {
                reinstall?.Invoke();
                if (State == RecorderState.Recording)
                {
                    restart();
                }
            }
            catch (Exception)
            {
                Finish(new MicrophoneRecorderException(MicrophoneRecorderErrorKind.ConfigurationChanged));
                return ConfigurationChangeOutcome.Finished;
            }
            ConfigurationRecoveries++;
            return ConfigurationChangeOutcome.Recovered;
        }
    }

    private const string WasapiRequestedFormat = "endpoint mix format, shared mode";

    private void OnRuntimeError(string message)
    {
        ICaptureSession? current;
        lock (gate)
        {
            if (State is not (RecorderState.Recording or RecorderState.Paused))
            {
                return;
            }
            diagnosticsBox.Update(value => value with
            {
                RuntimeErrors = value.RuntimeErrors + 1,
                LastRuntimeError = message,
            });
            current = session;
        }
        if (current is null)
        {
            return;
        }
        HandleConfigurationChange(current.IsDeviceActive(), restart: current.Start, reinstall: current.Reinstall);
    }

    private void OnDeviceRemoved(string uid)
    {
        lock (gate)
        {
            if (session is null || !string.Equals(uid, session.DeviceUid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        // Do not silently fall back to another microphone.
        HandleConfigurationChange(inputFormatValid: false, restart: () => { });
    }

    private void Attach(ICaptureSession opened)
    {
        session = opened;
        opened.RuntimeError += OnRuntimeError;
        opened.DeviceRemoved += OnDeviceRemoved;
    }

    /// <summary>Call with <see cref="gate"/> held.</summary>
    private void Finish(MicrophoneRecorderException? failure)
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
        {
            if (State == RecorderState.Idle)
            {
                State = RecorderState.Stopped;
                Output.Finish();
            }
            return;
        }
        Failure = failure;
        State = RecorderState.Stopped;
        if (session is ICaptureSession current)
        {
            current.RuntimeError -= OnRuntimeError;
            current.DeviceRemoved -= OnDeviceRemoved;
            session = null;
            var output = Output;
            // Buffers still in flight are delivered before the stream ends.
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

    /// <summary>What WASAPI and NAudio throw when capture cannot start (E_ACCESSDENIED can surface as UnauthorizedAccessException).</summary>
    private static bool IsCaptureFailure(Exception error) =>
        error is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private static MicrophoneRecorderException MapStartError(Exception error) =>
        error.HResult == CaptureErrorText.AccessDenied
            ? new MicrophoneRecorderException(MicrophoneRecorderErrorKind.PermissionDenied)
            : new MicrophoneRecorderException(MicrophoneRecorderErrorKind.EngineFailed, CaptureErrorText.Describe(error));
}

/// <summary>
/// Watches for a recording that never receives audio: <see cref="Check"/>
/// returns true once <see cref="Timeout"/> seconds of recording (paused time
/// excluded) passed without a single sample. Port of <c>NoAudioWatchdog</c> in
/// MicrophoneRecorder.swift.
/// </summary>
public struct NoAudioWatchdog : IEquatable<NoAudioWatchdog>
{
    public const double Timeout = 3;

    private double recordingTime;
    private double? lastCheck;
    private bool wasRecording;
    private bool heardAudio;

    /// <summary>
    /// Call periodically with a monotonic <paramref name="now"/> in seconds,
    /// whether capture is currently running (not paused), and the samples
    /// delivered so far.
    /// </summary>
    public bool Check(double now, bool isRecording, long samplesDelivered)
    {
        if (samplesDelivered > 0)
        {
            heardAudio = true;
        }
        if (heardAudio)
        {
            return false;
        }
        if (isRecording && wasRecording && lastCheck is double last)
        {
            recordingTime += Math.Max(0, now - last);
        }
        lastCheck = now;
        wasRecording = isRecording;
        return recordingTime >= Timeout;
    }

    public readonly bool Equals(NoAudioWatchdog other) =>
        recordingTime.Equals(other.recordingTime) && lastCheck.Equals(other.lastCheck)
        && wasRecording == other.wasRecording && heardAudio == other.heardAudio;

    public override readonly bool Equals(object? obj) => obj is NoAudioWatchdog other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(recordingTime, lastCheck, wasRecording, heardAudio);

    public static bool operator ==(NoAudioWatchdog left, NoAudioWatchdog right) => left.Equals(right);

    public static bool operator !=(NoAudioWatchdog left, NoAudioWatchdog right) => !left.Equals(right);
}

/// <summary>
/// Decides whether another configuration-change recovery is allowed: at most
/// <see cref="MaxRecoveries"/> within any <see cref="WindowSeconds"/>, so a
/// flapping device ends the recording instead of restarting forever. Port of
/// <c>ConfigurationRecovery</c> in MicrophoneRecorder.swift.
/// </summary>
internal sealed class ConfigurationRecovery
{
    public const int MaxRecoveries = 5;
    public const double WindowSeconds = 10;

    private readonly List<double> recent = [];

    public IReadOnlyList<double> Recent => recent;

    public bool AllowRecovery(double time)
    {
        recent.RemoveAll(value => time - value >= WindowSeconds);
        if (recent.Count >= MaxRecoveries)
        {
            return false;
        }
        recent.Add(time);
        return true;
    }
}
