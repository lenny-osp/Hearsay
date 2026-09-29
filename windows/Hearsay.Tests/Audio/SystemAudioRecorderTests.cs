using Hearsay.Core.Audio;
using static Hearsay.Tests.Audio.CaptureTestHelpers;

namespace Hearsay.Tests.Audio;

/// <summary>
/// <see cref="SystemAudioRecorder"/> on a <see cref="FakeCaptureSession"/>. The
/// Swift <c>SystemAudioRecorder</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/SystemAudioRecorder.swift has no
/// unit tests (it needs ScreenCaptureKit); these cover its pause and stop
/// contract and the Windows-only device-change handling (PLAN.md 18.4).
/// </summary>
public class SystemAudioRecorderTests
{
    private static (SystemAudioRecorder Recorder, FakeCaptureSession Session) RecordingRecorder(bool valid = true)
    {
        var session = new FakeCaptureSession(name: "Speakers (Fake)") { Active = valid };
        var recorder = new SystemAudioRecorder(receiver =>
        {
            session.Receiver = receiver;
            return session;
        });
        recorder.Start();
        session.ResetCounters();
        return (recorder, session);
    }

    [Fact]
    public async Task PauseKeepsCaptureRunningAndDropsChunks()
    {
        var (recorder, session) = RecordingRecorder();
        recorder.Output.Yield([0.1f], 1);
        recorder.Pause();
        Assert.Equal(RecorderState.Paused, recorder.State);
        Assert.Equal(0, session.Stops);
        recorder.Output.Yield([0.2f], 2);
        recorder.Resume();
        recorder.Output.Yield([0.3f], 3);
        recorder.Stop();
        Assert.Equal([[0.1f], [0.3f]], await Collect(recorder.Samples));
        Assert.Null(recorder.Failure);
        Assert.True(session.Drained);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task DefaultOutputChangeMovesCaptureToTheNewDevice()
    {
        var (recorder, session) = RecordingRecorder();
        session.RaiseDefaultDeviceChanged();
        await WaitUntil(() => session.Starts > 0);
        Assert.Equal(1, session.Reinstalls);
        Assert.Equal(1, session.Starts);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Equal(1, recorder.ConfigurationRecoveries);
        recorder.Stop();
        Assert.Null(recorder.Failure);
    }

    [Fact]
    public async Task CaptureErrorWithNoOutputLeftStops()
    {
        var (recorder, session) = RecordingRecorder(valid: false);
        session.RaiseRuntimeError("The audio device was invalidated. (0x88890004)");
        await WaitUntil(() => recorder.State == RecorderState.Stopped);
        Assert.Equal(SystemAudioRecorderErrorKind.StreamStopped, recorder.Failure?.Kind);
        Assert.Equal("System audio capture stopped: The audio device was invalidated. (0x88890004)",
            recorder.Failure?.Message);
        Assert.Equal(1, recorder.Diagnostics.RuntimeErrors);
        Assert.Empty(await Collect(recorder.Samples));
    }

    [Fact]
    public void FlappingOutputStopsOnTheSixthChangeWithinTenSeconds()
    {
        var (recorder, session) = RecordingRecorder();
        for (var index = 0; index < 5; index++)
        {
            Assert.True(recorder.HandleDeviceChange("changed", now: index));
        }
        Assert.False(recorder.HandleDeviceChange("changed", now: 6));
        Assert.Equal(SystemAudioRecorderErrorKind.StreamStopped, recorder.Failure?.Kind);
        Assert.Equal(5, recorder.ConfigurationRecoveries);
        Assert.Equal(5, session.Starts);
    }

    [Fact]
    public void ChangeWhilePausedStillRestartsCapture()
    {
        var (recorder, session) = RecordingRecorder();
        recorder.Pause();
        Assert.True(recorder.HandleDeviceChange("changed", now: 0));
        Assert.Equal(1, session.Starts);
        Assert.Equal(RecorderState.Paused, recorder.State);
    }

    [Fact]
    public void FailedReopenStops()
    {
        var (recorder, session) = RecordingRecorder();
        session.StartError = new SystemAudioRecorderException(SystemAudioRecorderErrorKind.NoOutputDevice);
        Assert.False(recorder.HandleDeviceChange("changed", now: 0));
        Assert.Equal(SystemAudioRecorderErrorKind.StreamStopped, recorder.Failure?.Kind);
        Assert.Equal("System audio capture stopped: No output device is available to capture system audio from.",
            recorder.Failure?.Message);
    }

    [Fact]
    public void ChangesWhenNotRecordingAreIgnored()
    {
        var idle = new SystemAudioRecorder(_ => new FakeCaptureSession());
        Assert.False(idle.HandleDeviceChange("changed"));
        Assert.Equal(RecorderState.Idle, idle.State);

        var (stopped, _) = RecordingRecorder();
        stopped.Stop();
        Assert.False(stopped.HandleDeviceChange("changed"));
        Assert.Null(stopped.Failure);
    }

    [Fact]
    public void StartWithoutOutputDeviceThrows()
    {
        var recorder = new SystemAudioRecorder(_ => throw new SystemAudioRecorderException(SystemAudioRecorderErrorKind.NoOutputDevice));
        var error = Assert.Throws<SystemAudioRecorderException>(recorder.Start);
        Assert.Equal(SystemAudioRecorderErrorKind.NoOutputDevice, error.Kind);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }
}
