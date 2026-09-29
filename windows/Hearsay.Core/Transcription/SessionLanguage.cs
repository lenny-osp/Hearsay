using System.Globalization;

namespace Hearsay.Core.Transcription;

/// <summary>
/// When a session runs language detection and when its language is settled
/// (PLAN.md section 1, "Languages"). Pure bookkeeping: the caller runs the
/// detection and reports the result.
/// Port of <c>SessionLanguageTracker</c> in
/// mac/HearsayCore/Sources/HearsayCore/Transcription/SessionLanguage.swift.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Auto: the language is undecided at the start. A detection is due each
/// time another <see cref="AttemptIntervalSeconds"/> of audio exists. A
/// confident result (<see cref="DecisionReason.Detected"/>) locks the
/// language; a result over <see cref="LimitSeconds"/> or more of audio locks
/// whatever <see cref="LanguageDecision.Decide"/> says, which is the preferred
/// language when detection is still unsure.</item>
/// <item>Fixed: the language is known from the start. The same schedule runs
/// one background check, settled by the first result that found speech (or
/// by the limit); its suggestion is offered, never applied.</item>
/// </list>
/// <see cref="Finish"/> is the end of the audio (Stop, or a whole file): it
/// settles with that result whatever it is. <see cref="Choose"/> is the user
/// picking a language to re-run in; it settles without detection.
/// </remarks>
public sealed class SessionLanguageTracker
{
    /// <summary>Audio between two detection attempts, in seconds.</summary>
    public const double AttemptIntervalSeconds = 30;

    /// <summary>Audio after which the session stops waiting for a confident result, in seconds.</summary>
    public const double LimitSeconds = 90;

    private readonly int sampleRate;
    private int nextAttemptSamples;

    public SessionLanguageTracker(LanguageChoice choice, TranscriptLanguage preferred, int sampleRate = 16_000)
    {
        Choice = choice;
        Preferred = preferred;
        this.sampleRate = sampleRate;
        nextAttemptSamples = IntervalSamples;
        if (choice.FixedLanguage is { } fixedLanguage)
        {
            Decision = new LanguageDecision(fixedLanguage, new DecisionReason.Chosen());
        }
    }

    public LanguageChoice Choice { get; }

    public TranscriptLanguage Preferred { get; }

    /// <summary>
    /// The current decision: null while Auto is undecided; for a fixed choice
    /// <see cref="DecisionReason.Chosen"/> from the start, with a suggestion
    /// once the check found one.
    /// </summary>
    public LanguageDecision? Decision { get; private set; }

    /// <summary>No more detection attempts are due.</summary>
    public bool IsSettled { get; private set; }

    /// <summary>The language to transcribe in now; null while Auto is undecided.</summary>
    public TranscriptLanguage? Language => Decision?.Language;

    /// <summary>Auto has not locked a language yet.</summary>
    public bool IsUndecided => Decision is null;

    private int LimitSamples => (int)(LimitSeconds * sampleRate);

    private int IntervalSamples => (int)(AttemptIntervalSeconds * sampleRate);

    /// <summary>
    /// How many leading samples to run detection on now that
    /// <paramref name="totalSamples"/> exist, or null when no attempt is due.
    /// Never more than the limit of audio, since detection uses at most three
    /// 30 s windows anyway.
    /// </summary>
    public int? AttemptDue(int totalSamples)
    {
        if (IsSettled || totalSamples < nextAttemptSamples) return null;
        return Math.Min(totalSamples, LimitSamples);
    }

    /// <summary>
    /// Records a detection over the first <paramref name="samplesUsed"/>
    /// samples (null when it failed). Returns the decision when this settled
    /// the session.
    /// </summary>
    public LanguageDecision? Record(DetectedLanguage? detection, int samplesUsed)
    {
        if (IsSettled) return null;
        var result = LanguageDecision.Decide(Choice, Preferred, detection);
        bool conclusive = Choice.IsAuto
            ? result.Reason is DecisionReason.Detected
            : detection?.Code is not null;
        if (conclusive || samplesUsed >= LimitSamples)
        {
            Decision = result;
            IsSettled = true;
            return result;
        }
        int interval = IntervalSamples;
        nextAttemptSamples = ((samplesUsed / interval) + 1) * interval;
        return null;
    }

