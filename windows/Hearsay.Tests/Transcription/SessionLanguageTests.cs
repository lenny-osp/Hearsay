using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>Port of SessionLanguageTrackerTests in mac/HearsayCore/Tests/HearsayCoreTests/SessionLanguageTests.swift.</summary>
public class SessionLanguageTrackerTests
{
    /// <summary>A small rate keeps the arithmetic readable: 1 sample per second.</summary>
    private const int Rate = 1;

    private static SessionLanguageTracker T(LanguageChoice choice, TranscriptLanguage preferred) =>
        new(choice, preferred, Rate);

    private static DetectedLanguage Det(string? code, float confidence) => new(code, confidence);

    private static LanguageDecision Chosen(TranscriptLanguage language, TranscriptLanguage? suggestion = null) =>
        new(language, new DecisionReason.Chosen(), suggestion);

    [Fact]
    public void Schedule()
    {
        Assert.Equal(30, SessionLanguageTracker.AttemptIntervalSeconds);
        Assert.Equal(90, SessionLanguageTracker.LimitSeconds);
    }

    [Fact]
    public void AutoStartsUndecidedAndFixedStartsChosen()
    {
        var auto = T(LanguageChoice.Auto, TranscriptLanguage.English);
        Assert.True(auto.IsUndecided);
        Assert.Null(auto.Language);
        var fixedTracker = T(LanguageChoice.Fixed(TranscriptLanguage.German), TranscriptLanguage.English);
        Assert.False(fixedTracker.IsUndecided);
        Assert.Equal(Chosen(TranscriptLanguage.German), fixedTracker.Decision);
    }

    [Fact]
    public void FirstAttemptIsDueAt30Seconds()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        Assert.Null(tracker.AttemptDue(29));
        Assert.Equal(30, tracker.AttemptDue(30));
        Assert.Equal(41, tracker.AttemptDue(41));
    }

    [Fact]
    public void AttemptNeverUsesMoreThanTheLimit()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        Assert.Equal(90, tracker.AttemptDue(200));
    }

    [Fact]
    public void AutoConfidentResultLocks()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        var decision = tracker.Record(Det("de", 0.95f), 30);
        Assert.Equal(new LanguageDecision(TranscriptLanguage.German, new DecisionReason.Detected(0.95f)), decision);
        Assert.True(tracker.IsSettled);
        Assert.Equal(TranscriptLanguage.German, tracker.Language);
        Assert.Null(tracker.AttemptDue(60));
        // Later results change nothing: the language is locked.
        Assert.Null(tracker.Record(Det("en", 0.99f), 60));
        Assert.Equal(TranscriptLanguage.German, tracker.Finish(Det("en", 0.99f)).Language);
    }

    [Fact]
    public void AutoUnsureRetriesEvery30SecondsThenFallsBackAt90()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.Spanish);
        Assert.Null(tracker.Record(Det("en", 0.5f), 30));
        Assert.True(tracker.IsUndecided);
        Assert.Null(tracker.AttemptDue(59));
        Assert.Equal(60, tracker.AttemptDue(60));
        Assert.Null(tracker.Record(Det(null, 0), 60));
        Assert.Null(tracker.AttemptDue(89));
        Assert.Equal(90, tracker.AttemptDue(90));
        var decision = tracker.Record(Det("en", 0.6f), 90);
        Assert.Equal(
            new LanguageDecision(TranscriptLanguage.Spanish, new DecisionReason.FallbackToPreferred(0.6f)),
            decision);
        Assert.True(tracker.IsSettled);
    }

    [Fact]
    public void LateAttemptSchedulesTheNextMultipleOf30()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        tracker.Record(Det("en", 0.5f), 45);
        Assert.Null(tracker.AttemptDue(59));
        Assert.Equal(60, tracker.AttemptDue(60));
    }

    [Fact]
    public void AutoFailedDetectionRetries()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        Assert.Null(tracker.Record(null, 30));
        Assert.Equal(60, tracker.AttemptDue(60));
    }

    [Fact]
    public void AutoFinishBeforeDecisionUsesTheWholeRecordingResult()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        Assert.Equal(
            new LanguageDecision(TranscriptLanguage.ChineseTaiwan, new DecisionReason.Detected(0.8f)),
            tracker.Finish(Det("zh", 0.8f)));
        var mainland = T(LanguageChoice.Auto, TranscriptLanguage.ChineseMainland);
        Assert.Equal(
            new LanguageDecision(TranscriptLanguage.ChineseMainland, new DecisionReason.Detected(0.8f)),
            mainland.Finish(Det("zh", 0.8f)));
        var unsure = T(LanguageChoice.Auto, TranscriptLanguage.German);
        Assert.Equal(
            new LanguageDecision(TranscriptLanguage.German, new DecisionReason.FallbackToPreferred(null)),
            unsure.Finish(Det(null, 0)));
        Assert.True(unsure.IsSettled);
    }

    [Fact]
    public void FixedCheckSettlesOnFirstSpeechAndKeepsTheSuggestion()
    {
        var tracker = T(LanguageChoice.Fixed(TranscriptLanguage.English), TranscriptLanguage.English);
        Assert.Null(tracker.Record(Det(null, 0), 30));
        Assert.Equal(TranscriptLanguage.English, tracker.Language);
        var decision = tracker.Record(Det("de", 0.97f), 60);
        Assert.Equal(Chosen(TranscriptLanguage.English, TranscriptLanguage.German), decision);
        Assert.True(tracker.IsSettled);
    }

    [Fact]
    public void FixedCheckWithSpeechButNoMismatchSettlesWithoutSuggestion()
    {
        var tracker = T(LanguageChoice.Fixed(TranscriptLanguage.English), TranscriptLanguage.German);
        var decision = tracker.Record(Det("de", 0.6f), 30);
        Assert.Equal(Chosen(TranscriptLanguage.English), decision);
        Assert.True(tracker.IsSettled);
    }

    [Fact]
    public void ChooseSettlesWithTheUsersLanguage()
    {
        var tracker = T(LanguageChoice.Auto, TranscriptLanguage.English);
        tracker.Finish(Det(null, 0));
        tracker.Choose(TranscriptLanguage.Spanish);
        Assert.Equal(Chosen(TranscriptLanguage.Spanish), tracker.Decision);
        Assert.Equal(LanguageChoice.Auto, tracker.Choice);
        Assert.Equal(TranscriptLanguage.English, tracker.Preferred);
    }

    [Fact]
    public void DefaultSampleRateIs16k()
    {
        var tracker = new SessionLanguageTracker(LanguageChoice.Auto, TranscriptLanguage.English);
        Assert.Null(tracker.AttemptDue((30 * 16_000) - 1));
        Assert.Equal(30 * 16_000, tracker.AttemptDue(30 * 16_000));
        Assert.Equal(90 * 16_000, tracker.AttemptDue(200 * 16_000));
    }
}

