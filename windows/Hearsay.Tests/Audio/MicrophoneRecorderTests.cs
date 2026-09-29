using Hearsay.Core.Audio;
using static Hearsay.Tests.Audio.CaptureTestHelpers;
using Outcome = Hearsay.Core.Audio.MicrophoneRecorder.ConfigurationChangeOutcome;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Port of <c>ConfigurationRecoveryTests</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/AudioTests.swift.
/// </summary>
public class ConfigurationRecoveryTests
{
    [Fact]
    public void AllowsFiveRecoveriesPerTenSecondsThenRefuses()
    {
        var recovery = new ConfigurationRecovery();
        for (var second = 0; second < 5; second++)
        {
            Assert.True(recovery.AllowRecovery(second));
        }
        Assert.False(recovery.AllowRecovery(5));
        Assert.False(recovery.AllowRecovery(9.9));
        // The first recovery (t = 0) has left the window.
        Assert.True(recovery.AllowRecovery(10));
        Assert.False(recovery.AllowRecovery(10.5));
    }

    [Fact]
    public void SpacedOutChangesAreAlwaysAllowed()
    {
        var recovery = new ConfigurationRecovery();
        for (var index = 0; index < 50; index++)
        {
            Assert.True(recovery.AllowRecovery(index * 2.5));
        }
    }
}

/// <summary>
/// Port of <c>MicrophoneRecorderConfigurationTests</c> in AudioTests.swift. The
/// Swift tests drive the recorder through <c>beginForTesting(probe:)</c> and a
/// private <c>NotificationCenter</c>; here the recorder starts on a
/// <see cref="FakeCaptureSession"/> whose events stand in for the
/// notifications. <c>interruptionIsCountedAndRestartsWhenItEnds</c> is not
/// ported: WASAPI has no interruption notification (PLAN.md 18.4).
/// </summary>
public class MicrophoneRecorderConfigurationTests
{
    private static (MicrophoneRecorder Recorder, FakeCaptureSession Session) RecordingRecorder(bool valid = true)
    {
        var session = new FakeCaptureSession { Active = valid };
        var recorder = new MicrophoneRecorder((_, receiver) =>
        {
            session.Receiver = receiver;
            return session;
        });
        recorder.Start(null);
        Assert.Equal(1, session.Starts);
        session.ResetCounters();
        return (recorder, session);
    }

    [Fact]
    public async Task ChangeRightAfterStartRestartsAndKeepsRecording()
    {
        var (recorder, _) = RecordingRecorder();
        var reinstalls = 0;
        var restarts = 0;
        var outcome = recorder.HandleConfigurationChange(
            inputFormatValid: true, now: 0, reinstall: () => reinstalls++, restart: () => restarts++);
        Assert.Equal(Outcome.Recovered, outcome);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Null(recorder.Failure);
        Assert.Equal(1, recorder.ConfigurationRecoveries);
        Assert.Equal(1, reinstalls);
        Assert.Equal(1, restarts);

        // The stream is still open: a later chunk arrives before stop.
        recorder.Output.Yield([0.25f], 1);
        recorder.Stop();
        var chunks = await Collect(recorder.Samples);
        Assert.Equal([[0.25f]], chunks);
        Assert.Null(recorder.Failure);
    }

    [Fact]
    public async Task RuntimeErrorIsRecoveredNotFinished()
    {
        var (recorder, session) = RecordingRecorder();
        session.RaiseRuntimeError("The audio device was invalidated. (0x88890004)");
        await WaitUntil(() => session.Starts > 0);
        Assert.Equal(1, session.Probes);
        // WASAPI cannot restart an invalidated client: the endpoint is reopened.
        Assert.Equal(1, session.Reinstalls);
        Assert.Equal(1, session.Starts);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Equal(1, recorder.ConfigurationRecoveries);
        Assert.Equal(1, recorder.Diagnostics.RuntimeErrors);
        Assert.Contains("0x88890004", recorder.Diagnostics.LastRuntimeError, StringComparison.Ordinal);

        // An error of another session is not ours.
        var other = new FakeCaptureSession();
        other.RaiseRuntimeError("other");
        await Task.Delay(50);
        Assert.Equal(1, session.Probes);

        recorder.Output.Yield([0.5f, -0.5f], 2);
        recorder.Stop();
        Assert.Equal([[0.5f, -0.5f]], await Collect(recorder.Samples));
    }

