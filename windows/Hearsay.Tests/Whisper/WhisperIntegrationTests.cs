using System.Diagnostics;
using System.Globalization;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using Xunit.Abstractions;

namespace Hearsay.Tests.Whisper;

/// <summary>
/// The engine against a real model on the shared fixtures: PLAN.md 18.5's
/// acceptance, the silence-gate hallucination test (18.4), language
/// detection, cancellation and the speed probe. Mirrors
/// mac/HearsayWhisper/Tests/HearsayWhisperTests/IntegrationTests.swift (the
/// Mac compares bytes with the Python output; Windows compares similarity
/// and timing, PLAN.md 18.4 "Output is not byte-identical"). Skipped unless
/// <c>TEST_RUNNER_HEARSAY_MODEL_DIR</c> names a model (<see cref="WhisperTestModel"/>).
/// </summary>
[Collection(WhisperModelGroup.Name)]
public sealed class WhisperIntegrationTests(WhisperEngineFixture fixture, ITestOutputHelper output)
{
    private const double TimingTolerance = 0.5;
    private const double MinimumSimilarity = 0.9;
    private const double SilencePadSeconds = 45;

    private static readonly Dictionary<string, TranscriptLanguage> Languages = new()
    {
        ["en"] = TranscriptLanguage.English,
        ["zh"] = TranscriptLanguage.ChineseTaiwan,
        ["de"] = TranscriptLanguage.German,
        ["es"] = TranscriptLanguage.Spanish,
    };

    /// <summary>Python's <c>detect_language</c> top probability per fixture (IntegrationTests.pythonDetection).</summary>
    private static readonly Dictionary<string, float> PythonDetection = new()
    {
        ["en"] = 0.999725f,
        ["zh"] = 0.997967f,
        ["de"] = 0.999425f,
        ["es"] = 0.999430f,
    };

    private WhisperEngine Engine => fixture.Engine;

    private static float[] Fixture(string code) => PcmWav.ReadMono16k(SharedFiles.Path("fixtures", $"{code}-30s.wav"));

    private static IReadOnlyList<TranscriptSegment> ExpectedCues(string code) =>
        Srt.Parse(SharedFiles.ReadText("fixtures", $"{code}-30s.expected.srt"));

    private IReadOnlyList<TranscriptSegment> TranscribeCues(float[] samples, string code, out double seconds, List<double>? progress = null)
    {
        var language = Languages[code];
        var watch = Stopwatch.StartNew();
        var result = Engine.Transcribe(samples, TranscriptionOptions.App(language),
            progress is null ? null : new SyncProgress(progress));
        seconds = watch.Elapsed.TotalSeconds;
        Assert.Equal(language.WhisperCode(), result.Language);
        return result.Cues(0, language.ChineseScript());
    }

    private void Print(string line) => output.WriteLine(line);

    private void PrintCues(IEnumerable<TranscriptSegment> cues)
    {
        foreach (var cue in cues)
        {
            Print(string.Create(CultureInfo.InvariantCulture, $"  {cue.Start,7:F2} -> {cue.End,7:F2}  {cue.Text}"));
        }
    }

