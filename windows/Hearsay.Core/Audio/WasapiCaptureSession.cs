using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Hearsay.Core.Audio;

/// <summary>
/// Owns one WASAPI shared-mode capture on NAudio's <see cref="WasapiRecorder"/>:
/// a microphone endpoint, or the loopback of the default output device.
/// Port of the private <c>CaptureSession</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MicrophoneRecorder.swift and of
/// the <c>SCStream</c> handling in SystemAudioRecorder.swift.
/// </summary>
/// <remarks>
/// <para>NAudio 3 marks the older <c>WasapiCapture</c> and
/// <c>WasapiLoopbackCapture</c> obsolete; <see cref="WasapiRecorder"/> replaces
/// both and, unlike them, passes on each packet's QPC position, which is what
/// the timestamps use (see <see cref="CaptureReceiver"/>).</para>
/// <para>Capture runs in the endpoint's mix format (whatever rate, channel
/// count and sample type the endpoint gives; usually 48 kHz stereo Float32),
/// event-driven with NAudio's default 100 ms buffer, and
/// <see cref="CaptureReceiver"/> converts it to 16 kHz mono with
/// <see cref="MonoResampler"/>, as the Mac asks the capture output for the
/// device rate and channels and resamples itself.</para>
/// <para>A <see cref="WasapiRecorder"/> initializes its audio client on start
/// and cannot be started twice, so pause and <see cref="Reinstall"/> release
/// it and <see cref="Start"/> opens the endpoint again (which also picks up a
/// changed mix format). NAudio posts <c>RecordingStopped</c> to the
/// <see cref="SynchronizationContext"/> it was created on; recorders are created
/// with none, so it runs on the capture thread, and every event this class
/// raises is moved to the thread pool (a recorder must not be disposed from
/// its own capture thread: <c>Dispose</c> joins it).</para>
/// <para>Loopback delivers nothing while no app plays audio. A timer makes up
/// the missing frames as silence at the capture rate
/// (<see cref="SilenceGapFiller"/>) so the system-audio lane stays continuous;
/// the timer and the capture thread share one lock around the receiver.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiCaptureSession : ICaptureSession
{
    private readonly bool loopback;
    private readonly CaptureReceiver receiver;
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDeviceNotificationClient notifications;
    private readonly Lock gate = new();
    private readonly Lock receiverGate = new();
    private readonly SilenceGapFiller? filler;
    private Timer? silenceTimer;
    private MMDevice? device;
    private WasapiRecorder? recorder;
    private CaptureFormat? captureFormat;
    private bool finished;

    private WasapiCaptureSession(bool loopback, string uid, string name, CaptureReceiver receiver)
    {
        this.loopback = loopback;
        this.receiver = receiver;
        DeviceUid = uid;
        DeviceName = name;
        enumerator = new MMDeviceEnumerator();
        notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        notifications.DeviceRemoved += (_, args) => RaiseRemoved(args.DeviceId);
        notifications.DeviceStateChanged += (_, args) =>
        {
            if (args.NewState != DeviceState.Active)
            {
                RaiseRemoved(args.DeviceId);
            }
        };
        notifications.DefaultDeviceChanged += (_, args) =>
        {
            if (this.loopback && args.Flow == DataFlow.Render && args.Role == Role.Multimedia)
            {
                Post(() => DefaultDeviceChanged?.Invoke());
            }
        };
        if (loopback)
        {
            filler = new SilenceGapFiller(HostClock.NowSeconds());
        }
    }

    public event Action<string>? RuntimeError;

    public event Action<string>? DeviceRemoved;

    public event Action? DefaultDeviceChanged;

    public string DeviceUid { get; private set; }

    public string DeviceName { get; private set; }

    public CaptureFormat? MixFormat { get; private set; }

    public CaptureFormat? DeviceFormat { get; private set; }

    /// <summary>
    /// Opens <paramref name="device"/>, or the default input when null, and
    /// reads its formats. Throws <see cref="MicrophoneRecorderException"/>, or
    /// <see cref="COMException"/> when Core Audio fails.
    /// </summary>
    public static WasapiCaptureSession OpenMicrophone(AudioInputDevice? device, CaptureReceiver receiver)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? endpoint;
        if (device is not null)
        {
            try
            {
                endpoint = enumerator.GetDevice(device.Uid);
            }
            catch (COMException)
            {
                throw new MicrophoneRecorderException(MicrophoneRecorderErrorKind.DeviceNotFound, device.Name);
            }
            if (endpoint.State != DeviceState.Active || endpoint.DataFlow != DataFlow.Capture)
            {
                endpoint.Dispose();
                throw new MicrophoneRecorderException(MicrophoneRecorderErrorKind.DeviceNotFound, device.Name);
            }
        }
        else if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out endpoint) || endpoint is null)
        {
            throw new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoInputDevice);
        }
        using (endpoint)
        {
            var described = AudioDeviceList.Describe(endpoint)
                ?? throw new MicrophoneRecorderException(MicrophoneRecorderErrorKind.DeviceNotFound, device?.Name ?? "");
            var session = new WasapiCaptureSession(loopback: false, described.Uid, device?.Name ?? described.Name, receiver);
            session.ReadFormats(endpoint);
            return session;
        }
    }

    /// <summary>
    /// Opens the loopback of the default output device. Throws
    /// <see cref="SystemAudioRecorderException"/>, or <see cref="COMException"/>.
    /// </summary>
    public static WasapiCaptureSession OpenLoopback(CaptureReceiver receiver)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var endpoint) || endpoint is null)
        {
            throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.NoOutputDevice);
        }
        using (endpoint)
        {
            var described = AudioDeviceList.Describe(endpoint)
                ?? throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.NoOutputDevice);
            var session = new WasapiCaptureSession(loopback: true, described.Uid, described.Name, receiver);
            session.ReadFormats(endpoint);
            return session;
        }
    }

    public bool IsDeviceActive()
    {
        try
        {
            if (loopback)
            {
                return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            using var endpoint = enumerator.GetDevice(DeviceUid);
            return endpoint.State == DeviceState.Active;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (finished || recorder is not null)
            {
                return;
            }
            var (newDevice, newRecorder) = Open();
            device = newDevice;
            recorder = newRecorder;
            try
            {
                newRecorder.StartRecording();
            }
            catch
            {
                recorder = null;
                device = null;
                newRecorder.Dispose();
                newDevice.Dispose();
                throw;
            }
            if (filler is not null)
            {
                // Silence is made up only from when capture really runs.
                lock (receiverGate)
                {
                    filler.Reset(HostClock.NowSeconds());
                }
            }
            if (loopback && silenceTimer is null)
            {
                var period = TimeSpan.FromSeconds(SilenceGapFiller.TickSeconds);
                silenceTimer = new Timer(_ => FillSilence(), null, period, period);
            }
        }
    }

    public void Stop()
    {
        WasapiRecorder? old;
        MMDevice? oldDevice;
        lock (gate)
        {
            old = recorder;
            oldDevice = device;
            recorder = null;
            device = null;
        }
        Release(old, oldDevice);
    }

    public void Reinstall() => Stop();

    public void StopAndDrain(Action done)
    {
        ArgumentNullException.ThrowIfNull(done);
        WasapiRecorder? old;
        MMDevice? oldDevice;
        lock (gate)
        {
            if (finished)
            {
                return;
            }
            finished = true;
            old = recorder;
            oldDevice = device;
            recorder = null;
            device = null;
            silenceTimer?.Dispose();
            silenceTimer = null;
        }
        var stopTime = HostClock.NowSeconds();
        Task.Run(() =>
        {
            // Dispose joins the capture thread, so every packet is delivered
            // before the resampler's tail.
            Release(old, oldDevice);
            lock (receiverGate)
            {
                if (old is not null)
                {
                    // Loopback: the silence up to the stop, as the Mac's
                    // stream delivers buffers right up to it.
                    FillSilence(stopTime, minimumGap: 0);
                }
                receiver.Flush();
            }
            done();
        });
    }

    public void Dispose()
    {
        WasapiRecorder? old;
        MMDevice? oldDevice;
        lock (gate)
        {
            finished = true;
            old = recorder;
            oldDevice = device;
            recorder = null;
            device = null;
            silenceTimer?.Dispose();
            silenceTimer = null;
        }
        Release(old, oldDevice);
        notifications.Dispose();
        enumerator.Dispose();
    }

    /// <summary>Opens the endpoint (for loopback, the current default output) and builds a recorder on it.</summary>
    private (MMDevice Device, WasapiRecorder Recorder) Open()
    {
        MMDevice endpoint;
        if (loopback)
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var found) || found is null)
            {
                throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.NoOutputDevice);
            }
            endpoint = found;
            if (AudioDeviceList.Describe(endpoint) is AudioInputDevice described)
            {
                DeviceUid = described.Uid;
                DeviceName = described.Name;
            }
        }
        else
        {
            endpoint = enumerator.GetDevice(DeviceUid);
        }

        WasapiRecorder built;
        var context = SynchronizationContext.Current;
        try
        {
            // NAudio captures the current context for RecordingStopped; with
            // none it is raised on the capture thread.
            SynchronizationContext.SetSynchronizationContext(null);
            var builder = new WasapiRecorderBuilder().WithDevice(endpoint).WithSharedMode();
            if (loopback)
            {
                builder = builder.WithLoopbackCapture();
            }
            built = builder.Build();
        }
        catch
        {
            endpoint.Dispose();
            throw;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
        ReadFormats(endpoint);
        var format = FromWaveFormat(built.WaveFormat);
        MixFormat = format;
        lock (receiverGate)
        {
            captureFormat = format;
        }
        built.DataAvailable += (buffer, _, _, qpcPosition) => OnData(buffer, qpcPosition, format);
        built.RecordingStopped += OnStopped;
        return (endpoint, built);
    }

    private void OnData(ReadOnlySpan<byte> buffer, long qpcPosition, CaptureFormat format)
    {
        var now = HostClock.NowSeconds();
        var frames = format.BlockAlign > 0 ? buffer.Length / format.BlockAlign : 0;
        var start = PacketClock.StartSeconds(qpcPosition, now, frames, format.SampleRate);
        lock (receiverGate)
        {
            receiver.Handle(buffer, format, start);
            if (frames > 0)
            {
                filler?.Delivered(start + (double)frames / format.SampleRate);
            }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        // A stop this session asked for has already cleared `recorder`.
        if (args.Exception is not Exception error || !ReferenceEquals(sender, Volatile.Read(ref recorder)))
        {
            return;
        }
        var message = CaptureErrorText.Describe(error);
        Post(() => RuntimeError?.Invoke(message));
    }

    private void FillSilence()
    {
        lock (receiverGate)
        {
            if (Volatile.Read(ref recorder) is not null)
            {
                FillSilence(HostClock.NowSeconds(), SilenceGapFiller.GapSeconds);
            }
        }
    }

    /// <summary>Makes up missing loopback frames up to <paramref name="now"/>; call with <see cref="receiverGate"/> held.</summary>
    private void FillSilence(double now, double minimumGap)
    {
        if (filler is null || captureFormat is not CaptureFormat format)
        {
            return;
        }
        var frames = filler.Take(now, format.SampleRate, minimumGap, out var end);
        if (frames <= 0)
        {
            return;
        }
        var silence = new byte[frames * format.BlockAlign];
        receiver.Handle(silence, format, end - (double)frames / format.SampleRate, synthesized: true);
    }

    private void ReadFormats(MMDevice endpoint)
    {
        try
        {
            using var client = endpoint.CreateAudioClient();
            MixFormat = FromWaveFormat(client.MixFormat);
        }
        catch (COMException)
        {
        }
        try
        {
            if (endpoint.Properties.TryGetValue(PropertyKeys.PKEY_AudioEngine_DeviceFormat, out byte[]? blob) && blob is not null)
            {
                DeviceFormat = CaptureFormat.FromWaveFormatBlob(blob);
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }
    }

    private static CaptureFormat FromWaveFormat(WaveFormat format)
    {
        // WASAPI's mix format is usually WAVEFORMATEXTENSIBLE; the standard
        // form says whether its samples are float or integer.
        var standard = format.AsStandardWaveFormat();
        return new CaptureFormat(standard.SampleRate, standard.Channels, standard.BitsPerSample,
            standard.Encoding == WaveFormatEncoding.IeeeFloat);
    }

    private void RaiseRemoved(string? id)
    {
        if (id is not null)
        {
            Post(() => DeviceRemoved?.Invoke(id));
        }
    }

    private static void Post(Action action) => ThreadPool.QueueUserWorkItem(_ => action());

    private static void Release(WasapiRecorder? old, MMDevice? oldDevice)
    {
        if (old is not null)
        {
            old.StopRecording();
            old.Dispose();
        }
        oldDevice?.Dispose();
    }
}
