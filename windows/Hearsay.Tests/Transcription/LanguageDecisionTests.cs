using System.Text.Json;
using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>Port of LanguageDecisionTests in mac/HearsayCore/Tests/HearsayCoreTests/LanguageDecisionTests.swift.</summary>
public class LanguageDecisionTests
{
    private static readonly LanguageChoice Auto = LanguageChoice.Auto;

    private static LanguageChoice Fixed(TranscriptLanguage language) => LanguageChoice.Fixed(language);

    private static LanguageDecision Decide(LanguageChoice choice, TranscriptLanguage preferred, string? code, float confidence) =>
        LanguageDecision.Decide(choice, preferred, new DetectedLanguage(code, confidence));

    private static LanguageDecision D(TranscriptLanguage language, DecisionReason reason, TranscriptLanguage? suggestion = null) =>
        new(language, reason, suggestion);

    private static DecisionReason Chosen => new DecisionReason.Chosen();

    [Fact]
    public void Thresholds()
    {
        Assert.Equal(0.7f, LanguageDecision.AutoThreshold);
        Assert.Equal(0.85f, LanguageDecision.MismatchThreshold);
    }

    // Auto

    [Fact]
    public void AutoConfidentUsesDetected()
    {
        Assert.Equal(
            D(TranscriptLanguage.German, new DecisionReason.Detected(0.93f)),
            Decide(Auto, TranscriptLanguage.English, "de", 0.93f));
    }

    [Fact]
    public void AutoAtThresholdUsesDetected()
    {
        Assert.Equal(
            D(TranscriptLanguage.Spanish, new DecisionReason.Detected(LanguageDecision.AutoThreshold)),
            Decide(Auto, TranscriptLanguage.English, "es", LanguageDecision.AutoThreshold));
    }

    [Fact]
    public void AutoJustBelowThresholdFallsBack()
    {
        float below = MathF.BitDecrement(LanguageDecision.AutoThreshold);
        Assert.Equal(
            D(TranscriptLanguage.ChineseMainland, new DecisionReason.FallbackToPreferred(below)),
            Decide(Auto, TranscriptLanguage.ChineseMainland, "es", below));
    }

    [Fact]
    public void AutoLowConfidenceFallsBackToPreferred()
    {
        var decision = Decide(Auto, TranscriptLanguage.English, "zh", 0.4f);
        Assert.Equal(D(TranscriptLanguage.English, new DecisionReason.FallbackToPreferred(0.4f)), decision);
        Assert.Null(decision.Suggestion);
    }

    [Fact]
    public void AutoDetectedZhUsesPreferredChineseVariant()
    {
        Assert.Equal(
            D(TranscriptLanguage.ChineseMainland, new DecisionReason.Detected(0.9f)),
            Decide(Auto, TranscriptLanguage.ChineseMainland, "zh", 0.9f));
        Assert.Equal(
            D(TranscriptLanguage.ChineseTaiwan, new DecisionReason.Detected(0.9f)),
            Decide(Auto, TranscriptLanguage.ChineseTaiwan, "zh", 0.9f));
    }

    [Fact]
    public void AutoDetectedZhDefaultsToTaiwanForOtherPreferred()
    {
        foreach (var preferred in new[] { TranscriptLanguage.English, TranscriptLanguage.German, TranscriptLanguage.Spanish })
        {
            Assert.Equal(
                D(TranscriptLanguage.ChineseTaiwan, new DecisionReason.Detected(0.9f)),
                Decide(Auto, preferred, "zh", 0.9f));
        }
    }

    [Fact]
    public void AutoDetectedPreferredLanguageIsDetected()
    {
        Assert.Equal(
            D(TranscriptLanguage.English, new DecisionReason.Detected(0.9f)),
            Decide(Auto, TranscriptLanguage.English, "en", 0.9f));
    }

    [Fact]
    public void AutoNoSpeechFallsBackWithoutConfidence()
    {
        Assert.Equal(
            D(TranscriptLanguage.German, new DecisionReason.FallbackToPreferred(null)),
            Decide(Auto, TranscriptLanguage.German, null, 0));
    }