    /// <summary>
    /// The audio ended: settles with <paramref name="detection"/> (null when
    /// it failed or did not run) unless the session is already settled.
    /// Returns the decision.
    /// </summary>
    public LanguageDecision Finish(DetectedLanguage? detection)
    {
        if (IsSettled && Decision is { } decision) return decision;
        var result = LanguageDecision.Decide(Choice, Preferred, detection);
        Decision = result;
        IsSettled = true;
        return result;
    }

    /// <summary>
    /// The user picked <paramref name="language"/> for this session (a re-run
    /// or "Transcribe again"). Settles; the choice and preferred language are
    /// unchanged.
    /// </summary>
    public void Choose(TranscriptLanguage language)
    {
        Decision = new LanguageDecision(language, new DecisionReason.Chosen());
        IsSettled = true;
    }
}

/// <summary>
/// What the Record and File tabs tell the user about a session's language
/// (PLAN.md section 1, "Languages"). Port of <c>LanguageNotice</c> in
/// SessionLanguage.swift.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is the English text. The app localizes it through
/// its <c>.resw</c> with <see cref="MessageKey"/> (the key in
/// shared/localization/strings-en.json, <c>%@</c> placeholders) and
/// <see cref="MessageArgument"/>.
/// </remarks>
public abstract record LanguageNotice
{
    public const string SuggestionKey = "This sounds like %@. Transcribe again in %@?";
    public const string FallbackKey =
        "Couldn't tell the language, so this was transcribed in %@ (your Auto mode default language).";

    private LanguageNotice()
    {
    }

    /// <summary>The user picked a language and detection is confident it is another.</summary>
    public sealed record Suggestion(TranscriptLanguage Language) : LanguageNotice;

    /// <summary>Auto could not tell, so the preferred language was used.</summary>
    public sealed record Fallback(TranscriptLanguage Preferred) : LanguageNotice;

    /// <summary>The notice for <paramref name="decision"/>, or null when there is nothing to say.</summary>
    public static LanguageNotice? From(LanguageDecision? decision)
    {
        if (decision is null) return null;
        if (decision.Suggestion is { } suggestion) return new Suggestion(suggestion);
        if (decision.Reason is DecisionReason.FallbackToPreferred) return new Fallback(decision.Language);
        return null;
    }

    /// <summary>The string catalog key of <see cref="Message"/>.</summary>
    public string MessageKey => this switch
    {
        Suggestion => SuggestionKey,
        _ => FallbackKey,
    };

    /// <summary>The language name every <c>%@</c> in <see cref="MessageKey"/> stands for.</summary>
    public string MessageArgument => this switch
    {
        Suggestion s => s.Language.DisplayName(),
        Fallback f => f.Preferred.DisplayName(),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The English message.</summary>
    public string Message => MessageKey.Replace("%@", MessageArgument, StringComparison.Ordinal);

    /// <summary>
    /// The languages offered as re-run buttons: the suggestion, or every other
    /// supported language after a fallback, in picker order.
    /// </summary>
    public IReadOnlyList<TranscriptLanguage> RerunLanguages => this switch
    {
        Suggestion s => [s.Language],
        Fallback f => [.. TranscriptLanguages.All.Where(language => language != f.Preferred)],
        _ => throw new InvalidOperationException(),
    };
}

/// <summary>
/// The debug-path summary of a decision. Port of the <c>debugSummary</c>
/// extension in SessionLanguage.swift.
/// </summary>
public static class LanguageDecisionDebug
{
    /// <summary>One line for the debug paths: language, reason, confidence, suggestion.</summary>
    public static string DebugSummary(this LanguageDecision decision)
    {
        var (reasonText, confidence) = decision.Reason switch
        {
            DecisionReason.Detected d => ("detected", Format(d.Confidence)),
            DecisionReason.FallbackToPreferred f =>
                ("fallback-to-preferred", f.DetectedConfidence is { } value ? Format(value) : "none"),
            _ => ("chosen", "-"),
        };
        return $"language {decision.Language.Code()}, reason {reasonText}, confidence {confidence}, "
            + $"suggestion {decision.Suggestion?.Code() ?? "none"}";
    }

