using System.Diagnostics;
using System.Globalization;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using Xunit.Abstractions;

namespace Hearsay.Tests.Whisper;

/// <summary>
/// Model-free tests of checkpoint merging, offsets, progress mapping and the
/// foreground counter. Mirror mac/HearsayWhisper/Tests/HearsayWhisperTests/ResumableDecodingTests.swift
/// (checkpoint validation, progress) and the ForegroundWork counter in
/// mac/Hearsay/Features/Transcription/WhisperEngine.swift (PLAN.md 18.10).
/// </summary>
public sealed class ResumableDecodingUnitTests
{
    private static TranscriptSegment Cue(double start, double end, string text = "x") => new(start, end, text);

    private static TranscriptionCheckpoint Checkpoint(double resume, int sampleCount = 16_000 * 100, string model = "m.bin", TranscriptionOptions? options = null) =>
        new([], resume, "en", Path.GetFullPath(model), sampleCount, 1, options ?? TranscriptionOptions.App(TranscriptLanguage.English));

    // Merge

    [Fact]
    public void MergeKeepsPriorThenFresh()
    {
        var merged = CheckpointMerge.Merge([Cue(0, 4, "a"), Cue(4, 9, "b")], 9, [Cue(9, 12, "c"), Cue(12, 15, "d")]);
        Assert.Equal(["a", "b", "c", "d"], merged.Select(c => c.Text));
    }

    [Fact]
    public void MergeDropsAFirstFreshCueThatEndsBeforeTheOffset()
    {
        var merged = CheckpointMerge.Merge([Cue(0, 9, "a")], 9, [Cue(7, 8.5, "dup"), Cue(9, 12, "c")]);
        Assert.Equal(["a", "c"], merged.Select(c => c.Text));
    }

    [Fact]
    public void MergeKeepsAFreshCueThatSpansTheOffset()
    {
        var merged = CheckpointMerge.Merge([Cue(0, 9, "a")], 9, [Cue(8.5, 12, "c")]);
        Assert.Equal(["a", "c"], merged.Select(c => c.Text));
    }

    [Fact]
    public void MergeDropsOnlyLeadingStaleCues()
    {
        var merged = CheckpointMerge.Merge([], 10, [Cue(8, 9, "s1"), Cue(9, 10, "s2"), Cue(10, 12, "ok"), Cue(5, 6, "later")]);
        Assert.Equal(["ok", "later"], merged.Select(c => c.Text));
    }

    [Fact]
    public void MergeDropsPriorCuesFromTheOffsetOn()
    {
        var merged = CheckpointMerge.Merge([Cue(0, 5, "a"), Cue(5, 9, "b"), Cue(9, 11, "tail")], 9, [Cue(9, 11, "tail")]);
        Assert.Equal(["a", "b", "tail"], merged.Select(c => c.Text));
        Assert.Equal(9, merged[2].Start);
    }

    [Fact]
    public void MergeWithNothingFreshIsThePrior()
    {
        Assert.Equal([Cue(0, 1, "a")], CheckpointMerge.Merge([Cue(0, 1, "a")], 1, []));
        Assert.Empty(CheckpointMerge.Merge([], 0, []));
    }

    [Fact]
    public void MergeOfAnUninterruptedPassIsTheFreshCues()
    {
        var fresh = new[] { Cue(0, 3, "a"), Cue(3, 6, "b") };
        Assert.Equal(fresh, CheckpointMerge.Merge([], 0, fresh));
    }

    // Offset and progress

    [Theory]
    [InlineData(0, 1600, 0)]
    [InlineData(10, 16_000, 0.1)]
    [InlineData(100, 16_000, 1)]
    [InlineData(250, 16_000, 1)]
    public void FractionDoneIsTheOffsetOverTheWholeInput(double resume, int seconds, double expected)
    {
        var checkpoint = Checkpoint(resume, sampleCount: seconds * 100);
        Assert.Equal(expected, checkpoint.FractionDone, 6);
    }

    [Fact]
    public void FractionDoneOfAnEmptyInputIsZero() => Assert.Equal(0, Checkpoint(5, 0).FractionDone);

    // Identity

    [Fact]
    public void CheckpointRemembersItsModel()
    {
        var checkpoint = Checkpoint(1, model: "models/a.bin");
        Assert.True(checkpoint.IsForModel("models/a.bin"));
        Assert.True(checkpoint.IsForModel(Path.GetFullPath("models/A.BIN")));
        Assert.False(checkpoint.IsForModel("models/b.bin"));
        Assert.False(checkpoint.IsForModel(null));
    }

