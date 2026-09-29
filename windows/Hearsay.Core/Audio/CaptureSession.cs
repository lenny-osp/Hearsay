using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Hearsay.Core.Audio;

/// <summary>
/// The PCM layout of a WASAPI capture buffer. WASAPI shared mode always
/// delivers interleaved frames, usually 32-bit float at the endpoint's mix
/// rate. The Windows counterpart of the <c>AudioStreamBasicDescription</c>
/// that <c>MicrophoneDiagnostics.describe</c> formats in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MicrophoneRecorder.swift.
/// </summary>
public readonly record struct CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    /// <summary>WAVE_FORMAT_PCM.</summary>
    private const ushort TagPcm = 1;

    /// <summary>WAVE_FORMAT_IEEE_FLOAT.</summary>
    private const ushort TagFloat = 3;

    /// <summary>WAVE_FORMAT_EXTENSIBLE.</summary>
    private const ushort TagExtensible = 0xFFFE;

    /// <summary>Bytes per interleaved frame.</summary>
    public int BlockAlign => Channels * BitsPerSample / 8;

    /// <summary>
    /// Reads a <c>WAVEFORMATEX</c> or <c>WAVEFORMATEXTENSIBLE</c> blob (the
    /// endpoint's <c>PKEY_AudioEngine_DeviceFormat</c>), or null when it is too
    /// short or neither PCM nor float.
    /// </summary>
    public static CaptureFormat? FromWaveFormatBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 16)
        {
            return null;
        }
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(blob[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(blob[14..]);
        if (tag == TagExtensible)
        {
            // cbSize (2), valid bits (2), channel mask (4), then the sub-format
            // GUID, whose first two bytes are the plain format tag.
            if (blob.Length < 26)
            {
                return null;
            }
            tag = BinaryPrimitives.ReadUInt16LittleEndian(blob[24..]);
        }
        if ((tag != TagPcm && tag != TagFloat) || channels == 0 || rate <= 0 || bits == 0)
        {
            return null;
        }
        return new CaptureFormat(rate, channels, bits, tag == TagFloat);
    }
}

/// <summary>
/// Turns interleaved capture bytes into interleaved Float32 samples in
/// [-1, 1]. Windows only: the Mac's capture output delivers Float32 already
/// (<c>CaptureSession.audioSettings</c> in MicrophoneRecorder.swift), WASAPI
/// shared mode delivers whatever the endpoint's mix format is.
/// </summary>
internal static class CaptureSampleDecoder
{
    public static float[] Decode(ReadOnlySpan<byte> data, CaptureFormat format)
    {
        var bytes = format.BitsPerSample / 8;
        if (bytes == 0 || format.BitsPerSample % 8 != 0 || data.Length % bytes != 0)
        {
            throw new AudioConversionException(AudioConversionErrorKind.UnsupportedFormat,
                MicrophoneDiagnostics.Describe(format));
        }
        var count = data.Length / bytes;
        var samples = new float[count];
        switch (format.IsFloat, format.BitsPerSample)
        {
            case (true, 32):
                MemoryMarshal.Cast<byte, float>(data).CopyTo(samples);
                break;
            case (true, 64):
                var doubles = MemoryMarshal.Cast<byte, double>(data);
                for (var index = 0; index < count; index++)
                {
                    samples[index] = (float)doubles[index];
                }
                break;
            case (false, 8):
                for (var index = 0; index < count; index++)
                {
                    samples[index] = (data[index] - 128) / 128f;
                }
                break;
            case (false, 16):
                var shorts = MemoryMarshal.Cast<byte, short>(data);
                for (var index = 0; index < count; index++)
                {
                    samples[index] = shorts[index] / 32768f;
                }
                break;
            case (false, 24):
                for (var index = 0; index < count; index++)
                {
                    var offset = index * 3;
                    var value = data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16);
                    samples[index] = value / 8388608f;
                }
                break;
            case (false, 32):
                var ints = MemoryMarshal.Cast<byte, int>(data);
                for (var index = 0; index < count; index++)
                {
                    samples[index] = (float)(ints[index] / 2147483648.0);
                }
                break;
            default:
                throw new AudioConversionException(AudioConversionErrorKind.UnsupportedFormat,
                    MicrophoneDiagnostics.Describe(format));
        }
        return samples;
    }
}