    [Fact]
    public async Task RuntimeErrorWithDeviceGoneFinishes()
    {
        var (recorder, session) = RecordingRecorder(valid: false);
        session.RaiseRuntimeError("gone");
        await WaitUntil(() => recorder.State == RecorderState.Stopped);
        Assert.Equal(MicrophoneRecorderErrorKind.ConfigurationChanged, recorder.Failure?.Kind);
        Assert.Empty(await Collect(recorder.Samples));
        Assert.True(session.Drained);
    }

    [Fact]
    public async Task DisconnectOfChosenDeviceFinishesOthersAreIgnored()
    {
        var (recorder, session) = RecordingRecorder();
        session.RaiseDeviceRemoved("{0.0.1.00000000}.{another}");
        await Task.Delay(50);
        Assert.Equal(RecorderState.Recording, recorder.State);

        recorder.Output.Yield([0.125f], 1);
        session.RaiseDeviceRemoved(session.DeviceUid);
        await WaitUntil(() => recorder.State == RecorderState.Stopped);
        Assert.Equal(RecorderState.Stopped, recorder.State);
        Assert.Equal(MicrophoneRecorderErrorKind.ConfigurationChanged, recorder.Failure?.Kind);
        // What arrived before the disconnect is still delivered.
        Assert.Equal([[0.125f]], await Collect(recorder.Samples));
    }

    [Fact]
    public void PausedRecorderReinstallsWithoutStarting()
    {
        var (recorder, session) = RecordingRecorder();
        recorder.Pause();
        Assert.Equal(1, session.Stops);
        var reinstalls = 0;
        var restarts = 0;
        var outcome = recorder.HandleConfigurationChange(
            inputFormatValid: true, now: 0, reinstall: () => reinstalls++, restart: () => restarts++);
        Assert.Equal(Outcome.Recovered, outcome);
        Assert.Equal(RecorderState.Paused, recorder.State);
        Assert.Equal(1, reinstalls);
        Assert.Equal(0, restarts);
        recorder.Resume();
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Equal(1, session.Starts);
    }

    [Fact]
    public async Task DeviceGoneFinishesWithConfigurationChanged()
    {
        var (recorder, _) = RecordingRecorder();
        var outcome = recorder.HandleConfigurationChange(inputFormatValid: false, now: 0, restart: () => { });
        Assert.Equal(Outcome.Finished, outcome);
        Assert.Equal(RecorderState.Stopped, recorder.State);
        Assert.Equal(MicrophoneRecorderErrorKind.ConfigurationChanged, recorder.Failure?.Kind);
        Assert.Empty(await Collect(recorder.Samples));
    }

    [Fact]
    public void FailedRestartFinishesWithConfigurationChanged()
    {
        var (recorder, _) = RecordingRecorder();
        var outcome = recorder.HandleConfigurationChange(
            inputFormatValid: true, now: 0, restart: () => throw new InvalidOperationException("boom"));
        Assert.Equal(Outcome.Finished, outcome);
        Assert.Equal(MicrophoneRecorderErrorKind.ConfigurationChanged, recorder.Failure?.Kind);
        Assert.Equal(0, recorder.ConfigurationRecoveries);
    }