/// <summary>Port of LanguageNoticeTests in SessionLanguageTests.swift.</summary>
public class LanguageNoticeTests
{
    [Fact]
    public void SuggestionNotice()
    {
        var decision = new LanguageDecision(TranscriptLanguage.English, new DecisionReason.Chosen(), TranscriptLanguage.German);
        var notice = LanguageNotice.From(decision);
        Assert.Equal(new LanguageNotice.Suggestion(TranscriptLanguage.German), notice);
        Assert.NotNull(notice);
        Assert.Equal("This sounds like Deutsch. Transcribe again in Deutsch?", notice.Message);
        Assert.Equal([TranscriptLanguage.German], notice.RerunLanguages);
    }

    [Fact]
    public void FallbackNotice()
    {
        var decision = new LanguageDecision(TranscriptLanguage.English, new DecisionReason.FallbackToPreferred(0.4f));
        var notice = LanguageNotice.From(decision);
        Assert.Equal(new LanguageNotice.Fallback(TranscriptLanguage.English), notice);
        Assert.NotNull(notice);
        Assert.Equal(
            "Couldn't tell the language, so this was transcribed in English (your Auto mode default language).",
            notice.Message);
        Assert.Equal(
            [TranscriptLanguage.ChineseTaiwan, TranscriptLanguage.ChineseMainland, TranscriptLanguage.German, TranscriptLanguage.Spanish],
            notice.RerunLanguages);
        Assert.Equal(
            [TranscriptLanguage.English, TranscriptLanguage.ChineseTaiwan, TranscriptLanguage.ChineseMainland, TranscriptLanguage.Spanish],
            new LanguageNotice.Fallback(TranscriptLanguage.German).RerunLanguages);
        Assert.Equal(
            [TranscriptLanguage.English, TranscriptLanguage.ChineseTaiwan, TranscriptLanguage.German, TranscriptLanguage.Spanish],
            new LanguageNotice.Fallback(TranscriptLanguage.ChineseMainland).RerunLanguages);
    }