    // Swift's String(format: "%.4f") promotes the Float to Double first.
    private static string Format(float value) =>
        ((double)value).ToString("F4", CultureInfo.InvariantCulture);
}

/// <summary>
/// The notes language for an SRT whose transcript language was not stored
/// (History "Generate notes..." on an older transcript): the fixed choice, or
/// the preferred language for Auto, with the confirm sheet's explanation.
/// Port of <c>StoredTranscriptLanguage.assumed</c> and <c>resolve(srtText:choice:preferred:)</c>
/// in SessionLanguage.swift.
/// </summary>
/// <remarks>The notes are English; the app localizes them by <see cref="ChoiceNoteKey"/>,
/// <see cref="AutoNoteKey"/> and <see cref="DetectedNoteKey"/>.</remarks>
public static class StoredTranscriptLanguage
{
    public const string ChoiceNoteKey = "%@ (your language choice; this transcript's language was not recorded)";
    public const string AutoNoteKey =
        "%@ (your Auto mode default language; this transcript's language was not recorded)";
    public const string DetectedNoteKey = "%@ (detected from the text)";

    public static (TranscriptLanguage Language, string Note) Assumed(LanguageChoice choice, TranscriptLanguage preferred)
    {
        if (choice.FixedLanguage is { } language)
        {
            return (language, ChoiceNoteKey.Replace("%@", language.DisplayName(), StringComparison.Ordinal));
        }
        return (preferred, AutoNoteKey.Replace("%@", preferred.DisplayName(), StringComparison.Ordinal));
    }

    /// <summary>
    /// The language detected from <paramref name="srtText"/> when
    /// <see cref="TranscriptTextLanguage"/> is confident (at least
    /// <see cref="TranscriptTextLanguage.ConfidenceThreshold"/>), noted as
    /// detected; otherwise <see cref="Assumed"/>.
    /// </summary>
    public static (TranscriptLanguage Language, string Note) Resolve(
        string srtText, LanguageChoice choice, TranscriptLanguage preferred)
    {
        if (TranscriptTextLanguage.DetectSrt(srtText) is { } detected
            && detected.Probability >= TranscriptTextLanguage.ConfidenceThreshold)
        {
            return (detected.Language,
                DetectedNoteKey.Replace("%@", detected.Language.DisplayName(), StringComparison.Ordinal));
        }
        return Assumed(choice, preferred);
    }
}

/// <summary>
/// The confirm sheet's lines under the "Notes language" picker. Port of
/// <c>NotesLanguageCaption</c> in SessionLanguage.swift (English text; the app
/// localizes by the same keys).
/// </summary>
public static class NotesLanguageCaption
{
    public const string TranscriptLineKey = "Transcript language: %@";
    public const string NotesLineKey = "Notes will be written in %@.";

    /// <summary>
    /// "Transcript language: 繁體中文", or with the caller's note, e.g.
    /// "Transcript language: Deutsch (detected from the text)".
    /// </summary>
    public static string TranscriptLine(TranscriptLanguage language, string? note = null) =>
        TranscriptLineKey.Replace("%@", note ?? language.DisplayName(), StringComparison.Ordinal);

    /// <summary>
    /// "Notes will be written in Deutsch." when the choice differs from the
    /// transcript language; null otherwise.
    /// </summary>
    public static string? NotesLine(TranscriptLanguage transcript, TranscriptLanguage notes) =>
        notes == transcript ? null : NotesLineKey.Replace("%@", notes.DisplayName(), StringComparison.Ordinal);
}

/// <summary>
/// Whisper's language distribution for one 30 s window, code to probability
/// over every language the model knows (sums to 1). Port of
/// <c>LanguageProbabilities</c> in
/// mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/LanguageDetection.swift
/// (the pure part; on Windows the engine is whisper.cpp through Whisper.net).
/// </summary>
public sealed class LanguageProbabilities(IReadOnlyDictionary<string, float> probabilities)
{
    public IReadOnlyDictionary<string, float> Probabilities { get; } = probabilities;