/// <summary>
/// Receives capture buffers, converts them to 16 kHz mono and yields them
/// into the recorder's <see cref="ChunkFanout"/> stamped with host time. Port
/// of <c>SampleReceiver</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MicrophoneRecorder.swift (and of
/// <c>StreamReceiver</c> in SystemAudioRecorder.swift). Not thread-safe: the
/// capture thread is the only caller, or callers share one lock.
/// </summary>
/// <remarks>
/// <para><b>Timestamps.</b> The Mac converts each buffer's presentation
/// timestamp to the host clock. WASAPI reports, for every capture packet,
/// the QPC time its first frame was recorded (<c>IAudioCaptureClient::GetBuffer</c>,
/// in 100 ns units), which NAudio's <c>WasapiRecorder</c> passes on; that is
/// the same clock as <see cref="HostClock"/>, so a packet is stamped with it
/// directly. When a driver reports no QPC position (0), the packet is stamped
/// <c>QPC at callback − frames / rate</c> instead: the callback runs within one
/// device period of the packet's last frame, and the mixer's
/// <see cref="AudioMixer.ResyncToleranceSeconds"/> absorbs that jitter.</para>
/// <para>The resampler holds back the last few input frames' outputs until
/// the next buffer, so a chunk starts slightly before its buffer; the stamp
/// is moved back by exactly the held-back output, which keeps consecutive
/// chunks contiguous on the host clock.</para>
/// <para>A failed conversion drops that buffer only and is counted in the
/// diagnostics; the recording goes on.</para>
/// </remarks>
internal sealed class CaptureReceiver
{
    private readonly ChunkFanout output;
    private readonly DiagnosticsBox diagnostics;
    private PcmBufferConverter? converter;
    private CaptureFormat converterFormat;
    private long inputFrames;
    private long outputSamples;
    private double lastInputEnd;

    public CaptureReceiver(ChunkFanout output, DiagnosticsBox diagnostics)
    {
        this.output = output;
        this.diagnostics = diagnostics;
    }

    /// <summary>
    /// Handles one buffer whose first frame was captured at
    /// <paramref name="startHostTime"/>. <paramref name="synthesized"/> marks
    /// silence Hearsay made up (system audio between sounds); it is not
    /// counted as a capture callback.
    /// </summary>
    public void Handle(ReadOnlySpan<byte> data, CaptureFormat format, double startHostTime, bool synthesized = false)
    {
        var block = Math.Max(1, format.BlockAlign);
        var frames = data.Length / block;
        if (synthesized)
        {
            diagnostics.Update(value => value with
            {
                SilenceFramesSynthesized = value.SilenceFramesSynthesized + frames,
            });
        }
        else
        {
            var described = MicrophoneDiagnostics.Describe(format);
            diagnostics.Update(value => value with
            {
                Callbacks = value.Callbacks + 1,
                FramesReceived = value.FramesReceived + frames,
                DeliveredFormat = value.DeliveredFormat ?? described,
            });
        }
        if (frames == 0 || format.SampleRate <= 0)
        {
            return;
        }
        float[] chunk;
        double hostTime;
        try
        {
            if (converter is null || converterFormat != format)
            {
                converter = new PcmBufferConverter(format.SampleRate, format.Channels);
                converterFormat = format;
                inputFrames = 0;
                outputSamples = 0;
            }
            var interleaved = CaptureSampleDecoder.Decode(data[..(frames * block)], format);
            var heldBack = PendingSeconds();
            chunk = converter.Convert(interleaved);
            inputFrames += frames;
            outputSamples += chunk.Length;
            hostTime = startHostTime - heldBack;
            lastInputEnd = startHostTime + (double)frames / format.SampleRate;
        }
        catch (AudioConversionException error)
        {
            var text = error.Message;
            diagnostics.Update(value => value with
            {
                ConversionFailures = value.ConversionFailures + 1,
                LastConversionError = text,
            });
            return;
        }
        Deliver(chunk, hostTime);
    }

