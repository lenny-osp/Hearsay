using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.Tests.Whisper;

/// <summary>
/// The idle release (PLAN.md 12; the Mac's WhisperEngine idle unload, 60 s):
/// <see cref="WhisperEngine.ModelReleased"/> fires once when the idle timer
/// frees a loaded model, and a later job loads the model again. Own engine with
/// a one second idle time, so the test is quick; it sits in the model
/// collection so it never runs beside the shared engine (two loaded contexts
/// would cost about 3 GB).
/// </summary>
[Collection(WhisperModelGroup.Name)]
public sealed class IdleReleaseIntegrationTests
{
    private static float[] Clip()
    {
        var clip = PcmWav.ReadMono16k(SharedFiles.Path("fixtures", "en-30s.wav"));
        return clip[..(5 * SilenceGate.SampleRate)];
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan limit)
    {
        var end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [WhisperModelFact]
    public void TheIdleTimerReleasesTheModelOnceAndALaterJobReloadsIt()
    {
        string path = WhisperTestModel.Path ?? throw new InvalidOperationException(WhisperTestModel.SkipReason);
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var samples = Clip();
        int released = 0;
        var log = new List<string>();
        using var engine = new WhisperEngine(line => { lock (log) log.Add(line); }, TimeSpan.FromSeconds(1));
        engine.ModelReleased += (_, _) => Interlocked.Increment(ref released);

        var first = engine.Transcribe(path, samples, options);
        Assert.NotEmpty(first.Cues(0, null));
        Assert.True(engine.IsLoaded);

        Assert.True(WaitFor(() => !engine.IsLoaded, TimeSpan.FromSeconds(10)), "the model was not released");
        Assert.True(WaitFor(() => Volatile.Read(ref released) > 0, TimeSpan.FromSeconds(10)), "ModelReleased did not fire");
        Thread.Sleep(1500);  // a second event would show here
        Assert.Equal(1, Volatile.Read(ref released));
        lock (log) Assert.Contains(log, line => line.Contains("model released after idle time", StringComparison.Ordinal));

        var second = engine.Transcribe(path, samples, options);
        Assert.NotEmpty(second.Cues(0, null));
        Assert.Equal(1, Volatile.Read(ref released));  // a reload by a job is not a release

        engine.Dispose();
        Assert.Equal(1, Volatile.Read(ref released));  // Dispose does not raise it
    }

    [WhisperModelFact]
    public void AnExplicitUnloadOfALoadedModelRaisesTheEventToo()
    {
        string path = WhisperTestModel.Path ?? throw new InvalidOperationException(WhisperTestModel.SkipReason);
        int released = 0;
        using var engine = new WhisperEngine();
        engine.ModelReleased += (_, _) => Interlocked.Increment(ref released);
        engine.Load(path);
        engine.Unload();
        Assert.False(engine.IsLoaded);
        Assert.Equal(1, released);
        engine.Unload();  // nothing loaded: no event
        Assert.Equal(1, released);
    }
}