    [Fact]
    public void NoNoticeForConfidentOrChosen()
    {
        Assert.Null(LanguageNotice.From(null));
        Assert.Null(LanguageNotice.From(new LanguageDecision(TranscriptLanguage.German, new DecisionReason.Detected(0.9f))));
        Assert.Null(LanguageNotice.From(new LanguageDecision(TranscriptLanguage.German, new DecisionReason.Chosen())));
    }

    [Fact]
    public void MessageKeysAreTheSharedCatalogKeys()
    {
        using var catalog = System.Text.Json.JsonDocument.Parse(SharedFiles.ReadText("localization", "strings-en.json"));
        var keys = catalog.RootElement.EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in new[]
        {
            LanguageNotice.SuggestionKey, LanguageNotice.FallbackKey,
            StoredTranscriptLanguage.ChoiceNoteKey, StoredTranscriptLanguage.AutoNoteKey,
            NotesLanguageCaption.TranscriptLineKey, NotesLanguageCaption.NotesLineKey,
        })
        {
            Assert.True(keys.Contains(key), key);
        }
    }

    [Fact]
    public void DebugSummary()
    {
        Assert.Equal(
            "language de, reason detected, confidence 0.9876, suggestion none",
            new LanguageDecision(TranscriptLanguage.German, new DecisionReason.Detected(0.98761f)).DebugSummary());
        Assert.Equal(
            "language en, reason chosen, confidence -, suggestion de",
            new LanguageDecision(TranscriptLanguage.English, new DecisionReason.Chosen(), TranscriptLanguage.German).DebugSummary());
        Assert.Equal(
            "language en, reason fallback-to-preferred, confidence none, suggestion none",
            new LanguageDecision(TranscriptLanguage.English, new DecisionReason.FallbackToPreferred(null)).DebugSummary());
    }

    [Fact]
    public void StoredTranscriptLanguageAssumed()
    {
        var fixedResult = StoredTranscriptLanguage.Assumed(LanguageChoice.Fixed(TranscriptLanguage.Spanish), TranscriptLanguage.English);
        Assert.Equal(TranscriptLanguage.Spanish, fixedResult.Language);
        Assert.Equal("Español (your language choice; this transcript's language was not recorded)", fixedResult.Note);
        var auto = StoredTranscriptLanguage.Assumed(LanguageChoice.Auto, TranscriptLanguage.German);
        Assert.Equal(TranscriptLanguage.German, auto.Language);
        Assert.Equal("Deutsch (your Auto mode default language; this transcript's language was not recorded)", auto.Note);
    }
}

/// <summary>Port of StoredTranscriptLanguageResolveTests in SessionLanguageTests.swift.</summary>
public class StoredTranscriptLanguageResolveTests
{
    private const string GermanSrt =
        "1\n00:00:00,000 --> 00:00:04,000\nWir fangen mit dem Budget an und besprechen dann den Bericht.\n\n"
        + "2\n00:00:04,000 --> 00:00:08,000\nDer Bericht muss bis Freitag fertig sein.\n\n";

    [Fact]
    public void ConfidentDetectionWins()
    {
        var resolved = StoredTranscriptLanguage.Resolve(GermanSrt, LanguageChoice.Fixed(TranscriptLanguage.English), TranscriptLanguage.English);
        Assert.Equal(TranscriptLanguage.German, resolved.Language);
        Assert.Equal("Deutsch (detected from the text)", resolved.Note);
    }

    [Fact]
    public void UnsureFallsBackToAssumed()
    {
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\nOK 好\n";
        var resolved = StoredTranscriptLanguage.Resolve(srt, LanguageChoice.Auto, TranscriptLanguage.Spanish);
        Assert.Equal(TranscriptLanguage.Spanish, resolved.Language);
        Assert.Equal("Español (your Auto mode default language; this transcript's language was not recorded)", resolved.Note);
        var empty = StoredTranscriptLanguage.Resolve("", LanguageChoice.Fixed(TranscriptLanguage.ChineseMainland), TranscriptLanguage.English);
        Assert.Equal(TranscriptLanguage.ChineseMainland, empty.Language);
    }

    [Fact]
    public void DetectedNoteKeyIsTheSharedCatalogKey()
    {
        using var catalog = System.Text.Json.JsonDocument.Parse(SharedFiles.ReadText("localization", "strings-en.json"));
        Assert.Contains(
            StoredTranscriptLanguage.DetectedNoteKey,
            catalog.RootElement.EnumerateArray().Select(entry => entry.GetProperty("key").GetString()));
    }
}