    [Fact]
    public void FingerprintSeparatesCountAndContent()
    {
        var a = new float[16_000];
        var b = new float[16_000];
        b[1600] = 0.5f;
        var c = new float[16_001];
        var d = new float[16_000];
        d[^1] = 0.25f;
        var prints = new[] { a, b, c, d }.Select(s => TranscriptionCheckpoint.Fingerprint(s)).ToArray();
        Assert.Equal(4, prints.Distinct().Count());
        Assert.Equal(prints[0], TranscriptionCheckpoint.Fingerprint(new float[16_000]));
    }

    [Fact]
    public void SameOptionsIgnoresThreadsAndListIdentity()
    {
        var a = TranscriptionOptions.App(TranscriptLanguage.English);
        Assert.True(TranscriptionCheckpoint.SameOptions(a, a with { Threads = 3, Temperatures = [0f] }));
        Assert.False(TranscriptionCheckpoint.SameOptions(a, a with { Language = "de" }));
        Assert.False(TranscriptionCheckpoint.SameOptions(a, a with { Temperatures = [0f, 0.2f] }));
        Assert.False(TranscriptionCheckpoint.SameOptions(a, a with { InitialPrompt = "hi" }));
    }

    [Fact]
    public void StepHoldsExactlyOneOutcome()
    {
        var done = TranscriptionStep.Done(new WhisperTranscription([], "en"));
        Assert.NotNull(done.Finished);
        Assert.Null(done.Suspended);
        var suspended = TranscriptionStep.Suspend(Checkpoint(1));
        Assert.Null(suspended.Finished);
        Assert.NotNull(suspended.Suspended);
    }

    [Fact]
    public void CheckpointErrorsHaveMessages()
    {
        foreach (var error in Enum.GetValues<TranscriptionCheckpointError>())
        {
            var exception = new TranscriptionCheckpointException(error);
            Assert.Equal(error, exception.Error);
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        }
    }

    // Resume point

    [Theory]
    [InlineData(0, 0, 100, 1, 30)]
    [InlineData(0, 0, 100, 2, 60)]
    [InlineData(0, 0, 45, 2, 45)]
    [InlineData(60, 60, 75, 1, 75)]
    [InlineData(40.38, 40.38, 100, 1, 70.38)]
    public void EmptyWindowsAdvanceThirtySecondsEach(double callOffset, double runStart, double runEnd, int windows, double expected)
    {
        Assert.Equal(expected, WhisperEngine.ResumeSeconds(callOffset, runStart, runEnd, null, windows), 6);
    }

    [Fact]
    public void AMixedRunResumesAtTheLastSegmentEnd()
    {
        Assert.Equal(41.6, WhisperEngine.ResumeSeconds(28.94, 28.94, 100, 41.6, 2));
        Assert.Equal(10, WhisperEngine.ResumeSeconds(0, 5, 100, 10, 3));
    }

    [Theory]
    [InlineData(0, 0, 100, null, 1)]
    [InlineData(12.34, 12.34, 90, null, 1)]
    [InlineData(50, 50, 55, null, 1)]
    [InlineData(5, 5, 100, 9.5, 1)]
    public void ACallThatCompletedAWindowMovesPastItsOffset(double callOffset, double runStart, double runEnd, double? lastEnd, int windows)
    {
        Assert.True(WhisperEngine.ResumeSeconds(callOffset, runStart, runEnd, lastEnd, windows) > callOffset);
    }

    // Foreground counter

    [Fact]
    public void ForegroundCounterCountsWaitingCalls()
    {
        var work = new ForegroundWork();
        Assert.Equal(0, work.Waiting);
        using (work.Enter())
        {
            Assert.Equal(1, work.Waiting);
            using (work.Enter()) Assert.Equal(2, work.Waiting);
            Assert.Equal(1, work.Waiting);
        }
        Assert.Equal(0, work.Waiting);
    }

    [Fact]
    public void DisposingAForegroundScopeTwiceCountsOnce()
    {
        var work = new ForegroundWork();
        var first = work.Enter();
        using var second = work.Enter();
        first.Dispose();
        first.Dispose();
        Assert.Equal(1, work.Waiting);
    }