    [Fact]
    public void FlappingDeviceFinishesOnSixthChangeWithinTenSeconds()
    {
        var (recorder, _) = RecordingRecorder();
        var restarts = 0;
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(Outcome.Recovered,
                recorder.HandleConfigurationChange(inputFormatValid: true, now: index, restart: () => restarts++));
        }
        Assert.Equal(Outcome.Finished, recorder.HandleConfigurationChange(inputFormatValid: true, now: 6, restart: () => { }));
        Assert.Equal(MicrophoneRecorderErrorKind.ConfigurationChanged, recorder.Failure?.Kind);
        Assert.Equal(5, recorder.ConfigurationRecoveries);
        Assert.Equal(5, restarts);
    }

    [Fact]
    public void ChangesWhenNotRecordingAreIgnored()
    {
        var idle = new MicrophoneRecorder((_, _) => new FakeCaptureSession());
        Assert.Equal(Outcome.Ignored, idle.HandleConfigurationChange(inputFormatValid: false, restart: () => { }));
        Assert.Equal(RecorderState.Idle, idle.State);

        var (stopped, _) = RecordingRecorder();
        stopped.Stop();
        Assert.Equal(Outcome.Ignored, stopped.HandleConfigurationChange(inputFormatValid: false, restart: () => { }));
        Assert.Null(stopped.Failure);
    }

    // Windows-only rules around the Swift ones.

    [Fact]
    public async Task FailedResumeIsHandledAsACaptureError()
    {
        var (recorder, session) = RecordingRecorder(valid: false);
        recorder.Pause();
        session.StartError = new FakeComException("gone", unchecked((int)0x88890004));
        recorder.Resume();
        await WaitUntil(() => recorder.State == RecorderState.Stopped);
        Assert.Equal(MicrophoneRecorderErrorKind.ConfigurationChanged, recorder.Failure?.Kind);
        Assert.Equal(1, recorder.Diagnostics.RuntimeErrors);
    }

    [Fact]
    public void AccessDeniedAtStartIsPermissionDenied()
    {
        var session = new FakeCaptureSession { StartError = new FakeComException("denied", unchecked((int)0x80070005)) };
        var recorder = new MicrophoneRecorder((_, _) => session);
        var error = Assert.Throws<MicrophoneRecorderException>(() => recorder.Start(null));
        Assert.Equal(MicrophoneRecorderErrorKind.PermissionDenied, error.Kind);
        Assert.StartsWith("Hearsay has no microphone access.", error.Message, StringComparison.Ordinal);
        Assert.True(session.Disposed);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public void UnauthorizedAccessAtStartIsPermissionDenied()
    {
        var session = new FakeCaptureSession { StartError = new UnauthorizedAccessException("Access is denied.") };
        var recorder = new MicrophoneRecorder((_, _) => session);
        var error = Assert.Throws<MicrophoneRecorderException>(() => recorder.Start(null));
        Assert.Equal(MicrophoneRecorderErrorKind.PermissionDenied, error.Kind);
    }

    [Fact]
    public void OtherStartFailuresAreEngineFailed()
    {
        var session = new FakeCaptureSession { StartError = new FakeComException("busy", unchecked((int)0x8889000A)) };
        var recorder = new MicrophoneRecorder((_, _) => session);
        var error = Assert.Throws<MicrophoneRecorderException>(() => recorder.Start(null));
        Assert.Equal(MicrophoneRecorderErrorKind.EngineFailed, error.Kind);
        Assert.Equal("Audio capture failed: busy (0x8889000A)", error.Message);
    }

    [Fact]
    public void SecondStartIsInvalidState()
    {
        var (recorder, _) = RecordingRecorder();
        var error = Assert.Throws<MicrophoneRecorderException>(() => recorder.Start(null));
        Assert.Equal(MicrophoneRecorderErrorKind.InvalidState, error.Kind);
        Assert.Equal("This recorder has already been started.", error.Message);
    }

    [Fact]
    public void StartRecordsTheDeviceInTheDiagnostics()
    {
        var (recorder, _) = RecordingRecorder();
        var diagnostics = recorder.Diagnostics;
        Assert.Equal("Fake Mic", diagnostics.DeviceName);
        Assert.Equal(48_000, diagnostics.DeviceNominalSampleRate);
        Assert.Equal("endpoint mix format, shared mode", diagnostics.RequestedFormat);
        Assert.Equal("2 ch, 48000 Hz, Int24 interleaved", diagnostics.DeviceFormat);
    }

    [Fact]
    public async Task PauseDropsChunksAndRemovesPausedTimeFromTimestamps()
    {
        var (recorder, _) = RecordingRecorder();
        var timed = recorder.TimedSamples;
        recorder.Output.Yield([0.1f], 100);
        recorder.Pause();
        recorder.Output.Yield([0.2f], 101);
        recorder.Resume();
        recorder.Output.Yield([0.3f], HostClock.NowSeconds());
        recorder.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chunks = new List<TimedChunk>();
        await foreach (var chunk in timed.ReadAllAsync(timeout.Token))
        {
            chunks.Add(chunk);
        }
        Assert.Equal(2, chunks.Count);
        Assert.Equal(0.1f, chunks[0].Samples[0]);
        Assert.Equal(0.3f, chunks[1].Samples[0]);
        Assert.True(chunks[1].HostTime < HostClock.NowSeconds());
    }
}