/// <summary>Port of NotesLanguageCaptionTests in SessionLanguageTests.swift.</summary>
public class NotesLanguageCaptionTests
{
    [Fact]
    public void TranscriptLine()
    {
        Assert.Equal("Transcript language: 繁體中文", NotesLanguageCaption.TranscriptLine(TranscriptLanguage.ChineseTaiwan));
        Assert.Equal("Transcript language: 简体中文", NotesLanguageCaption.TranscriptLine(TranscriptLanguage.ChineseMainland));
        Assert.Equal(
            "Transcript language: Deutsch (detected from the text)",
            NotesLanguageCaption.TranscriptLine(TranscriptLanguage.German, "Deutsch (detected from the text)"));
    }

    [Fact]
    public void NotesLineOnlyWhenDifferent()
    {
        Assert.Null(NotesLanguageCaption.NotesLine(TranscriptLanguage.English, TranscriptLanguage.English));
        Assert.Equal(
            "Notes will be written in Deutsch.",
            NotesLanguageCaption.NotesLine(TranscriptLanguage.ChineseTaiwan, TranscriptLanguage.German));
    }
}

/// <summary>
/// Port of mac/HearsayWhisper/Tests/HearsayWhisperTests/LanguageDetectionTests.swift
/// (candidate restriction and window averaging, no model needed), plus the
/// window walk with a fake engine.
/// </summary>
public class LanguageDetectionTests
{
    private static readonly string[] Four = ["en", "zh", "de", "es"];

    private static readonly LanguageProbabilities Sample = new(new Dictionary<string, float>
    {
        ["en"] = 0.5f, ["zh"] = 0.1f, ["de"] = 0.2f, ["es"] = 0.1f, ["fr"] = 0.1f,
    });

    private static void Near(double expected, double actual) => Assert.True(Math.Abs(expected - actual) < 1e-6, $"{actual} != {expected}");

    [Fact]
    public void RestrictedRenormalizesOverCandidates()
    {
        var r = Sample.Restricted(Four);
        Assert.Equal(4, r.Count);
        Near(1, r.Values.Sum());
        Near(0.5 / 0.9, r["en"]);
        Near(0.2 / 0.9, r["de"]);
        Assert.False(r.ContainsKey("fr"));
    }

    [Fact]
    public void RestrictedIgnoresUnknownAndDuplicateCodes()
    {
        var r = Sample.Restricted(["de", "xx", "de", "es"]);
        Assert.Equal(["de", "es"], r.Keys.Order(StringComparer.Ordinal));
        Near(2.0 / 3.0, r["de"]);
    }

    [Fact]
    public void RestrictedIsEmptyWithoutProbabilityMass()
    {
        Assert.Empty(Sample.Restricted([]));
        Assert.Empty(Sample.Restricted(["xx"]));
        var zeros = new LanguageProbabilities(new Dictionary<string, float> { ["en"] = 0, ["de"] = 0 });
        Assert.Empty(zeros.Restricted(["en", "de"]));
    }

    [Fact]
    public void BestPicksHighestRestrictedProbability()
    {
        var best = Sample.Best(["zh", "de", "es"]);
        Assert.NotNull(best);
        Assert.Equal("de", best.Value.Code);
        Near(0.5, best.Value.Confidence);
        Assert.Null(Sample.Best(["xx"]));
    }

    [Fact]
    public void BestBreaksTiesByCandidateOrder()
    {
        Assert.Equal("zh", Sample.Best(["zh", "es"])?.Code);
        Assert.Equal("es", Sample.Best(["es", "zh"])?.Code);
    }

    [Fact]
    public void AveragingKeepsOneOutlierWindowFromVetoing()
    {
        IReadOnlyDictionary<string, float>[] windows =
        [
            new Dictionary<string, float> { ["en"] = 0.05f, ["de"] = 0.95f },
            new Dictionary<string, float> { ["en"] = 0.10f, ["de"] = 0.90f },
            new Dictionary<string, float> { ["en"] = 0.999f, ["de"] = 0.001f }, // e.g. an English phrase in a German meeting
        ];
        var averaged = LanguageDetection.AverageDistributions(windows, ["en", "de"]);
        var best = LanguageDetection.BestCandidate(averaged, ["en", "de"]);
        Assert.NotNull(best);
        Assert.Equal("de", best.Value.Code);
        Near((0.95 + 0.90 + 0.001) / 3, best.Value.Confidence);
        Assert.Empty(LanguageDetection.AverageDistributions([], ["en"]));
    }