    [Fact]
    public void ForegroundCounterIsThreadSafe()
    {
        var work = new ForegroundWork();
        Parallel.For(0, 10_000, _ =>
        {
            using (work.Enter())
            {
                Assert.True(work.Waiting >= 1);
            }
        });
        Assert.Equal(0, work.Waiting);
    }

    [Fact]
    public void EngineExposesTheCounterWithoutLoadingAModel()
    {
        using var engine = new WhisperEngine();
        Assert.Equal(0, engine.ForegroundWaiting);
        using (engine.EnterForeground()) Assert.Equal(1, engine.ForegroundWaiting);
        Assert.Equal(0, engine.ForegroundWaiting);
        Assert.False(engine.IsBusy);
    }

    [Fact]
    public void StepWithoutAModelIsRejected()
    {
        using var engine = new WhisperEngine();
        Assert.Throws<WhisperEngineException>(() => engine.TranscribeStep(new float[16_000], new TranscriptionOptions()));
    }
}

/// <summary>
/// The resumable pass against a real model: a multi-window input from the
/// shared fixtures, transcribed once straight through and once suspended at
/// every window boundary and resumed (PLAN.md 18.10). Skipped unless
/// <c>TEST_RUNNER_HEARSAY_MODEL_DIR</c> names a model.
/// </summary>
[Collection(WhisperModelGroup.Name)]
public sealed class ResumableDecodingIntegrationTests(WhisperEngineFixture fixture, ITestOutputHelper output)
{
    private const double MinimumSimilarity = 0.98;

    private WhisperEngine Engine => fixture.Engine;

    private static float[] Fixture(string code) => PcmWav.ReadMono16k(SharedFiles.Path("fixtures", $"{code}-30s.wav"));

    private static float[] Silence(double seconds) => new float[(int)(seconds * SilenceGate.SampleRate)];

    private void Print(string line) => output.WriteLine(line);

    /// <summary>Four en clips with 2 s gaps: about 80 s, one speech run, three 30 s windows.</summary>
    private static float[] MultiWindowInput()
    {
        var clip = Fixture("en");
        return [.. clip, .. Silence(2), .. clip, .. Silence(2), .. clip, .. Silence(2), .. clip];
    }

    /// <summary>en, 100 s of silence (whole silent windows), en, 2 s, en: two speech runs.</summary>
    private static float[] TwoRunInput()
    {
        var clip = Fixture("en");
        return [.. clip, .. Silence(100), .. clip, .. Silence(2), .. clip];
    }

    private sealed record Resumed(IReadOnlyList<TranscriptSegment> Cues, List<TranscriptionCheckpoint> Checkpoints, List<double> Progress, double Seconds);

    private Resumed RunResumed(float[] samples, TranscriptionOptions options, Func<int, bool> yieldAt)
    {
        var checkpoints = new List<TranscriptionCheckpoint>();
        var progress = new List<double>();
        var watch = Stopwatch.StartNew();
        TranscriptionCheckpoint? checkpoint = null;
        for (int call = 0; call < 100; call++)
        {
            int step = call;
            var result = Engine.TranscribeStep(samples, options, checkpoint, new SyncProgress(progress), () => yieldAt(step));
            if (result.Finished is { } finished)
            {
                return new Resumed(finished.Cues(0, null), checkpoints, progress, watch.Elapsed.TotalSeconds);
            }
            checkpoint = result.Suspended ?? throw new InvalidOperationException("neither finished nor suspended");
            checkpoints.Add(checkpoint);
        }
        throw new InvalidOperationException("the resumed pass never finished");
    }

