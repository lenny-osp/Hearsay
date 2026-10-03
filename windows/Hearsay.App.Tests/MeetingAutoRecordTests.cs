using System.Runtime.InteropServices;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using static Hearsay.App.Tests.QueueRig;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="MeetingAutoRecord"/> on a real <see cref="RecordingController"/>
/// with fake capture and a fake queue engine (the
/// <see cref="RecordingControllerTests"/> rig): a scripted probe result and a
/// stepped clock drive <see cref="MeetingAutoRecord.Apply"/>, the timer's
/// decision step. The Mac runs these cases against MeetingAutoRecordRules
/// (MeetingAutoRecordRulesTests.swift). The
/// detector's own timing rules are in Hearsay.Tests (MeetingDetectorTests).
/// </summary>
public sealed class MeetingAutoRecordTests
{
    private static readonly CaptureSession TeamsCall =
        new("{0.0.1.00000000}.{headset}", "Headset Microphone", 4242, "ms-teams", null, CaptureSessionState.Active);

    private sealed class Rig : IDisposable
    {
        private readonly QueueRig queueRig;
        private readonly TranscriptionEngine engine = new();
        private readonly ModelStore models;
        private readonly Random random = new(11);

        public Rig(TimeSpan? pollInterval = null)
        {
            queueRig = new QueueRig(FinalPassTiming.WhenIdle);
            queueRig.Settings.CaptureSystemAudio = false;
            queueRig.Settings.AutoRecordTeamsMeetings = true;
            models = new ModelStore(queueRig.Settings, queueRig.Combine("models"));
            var sources = new CaptureSources(
                () =>
                {
                    MicRequests++;
                    if (MicFailure is { } failure) return new ThrowingMic(failure);
                    var mic = new RecordingControllerTests.FakeMic();
                    Mics.Add(mic);
                    return mic;
                },
                _ => new RecordingControllerTests.FakeSystem(),
                _ => throw new WhisperEngineException(WhisperEngineError.NoActiveModel, null),
                () => new InputDeviceList([new AudioInputDevice("{0.0.1.00000000}.{test}", "Test microphone")], "{0.0.1.00000000}.{test}"));
            Controller = new RecordingController(queueRig.Settings, models, engine, queueRig.Queue, queueRig.Spool, sources);
            Auto = new MeetingAutoRecord(
                Settings, Controller,
                () =>
                {
                    Interlocked.Increment(ref probeCalls);
                    if (ProbeFailure is { } failure) throw failure;
                    return [];
                },
                () => Now,
                (title, message) => Balloons.Add((title, message)),
                AskAsync,
                pollInterval ?? TimeSpan.FromHours(1));
        }

        private int probeCalls;

        public AppSettings Settings => queueRig.Settings;

        public TranscriptionQueue Queue => queueRig.Queue;

        public RecordingController Controller { get; }

        public MeetingAutoRecord Auto { get; }

        public List<RecordingControllerTests.FakeMic> Mics { get; } = [];

        public int MicRequests { get; private set; }

        public Exception? MicFailure { get; set; }

        public Exception? ProbeFailure { get; set; }

        public int ProbeCalls => Volatile.Read(ref probeCalls);

        public DateTimeOffset Now { get; private set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public List<(string Title, string Message)> Balloons { get; } = [];

        // The language prompt: each call waits for the test's answer (or a cancel).
        public int PromptCalls { get; private set; }

        public TaskCompletionSource<LanguageChoice?>? OpenPrompt { get; private set; }

        public bool PromptCancelled { get; private set; }

        private Task<LanguageChoice?> AskAsync(CancellationToken token)
        {
            PromptCalls++;
            var answer = new TaskCompletionSource<LanguageChoice?>();
            token.Register(() =>
            {
                PromptCancelled = true;
                answer.TrySetResult(null);
            });
            OpenPrompt = answer;
            return answer.Task;
        }

        /// <summary>One probe <paramref name="seconds"/> after the previous one, with or without a Teams call.</summary>
        public void Poll(bool teams, double seconds = 1)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Auto.Apply(teams ? [TeamsCall] : [], Now);
        }

        /// <summary>Probes until the detector reports Started (its start delay has passed).</summary>
        public void MeetingStarts()
        {
            Poll(true);
            Poll(true, MeetingDetector.StartDelay.TotalSeconds);
        }

        /// <summary>Probes without Teams until the detector reports Ended (its end grace has passed).</summary>
        public void MeetingEnds()
        {
            Poll(false);
            Poll(false, MeetingDetector.EndGrace.TotalSeconds);
        }