    [Fact]
    public void WindowSliceClampsToBuffer()
    {
        int n = LanguageDetection.WindowSamples;
        Assert.Equal(480_000, n);
        var samples = new ReadOnlyMemory<float>(Enumerable.Repeat(1f, n + 10).ToArray());
        Assert.Equal(n, LanguageDetection.WindowSlice(samples, 0).Length);
        Assert.Equal(10, LanguageDetection.WindowSlice(samples, n).Length);
        Assert.True(LanguageDetection.WindowSlice(samples, n + 50).IsEmpty);
        Assert.Equal(n, LanguageDetection.WindowSlice(samples, -5).Length);
    }

    [Fact]
    public void RootMeanSquareOfSilenceIsZero()
    {
        Assert.Equal(0f, LanguageDetection.RootMeanSquare(new float[100]));
        Assert.Equal(0f, LanguageDetection.RootMeanSquare([]));
        Near(0.5, LanguageDetection.RootMeanSquare([0.5f, -0.5f]));
    }

    [Fact]
    public void SkipRuleConstants()
    {
        Assert.Equal(3, LanguageDetection.MaxSpeechWindows);
        Assert.Equal(0.6f, LanguageDetection.NoSpeechThreshold);
        Assert.Equal(0.001f, LanguageDetection.SilenceRms);
        // 0.001 RMS is -60 dBFS.
        Near(-60, 20 * Math.Log10(LanguageDetection.SilenceRms));
    }

    /// <summary>
    /// Five 30 s windows: silent (skipped by RMS), no-speech (skipped by the
    /// no-speech probability), then German, an English outlier, German; the
    /// sixth window is never looked at because three speech windows are enough.
    /// </summary>
    [Fact]
    public void DetectSkipsSilenceAndNoSpeechAndAveragesUpToThreeWindows()
    {
        int n = LanguageDetection.WindowSamples;
        var samples = new float[n * 6];
        // Window 0 stays digital silence; the others are loud enough.
        for (int w = 1; w < 6; w++)
        {
            float level = 0.1f;
            Array.Fill(samples, level, w * n, n);
        }
        var perCall = new List<(float NoSpeech, Dictionary<string, float> Probabilities)>
        {
            (0.61f, new() { ["en"] = 1f }),
            (0.6f, new() { ["de"] = 0.9f, ["en"] = 0.05f, ["fr"] = 0.05f }),
            (0.0f, new() { ["en"] = 0.8f, ["de"] = 0.2f }),
            (0.1f, new() { ["de"] = 0.5f, ["en"] = 0.5f }),
        };
        int calls = 0;
        var result = LanguageDetection.Detect(samples, Four, window =>
        {
            Assert.Equal(n, window.Length);
            var (noSpeech, probabilities) = perCall[calls++];
            return new WindowLanguage(new LanguageProbabilities(probabilities), noSpeech);
        });
        Assert.Equal(4, calls);
        Assert.Equal(3, result.WindowsUsed);
        Assert.Equal("de", result.Code);
        Near(((0.9 / 0.95) + 0.2 + 0.5) / 3, result.Confidence);
        Assert.Equal(new DetectedLanguage("de", result.Confidence), result.Detection);
    }

    [Fact]
    public void DetectFindsNoSpeechInSilence()
    {
        var result = LanguageDetection.Detect(new float[LanguageDetection.WindowSamples * 2], Four,
            _ => throw new Xunit.Sdk.XunitException("silent windows must not reach the model"));
        Assert.Null(result.Code);
        Assert.Equal(0f, result.Confidence);
        Assert.Equal(0, result.WindowsUsed);
        Assert.Equal(new LanguageDecision(TranscriptLanguage.German, new DecisionReason.FallbackToPreferred(null)),
            LanguageDecision.Decide(LanguageChoice.Auto, TranscriptLanguage.German, result.Detection));
    }

    [Fact]
    public void DetectOnAShortRecordingUsesOnePartialWindow()
    {
        var samples = Enumerable.Repeat(0.1f, 16_000 * 5).ToArray();
        var result = LanguageDetection.Detect(samples, Four, window =>
        {
            Assert.Equal(16_000 * 5, window.Length);
            return new WindowLanguage(new LanguageProbabilities(new Dictionary<string, float> { ["es"] = 0.9f, ["en"] = 0.1f }), 0);
        });
        Assert.Equal("es", result.Code);
        Near(0.9, result.Confidence);
        Assert.Equal(1, result.WindowsUsed);
    }
}