    [Fact]
    public void AutoNoDetectionFallsBackWithoutConfidence()
    {
        Assert.Equal(
            D(TranscriptLanguage.Spanish, new DecisionReason.FallbackToPreferred(null)),
            LanguageDecision.Decide(Auto, TranscriptLanguage.Spanish, null));
    }

    [Fact]
    public void AutoUnsupportedCodeCountsAsNoSpeech()
    {
        Assert.Equal(
            D(TranscriptLanguage.English, new DecisionReason.FallbackToPreferred(null)),
            Decide(Auto, TranscriptLanguage.English, "fr", 0.99f));
    }

    // Fixed

    [Fact]
    public void FixedSameLanguageNoSuggestion()
    {
        Assert.Equal(
            D(TranscriptLanguage.ChineseTaiwan, Chosen),
            Decide(Fixed(TranscriptLanguage.ChineseTaiwan), TranscriptLanguage.English, "zh", 0.99f));
    }

    [Fact]
    public void FixedChineseVariantNeverSuggestsTheOtherVariant()
    {
        foreach (var preferred in TranscriptLanguages.All)
        {
            foreach (var fixedLanguage in new[] { TranscriptLanguage.ChineseTaiwan, TranscriptLanguage.ChineseMainland })
            {
                Assert.Equal(D(fixedLanguage, Chosen), Decide(Fixed(fixedLanguage), preferred, "zh", 0.99f));
            }
        }
    }

    [Fact]
    public void FixedMismatchToZhSuggestsPreferredVariant()
    {
        Assert.Equal(
            TranscriptLanguage.ChineseMainland,
            Decide(Fixed(TranscriptLanguage.English), TranscriptLanguage.ChineseMainland, "zh", 0.95f).Suggestion);
        Assert.Equal(
            TranscriptLanguage.ChineseTaiwan,
            Decide(Fixed(TranscriptLanguage.English), TranscriptLanguage.German, "zh", 0.95f).Suggestion);
    }

    [Fact]
    public void FixedConfidentMismatchSuggests()
    {
        Assert.Equal(
            D(TranscriptLanguage.English, Chosen, TranscriptLanguage.German),
            Decide(Fixed(TranscriptLanguage.English), TranscriptLanguage.English, "de", 0.95f));
    }

    [Fact]
    public void FixedMismatchAtThresholdSuggests()
    {
        Assert.Equal(
            D(TranscriptLanguage.English, Chosen, TranscriptLanguage.Spanish),
            Decide(Fixed(TranscriptLanguage.English), TranscriptLanguage.English, "es", LanguageDecision.MismatchThreshold));
    }

    [Fact]
    public void FixedMismatchJustBelowThresholdNoSuggestion()
    {
        Assert.Equal(
            D(TranscriptLanguage.English, Chosen),
            Decide(Fixed(TranscriptLanguage.English), TranscriptLanguage.English, "es",
                MathF.BitDecrement(LanguageDecision.MismatchThreshold)));
    }

    [Fact]
    public void FixedMismatchAboveAutoButBelowMismatchNoSuggestion()
    {
        Assert.Equal(
            D(TranscriptLanguage.German, Chosen),
            Decide(Fixed(TranscriptLanguage.German), TranscriptLanguage.English, "en", 0.8f));
    }

    [Fact]
    public void FixedNoSpeechOrNoDetectionNoSuggestion()
    {
        foreach (DetectedLanguage? detection in new DetectedLanguage?[] { new(null, 0), null, new("fr", 0.99f) })
        {
            Assert.Equal(
                D(TranscriptLanguage.Spanish, Chosen),
                LanguageDecision.Decide(Fixed(TranscriptLanguage.Spanish), TranscriptLanguage.English, detection));
        }
    }

    [Fact]
    public void FixedIgnoresPreferred()
    {
        foreach (var preferred in TranscriptLanguages.All)
        {
            Assert.Equal(
                TranscriptLanguage.ChineseMainland,
                Decide(Fixed(TranscriptLanguage.ChineseMainland), preferred, "zh", 0.2f).Language);
        }
    }
}