        /// <summary>One second of quiet audio through <paramref name="mic"/>.</summary>
        public async Task FeedAsync(RecordingControllerTests.FakeMic mic)
        {
            var start = HostClock.NowSeconds();
            for (var i = 0; i < 10; i++)
            {
                var samples = new float[1600];
                for (var n = 0; n < samples.Length; n++) samples[n] = (float)(random.NextDouble() - 0.5) * 0.1f;
                mic.Feed(samples, start + i / 10.0);
            }
            await WaitUntil(() => Controller.Elapsed >= 0.5, "the recording to catch up");
        }

        public async Task FinishAsync()
        {
            await Controller.StopForQuitAsync();
            await Queue.PrepareForQuitAsync();
        }

        public void Dispose()
        {
            Auto.Dispose();
            Controller.Dispose();
            engine.Dispose();
            models.Dispose();
            queueRig.Dispose();
        }
    }

    private static async Task RunAsync(Func<Rig, Task> body, TimeSpan? pollInterval = null)
    {
        using var ui = new UiThread();
        Rig? rig = null;
        try
        {
            await ui.RunAsync(async () =>
            {
                rig = new Rig(pollInterval);
                rig.Auto.Start();
                await body(rig);
            });
        }
        finally
        {
            if (rig is not null)
            {
                await ui.RunAsync(() =>
                {
                    rig.Dispose();
                    return Task.CompletedTask;
                });
            }
        }
    }

    /// <summary>A microphone whose start fails, as an unplugged or busy device does.</summary>
    private sealed class ThrowingMic(Exception failure) : IMicrophoneCapture
    {
        private readonly System.Threading.Channels.Channel<TimedChunk> channel = System.Threading.Channels.Channel.CreateUnbounded<TimedChunk>();

        public System.Threading.Channels.ChannelReader<TimedChunk> TimedSamples => channel.Reader;

        public MicrophoneDiagnostics Diagnostics => new() { DeviceName = "fake" };

        public MicrophoneRecorderException? Failure => null;

        public void Start(AudioInputDevice? device) => throw failure;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop() => channel.Writer.TryComplete();
    }

    private static (string, string) Started => (Strings.MeetingBalloonStartedTitle, Strings.MeetingBalloonStartedText);

    private static (string, string) Stopped => (Strings.MeetingBalloonStoppedTitle, Strings.MeetingBalloonStoppedText);

    private static (string, string) Failed => (Strings.MeetingBalloonFailedTitle, Strings.MeetingBalloonFailedText);

    [Fact]
    public Task WithTheSettingOffAMeetingNeverStartsARecording() => RunAsync(async rig =>
    {
        rig.Settings.AutoRecordTeamsMeetings = false;
        await WaitUntil(() => !rig.Auto.IsWatching, "watching to stop");
        rig.MeetingStarts();
        rig.Poll(true);
        Assert.False(rig.Controller.IsSessionActive);
        Assert.Empty(rig.Mics);
        Assert.Empty(rig.Balloons);
        Assert.Null(rig.Controller.AutomaticStartNotice);
    });

    [Fact]
    public Task TheTimerProbesOnlyWhileTheSettingIsOnAndGoesOnAfterAProbeFailure() => RunAsync(async rig =>
    {
        Assert.True(rig.Auto.IsWatching);
        await WaitUntil(() => rig.ProbeCalls >= 2, "the timer to probe");

        rig.Settings.AutoRecordTeamsMeetings = false;
        await WaitUntil(() => !rig.Auto.IsWatching, "watching to stop");
        await Task.Delay(50);
        var calls = rig.ProbeCalls;
        await Stays(() => rig.ProbeCalls == calls, "no probe while the setting is off");

        // AUDCLNT_E_DEVICE_INVALIDATED, as a probe of an endpoint that went away throws.
        rig.ProbeFailure = Marshal.GetExceptionForHR(unchecked((int)0x88890004));
        rig.Settings.AutoRecordTeamsMeetings = true;
        await WaitUntil(() => rig.Auto.IsWatching, "watching to start again");
        await WaitUntil(() => rig.ProbeCalls >= calls + 3, "probing to go on after failures");
        Assert.False(rig.Controller.IsSessionActive);
    }, pollInterval: TimeSpan.FromMilliseconds(10));