    /// <summary>
    /// The probabilities of <paramref name="candidates"/> only, renormalized to
    /// sum to 1. Codes the model does not know are left out; duplicates count
    /// once. Empty when no candidate has a positive probability.
    /// </summary>
    public IReadOnlyDictionary<string, float> Restricted(IReadOnlyList<string> candidates)
    {
        var kept = new Dictionary<string, float>();
        var order = new List<string>();
        foreach (var code in candidates)
        {
            if (Probabilities.TryGetValue(code, out float p))
            {
                if (!kept.ContainsKey(code)) order.Add(code);
                kept[code] = p;
            }
        }
        float total = 0;
        foreach (var code in order) total += kept[code];
        if (!(total > 0) || !float.IsFinite(total)) return new Dictionary<string, float>();
        var result = new Dictionary<string, float>();
        foreach (var code in order) result[code] = kept[code] / total;
        return result;
    }

    /// <summary>
    /// The most probable of <paramref name="candidates"/> and its renormalized
    /// probability; the earlier candidate wins a tie. Null when
    /// <see cref="Restricted"/> is empty.
    /// </summary>
    public DetectedLanguage? Best(IReadOnlyList<string> candidates) =>
        LanguageDetection.BestCandidate(Restricted(candidates), candidates);
}

/// <summary>What the engine reports for one 30 s window.</summary>
/// <param name="Probabilities">Language distribution over every language the model knows.</param>
/// <param name="NoSpeechProbability">The no-speech token's probability at the start-of-transcript position.</param>
public readonly record struct WindowLanguage(LanguageProbabilities Probabilities, float NoSpeechProbability);

/// <summary>Result of <see cref="LanguageDetection.Detect"/>. Port of <c>DetectionResult</c> in LanguageDetection.swift.</summary>
/// <param name="Code">The detected candidate; null when no speech window was found.</param>
/// <param name="Confidence">The averaged restricted probability of <paramref name="Code"/> (0 when it is null).</param>
/// <param name="WindowsUsed">Number of speech windows that were averaged.</param>
/// <param name="PerWindow">Restricted (renormalized over the candidates) probabilities of each speech window used, in order.</param>
public sealed record DetectionResult(
    string? Code,
    float Confidence,
    int WindowsUsed,
    IReadOnlyList<IReadOnlyDictionary<string, float>> PerWindow)
{
    /// <summary>The input <see cref="LanguageDecision.Decide"/> and <see cref="SessionLanguageTracker"/> take.</summary>
    public DetectedLanguage Detection => new(Code, Confidence);
}

/// <summary>
/// Session language detection over up to three 30 s speech windows: the
/// engine-independent part of <c>Transcriber.detectLanguage(samples:candidates:)</c>
/// in mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/LanguageDetection.swift.
/// The engine supplies one window's distribution and no-speech probability
/// through a callback.
/// </summary>
public static class LanguageDetection
{
    /// <summary>Samples in one 30 s window at 16 kHz (Whisper's <c>N_SAMPLES</c>).</summary>
    public const int WindowSamples = 30 * 16_000;

    /// <summary>At most this many speech windows are averaged.</summary>
    public const int MaxSpeechWindows = 3;

    /// <summary>
    /// A window whose no-speech probability is above this is skipped; the same
    /// value <c>transcribe()</c> compares with <c>no_speech_threshold</c>.
    /// </summary>
    public const float NoSpeechThreshold = 0.6f;

    /// <summary>
    /// A window whose RMS is below this is skipped: 0.001 is -60 dBFS. The RMS
    /// gate is needed because large-v3-turbo's no-speech probability does not
    /// flag silence (it stays near zero even on digital silence, measured
    /// 1.5e-10 in Python on 30 s of zeros). Pass 0 to rely on the no-speech
    /// probability alone.
    /// </summary>
    public const float SilenceRms = 0.001f;