    [WhisperModelTheory]
    [InlineData("oneRun")]
    [InlineData("twoRuns")]
    public void ResumingAtEveryWindowMatchesAnUninterruptedPass(string input)
    {
        var samples = input == "oneRun" ? MultiWindowInput() : TwoRunInput();
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var watch = Stopwatch.StartNew();
        var straight = Engine.Transcribe(samples, options).Cues(0, null);
        double straightSeconds = watch.Elapsed.TotalSeconds;

        var resumed = RunResumed(samples, options, _ => true);

        double similarity = TranscriptMetrics.Similarity(
            TranscriptMetrics.Normalize(TranscriptMetrics.Joined(straight), false),
            TranscriptMetrics.Normalize(TranscriptMetrics.Joined(resumed.Cues), false));
        Print(string.Create(CultureInfo.InvariantCulture,
            $"{input}: {samples.Length / 16_000.0:F0} s input: uninterrupted {straightSeconds:F2} s / {straight.Count} cues, resumed {resumed.Seconds:F2} s / {resumed.Cues.Count} cues in {resumed.Checkpoints.Count + 1} calls, similarity {similarity:F4}"));
        foreach (var checkpoint in resumed.Checkpoints)
        {
            Print(string.Create(CultureInfo.InvariantCulture,
                $"  checkpoint: resume at {checkpoint.ResumeSeconds:F2} s, {checkpoint.Segments.Count} cues, last end {(checkpoint.Segments.Count > 0 ? checkpoint.Segments[^1].End : 0):F2} s"));
        }
        Print("uninterrupted:");
        foreach (var cue in straight) Print(string.Create(CultureInfo.InvariantCulture, $"  {cue.Start,7:F2} -> {cue.End,7:F2}  {cue.Text}"));
        Print("resumed:");
        foreach (var cue in resumed.Cues) Print(string.Create(CultureInfo.InvariantCulture, $"  {cue.Start,7:F2} -> {cue.End,7:F2}  {cue.Text}"));

        if (input == "twoRuns")
        {
            // The pass also stopped between the two speech runs: resume at the second run's start (grid window 90 s).
            Assert.Contains(resumed.Checkpoints, c => c.ResumeSeconds == 90);
        }
        Assert.True(resumed.Checkpoints.Count >= 2, $"suspended {resumed.Checkpoints.Count} times");
        Assert.True(similarity >= MinimumSimilarity, $"similarity {similarity:F4}");

        // Every checkpoint advances, keeps its earlier cues, and its offset is
        // not before the last cue it holds.
        double previous = -1;
        int previousCount = 0;
        foreach (var checkpoint in resumed.Checkpoints)
        {
            Assert.True(checkpoint.ResumeSeconds > previous, $"{checkpoint.ResumeSeconds} after {previous}");
            Assert.True(checkpoint.Segments.Count >= previousCount);
            if (checkpoint.Segments.Count > 0) Assert.True(checkpoint.Segments[^1].End <= checkpoint.ResumeSeconds + 0.01);
            previous = checkpoint.ResumeSeconds;
            previousCount = checkpoint.Segments.Count;
        }

        // No duplicated or overlapping cue anywhere, at the resume points in particular.
        for (int i = 1; i < resumed.Cues.Count; i++)
        {
            var a = resumed.Cues[i - 1];
            var b = resumed.Cues[i];
            Assert.True(b.Start >= a.Start, $"cue {i} starts before cue {i - 1}");
            Assert.True(b.Start >= a.End - 0.05, $"cue {i} ({b.Start:F2}) overlaps cue {i - 1} (ends {a.End:F2})");
            Assert.False(a.Text.Trim() == b.Text.Trim() && Math.Abs(a.Start - b.Start) < 1, $"duplicate cue at {b.Start:F2}");
        }

        // Nothing missing: every uninterrupted cue has a resumed cue overlapping it.
        var matches = TranscriptMetrics.MatchCues(straight, resumed.Cues);
        Assert.All(matches, match => Assert.NotNull(match.Output));
        // The cue count may differ: whisper.cpp splits a window into cues by its own timestamp
        // tokens, and a window that starts at the offset can split differently from one
        // that follows a window in the same call (same text, other cue boundaries).
        Assert.True(resumed.Cues.Count >= straight.Count / 2, $"{straight.Count} vs {resumed.Cues.Count} cues");

        // Progress is for the whole input: never back, ends at 1, and a resumed call starts at its checkpoint's fraction.
        for (int i = 1; i < resumed.Progress.Count; i++)
        {
            Assert.True(resumed.Progress[i] >= resumed.Progress[0] - 1e-9);
        }
        Assert.Equal(1.0, resumed.Progress[^1]);
        Assert.Contains(resumed.Progress, value => Math.Abs(value - resumed.Checkpoints[0].FractionDone) < 1e-9);
    }

    [WhisperModelFact]
    public void EveryCallDecodesAtLeastOneWindowEvenWhenYieldAlwaysFires()
    {
        var samples = MultiWindowInput();
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var step = Engine.TranscribeStep(samples, options, null, null, () => true);
        var first = Assert.IsType<TranscriptionCheckpoint>(step.Suspended);
        Assert.NotEmpty(first.Segments);
        Assert.True(first.ResumeSeconds > 0);
        Assert.Equal("en", first.Language);
        Assert.True(first.IsForModel(Engine.ModelPath));
        var second = Assert.IsType<TranscriptionCheckpoint>(Engine.TranscribeStep(samples, options, first, null, () => true).Suspended);
        Assert.True(second.ResumeSeconds > first.ResumeSeconds);
    }

