using Hearsay.Core.Transcription;
using Hearsay.Whisper.Native;

namespace Hearsay.Whisper;

/// <summary>
/// Decoding options, the fields of the Mac's <c>TranscriptionOptions</c>
/// (mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/TranscriptionOptions.swift)
/// that whisper.cpp has, with the Mac's defaults; <see cref="App"/> is
/// mac/Hearsay/Features/Transcription/TranscriptionOptions+App.swift.
/// </summary>
/// <remarks>
/// Mapping to <c>whisper_full_params</c> (<see cref="Apply"/>):
/// <list type="bullet">
/// <item><see cref="Language"/>: <c>language</c>; null means detect, which
/// the engine resolves itself on the first transcribed window (as
/// <c>resolveLanguage</c> does on the Mac) and then passes as a fixed code.</item>
/// <item><see cref="InitialPrompt"/>: <c>initial_prompt</c> (the app passes
/// none for any language; owner decision, PLAN.md section 6).</item>
/// <item><see cref="ConditionOnPreviousText"/> false: <c>no_context</c> true.</item>
/// <item><see cref="Temperatures"/>: whisper.cpp takes a start and an
/// increment, so the list must be evenly spaced up to 1.0; the Mac's default
/// [0] is <c>temperature</c> 0 with <c>temperature_inc</c> 0 (no fallback).</item>
/// <item><see cref="CompressionRatioThreshold"/>: <c>entropy_thold</c>. The
/// number is the same (2.4), the test is not: whisper.cpp measures token
/// entropy where the Mac and Python measure the zlib ratio. Null disables it.</item>
/// <item><see cref="LogprobThreshold"/>: <c>logprob_thold</c>;
/// <see cref="NoSpeechThreshold"/>: <c>no_speech_thold</c> (whisper.cpp's
/// value never reaches it, see <see cref="SilenceGate"/>).</item>
/// <item><see cref="BestOf"/>: <c>greedy.best_of</c>.</item>
/// <item><c>hallucinationSilenceThreshold</c> has no whisper.cpp
/// counterpart; <see cref="SilenceGate"/> replaces it (PLAN.md 18.4).</item>
/// <item>Always: timestamps on (<c>no_timestamps</c> false), one segment per
/// timestamp pair (<c>single_segment</c> false), non-speech tokens
/// suppressed like Python's <c>suppress_tokens="-1"</c>
/// (<c>suppress_nst</c>), blank suppressed at the start, whisper.cpp
/// printing off, no VAD.</item>
/// </list>
/// A live-preview chunk (at most 30 s) and a whole recording take the same
/// call, as on the Mac: a chunk is one window.
/// </remarks>
public sealed record TranscriptionOptions
{
    /// <summary>Whisper code ("en", "zh", "de", "es"); null detects the language.</summary>
    public string? Language { get; init; }

    /// <summary>Text fed as context for the first window.</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>Feed the previous windows' text as the prompt of the next window.</summary>
    public bool ConditionOnPreviousText { get; init; }

    /// <summary>Decoding temperatures tried in order; the Mac's default [0] (no fallback).</summary>
    public IReadOnlyList<float> Temperatures { get; init; } = [0f];

    /// <summary>Repetition threshold (see the remarks); null disables it.</summary>
    public float? CompressionRatioThreshold { get; init; } = 2.4f;

    /// <summary>A window whose average token log probability is below this is retried; null disables it.</summary>
    public float? LogprobThreshold { get; init; } = -1.0f;

    /// <summary>No-speech skip threshold; null disables it.</summary>
    public float? NoSpeechThreshold { get; init; } = 0.6f;

    /// <summary>Candidates per window above temperature zero (whisper.cpp <c>greedy.best_of</c>).</summary>
    public int? BestOf { get; init; } = 5;

    /// <summary>Worker threads; null picks <see cref="DefaultThreads"/> for the loaded runtime.</summary>
    public int? Threads { get; init; }

    /// <summary>
    /// The options whisper-tools passes to <c>mlx_whisper</c> (PLAN.md section
    /// 6): the session language, no initial prompt, no conditioning on
    /// previous text, and the decoder defaults for everything else.
    /// </summary>
    public static TranscriptionOptions App(TranscriptLanguage language) => new()
    {
        Language = language.WhisperCode(),
        InitialPrompt = null,
        ConditionOnPreviousText = false,
    };