    /// <summary>Emits what the resampler still holds; call after the last buffer.</summary>
    public void Flush()
    {
        if (converter is null)
        {
            return;
        }
        var heldBack = PendingSeconds();
        float[] chunk;
        try
        {
            chunk = converter.Flush();
        }
        catch (AudioConversionException)
        {
            return;
        }
        outputSamples += chunk.Length;
        Deliver(chunk, lastInputEnd - heldBack);
    }

    /// <summary>Output owed for input already received, in seconds at 16 kHz.</summary>
    private double PendingSeconds()
    {
        var expected = inputFrames * (double)MonoResampler.SampleRate / converterFormat.SampleRate;
        return Math.Max(0, expected - outputSamples) / MonoResampler.SampleRate;
    }

    private void Deliver(float[] chunk, double hostTime)
    {
        if (chunk.Length == 0)
        {
            return;
        }
        output.Yield(chunk, hostTime);
        var lag = HostClock.NowSeconds() - (hostTime + (double)chunk.Length / MonoResampler.SampleRate);
        var count = chunk.Length;
        diagnostics.Update(value => value with
        {
            SamplesDelivered = value.SamplesDelivered + count,
            TimestampLagSeconds = lag,
        });
    }
}

/// <summary>
/// Thread-safe holder for <see cref="MicrophoneDiagnostics"/>, written from
/// the capture thread and read from anywhere. Port of <c>DiagnosticsBox</c> in
/// MicrophoneRecorder.swift.
/// </summary>
internal sealed class DiagnosticsBox
{
    private readonly Lock gate = new();
    private MicrophoneDiagnostics value = new();

    public MicrophoneDiagnostics Value
    {
        get
        {
            lock (gate)
            {
                return value;
            }
        }
    }

    public void Update(Func<MicrophoneDiagnostics, MicrophoneDiagnostics> body)
    {
        lock (gate)
        {
            value = body(value);
        }
    }
}

/// <summary>
/// What the recorders need from a capture session: the seam the unit tests
/// fake, as the Swift tests replace <c>CaptureSession</c> through
/// <c>MicrophoneRecorder.beginForTesting(probe:)</c>. The WASAPI
/// implementation is <see cref="WasapiCaptureSession"/>; buffers go straight
/// from the session to its <see cref="CaptureReceiver"/>.
/// </summary>
/// <remarks>
/// The events are raised on a thread-pool thread, never on the capture thread
/// or the Core Audio notification thread, so a handler may tear the session
/// down (the Mac handles them on the main queue).
/// </remarks>
internal interface ICaptureSession : IDisposable
{
    /// <summary>Endpoint id currently captured.</summary>
    string DeviceUid { get; }

    /// <summary>Friendly name of that endpoint.</summary>
    string DeviceName { get; }

    /// <summary>The shared-mode mix format capture delivers, when known.</summary>
    CaptureFormat? MixFormat { get; }

    /// <summary>The endpoint's own device format (the hardware side of the mix), when readable.</summary>
    CaptureFormat? DeviceFormat { get; }

    /// <summary>Whether the endpoint can still be recorded (present and active).</summary>
    bool IsDeviceActive();

    /// <summary>Starts capture, opening the endpoint afresh when it has no audio client.</summary>
    void Start();

    /// <summary>Stops capture and releases the audio client (pause); <see cref="Start"/> resumes.</summary>
    void Stop();

    /// <summary>Drops the audio client so the next <see cref="Start"/> opens the endpoint again with its current format.</summary>
    void Reinstall();

