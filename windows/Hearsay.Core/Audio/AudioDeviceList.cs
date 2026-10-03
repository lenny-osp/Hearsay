using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Hearsay.Core.Audio;

/// <summary>
/// An active WASAPI capture endpoint. Port of <c>AudioInputDevice</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/AudioDeviceList.swift.
/// </summary>
/// <remarks>
/// The Swift type carries a per-boot CoreAudio object id plus the stable UID.
/// Windows has one identifier: the endpoint id string
/// (<c>{0.0.1.00000000}.{guid}</c>), which survives reboots and replugging
/// into the same port, so it plays the UID's role and is what settings
/// persist. <see cref="Name"/> is the endpoint's friendly name as the Sound
/// settings show it ("Microphone (Realtek(R) Audio)").
/// </remarks>
public sealed record AudioInputDevice(string Uid, string Name);

/// <summary>
/// WASAPI enumeration of capture endpoints (PLAN.md 4.1 step 1). Port of
/// <c>AudioDeviceList</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/AudioDeviceList.swift, on
/// NAudio's <see cref="MMDeviceEnumerator"/> with
/// <see cref="DataFlow.Capture"/> and <see cref="DeviceState.Active"/>
/// (disabled, unplugged and absent endpoints are not recordable, as a
/// CoreAudio device without input channels is not listed on the Mac).
/// </summary>
public static class AudioDeviceList
{
    /// <summary>Every active capture endpoint, in enumeration order.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<AudioInputDevice> InputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var devices = new List<AudioInputDevice>(collection.Count);
        foreach (var endpoint in collection)
        {
            using (endpoint)
            {
                if (Describe(endpoint) is AudioInputDevice device)
                {
                    devices.Add(device);
                }
            }
        }
        return devices;
    }

    /// <summary>
    /// The system default input (the Console role, which is what the Sound
    /// settings call the default device), or null when there is none.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static AudioInputDevice? DefaultInputDevice() => DefaultEndpoint(DataFlow.Capture);

    /// <summary>
    /// The default output device whose mix system audio capture records
    /// (the Multimedia role, as <c>WasapiLoopbackCapture</c> uses), or null.
    /// Windows only; the Mac captures the display's audio instead.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static AudioInputDevice? DefaultOutputDevice() => DefaultEndpoint(DataFlow.Render, Role.Multimedia);

    /// <summary>The input device with this id, if it is currently active.</summary>
    [SupportedOSPlatform("windows")]
    public static AudioInputDevice? InputDevice(string uid) =>
        InputDevices().FirstOrDefault(device => device.Uid == uid);

    /// <summary>
    /// Calls <paramref name="handler"/> whenever capture endpoints are added,
    /// removed, enabled, disabled, unplugged, or the default input changes.
    /// It runs on the <see cref="SynchronizationContext"/> current when this is
    /// called (the UI thread in the app, like the Mac's main queue), else on a
    /// thread-pool thread; never on the Core Audio notification thread, which
    /// holds a lock while it dispatches. Keep the returned observation alive
    /// for as long as updates are wanted and dispose it afterwards.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static AudioDeviceListObservation ObserveChanges(Action handler) => new(handler);

    [SupportedOSPlatform("windows")]
    private static AudioInputDevice? DefaultEndpoint(DataFlow flow, Role role = Role.Console)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(flow, role, out var endpoint) || endpoint is null)
        {
            return null;
        }
        using (endpoint)
        {
            return Describe(endpoint);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static AudioInputDevice? Describe(MMDevice endpoint)
    {
        try
        {
            var id = endpoint.ID;
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            var name = endpoint.FriendlyName;
            return new AudioInputDevice(id, string.IsNullOrEmpty(name) ? id : name);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The endpoint went away while it was being read.
            return null;
        }
    }
}

/// <summary>
/// Keeps a Core Audio endpoint notification registration alive until
/// disposed. Port of <c>AudioDeviceListObservation</c> in AudioDeviceList.swift.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioDeviceListObservation : IDisposable
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDeviceNotificationClient client;
    private readonly Action handler;
    private readonly SynchronizationContext? context;
    private int disposed;

    internal AudioDeviceListObservation(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.handler = handler;
        context = SynchronizationContext.Current;
        enumerator = new MMDeviceEnumerator();
        client = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        client.DeviceAdded += (_, _) => Notify();
        client.DeviceRemoved += (_, _) => Notify();
        client.DeviceStateChanged += (_, _) => Notify();
        client.DefaultDeviceChanged += (_, args) =>
        {
            if (args.Flow == DataFlow.Capture)
            {
                Notify();
            }
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        client.Dispose();
        enumerator.Dispose();
    }

    private void Notify()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        if (context is not null)
        {
            context.Post(_ => Run(), null);
        }
        else
        {
            ThreadPool.QueueUserWorkItem(_ => Run());
        }
    }

    private void Run()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            handler();
        }
    }
}