    [Fact]
    public Task AMeetingStartsARecordingWhenTheControllerIsIdle() => RunAsync(async rig =>
    {
        rig.Poll(true);
        Assert.False(rig.Controller.IsSessionActive, "the start delay has not passed yet");
        rig.Poll(true, MeetingDetector.StartDelay.TotalSeconds);
        Assert.True(rig.Controller.IsSessionActive);
        Assert.True(rig.Auto.IsAutomaticSession);
        Assert.Equal(Strings.AutomaticRecordingNotice, rig.Controller.AutomaticStartNotice);
        Assert.Equal("Recording started automatically for a Microsoft Teams meeting.", rig.Controller.AutomaticStartNotice);
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "recording");
        Assert.Equal([Started], rig.Balloons);
        Assert.Equal(LanguageChoice.Auto, rig.Controller.LanguageTracker.Choice);

        // Further probes during the meeting change nothing.
        rig.Poll(true);
        rig.Poll(true);
        Assert.Single(rig.Mics);
        await rig.FeedAsync(rig.Mics[0]);
        await rig.FinishAsync();
        Assert.Null(rig.Controller.AutomaticStartNotice);
    });

    [Fact]
    public Task AMeetingDuringAManualRecordingLeavesItAlone() => RunAsync(async rig =>
    {
        rig.Controller.Start();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "the user's recording");
        rig.MeetingStarts();
        Assert.False(rig.Auto.IsAutomaticSession);
        Assert.Null(rig.Controller.AutomaticStartNotice);
        Assert.Empty(rig.Balloons);
        await rig.FeedAsync(rig.Mics[0]);

        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.IsType<ControllerPhase.Recording>(rig.Controller.Phase);
        Assert.Empty(rig.Queue.Jobs);
        Assert.Empty(rig.Balloons);
        Assert.Single(rig.Mics);
        await rig.FinishAsync();
    });

    [Fact]
    public Task TheMeetingsEndStopsTheAutomaticRecording() => RunAsync(async rig =>
    {
        rig.MeetingStarts();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);
        // A short gap (a device switch) does not end the meeting.
        rig.Poll(false);
        rig.Poll(false, 5);
        rig.Poll(true);
        Assert.IsType<ControllerPhase.Recording>(rig.Controller.Phase);

        rig.Controller.Pause();
        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.IsType<ControllerPhase.Idle>(rig.Controller.Phase);
        var job = Assert.Single(rig.Queue.Jobs);
        Assert.True(File.Exists(job.Recording.Path));
        Assert.Equal([Started, Stopped], rig.Balloons);
        Assert.Null(rig.Controller.AutomaticStartNotice);
        Assert.False(rig.Auto.IsAutomaticSession);
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task AfterTheUserStopsNothingRestartsUntilTheNextMeeting() => RunAsync(async rig =>
    {
        rig.MeetingStarts();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);
        await rig.Controller.StopAsync();
        Assert.False(rig.Auto.IsAutomaticSession);
        Assert.Null(rig.Controller.AutomaticStartNotice);

        for (var i = 0; i < 5; i++) rig.Poll(true, 2);
        Assert.False(rig.Controller.IsSessionActive);
        Assert.Single(rig.Mics);

        rig.MeetingEnds();
        Assert.Equal([Started], rig.Balloons);
        rig.MeetingStarts();
        Assert.True(rig.Controller.IsSessionActive);
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "the next meeting's recording");
        Assert.Equal(2, rig.Mics.Count);
        Assert.True(rig.Auto.IsAutomaticSession);
        await rig.FeedAsync(rig.Mics[1]);
        await rig.FinishAsync();
    });

    [Fact]
    public Task StopAndStartNextKeepsTheNextSessionAutomatic() => RunAsync(async rig =>
    {
        rig.MeetingStarts();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);
        rig.Controller.StopAndStartNext();
        await WaitUntil(() => rig.Mics.Count == 2 && rig.Controller.Phase is ControllerPhase.Recording, "the next session");
        Assert.True(rig.Auto.IsAutomaticSession);
        Assert.Equal(Strings.AutomaticRecordingNotice, rig.Controller.AutomaticStartNotice);
        await rig.FeedAsync(rig.Mics[1]);

        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.False(rig.Controller.IsSessionActive);
        Assert.Equal(2, rig.Queue.Jobs.Count);
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task AStartThatFailsShowsTheFailureBalloonAndIsNotRetried() => RunAsync(async rig =>
    {
        rig.MicFailure = new InvalidOperationException("No microphone.");
        rig.MeetingStarts();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Failed, "the failure");
        Assert.Equal([Failed], rig.Balloons);
        Assert.False(rig.Auto.IsAutomaticSession);
        Assert.Null(rig.Controller.AutomaticStartNotice);
        Assert.Equal(1, rig.MicRequests);

        for (var i = 0; i < 5; i++) rig.Poll(true, 2);
        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.Equal(1, rig.MicRequests);
        Assert.Equal([Failed], rig.Balloons);
    });

    [Fact]
    public Task TurningTheSettingOffLeavesTheRecordingRunning() => RunAsync(async rig =>
    {
        rig.MeetingStarts();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);

        rig.Settings.AutoRecordTeamsMeetings = false;
        await WaitUntil(() => !rig.Auto.IsWatching, "watching to stop");
        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.IsType<ControllerPhase.Recording>(rig.Controller.Phase);
        Assert.Empty(rig.Queue.Jobs);
        Assert.Equal([Started], rig.Balloons);

        // On again mid-recording: the running session is not taken over.
        rig.Settings.AutoRecordTeamsMeetings = true;
        await WaitUntil(() => rig.Auto.IsWatching, "watching to start");
        rig.MeetingStarts();
        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.IsType<ControllerPhase.Recording>(rig.Controller.Phase);
        await rig.FinishAsync();
    });

    [Fact]
    public Task WithAskOnTheAnsweredLanguageIsUsedAndKept() => RunAsync(async rig =>
    {
        rig.Settings.AutoRecordAsksLanguage = true;
        rig.Settings.LanguageChoice = LanguageChoice.Auto;
        rig.MeetingStarts();
        Assert.Equal(1, rig.PromptCalls);
        Assert.True(rig.Auto.IsPrompting);
        Assert.False(rig.Controller.IsSessionActive, "nothing starts before the answer");

        rig.OpenPrompt?.SetResult(LanguageChoice.Fixed(TranscriptLanguage.German));
        await rig.Auto.Pending;
        Assert.False(rig.Auto.IsPrompting);
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.German), rig.Controller.LanguageChoice);
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.German), rig.Settings.LanguageChoice);
        Assert.True(rig.Controller.IsSessionActive);
        Assert.Equal(Strings.AutomaticRecordingNotice, rig.Controller.AutomaticStartNotice);
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "recording");
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.German), rig.Controller.LanguageTracker.Choice);
        Assert.Equal([Started], rig.Balloons);
        await rig.FeedAsync(rig.Mics[0]);

        rig.MeetingEnds();
        await rig.Auto.Pending;
        Assert.False(rig.Controller.IsSessionActive);
        Assert.Single(rig.Queue.Jobs);
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task WithAskOnDontRecordWaitsForTheNextMeeting() => RunAsync(async rig =>
    {
        rig.Settings.AutoRecordAsksLanguage = true;
        rig.MeetingStarts();
        rig.OpenPrompt?.SetResult(null);
        await rig.Auto.Pending;
        Assert.False(rig.Controller.IsSessionActive);
        Assert.Equal(LanguageChoice.Auto, rig.Settings.LanguageChoice);

        for (var i = 0; i < 5; i++) rig.Poll(true, 2);
        Assert.Equal(1, rig.PromptCalls);
        rig.MeetingEnds();
        Assert.False(rig.PromptCancelled);
        rig.MeetingStarts();
        Assert.Equal(2, rig.PromptCalls);
        rig.OpenPrompt?.SetResult(null);
        await rig.Auto.Pending;
        Assert.Empty(rig.Mics);
        Assert.Empty(rig.Balloons);
    });

    [Fact]
    public Task WithAskOnTheMeetingsEndCancelsAnOpenPrompt() => RunAsync(async rig =>
    {
        rig.Settings.AutoRecordAsksLanguage = true;
        rig.MeetingStarts();
        Assert.True(rig.Auto.IsPrompting);
        rig.MeetingEnds();
        Assert.True(rig.PromptCancelled);
        Assert.False(rig.Auto.IsPrompting);
        await rig.Auto.Pending;
        // A late answer after the cancel starts nothing either.
        rig.OpenPrompt?.TrySetResult(LanguageChoice.Fixed(TranscriptLanguage.Spanish));
        Assert.False(rig.Controller.IsSessionActive);
        Assert.Empty(rig.Mics);
        Assert.Equal(LanguageChoice.Auto, rig.Settings.LanguageChoice);
        Assert.Empty(rig.Balloons);
    });

    [Fact]
    public Task WithAskOnAManualRecordingIsNeverPrompted() => RunAsync(async rig =>
    {
        rig.Settings.AutoRecordAsksLanguage = true;
        rig.Controller.Start();
        await WaitUntil(() => rig.Controller.Phase is ControllerPhase.Recording, "the user's recording");
        rig.MeetingStarts();
        Assert.Equal(0, rig.PromptCalls);
        Assert.False(rig.Auto.IsPrompting);
        await rig.FeedAsync(rig.Mics[0]);
        rig.MeetingEnds();
        Assert.IsType<ControllerPhase.Recording>(rig.Controller.Phase);
        await rig.FinishAsync();
    });
}
