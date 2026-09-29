using System.Globalization;
using Hearsay.Core.Audio;

namespace Hearsay.Tests.Audio;

/// <summary>
/// A fact that runs only when an environment variable is set (xUnit 2.9 has
/// no <c>Assert.Skip</c>, so the skip reason is set when the test is
/// discovered). With <c>requiredValue</c> the variable must equal it;
/// without, any non-empty value enables the test.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variable, string? requiredValue = null)
    {
        Variable = variable;
        RequiredValue = requiredValue;
        var value = Environment.GetEnvironmentVariable(variable);
        var enabled = requiredValue is null ? !string.IsNullOrEmpty(value) : value == requiredValue;
        if (!enabled)
        {
            Skip = requiredValue is null
                ? $"Set {variable} to run this test against real audio devices."
                : $"Set {variable}={requiredValue} to run this test against real audio devices.";
        }
    }

    public string Variable { get; }

    public string? RequiredValue { get; }

    /// <summary>The variable as a positive number of seconds, else <paramref name="fallback"/>.</summary>
    public static double Seconds(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? seconds
            : fallback;
}

/// <summary>
/// An <see cref="EnvironmentFactAttribute"/> that also skips when the machine
/// has no active capture endpoint (checked only when the variable is set).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MicrophoneFactAttribute : EnvironmentFactAttribute
{
    public MicrophoneFactAttribute(string variable)
        : base(variable)
    {
        if (Skip is null && OperatingSystem.IsWindows() && AudioDeviceList.DefaultInputDevice() is null)
        {
            Skip = "This machine has no active capture endpoint to record from.";
        }
    }
}

/// <summary>
/// Stands in for the WASAPI session, as the Swift tests replace the
/// <c>AVCaptureSession</c> through <c>beginForTesting(probe:)</c>: counts
/// starts, stops and reinstalls, answers <see cref="IsDeviceActive"/> from
/// <see cref="Active"/>, and raises its events on the thread pool like the
/// real session.
/// </summary>
internal sealed class FakeCaptureSession : ICaptureSession
{
    private int starts;
    private int stops;
    private int reinstalls;
    private int probes;

    public FakeCaptureSession(string uid = "{0.0.1.00000000}.{fake}", string name = "Fake Mic")
    {
        DeviceUid = uid;
        DeviceName = name;
    }

    public event Action<string>? RuntimeError;

    public event Action<string>? DeviceRemoved;

    public event Action? DefaultDeviceChanged;

    /// <summary>Set by the recorder's session factory.</summary>
    public CaptureReceiver? Receiver { get; set; }

    public string DeviceUid { get; }

    public string DeviceName { get; }

    public CaptureFormat? MixFormat { get; set; } = new CaptureFormat(48_000, 2, 32, true);

    public CaptureFormat? DeviceFormat { get; set; } = new CaptureFormat(48_000, 2, 24, false);

    public bool Active { get; set; } = true;

    /// <summary>Thrown by <see cref="Start"/> when set.</summary>
    public Exception? StartError { get; set; }

    public int Starts => Volatile.Read(ref starts);

    public int Stops => Volatile.Read(ref stops);

    public int Reinstalls => Volatile.Read(ref reinstalls);

    public int Probes => Volatile.Read(ref probes);

    public bool Drained { get; private set; }

    public bool Disposed { get; private set; }

    public bool IsDeviceActive()
    {
        Interlocked.Increment(ref probes);
        return Active;
    }

    public void Start()
    {
        if (StartError is Exception error)
        {
            throw error;
        }
        Interlocked.Increment(ref starts);
    }

    public void Stop() => Interlocked.Increment(ref stops);

    public void Reinstall() => Interlocked.Increment(ref reinstalls);

    public void StopAndDrain(Action done)
    {
        Drained = true;
        Receiver?.Flush();
        done();
    }

    public void Dispose() => Disposed = true;

    /// <summary>Clears the counters (after the recorder's own first start).</summary>
    public void ResetCounters()
    {
        starts = 0;
        stops = 0;
        reinstalls = 0;
        probes = 0;
    }

    public void RaiseRuntimeError(string message) => ThreadPool.QueueUserWorkItem(_ => RuntimeError?.Invoke(message));

    public void RaiseDeviceRemoved(string uid) => ThreadPool.QueueUserWorkItem(_ => DeviceRemoved?.Invoke(uid));

    public void RaiseDefaultDeviceChanged() => ThreadPool.QueueUserWorkItem(_ => DefaultDeviceChanged?.Invoke());
}

/// <summary>A Core Audio failure with an HRESULT, as NAudio's <c>CoreAudioException</c> is.</summary>
internal sealed class FakeComException : System.Runtime.InteropServices.COMException
{
    public FakeComException(string message, int hresult)
        : base(message, hresult)
    {
    }
}

internal static class CaptureTestHelpers
{
    /// <summary>Waits up to a second for <paramref name="condition"/>, as the Swift tests poll 100 × 10 ms.</summary>
    public static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>Every chunk until the stream completes (fails after five seconds).</summary>
    public static async Task<List<float[]>> Collect(System.Threading.Channels.ChannelReader<float[]> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chunks = new List<float[]>();
        await foreach (var chunk in reader.ReadAllAsync(timeout.Token))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    /// <summary>Interleaved Float32 frames as little-endian bytes.</summary>
    public static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
