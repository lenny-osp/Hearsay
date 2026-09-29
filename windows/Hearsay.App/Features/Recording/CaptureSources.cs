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
/// Where a recording's audio and model come from. <see cref="Live"/> is the
/// app; only the debug replay (<see cref="RecordingReplay"/>) passes anything
/// else. Port of <c>CaptureSources</c> in RecordingController.swift. There is
/// no permission request: Windows cannot ask for microphone access in
/// advance, and a denied start fails with
/// <see cref="MicrophoneRecorderErrorKind.PermissionDenied"/> (PLAN.md 18.4, W3).
/// </summary>
internal sealed record CaptureSources(
    Func<IMicrophoneCapture> MakeMicrophone,
    Func<ISystemAudioCapture> MakeSystemAudio,
    Func<ModelStore, string> ModelPath)
{
    public static CaptureSources Live { get; } = new(
        () => new LiveMicrophone(),
        () => new LiveSystemAudio(),
        WhisperModelLocation.Active);
}