    /// <summary>
    /// Detects the language among <paramref name="candidates"/> (Whisper codes)
    /// by walking 30 s windows from the start of <paramref name="samples"/>.
    /// </summary>
    /// <remarks>
    /// A window counts as speech when its RMS is at least
    /// <paramref name="silenceRms"/> and its no-speech probability is at most
    /// <paramref name="noSpeechThreshold"/>. Up to
    /// <paramref name="maxSpeechWindows"/> speech windows are combined by
    /// <b>averaging their restricted probabilities</b>, not by summing log
    /// probabilities. Summing logs treats windows as independent evidence, so
    /// one window that is confidently wrong (music, a quoted name, a phrase in
    /// another language) can drive a candidate to near zero and outvote the
    /// others; averaging keeps each window's say bounded and keeps the
    /// confidence on the same 0...1 scale as a single window, which is what
    /// the caller's confidence threshold is written against.
    /// The code is null when no speech window was found.
    /// </remarks>
    /// <param name="samples">16 kHz mono samples.</param>
    /// <param name="candidates">Whisper codes; duplicates count once. The caller checks the model knows them.</param>
    /// <param name="detectWindow">Runs the model on one window (up to 30 s; the engine pads it).</param>
    public static DetectionResult Detect(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> candidates,
        Func<ReadOnlyMemory<float>, WindowLanguage> detectWindow,
        int maxSpeechWindows = MaxSpeechWindows,
        float noSpeechThreshold = NoSpeechThreshold,
        float silenceRms = SilenceRms)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(detectWindow);
        var codes = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!codes.Contains(candidate)) codes.Add(candidate);
        }

        var perWindow = new List<IReadOnlyDictionary<string, float>>();
        int start = 0;
        while (start < samples.Length && perWindow.Count < maxSpeechWindows)
        {
            var window = WindowSlice(samples, start);
            start += WindowSamples;
            if (RootMeanSquare(window.Span) < silenceRms) continue;
            var (probabilities, noSpeech) = detectWindow(window);
            if (noSpeech > noSpeechThreshold) continue;
            var restricted = probabilities.Restricted(codes);
            if (restricted.Count > 0) perWindow.Add(restricted);
        }

        var averaged = AverageDistributions(perWindow, codes);
        var best = BestCandidate(averaged, codes);
        return new DetectionResult(best?.Code, best?.Confidence ?? 0, perWindow.Count, perWindow);
    }

    /// <summary>The highest value in <paramref name="probabilities"/>, ties broken by position in <paramref name="order"/>.</summary>
    public static DetectedLanguage? BestCandidate(
        IReadOnlyDictionary<string, float> probabilities, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentNullException.ThrowIfNull(order);
        DetectedLanguage? best = null;
        foreach (var code in order)
        {
            if (!probabilities.TryGetValue(code, out float p)) continue;
            if (best is { } current && p <= current.Confidence) continue;
            best = new DetectedLanguage(code, p);
        }
        return best;
    }

    /// <summary>Average of per-window restricted distributions, per code.</summary>
    public static IReadOnlyDictionary<string, float> AverageDistributions(
        IReadOnlyList<IReadOnlyDictionary<string, float>> windows, IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(codes);
        var result = new Dictionary<string, float>();
        if (windows.Count == 0) return result;
        foreach (var code in codes)
        {
            float sum = 0;
            foreach (var window in windows)
            {
                sum += window.TryGetValue(code, out float p) ? p : 0;
            }
            result[code] = sum / windows.Count;
        }
        return result;
    }

    /// <summary>Root mean square of <paramref name="samples"/> (0 for an empty span), accumulated in double.</summary>
    public static float RootMeanSquare(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (float s in samples) sum += (double)s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Up to 30 s of samples from <paramref name="start"/>, clamped to the buffer.</summary>
    public static ReadOnlyMemory<float> WindowSlice(ReadOnlyMemory<float> samples, int start)
    {
        int lower = Math.Min(Math.Max(start, 0), samples.Length);
        int upper = Math.Min(lower + WindowSamples, samples.Length);
        return samples[lower..upper];
    }
}
