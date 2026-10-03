using System.Threading.Channels;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Whisper;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// What <see cref="RecordingController"/> needs from a microphone recorder.
/// Production uses <see cref="MicrophoneRecorder"/>; the debug replay feeds a
/// WAV instead. Port of the <c>MicrophoneCapture</c> protocol in
/// mac/Hearsay/Features/Recording/RecordingController.swift.
/// </summary>
internal interface IMicrophoneCapture
{
    ChannelReader<TimedChunk> TimedSamples { get; }

    MicrophoneDiagnostics Diagnostics { get; }

    MicrophoneRecorderException? Failure { get; }

    void Start(AudioInputDevice? device);

    void Pause();

    void Resume();

    void Stop();
}

/// <summary>
/// What <see cref="RecordingController"/> needs from a system-audio recorder
/// (the <c>SystemAudioCapture</c> protocol in RecordingController.swift).
/// </summary>
internal interface ISystemAudioCapture
{
    ChannelReader<TimedChunk> TimedSamples { get; }

    SystemAudioRecorderException? Failure { get; }

    void Pause();

    void Resume();

    void Stop();
}

/// <summary>Core's <see cref="MicrophoneRecorder"/> behind <see cref="IMicrophoneCapture"/> (the Swift extension conformance).</summary>
internal sealed class LiveMicrophone : IMicrophoneCapture
{
    private readonly MicrophoneRecorder recorder = new();

    public ChannelReader<TimedChunk> TimedSamples => recorder.TimedSamples;

    public MicrophoneDiagnostics Diagnostics => recorder.Diagnostics;

    public MicrophoneRecorderException? Failure => recorder.Failure;

    public void Start(AudioInputDevice? device) => recorder.Start(device);

    public void Pause() => recorder.Pause();

    public void Resume() => recorder.Resume();

    public void Stop() => recorder.Stop();
}

/// <summary>Core's <see cref="SystemAudioRecorder"/> behind <see cref="ISystemAudioCapture"/>; started on creation.</summary>
internal sealed class LiveSystemAudio : ISystemAudioCapture
{
    private readonly SystemAudioRecorder recorder = new();

    /// <exception cref="SystemAudioRecorderException">No output device, or capture cannot start.</exception>
    public LiveSystemAudio() => recorder.Start();

    public ChannelReader<TimedChunk> TimedSamples => recorder.TimedSamples;

    public SystemAudioRecorderException? Failure => recorder.Failure;

    public void Pause() => recorder.Pause();

    public void Resume() => recorder.Resume();

    public void Stop() => recorder.Stop();
}

/// <summary>
/// The input devices and the system default one, as the Microphone picker
/// lists them (<see cref="CaptureSources.ListInputDevices"/>).
/// </summary>
internal sealed record InputDeviceList(IReadOnlyList<AudioInputDevice> Devices, string? DefaultId);

/// <summary>
/// Where a recording's audio and model come from. <see cref="Live"/> is the
/// app; only the debug replay (<see cref="RecordingReplay"/>) and the tests
/// pass anything else. Port of <c>CaptureSources</c> in
/// RecordingController.swift. There is no permission request: Windows cannot
/// ask for microphone access in advance, and a denied start fails with
/// <see cref="MicrophoneRecorderErrorKind.PermissionDenied"/> (PLAN.md 18.4,
/// W3). <c>MakeSystemAudio</c> gets true when system audio is the only
/// source (no microphone, PLAN.md 4.13), which is how the replay knows to
/// feed its WAV there. <c>ListInputDevices</c> null reads the real devices.
/// </summary>
internal sealed record CaptureSources(
    Func<IMicrophoneCapture> MakeMicrophone,
    Func<bool, ISystemAudioCapture> MakeSystemAudio,
    Func<ModelStore, string> ModelPath,
    Func<InputDeviceList>? ListInputDevices = null)
{
    public static CaptureSources Live { get; } = new(
        () => new LiveMicrophone(),
        _ => new LiveSystemAudio(),
        WhisperModelLocation.Active);
}