    [WhisperModelFact]
    public void EmptyWindowsStillAdvanceUnderConstantYielding()
    {
        // A clip, then 70 s of quiet noise above the silence gate that yields no text.
        var random = new Random(7);
        var noise = new float[70 * SilenceGate.SampleRate];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((random.NextDouble() - 0.5) * 0.02);
        float[] samples = [.. Fixture("en"), .. noise];
        Assert.False(SilenceGate.IsSilent(noise.AsSpan(0, SilenceGate.WindowSamples)));
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var resumed = RunResumed(samples, options, _ => true);
        double previous = -1;
        foreach (var checkpoint in resumed.Checkpoints)
        {
            Assert.True(checkpoint.ResumeSeconds > previous, $"{checkpoint.ResumeSeconds} after {previous}");
            previous = checkpoint.ResumeSeconds;
        }
        Print($"noise tail: {resumed.Checkpoints.Count} suspensions, {resumed.Cues.Count} cues, resume points {string.Join(", ", resumed.Checkpoints.Select(c => c.ResumeSeconds.ToString("F2", CultureInfo.InvariantCulture)))}");
    }

    [WhisperModelFact]
    public void ResumingOnlyWhenTheQueueSaysSoStillFinishes()
    {
        var samples = MultiWindowInput();
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var resumed = RunResumed(samples, options, call => call % 2 == 0);
        Assert.NotEmpty(resumed.Cues);
        Print($"yield on even calls: {resumed.Checkpoints.Count} suspensions, {resumed.Cues.Count} cues, {resumed.Seconds:F2} s");
    }

    [WhisperModelFact]
    public void AutoLanguageIsKeptAcrossResumes()
    {
        var samples = MultiWindowInput();
        var options = new TranscriptionOptions();
        var first = Assert.IsType<TranscriptionCheckpoint>(Engine.TranscribeStep(samples, options, null, null, () => true).Suspended);
        Assert.Equal("en", first.Language);
        var result = Engine.TranscribeStep(samples, options, first).Finished;
        Assert.NotNull(result);
        Assert.Equal("en", result.Language);
    }

    [WhisperModelFact]
    public void ACheckpointForOtherAudioOrOptionsIsRejected()
    {
        var samples = MultiWindowInput();
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var checkpoint = Assert.IsType<TranscriptionCheckpoint>(Engine.TranscribeStep(samples, options, null, null, () => true).Suspended);

        float[] other = [.. samples, 0f];
        Assert.Equal(TranscriptionCheckpointError.SamplesMismatch,
            Assert.Throws<TranscriptionCheckpointException>(() => Engine.TranscribeStep(other, options, checkpoint)).Error);
        Assert.Equal(TranscriptionCheckpointError.OptionsMismatch,
            Assert.Throws<TranscriptionCheckpointException>(() => Engine.TranscribeStep(samples, options with { Language = "de" }, checkpoint)).Error);
        Assert.Equal(TranscriptionCheckpointError.ModelMismatch,
            Assert.Throws<TranscriptionCheckpointException>(() => Engine.TranscribeStep(samples, options, checkpoint with { ModelPath = "C:\\other.bin" })).Error);
    }

    [WhisperModelFact]
    public void CancelWinsOverYieldAndKeepsTheCheckpointsSegments()
    {
        var samples = MultiWindowInput();
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var checkpoint = Assert.IsType<TranscriptionCheckpoint>(Engine.TranscribeStep(samples, options, null, null, () => true).Suspended);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = Assert.Throws<TranscriptionCancelledException>(() =>
            Engine.TranscribeStep(samples, options, checkpoint, null, () => true, cancel.Token));
        Assert.Equal(checkpoint.Segments, error.Partial);
    }

    [WhisperModelFact]
    public void WithoutShouldYieldTheStepEqualsTranscribe()
    {
        var clip = Fixture("en");
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        var direct = Engine.Transcribe(clip, options);
        var step = Engine.TranscribeStep(clip, options).Finished;
        Assert.NotNull(step);
        Assert.Equal(direct.Segments, step.Segments);
        Assert.Equal(direct.Language, step.Language);
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
