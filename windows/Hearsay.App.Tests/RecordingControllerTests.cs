using System.ComponentModel;
using System.Threading.Channels;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Transcription;
using Hearsay.Core;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using static Hearsay.App.Tests.QueueRig;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="RecordingController"/> on top of the queue (PLAN.md 4.9; the
/// port of mac/Hearsay/Features/Recording/RecordingController.swift): Stop
/// hands the session to the queue and the controller is idle at once, Stop
/// &amp; Start Next keeps the input device, the system-audio choice and the
/// language choice, a capture failure stays on the Record tab. The capture is
/// fake (a channel the test feeds), the model is missing so no live preview
/// runs, and the queue's engine is fake.
/// </summary>
public sealed class RecordingControllerTests
{
    private sealed class Rig : IDisposable
    {
        private readonly QueueRig queueRig;
        private readonly TranscriptionEngine engine = new();
        private readonly ModelStore models;
        private readonly Random random = new(7);

        public Rig(FinalPassTiming timing)
        {
            queueRig = new QueueRig(timing);
            queueRig.Settings.CaptureSystemAudio = false;
            models = new ModelStore(queueRig.Settings, queueRig.Combine("models"));
            Sources = new CaptureSources(
                () =>
                {
                    var mic = new FakeMic();
                    Mics.Add(mic);
                    return mic;
                },
                () =>
                {
                    var system = new FakeSystem();
                    Systems.Add(system);
                    return system;
                },
                _ => throw new WhisperEngineException(WhisperEngineError.NoActiveModel, null));
            Controller = new RecordingController(queueRig.Settings, models, engine, queueRig.Queue, queueRig.Spool, Sources);
            Queue.PropertyChanged += OnQueueChanged;
        }

        public QueueRig Scratch => queueRig;

        public TranscriptionQueue Queue => queueRig.Queue;

        public AppSettings Settings => queueRig.Settings;

        public RecordingController Controller { get; }

        public CaptureSources Sources { get; }

        public List<FakeMic> Mics { get; } = [];

        public List<FakeSystem> Systems { get; } = [];

        /// <summary>The session-active flag of the queue went false at some point.</summary>
        public bool SessionFlagDropped { get; private set; }

        private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TranscriptionQueue.IsSessionActive) && !Queue.IsSessionActive) SessionFlagDropped = true;
        }

        /// <summary>One second of quiet audio through <paramref name="mic"/> and, when the session has one, its system audio.</summary>
        public async Task FeedAsync(FakeMic mic)
        {
            var system = Systems.Count > 0 && Mics.IndexOf(mic) < Systems.Count ? Systems[Mics.IndexOf(mic)] : null;
            var start = HostClock.NowSeconds();
            for (var i = 0; i < 10; i++)
            {
                var samples = new float[1600];
                for (var n = 0; n < samples.Length; n++) samples[n] = (float)(random.NextDouble() - 0.5) * 0.1f;
                mic.Feed(samples, start + i / 10.0);
                system?.Feed(new float[1600], start + i / 10.0);
            }
            await WaitUntil(() => Controller.Elapsed >= 0.5, "the recording to catch up");
        }

        public void Dispose()
        {
            Controller.Dispose();
            engine.Dispose();
            models.Dispose();
            queueRig.Dispose();
        }
    }

    private static async Task RunAsync(FinalPassTiming timing, Func<Rig, Task> body)
    {
        using var ui = new UiThread();
        Rig? rig = null;
        try
        {
            await ui.RunAsync(async () =>
            {
                rig = new Rig(timing);
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

    [Fact]
    public Task StopHandsTheSessionToTheQueueAndTheControllerIsIdleAtOnce() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var controller = rig.Controller;
        controller.Start();
        Assert.IsType<ControllerPhase.Starting>(controller.Phase);
        Assert.True(rig.Queue.IsSessionActive);
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);

        await controller.StopAsync();
        Assert.IsType<ControllerPhase.Idle>(controller.Phase);
        Assert.True(controller.CanStart, "a recording being transcribed never blocks Start");
        Assert.False(rig.Queue.IsSessionActive);
        var job = Assert.Single(rig.Queue.Jobs);
        Assert.True(job.Recording.InSpool);
        Assert.True(File.Exists(job.Recording.Path));
        Assert.Equal(Path.GetDirectoryName(job.Recording.Path), rig.Scratch.SpoolPath);
        Assert.Equal(LanguageChoice.Auto, job.Tracker.Choice);
        Assert.Empty(controller.LiveSegments);
        Assert.Contains("Live preview off", job.LiveNotice);

        // A new recording starts while the first waits (or runs) in the queue.
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "the second recording");
        Assert.Equal(2, rig.Mics.Count);
        await rig.FeedAsync(rig.Mics[1]);
        await controller.StopForQuitAsync();
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task StopUnderManualTimingHoldsTheJobAndAStopAndStartNextHoldsBoth() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var controller = rig.Controller;
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);
        controller.StopAndStartNext();
        await WaitUntil(() => rig.Queue.Jobs.Count == 1 && controller.Phase is ControllerPhase.Recording && rig.Mics.Count == 2, "the next session");
        await rig.FeedAsync(rig.Mics[1]);
        await controller.StopAsync();

        Assert.Equal(2, rig.Queue.Jobs.Count);
        Assert.All(rig.Queue.Jobs, job =>
        {
            Assert.False(job.IsReleased);
            Assert.True(job.IsHeld);
            Assert.Equal(TranscriptionJobState.Waiting, job.State);
        });
        Assert.True(rig.Queue.AllPendingHeld);
        Assert.False(rig.Queue.BlocksUpdateInstall);
        await Stays(() => rig.Queue.Jobs.All(job => job.State == TranscriptionJobState.Waiting), "nothing is transcribed by itself");

        rig.Queue.ReleaseAll();
        await WaitUntil(() => rig.Queue.Jobs.All(job => job.State == TranscriptionJobState.Done), "both jobs after Transcribe All");
    });

    [Fact]
    public Task TryAgainAfterACaptureFailureIsReleasedUnderManualTiming() => RunAsync(FinalPassTiming.Manual, async rig =>
    {
        var controller = rig.Controller;
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        var mic = rig.Mics[0];
        await rig.FeedAsync(mic);
        mic.FailWith = new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoAudio, "the test microphone");
        await controller.StopAsync();
        Assert.True(controller.CanRetryTranscription);

        controller.RetryTranscription();
        var job = Assert.Single(rig.Queue.Jobs);
        Assert.True(job.IsReleased);
        Assert.False(job.IsHeld);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the retry to be transcribed");
    });

    [Fact]
    public Task StopAndStartNextKeepsTheDeviceTheSystemAudioAndTheLanguageChoice() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var controller = rig.Controller;
        rig.Settings.LanguageChoice = LanguageChoice.Auto;
        rig.Settings.CaptureSystemAudio = true;
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        var firstTracker = controller.LanguageTracker;
        await rig.FeedAsync(rig.Mics[0]);

        controller.StopAndStartNext();
        await WaitUntil(() => rig.Mics.Count == 2 && controller.Phase is ControllerPhase.Recording, "the next session");
        Assert.False(rig.SessionFlagDropped, "the session-active flag never drops between the two sessions");
        Assert.Equal(rig.Mics[0].StartedWith, rig.Mics[1].StartedWith);
        Assert.Equal(2, rig.Systems.Count);
        Assert.True(rig.Settings.CaptureSystemAudio);
        Assert.Equal(LanguageChoice.Auto, rig.Settings.LanguageChoice);
        Assert.Equal(LanguageChoice.Auto, controller.LanguageTracker.Choice);
        Assert.True(controller.LanguageTracker.IsUndecided, "Auto detects again for the new session");
        Assert.NotSame(firstTracker, controller.LanguageTracker);
        var job = Assert.Single(rig.Queue.Jobs);
        Assert.Same(firstTracker, job.Tracker);
        Assert.Equal(TranscriptionJobState.Waiting, job.State);
        Assert.True(rig.Queue.IsPausedForSession(job));
        Assert.False(controller.CanStart);

        await rig.FeedAsync(rig.Mics[1]);
        await controller.StopAsync();
        Assert.Equal(2, rig.Queue.Jobs.Count);
        Assert.NotEqual(rig.Queue.Jobs[0].Recording.Path, rig.Queue.Jobs[1].Recording.Path);
        Assert.False(rig.SessionFlagDropped && rig.Queue.IsSessionActive);
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task AFixedLanguageChoiceIsKeptForTheNextSession() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var controller = rig.Controller;
        rig.Settings.LanguageChoice = LanguageChoice.Fixed(TranscriptLanguage.German);
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);
        controller.StopAndStartNext();
        await WaitUntil(() => rig.Mics.Count == 2 && controller.Phase is ControllerPhase.Recording, "the next session");
        Assert.Equal(TranscriptLanguage.German, controller.SessionLanguage);
        Assert.Empty(rig.Systems);
        await rig.FeedAsync(rig.Mics[1]);
        await controller.StopForQuitAsync();
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task QuittingStartsNoNextSessionEvenAfterStopAndStartNext() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var controller = rig.Controller;
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        await rig.FeedAsync(rig.Mics[0]);
        controller.StopAndStartNext();
        await controller.StopForQuitAsync();
        Assert.IsType<ControllerPhase.Idle>(controller.Phase);
        Assert.Single(rig.Mics);
        Assert.Single(rig.Queue.Jobs);
        controller.Start();
        Assert.IsType<ControllerPhase.Idle>(controller.Phase);
        controller.QuitCancelled();
        controller.Start();
        Assert.IsType<ControllerPhase.Starting>(controller.Phase);
        await controller.StopAsync();
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task AStopAndStartNextIsIgnoredUnlessAudioIsBeingCaptured() => RunAsync(FinalPassTiming.WhenIdle, rig =>
    {
        rig.Controller.StopAndStartNext();
        Assert.IsType<ControllerPhase.Idle>(rig.Controller.Phase);
        Assert.Empty(rig.Mics);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ACaptureFailureKeepsTheWavInTheOutputFolderAndTryAgainQueuesIt() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var controller = rig.Controller;
        controller.Start();
        await WaitUntil(() => controller.Phase is ControllerPhase.Recording, "recording");
        var mic = rig.Mics[0];
        await rig.FeedAsync(mic);
        mic.FailWith = new MicrophoneRecorderException(MicrophoneRecorderErrorKind.NoAudio, "the test microphone");

        await controller.StopAsync();
        Assert.IsType<ControllerPhase.Failed>(controller.Phase);
        Assert.Empty(rig.Queue.Jobs);
        Assert.NotNull(controller.FinishedRecording);
        Assert.True(File.Exists(controller.FinishedRecording));
        Assert.Equal(rig.Scratch.OutputPath, Path.GetDirectoryName(controller.FinishedRecording));
        Assert.Contains(controller.FinishedRecording, controller.ErrorMessage);
        Assert.True(controller.CanRetryTranscription);
        Assert.True(controller.CanStart);

        controller.RetryTranscription();
        Assert.IsType<ControllerPhase.Idle>(controller.Phase);
        Assert.Null(controller.FinishedRecording);
        var job = Assert.Single(rig.Queue.Jobs);
        Assert.False(job.Recording.InSpool);
        Assert.True(File.Exists(job.Recording.Path));
        Assert.Contains(Path.GetFileNameWithoutExtension(job.Recording.Path), rig.Queue.BusyStems);
        await rig.Queue.PrepareForQuitAsync();
    });

    [Fact]
    public Task StopWhileStartingEndsIdleAndHandsNothingOver() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var controller = rig.Controller;
        controller.Start();
        await controller.StopAsync();
        Assert.False(controller.IsSessionActive);
        Assert.Empty(rig.Queue.Jobs);
    });

    /// <summary>A microphone the test feeds by hand.</summary>
    internal sealed class FakeMic : IMicrophoneCapture
    {
        private readonly Channel<TimedChunk> channel = Channel.CreateUnbounded<TimedChunk>();
        private long delivered;

        public ChannelReader<TimedChunk> TimedSamples => channel.Reader;

        public MicrophoneDiagnostics Diagnostics => new() { DeviceName = "fake", SamplesDelivered = Interlocked.Read(ref delivered) };

        public MicrophoneRecorderException? FailWith { get; set; }

        public MicrophoneRecorderException? Failure => FailWith;

        public AudioInputDevice? StartedWith { get; private set; }

        public void Start(AudioInputDevice? device) => StartedWith = device;

        public void Feed(float[] samples, double hostTime)
        {
            Interlocked.Add(ref delivered, samples.Length);
            channel.Writer.TryWrite(new TimedChunk(samples, hostTime));
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop() => channel.Writer.TryComplete();
    }

    /// <summary>Silent system audio that stops when asked.</summary>
    internal sealed class FakeSystem : ISystemAudioCapture
    {
        private readonly Channel<TimedChunk> channel = Channel.CreateUnbounded<TimedChunk>();

        public ChannelReader<TimedChunk> TimedSamples => channel.Reader;

        public SystemAudioRecorderException? Failure => null;

        public void Feed(float[] samples, double hostTime) => channel.Writer.TryWrite(new TimedChunk(samples, hostTime));

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop() => channel.Writer.TryComplete();
    }
}