/// <summary>Port of <c>NoAudioWatchdogTests</c> in AudioTests.swift.</summary>
public class NoAudioWatchdogTests
{
    [Fact]
    public void FiresAfterThreeSecondsOfRecordingWithoutSamples()
    {
        var watchdog = new NoAudioWatchdog();
        Assert.False(watchdog.Check(100, isRecording: true, samplesDelivered: 0));
        Assert.False(watchdog.Check(101.5, isRecording: true, samplesDelivered: 0));
        Assert.False(watchdog.Check(102.9, isRecording: true, samplesDelivered: 0));
        Assert.True(watchdog.Check(103, isRecording: true, samplesDelivered: 0));
    }

    [Fact]
    public void NeverFiresOnceAudioArrived()
    {
        var watchdog = new NoAudioWatchdog();
        Assert.False(watchdog.Check(0, isRecording: true, samplesDelivered: 0));
        Assert.False(watchdog.Check(1, isRecording: true, samplesDelivered: 1600));
        // A later silent device is the silence warning's job, not this one.
        Assert.False(watchdog.Check(10, isRecording: true, samplesDelivered: 1600));
    }

    [Fact]
    public void PausedTimeDoesNotCount()
    {
        var watchdog = new NoAudioWatchdog();
        Assert.False(watchdog.Check(0, isRecording: true, samplesDelivered: 0));
        Assert.False(watchdog.Check(2, isRecording: true, samplesDelivered: 0));
        Assert.False(watchdog.Check(3, isRecording: false, samplesDelivered: 0));
        Assert.False(watchdog.Check(60, isRecording: false, samplesDelivered: 0));
        Assert.False(watchdog.Check(60.5, isRecording: true, samplesDelivered: 0));
        Assert.False(watchdog.Check(61, isRecording: true, samplesDelivered: 0));
        Assert.True(watchdog.Check(61.5, isRecording: true, samplesDelivered: 0));
    }

    [Fact]
    public void NoAudioMessageNamesTheDevice()
    {
        var message = new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoAudio, "AirPods Pro").Message;
        Assert.StartsWith("No audio from AirPods Pro.", message, StringComparison.Ordinal);
        Assert.Contains("3 seconds", message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Port of <c>MicrophoneDiagnosticsTests.describesFormatsAndCounters</c> in
/// AudioTests.swift, with a <see cref="CaptureFormat"/> in place of an
/// <c>AudioStreamBasicDescription</c> (WASAPI shared mode is interleaved).
/// </summary>
public class MicrophoneDiagnosticsTests
{
    [Fact]
    public void DescribesFormatsAndCounters()
    {
        Assert.Equal("1 ch, 16000 Hz, Float32 interleaved",
            MicrophoneDiagnostics.Describe(new CaptureFormat(16_000, 1, 32, IsFloat: true)));
        Assert.Equal("2 ch, 44100 Hz, Int16 interleaved",
            MicrophoneDiagnostics.Describe(new CaptureFormat(44_100, 2, 16, IsFloat: false)));

        var diagnostics = new MicrophoneDiagnostics
        {
            DeviceName = "AirPods Pro",
            DeviceNominalSampleRate = 24_000,
            ConversionFailures = 2,
            LastConversionError = "boom",
        };
        var text = diagnostics.Description;
        Assert.Contains("device nominal rate: 24000 Hz", text, StringComparison.Ordinal);
        Assert.Contains("conversion failures: 2 (last: boom)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionHasTheMacLinesForAnEmptyRecorder()
    {
        Assert.Equal(
            "device: system default\n" +
            "device nominal rate: unknown\n" +
            "requested format: none\n" +
            "device format: unknown\n" +
            "delivered format: none\n" +
            "callbacks: 0, frames: 0, samples delivered: 0\n" +
            "conversion failures: 0\n" +
            "runtime errors: 0, interruptions: 0\n" +
            "timestamp lag: n/a",
            new MicrophoneDiagnostics().Description);
    }
}