/// <summary>
/// Runs shared/language-decision-tests.json. Port of
/// SharedLanguageDecisionVectorTests in LanguageDecisionTests.swift.
/// </summary>
public class SharedLanguageDecisionVectorTests
{
    private sealed record VectorDetection(string? Code, float Confidence);

    private sealed record VectorExpect(string Language, string Reason, float? Confidence, string? Suggestion);

    private sealed record VectorCase(
        string Note, string Choice, string Preferred, VectorDetection? Detection, VectorExpect Expect);

    private sealed record VectorThresholds(float Auto, float Mismatch);

    private sealed record Vectors(VectorThresholds Thresholds, IReadOnlyList<VectorCase> Cases);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    private static Vectors Load()
    {
        var vectors = JsonSerializer.Deserialize<Vectors>(SharedFiles.ReadText("language-decision-tests.json"), Options);
        Assert.NotNull(vectors);
        return vectors;
    }

    private static TranscriptLanguage Language(string code, string note) =>
        TranscriptLanguages.FromCode(code) ?? throw new Xunit.Sdk.XunitException($"unknown language {code} in {note}");

    [Fact]
    public void ThresholdsMatch()
    {
        var vectors = Load();
        Assert.Equal(LanguageDecision.AutoThreshold, vectors.Thresholds.Auto);
        Assert.Equal(LanguageDecision.MismatchThreshold, vectors.Thresholds.Mismatch);
        // The "just below" vectors really are the next float down.
        var confidences = vectors.Cases.Select(c => c.Detection).OfType<VectorDetection>().Select(d => d.Confidence).ToList();
        Assert.Contains(MathF.BitDecrement(LanguageDecision.AutoThreshold), confidences);
        Assert.Contains(MathF.BitDecrement(LanguageDecision.MismatchThreshold), confidences);
        Assert.Contains(LanguageDecision.AutoThreshold, confidences);
        Assert.Contains(LanguageDecision.MismatchThreshold, confidences);
    }

    [Fact]
    public void JustBelowLiteralsParseToTheNextFloatDown()
    {
        Assert.Equal(MathF.BitDecrement(0.7f), JsonSerializer.Deserialize<float>("0.69999993"));
        Assert.Equal(MathF.BitDecrement(0.85f), JsonSerializer.Deserialize<float>("0.84999996"));
    }

    [Fact]
    public void EveryVector()
    {
        var cases = Load().Cases;
        Assert.NotEmpty(cases);
        foreach (var vector in cases)
        {
            string note = vector.Note;
            var choice = LanguageChoice.FromStorageValue(vector.Choice)
                ?? throw new Xunit.Sdk.XunitException($"unknown choice {vector.Choice} in {note}");
            var preferred = Language(vector.Preferred, note);
            var language = Language(vector.Expect.Language, note);
            TranscriptLanguage? suggestion = vector.Expect.Suggestion is { } s ? Language(s, note) : null;
            DecisionReason reason;
            switch (vector.Expect.Reason)
            {
                case "chosen":
                    Assert.True(vector.Expect.Confidence is null, note);
                    reason = new DecisionReason.Chosen();
                    break;
                case "detected":
                    reason = new DecisionReason.Detected(
                        vector.Expect.Confidence ?? throw new Xunit.Sdk.XunitException($"missing confidence in {note}"));
                    break;
                case "fallbackToPreferred":
                    reason = new DecisionReason.FallbackToPreferred(vector.Expect.Confidence);
                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"unknown reason {vector.Expect.Reason} in {note}");
            }
            DetectedLanguage? detection = vector.Detection is { } d ? new DetectedLanguage(d.Code, d.Confidence) : null;
            var decision = LanguageDecision.Decide(choice, preferred, detection);
            var expected = new LanguageDecision(language, reason, suggestion);
            Assert.True(expected == decision, $"{note}: expected {expected}, got {decision}");
        }
    }
}
