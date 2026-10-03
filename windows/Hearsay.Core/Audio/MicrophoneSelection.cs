namespace Hearsay.Core.Audio;

/// <summary>
/// The Record tab's Microphone choice: an input device, or no microphone at
/// all so only system audio is recorded (PLAN.md 4.13). Port of
/// <c>MicrophoneChoice</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/MicrophoneSelection.swift.
/// </summary>
public abstract record MicrophoneChoice
{
    private MicrophoneChoice()
    {
    }

    /// <summary>"No microphone (system audio only)".</summary>
    public static MicrophoneChoice NoMicrophone { get; } = new NoMicrophoneChoice();

    /// <summary>The capture endpoint with this id (<see cref="AudioInputDevice.Uid"/>).</summary>
    public static MicrophoneChoice ForDevice(string uid) => new Device(uid);

    /// <summary>An input device.</summary>
    public sealed record Device(string Uid) : MicrophoneChoice;

    /// <summary>The no-microphone row; use <see cref="NoMicrophone"/>.</summary>
    public sealed record NoMicrophoneChoice : MicrophoneChoice;
}

/// <summary>
/// The Microphone picker's state and how it follows the device list
/// (PLAN.md 4.13). It is never persisted: each launch starts here, with the
/// default microphone once the first device list arrives, so a forgotten
/// "no microphone" never silently drops the user's own voice. Port of
/// <c>MicrophoneSelection</c> in MicrophoneSelection.swift.
/// </summary>
/// <remarks>
/// <para>Rules: a device that is still connected stays chosen; a chosen
/// device that went away falls back to the default input device, else the
/// first one; no input device at all selects "no microphone" by itself
/// (<see cref="IsAutomatic"/>), and when a device appears later that
/// automatic choice switches back to the default device while a "no
/// microphone" the user picked stays. The caller never updates the choice
/// while a session runs; Stop &amp; Start Next keeps "no microphone"
/// (<c>keepingNoMicrophone</c>).</para>
/// </remarks>
public sealed class MicrophoneSelection
{
    /// <summary>Before the first device list: the first <see cref="Update"/> picks the default.</summary>
    public MicrophoneSelection()
    {
        Choice = MicrophoneChoice.NoMicrophone;
        IsAutomatic = true;
    }

    public MicrophoneChoice Choice { get; private set; }

    /// <summary>"No microphone" was selected by Hearsay because no input device existed, not by the user.</summary>
    public bool IsAutomatic { get; private set; }

    /// <summary>The user picked <paramref name="choice"/> in the Microphone picker.</summary>
    public void Select(MicrophoneChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Choice = choice;
        IsAutomatic = false;
    }

    /// <summary>
    /// Follows a new device list. <paramref name="defaultId"/> is the system's
    /// default input device. With <paramref name="keepingNoMicrophone"/> a
    /// "no microphone" choice stays whoever made it (Stop &amp; Start Next
    /// keeps the session's sources).
    /// </summary>
    public void Update(IReadOnlyList<string> deviceIds, string? defaultId, bool keepingNoMicrophone = false)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        switch (Choice)
        {
            case MicrophoneChoice.Device device when deviceIds.Contains(device.Uid):
                return;
            case MicrophoneChoice.NoMicrophoneChoice when !IsAutomatic || keepingNoMicrophone:
                return;
        }
        if (defaultId is not null && deviceIds.Contains(defaultId))
        {
            Choice = MicrophoneChoice.ForDevice(defaultId);
            IsAutomatic = false;
        }
        else if (deviceIds.Count > 0)
        {
            Choice = MicrophoneChoice.ForDevice(deviceIds[0]);
            IsAutomatic = false;
        }
        else
        {
            Choice = MicrophoneChoice.NoMicrophone;
            IsAutomatic = true;
        }
    }

    /// <summary>The id of the chosen device; null for "no microphone".</summary>
    public string? DeviceId => Choice is MicrophoneChoice.Device device ? device.Uid : null;

    /// <summary>A microphone is part of the recording.</summary>
    public bool RecordsMicrophone => Choice is not MicrophoneChoice.NoMicrophoneChoice;

    /// <summary>
    /// Whether system audio is captured: always without a microphone,
    /// otherwise the stored "Also capture system audio" setting, which "no
    /// microphone" never changes.
    /// </summary>
    public bool CapturesSystemAudio(bool stored) => !RecordsMicrophone || stored;
}
