namespace Hearsay.Core.Transcription;

/// <summary>
/// A language detection result as the decision rules take it: a Whisper code
/// (<see cref="TranscriptLanguages.WhisperCodes"/>) and its confidence. A null
/// <see cref="Code"/> means no speech was detected. Confidences are 32-bit
/// floats, as on the Mac, so the threshold comparisons agree bit for bit.
/// Port of the <c>(code: String?, confidence: Float)</c> tuple in
/// mac/HearsayCore/Sources/HearsayCore/Transcription/LanguageDecision.swift.
/// </summary>
public readonly record struct DetectedLanguage(string? Code, float Confidence);

/// <summary>
/// Why a session is transcribed in its language. Port of
/// <c>LanguageDecision.Reason</c> in LanguageDecision.swift.
/// </summary>
public abstract record DecisionReason
{
    private DecisionReason()
    {
    }

    /// <summary>The user picked a fixed language.</summary>
    public sealed record Chosen : DecisionReason;

    /// <summary>Auto: detection was confident enough.</summary>
    public sealed record Detected(float Confidence) : DecisionReason;

    /// <summary>
    /// Auto: detection was below <see cref="LanguageDecision.AutoThreshold"/>,
    /// or found no speech (<see cref="DetectedConfidence"/> null), so the
    /// preferred language is used.
    /// </summary>
    public sealed record FallbackToPreferred(float? DetectedConfidence) : DecisionReason;
}

/// <summary>
/// Which language a session is transcribed in, and why (PLAN.md section 1,
/// "Languages"). Pure rules: the detection itself (restricted to the four
/// supported languages, no-speech windows skipped) happens elsewhere.
/// Port of mac/HearsayCore/Sources/HearsayCore/Transcription/LanguageDecision.swift.
/// </summary>
/// <param name="Language">The language to transcribe in.</param>
/// <param name="Reason">Why.</param>
/// <param name="Suggestion">A different language to offer re-running in; never applied automatically.</param>
public sealed record LanguageDecision(
    TranscriptLanguage Language,
    DecisionReason Reason,
    TranscriptLanguage? Suggestion = null)
{
    /// <summary>Auto uses the detected language at or above this confidence.</summary>
    public const float AutoThreshold = 0.7f;

    /// <summary>
    /// With a fixed choice, a different detected language at or above this
    /// confidence is offered as a re-run suggestion.
    /// </summary>
    public const float MismatchThreshold = 0.85f;

    /// <summary>
    /// Applies the rules.
    /// </summary>
    /// <param name="choice">What the user picked.</param>
    /// <param name="preferred">The preferred language from Settings &gt; General (the Auto mode default language).</param>
    /// <param name="detection">
    /// The detection result, or null when detection did not run. Its code is a
    /// Whisper code; a null code means no speech was detected. A code outside
    /// the supported languages is treated like no speech. A detected "zh" is
    /// the preferred language's Chinese variant when the preferred language is
    /// ZH-TW or ZH-CN, otherwise ZH-TW.
    /// </param>
    /// <remarks>
    /// With a fixed choice, a suggestion needs a different Whisper language:
    /// "zh" never suggests the other Chinese variant.
    /// </remarks>
    public static LanguageDecision Decide(
        LanguageChoice choice,
        TranscriptLanguage preferred,
        DetectedLanguage? detection)
    {
        (TranscriptLanguage Language, float Confidence)? detected = null;
        if (detection is { Code: { } code } result
            && TranscriptLanguages.FromWhisperCode(code, preferred) is { } language)
        {
            detected = (language, result.Confidence);
        }

        if (choice.FixedLanguage is not { } fixedLanguage)
        {
            if (detected is { } confident && confident.Confidence >= AutoThreshold)
            {
                return new LanguageDecision(confident.Language, new DecisionReason.Detected(confident.Confidence));
            }
            return new LanguageDecision(
                preferred,
                new DecisionReason.FallbackToPreferred(detected?.Confidence));
        }

        TranscriptLanguage? suggestion = null;
        if (detected is { } other
            && other.Language.WhisperCode() != fixedLanguage.WhisperCode()
            && other.Confidence >= MismatchThreshold)
        {
            suggestion = other.Language;
        }
        return new LanguageDecision(fixedLanguage, new DecisionReason.Chosen(), suggestion);
    }
}