    /// <summary>
    /// Threads for a runtime (PLAN.md 18.4, "Speed"): on the CPU
    /// max(4, min(12, logical cores)), since 8 to 12 threads gained 20 to 25 %
    /// for turbo in the W1 spike; on a GPU runtime whisper.cpp's own default
    /// min(4, logical cores), since thread count barely matters there.
    /// </summary>
    public static int DefaultThreads(bool cpuRuntime, int logicalCores) =>
        cpuRuntime ? Math.Max(4, Math.Min(12, logicalCores)) : Math.Max(1, Math.Min(4, logicalCores));

    /// <summary>
    /// whisper.cpp's <c>temperature</c> and <c>temperature_inc</c> for
    /// <paramref name="temperatures"/>: a single value is (t, 0); a list must
    /// start at its first value and step evenly to at least 1.0 (whisper.cpp
    /// tries start, start + inc, ... while at most 1.0).
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty or cannot be expressed as start plus increment.</exception>
    public static (float Start, float Increment) TemperatureSchedule(IReadOnlyList<float> temperatures)
    {
        ArgumentNullException.ThrowIfNull(temperatures);
        if (temperatures.Count == 0)
        {
            throw new ArgumentException("At least one temperature is needed.", nameof(temperatures));
        }
        float start = temperatures[0];
        if (temperatures.Count == 1)
        {
            return (start, 0f);
        }
        float increment = temperatures[1] - temperatures[0];
        const float tolerance = 1e-4f;
        bool even = increment > 0;
        for (int i = 1; even && i < temperatures.Count; i++)
        {
            even = Math.Abs(temperatures[i] - (start + i * increment)) <= tolerance;
        }
        float last = temperatures[^1];
        if (!even || last > 1.0f + tolerance || last + increment <= 1.0f + tolerance)
        {
            throw new ArgumentException(
                "whisper.cpp takes temperatures as a start and an even step up to 1.0.", nameof(temperatures));
        }
        return (start, increment);
    }

    /// <summary>
    /// Writes these options into <paramref name="p"/>, a copy of
    /// <c>whisper_full_default_params(WHISPER_SAMPLING_GREEDY)</c>. The
    /// language and prompt pointers are the caller's (UTF-8, NUL-terminated,
    /// alive for the call); pass zero for none.
    /// </summary>
    internal void Apply(ref WhisperFullParams p, int threads, IntPtr language, IntPtr initialPrompt)
    {
        var (start, increment) = TemperatureSchedule(Temperatures);
        p.Strategy = WhisperSamplingStrategy.Greedy;
        p.Threads = threads;
        p.Translate = 0;
        p.NoContext = ConditionOnPreviousText ? (byte)0 : (byte)1;
        p.NoTimestamps = 0;
        p.SingleSegment = 0;
        p.PrintSpecial = 0;
        p.PrintProgress = 0;
        p.PrintRealtime = 0;
        p.PrintTimestamps = 0;
        p.TokenTimestamps = 0;
        p.MaxSegmentLength = 0;
        p.SplitOnWord = 0;
        p.MaxTokens = 0;
        p.TinyDiarize = 0;
        p.OffsetMs = 0;
        p.DurationMs = 0;
        p.InitialPrompt = initialPrompt;
        p.CarryInitialPrompt = 0;
        p.PromptTokens = IntPtr.Zero;
        p.PromptTokenCount = 0;
        p.Language = language;
        p.DetectLanguage = 0;
        p.SuppressBlank = 1;
        p.SuppressNonSpeechTokens = 1;
        p.SuppressRegex = IntPtr.Zero;
        p.Temperature = start;
        p.TemperatureIncrement = increment;
        // whisper.cpp disables a threshold by a value that can never trigger.
        p.EntropyThreshold = CompressionRatioThreshold ?? float.PositiveInfinity;
        p.LogprobThreshold = LogprobThreshold ?? float.NegativeInfinity;
        p.NoSpeechThreshold = NoSpeechThreshold ?? float.PositiveInfinity;
        p.Greedy.BestOf = BestOf ?? 1;
        p.Vad = 0;
        p.VadModelPath = IntPtr.Zero;
        p.GrammarRules = IntPtr.Zero;
        p.GrammarRuleCount = 0;
    }
}
