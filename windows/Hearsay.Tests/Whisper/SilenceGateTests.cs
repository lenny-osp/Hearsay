using Hearsay.Core.Audio;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.Tests.Whisper;

/// <summary>
/// The audio-level silence gate (PLAN.md 18.4, "Silence gate"), on synthetic
/// audio. No Swift counterpart: it replaces the Mac's
/// <c>hallucination_silence_threshold</c>; its RMS rule mirrors
/// <c>rootMeanSquareOfSilenceIsZero</c> and the <c>silenceRMS</c> gate in
/// mac/HearsayWhisper/Tests/HearsayWhisperTests/LanguageDetectionTests.swift.
/// </summary>
public sealed class SilenceGateTests
{
    private const int Rate = 16_000;

    /// <summary>A sine of RMS <paramref name="rms"/> (amplitude rms × √2) at 440 Hz.</summary>
    private static float[] Tone(double seconds, double rms)
    {
        var samples = new float[(int)Math.Round(seconds * Rate)];
        double amplitude = rms * Math.Sqrt(2);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / Rate));
        }
        return samples;
    }

    private static float[] Silence(double seconds) => new float[(int)Math.Round(seconds * Rate)];

    private static float[] Concat(params float[][] parts) => [.. parts.SelectMany(part => part)];

    [Fact]
    public void ConstantsAreMinusSixtyDbfsOverThirtySecondWindows()
    {
        Assert.Equal(-60.0, SilenceGate.ThresholdDbfs);
        Assert.Equal(SilenceGate.ThresholdRms, Math.Pow(10, SilenceGate.ThresholdDbfs / 20), 1e-9);
        Assert.Equal(LanguageDetection.SilenceRms, SilenceGate.ThresholdRms);
        Assert.Equal(LevelMeter.FloorDB, SilenceGate.ThresholdDbfs);
        Assert.Equal(30 * 16_000, SilenceGate.WindowSamples);
        Assert.Equal(LanguageDetection.WindowSamples, SilenceGate.WindowSamples);
    }

    [Fact]
    public void RootMeanSquareOfSilenceIsZero()
    {
        Assert.Equal(0f, SilenceGate.RootMeanSquare(new float[100]));
        Assert.Equal(0f, SilenceGate.RootMeanSquare([]));
        Assert.Equal(0.5f, SilenceGate.RootMeanSquare([0.5f, -0.5f, 0.5f, -0.5f]), 6);
    }

    [Fact]
    public void LevelBelowTheThresholdIsSilentAndAtItIsNot()
    {
        Assert.True(SilenceGate.IsSilent([]));
        Assert.True(SilenceGate.IsSilent(Silence(1)));
        Assert.True(SilenceGate.IsSilent(Tone(1, 0.0005)));   // -66 dBFS
        Assert.False(SilenceGate.IsSilent(Tone(1, 0.002)));   // -54 dBFS
        Assert.False(SilenceGate.IsSilent([0.001f, -0.001f])); // exactly -60 dBFS
        Assert.True(SilenceGate.IsSilent([0.000999f, -0.000999f]));
    }

    [Fact]
    public void DigitalSilenceHasNoSpeechRuns()
    {
        Assert.Empty(SilenceGate.SpeechRuns(Silence(95)));
        Assert.Empty(SilenceGate.SpeechRuns([]));
    }

    [Fact]
    public void FixtureWithFortyFiveSecondsOfSilenceKeepsOnlyTheFirstWindow()
    {
        // The hallucination case: 19 s of speech, then 45 s of zeros (64 s).
        var samples = Concat(Tone(19, 0.1), Silence(45));
        var runs = SilenceGate.SpeechRuns(samples);
        Assert.Equal([new SilenceGate.SampleRange(0, 30 * Rate)], runs);
    }

    [Fact]
    public void ConsecutiveSpeechWindowsMergeAndSilentWindowsSplitRuns()
    {
        // speech 0-40 s, silent 60-90 s window, speech again at 95 s, short last window.
        var samples = Concat(Tone(40, 0.1), Silence(55), Tone(5, 0.1));
        var runs = SilenceGate.SpeechRuns(samples);
        Assert.Equal(
            [new SilenceGate.SampleRange(0, 60 * Rate), new SilenceGate.SampleRange(90 * Rate, 100 * Rate)],
            runs);
    }

    [Fact]
    public void AQuietWindowIsSkippedEvenWhenNotDigitalSilence()
    {
        var samples = Concat(Tone(30, 0.1), Tone(30, 0.0003), Tone(30, 0.1));
        var runs = SilenceGate.SpeechRuns(samples);
        Assert.Equal(
            [new SilenceGate.SampleRange(0, 30 * Rate), new SilenceGate.SampleRange(60 * Rate, 90 * Rate)],
            runs);
    }

    [Fact]
    public void AWindowWithLittleSpeechIsTranscribed()
    {
        // 1 s at -20 dBFS in 30 s is about -35 dBFS over the window.
        var samples = Concat(Silence(10), Tone(1, 0.1), Silence(19));
        Assert.Equal([new SilenceGate.SampleRange(0, 30 * Rate)], SilenceGate.SpeechRuns(samples));
    }

    [Fact]
    public void SilentCuesAreDroppedAndSpeechCuesKept()
    {
        var samples = Concat(Tone(19, 0.1), Silence(45));
        var cues = new List<TranscriptSegment>
        {
            new(0, 4.48, "Welcome"),
            new(15.62, 18.44, "Thank you everyone."),
            new(18.5, 20.5, "straddles the end of speech"),
            new(30, 59.98, "Thank you."),
            new(60, 89.98, "Thank you."),   // past the end of the audio
            new(64, 64, "empty span"),
        };
        var kept = SilenceGate.DropSilentCues(cues, samples);
        Assert.Equal(["Welcome", "Thank you everyone.", "straddles the end of speech"], kept.Select(c => c.Text));
    }

    [Fact]
    public void CueSpanRoundsToSamplesAndClampsToTheAudio()
    {
        Assert.Equal(new SilenceGate.SampleRange(16_000, 32_000), SilenceGate.CueSpan(new(1, 2, ""), 100_000));
        Assert.Equal(new SilenceGate.SampleRange(0, 8), SilenceGate.CueSpan(new(-1, 0.0005, ""), 100_000));
        Assert.Equal(new SilenceGate.SampleRange(100_000, 100_000), SilenceGate.CueSpan(new(10, 12, ""), 100_000));
        Assert.Equal(new SilenceGate.SampleRange(48_000, 48_000), SilenceGate.CueSpan(new(3, 2, ""), 100_000));
        Assert.Equal(new SilenceGate.SampleRange(0, 0), SilenceGate.CueSpan(new(double.NaN, double.NaN, ""), 100_000));
    }
}