    [WhisperModelTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh")]
    public void FixtureMeetsTheAcceptanceRules(string code)
    {
        var samples = Fixture(code);
        var progress = new List<double>();
        var cues = TranscribeCues(samples, code, out var seconds, progress);
        var expected = ExpectedCues(code);
        bool chinese = code == "zh";

        // Text: normalized similarity to the expected SRT; zh by characters
        // against the owner-corrected Traditional truth, after conversion.
        var reference = chinese ? Srt.Parse(SharedFiles.ReadText("fixtures", "zh-30s.truth.srt")) : expected;
        double similarity = TranscriptMetrics.Similarity(
            TranscriptMetrics.Normalize(TranscriptMetrics.Joined(cues), chinese),
            TranscriptMetrics.Normalize(TranscriptMetrics.Joined(reference), chinese));

        // Timing: each output cue against the expected cue it overlaps most,
        // and each expected cue against the output cue it overlaps most.
        var outputSide = TranscriptMetrics.MatchCues(cues, expected);
        var expectedSide = TranscriptMetrics.MatchCues(expected, cues);
        double worstStart = outputSide.Concat(expectedSide).Max(m => m.DeltaStart);
        double worstEnd = outputSide.Concat(expectedSide).Max(m => m.DeltaEnd);

        Print(string.Create(CultureInfo.InvariantCulture,
            $"{code}: {samples.Length / 16_000.0:F1} s audio, {seconds:F2} s wall ({samples.Length / 16_000.0 / seconds:F2}x real time), runtime {WhisperRuntime.LibraryName}, {cues.Count} cues, similarity {similarity:F3}, worst |d start| {worstStart:F2} s, worst |d end| {worstEnd:F2} s"));
        PrintCues(cues);

        Assert.True(similarity >= MinimumSimilarity, $"{code}: similarity {similarity:F3}");
        Assert.True(worstStart <= TimingTolerance, $"{code}: worst start delta {worstStart:F2} s");
        Assert.True(worstEnd <= TimingTolerance, $"{code}: worst end delta {worstEnd:F2} s");
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1]);
        Assert.Equal(progress.OrderBy(p => p), progress);
        fixture.Remember(code, cues);
    }

    [WhisperModelTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh")]
    public void AppendedSilenceKeepsTheCuesAndInventsNothing(string code)
    {
        var clip = Fixture(code);
        var original = fixture.Remembered(code) ?? TranscribeCues(clip, code, out _);
        var padded = new float[clip.Length + (int)(SilencePadSeconds * 16_000)];
        clip.CopyTo(padded, 0);
        double speechEnd = clip.Length / 16_000.0;

        var cues = TranscribeCues(padded, code, out var seconds);
        Print(string.Create(CultureInfo.InvariantCulture,
            $"{code} + {SilencePadSeconds:F0} s silence: {padded.Length / 16_000.0:F1} s audio, {seconds:F2} s wall, {cues.Count} cues (original {original.Count})"));
        PrintCues(cues);

        Assert.DoesNotContain(cues, cue => cue.Start >= speechEnd);
        Assert.Equal(original.Count, cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            Assert.Equal(original[i].Text, cues[i].Text);
            Assert.InRange(Math.Abs(cues[i].Start - original[i].Start), 0, TimingTolerance);
            Assert.InRange(Math.Abs(cues[i].End - original[i].End), 0, TimingTolerance);
        }
    }

    [WhisperModelTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh")]
    public void AutoDetectionPicksTheFixtureLanguage(string code)
    {
        var watch = Stopwatch.StartNew();
        var result = Engine.DetectLanguage(Fixture(code));
        Print(string.Create(CultureInfo.InvariantCulture,
            $"detect {code}-30s among en/zh/de/es: {result.Code ?? "none"} {result.Confidence:F4} over {result.WindowsUsed} window(s) in {watch.Elapsed.TotalSeconds:F2} s; [{string.Join(", ", result.PerWindow.SelectMany(w => w).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value:F4}")))}]"));
        Assert.Equal(code, result.Code);
        Assert.True(result.Confidence > 0.9f, $"{code}: {result.Confidence}");
        Assert.Equal(1, result.WindowsUsed);
        var decision = LanguageDecision.Decide(LanguageChoice.Auto, TranscriptLanguage.ChineseTaiwan, result.Detection);
        Assert.Equal(TranscriptLanguages.FromWhisperCode(code, TranscriptLanguage.ChineseTaiwan), decision.Language);
    }

    [WhisperModelTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh")]
    public void SingleWindowDetectionMatchesPython(string code)
    {
        var window = Engine.DetectWindow(Fixture(code));
        var all = window.Probabilities.Probabilities;
        var top = all.MaxBy(kv => kv.Value);
        Print(string.Create(CultureInfo.InvariantCulture,
            $"detect {code}-30s over all {all.Count}: {top.Key} {top.Value:F6} (python {PythonDetection[code]:F6}), no-speech {window.NoSpeechProbability:E3}"));
        Assert.Equal(100, all.Count);
        Assert.InRange(all.Values.Sum(), 0.999f, 1.001f);
        Assert.Equal(code, top.Key);
        Assert.InRange(Math.Abs(top.Value - PythonDetection[code]), 0, 0.01f);
        Assert.True(window.NoSpeechProbability < 0.6f);
    }

    [WhisperModelFact]
    public void DetectionSkipsSilenceAndCapsWindows()
    {
        var clip = Fixture("de");
        int window = LanguageDetection.WindowSamples;
        var samples = new float[window * 6];
        for (int i = 1; i <= 5; i++) clip.CopyTo(samples, i * window);  // silence, then five windows each starting with the clip
        var result = Engine.DetectLanguage(samples);
        Assert.Equal("de", result.Code);
        Assert.Equal(3, result.WindowsUsed);
        Assert.True(result.Confidence > 0.9f);
    }

    [WhisperModelFact]
    public void SilentAudioDetectsAndTranscribesNothing()
    {
        var silence = new float[60 * 16_000];
        var detection = Engine.DetectLanguage(silence);
        Assert.Null(detection.Code);
        Assert.Equal(0f, detection.Confidence);
        Assert.Equal(0, detection.WindowsUsed);
        var progress = new List<double>();
        var watch = Stopwatch.StartNew();
        var result = Engine.Transcribe(silence, TranscriptionOptions.App(TranscriptLanguage.English), new SyncProgress(progress));
        Assert.Empty(result.Segments);
        Assert.Equal("en", result.Language);
        Assert.Equal([1.0], progress);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "no window should reach whisper.cpp");
    }

    [WhisperModelFact]
    public void AutoLanguageTranscriptionResolvesTheLanguage()
    {
        var result = Engine.Transcribe(Fixture("de"), new TranscriptionOptions());
        Assert.Equal("de", result.Language);
        Assert.NotEmpty(result.Segments);
    }

    [WhisperModelFact]
    public void CancelBeforeSecondWindowKeepsFirstWindowSegments()
    {
        var clip = Fixture("en");
        float[] samples = [.. clip, .. clip, .. clip];  // 57 s: at least two windows
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress([], fraction =>
        {
            if (fraction > 0 && fraction < 1) cancel.Cancel();  // after the first window
        });
        var error = Assert.Throws<TranscriptionCancelledException>(() =>
            Engine.Transcribe(samples, TranscriptionOptions.App(TranscriptLanguage.English), progress, cancel.Token));
        Print($"cancelled with {error.Partial.Count} partial segments, last end {(error.Partial.Count > 0 ? error.Partial[^1].End : 0):F2} s");
        Assert.NotEmpty(error.Partial);
        Assert.Equal(0, error.Partial[0].Start);
        Assert.All(error.Partial, segment => Assert.True(segment.Start < 30, $"{segment.Start}"));
    }

    [WhisperModelFact]
    public void CancelledTokenStopsBeforeTheFirstWindow()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = Assert.Throws<TranscriptionCancelledException>(() =>
            Engine.Transcribe(Fixture("en"), TranscriptionOptions.App(TranscriptLanguage.English), null, cancel.Token));
        Assert.Empty(error.Partial);
        Assert.Throws<OperationCanceledException>(() => Engine.DetectLanguage(Fixture("en"), cancel.Token));
    }

    [WhisperModelFact]
    public void UnknownLanguageIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Engine.Transcribe(Fixture("en"), new TranscriptionOptions { Language = "xx" }));
    }

    [WhisperModelFact]
    public void SpeedProbeTimesAWarmWindow()
    {
        var result = Engine.MeasureWindowSeconds();
        Print(string.Create(CultureInfo.InvariantCulture,
            $"speed probe: {result.WindowSeconds:F2} s per warm 30 s window, warm-up {(result.WarmUpSeconds is { } w ? w.ToString("F2", CultureInfo.InvariantCulture) + " s" : "not needed")}, runtime {result.Runtime}, {result.Threads} threads, live preview {(result.LivePreviewFeasible ? "on" : "off")}"));
        foreach (var line in fixture.Log) Print(line);
        Assert.True(result.WindowSeconds > 0);
        if (result.WarmUpSeconds is { } warmUp)
        {
            Assert.True(warmUp > 0.1, $"the warm-up reached whisper.cpp ({warmUp:F2} s)");
        }
        Assert.Equal(WhisperRuntime.LibraryName, result.Runtime);
        Assert.NotNull(WhisperRuntime.LibraryPath);
        Assert.True(File.Exists(WhisperRuntime.LibraryPath));
    }

    /// <summary>Records progress on the calling thread (<see cref="Progress{T}"/> would post it elsewhere).</summary>
    private sealed class SyncProgress(List<double> values, Action<double>? onReport = null) : IProgress<double>
    {
        public void Report(double value)
        {
            values.Add(value);
            onReport?.Invoke(value);
        }
    }
}