    /// <summary>Stops for good, delivers every buffered frame and the resampler's tail, then calls <paramref name="done"/>.</summary>
    void StopAndDrain(Action done);

    /// <summary>Capture stopped on its own with this error (device invalidated, format changed, service stopped).</summary>
    event Action<string>? RuntimeError;

    /// <summary>An endpoint (by id) was removed, disabled, or unplugged.</summary>
    event Action<string>? DeviceRemoved;

    /// <summary>The default output device changed (loopback sessions only).</summary>
    event Action? DefaultDeviceChanged;
}

/// <summary>
/// Decides when system audio capture must make up silence. Windows only:
/// WASAPI loopback delivers no packets while nothing plays, whereas the Mac's
/// ScreenCaptureKit stream delivers continuous buffers. Pure logic so it can
/// be tested without a device; see <see cref="SystemAudioRecorder"/>.
/// </summary>
internal sealed class SilenceGapFiller
{
    /// <summary>A gap this long since the last delivered frame is filled.</summary>
    public const double GapSeconds = 0.2;

    /// <summary>How often the recorder checks for a gap.</summary>
    public const double TickSeconds = 0.1;

    private double lastEnd;

    public SilenceGapFiller(double startHostTime)
    {
        lastEnd = startHostTime;
    }

    /// <summary>Host time of the last frame delivered or made up.</summary>
    public double LastEnd => lastEnd;

    /// <summary>Real capture delivered a buffer ending at <paramref name="endHostTime"/>.</summary>
    public void Delivered(double endHostTime) => lastEnd = Math.Max(lastEnd, endHostTime);

    /// <summary>Forgets the gap, for example after the capture was reopened.</summary>
    public void Reset(double hostTime) => lastEnd = hostTime;

    /// <summary>
    /// The frames of silence at <paramref name="sampleRate"/> to make up at
    /// <paramref name="now"/> (0 when nothing is missing yet), and the host
    /// time their last frame ends at. Taking them advances the filler.
    /// </summary>
    public int Take(double now, int sampleRate, out double endHostTime) => Take(now, sampleRate, GapSeconds, out endHostTime);

    /// <summary>
    /// As <see cref="Take(double, int, out double)"/> with a gap threshold of
    /// <paramref name="minimumGap"/> seconds; 0 at stop fills right up to it.
    /// </summary>
    public int Take(double now, int sampleRate, double minimumGap, out double endHostTime)
    {
        endHostTime = lastEnd;
        if (sampleRate <= 0 || now - lastEnd < minimumGap)
        {
            return 0;
        }
        var frames = (int)Math.Floor((now - lastEnd) * sampleRate);
        if (frames <= 0)
        {
            return 0;
        }
        lastEnd += (double)frames / sampleRate;
        endHostTime = lastEnd;
        return frames;
    }
}

/// <summary>
/// Host time of a WASAPI packet's first frame: its QPC position (100 ns
/// units) when the driver reports one, else the callback time minus the
/// packet's duration. See <see cref="CaptureReceiver"/>.
/// </summary>
internal static class PacketClock
{
    public static double StartSeconds(long qpcPosition100ns, double callbackSeconds, int frames, int sampleRate)
    {
        if (qpcPosition100ns > 0)
        {
            return qpcPosition100ns / 10_000_000.0;
        }
        return sampleRate > 0 ? callbackSeconds - (double)frames / sampleRate : callbackSeconds;
    }
}

/// <summary>Formatting helpers for WASAPI error text in diagnostics and messages.</summary>
internal static class CaptureErrorText
{
    /// <summary>E_ACCESSDENIED: Windows privacy settings block the microphone.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED: the endpoint went away or its format changed.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>AUDCLNT_E_DEVICE_IN_USE: another app holds the endpoint in exclusive mode.</summary>
    public const int DeviceInUse = unchecked((int)0x8889000A);

    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Create(CultureInfo.InvariantCulture, $"{error.Message} (0x{error.HResult:X8})");
    }
}
