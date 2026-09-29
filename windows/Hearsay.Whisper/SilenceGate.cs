using Hearsay.Core.Transcription;

namespace Hearsay.Whisper;

/// <summary>
/// The audio-level hallucination gate Windows uses in place of the Mac's
/// <c>hallucination_silence_threshold</c> (PLAN.md 18.4, "Silence gate";
/// 18.8, "Hallucination filter"). whisper.cpp's no-speech probability is
/// about 1e-10 on digital silence with turbo, so its own no-speech skip never
/// fires, and turbo invents "Thank you." (or a subtitle credit for zh) in
/// every silent window. Two rules, both on the RMS level of the samples:
/// <list type="number">
/// <item>a 30 s window (on a grid from sample 0 of the input) whose RMS is
/// below <see cref="ThresholdDbfs"/> is not transcribed;</item>
/// <item>a cue whose whole sample span is below that level is dropped (a cue
/// with no samples at all, for example one past the end of the audio, counts
/// as silent).</item>
/// </list>
/// The level is the one <c>Transcriber.detectLanguage</c> already gates
/// language detection with on the Mac (<c>silenceRMS</c> 0.001,
/// mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/LanguageDetection.swift;
/// <see cref="LanguageDetection.SilenceRms"/> in Core).
/// </summary>
public static class SilenceGate
{
    /// <summary>The gate level in dBFS.</summary>
    public const double ThresholdDbfs = -60.0;

    /// <summary><see cref="ThresholdDbfs"/> as an RMS amplitude (full scale 1.0): 10^(-60/20).</summary>
    public const float ThresholdRms = 0.001f;

    /// <summary>The window length: Whisper's 30 s at 16 kHz.</summary>
    public const int WindowSamples = LanguageDetection.WindowSamples;

    /// <summary>16 kHz, the only rate the engine takes.</summary>
    public const int SampleRate = 16_000;

    /// <summary>A stretch of samples to transcribe, [Start, End).</summary>
    public readonly record struct SampleRange(int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>True when <paramref name="samples"/> are below the gate level (an empty span is silent).</summary>
    public static bool IsSilent(ReadOnlySpan<float> samples) =>
        samples.IsEmpty || RootMeanSquare(samples) < ThresholdRms;

    /// <summary>
    /// The stretches to transcribe: runs of consecutive 30 s grid windows that
    /// are not silent, merged, in order. Whisper seeks freely inside a run;
    /// silent windows between runs are skipped. The last window may be short.
    /// </summary>
    public static IReadOnlyList<SampleRange> SpeechRuns(ReadOnlySpan<float> samples)
    {
        var runs = new List<SampleRange>();
        int? runStart = null;
        for (int start = 0; start < samples.Length; start += WindowSamples)
        {
            int end = Math.Min(start + WindowSamples, samples.Length);
            if (IsSilent(samples[start..end]))
            {
                if (runStart is { } open)
                {
                    runs.Add(new SampleRange(open, start));
                    runStart = null;
                }
            }
            else
            {
                runStart ??= start;
            }
        }
        if (runStart is { } last)
        {
            runs.Add(new SampleRange(last, samples.Length));
        }
        return runs;
    }

    /// <summary>
    /// <paramref name="segments"/> without the cues whose span, in seconds
    /// from the start of <paramref name="samples"/>, is silent.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> DropSilentCues(
        IReadOnlyList<TranscriptSegment> segments, ReadOnlySpan<float> samples)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var kept = new List<TranscriptSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var span = CueSpan(segment, samples.Length);
            if (!IsSilent(samples[span.Start..span.End]))
            {
                kept.Add(segment);
            }
        }
        return kept;
    }

    /// <summary>The sample span of a cue, clamped to the audio (empty when it lies outside).</summary>
    public static SampleRange CueSpan(TranscriptSegment segment, int sampleCount)
    {
        int Clamp(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds <= 0) return 0;
            double position = Math.Round(seconds * SampleRate);
            return position >= sampleCount ? sampleCount : (int)position;
        }
        int start = Clamp(segment.Start);
        int end = Math.Max(start, Clamp(segment.End));
        return new SampleRange(start, end);
    }

    /// <summary>Root mean square of <paramref name="samples"/> (0 for none), summed in double as the Mac does.</summary>
    public static float RootMeanSquare(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
