using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Hearsay.Core.Transcription;

/// <summary>
/// Guesses the language of a transcript's text, for SRTs whose transcript
/// language was not stored (History "Generate notes..." on an older file).
/// Port of the public surface of
/// mac/HearsayCore/Sources/HearsayCore/Transcription/TranscriptTextLanguage.swift.
/// </summary>
/// <remarks>
/// <para>The Mac uses Apple's <c>NLLanguageRecognizer</c>; Windows has no
/// equivalent with a confidence, so this is the rule detector chosen in
/// PLAN.md 18.3 ("Transcript text language") and measured in
/// windows/Spike/TextSpike/REPORT.md, section B.2. The constants are the
/// Mac's: <see cref="SampleLength"/>, <see cref="MinimumLetters"/>,
/// <see cref="ConfidenceThreshold"/>, and ties break in picker order.</para>
/// <para>Tokens: every Han ideograph is one token, every maximal run of other
/// letters (with inner apostrophes) one word token; N = han + words.</para>
/// <list type="bullet">
/// <item>P(zh) = han / N, split into ZH-TW and ZH-CN by the Traditional-only
/// characters t (ICU Hant-Hans changes them) and Simplified-only characters s
/// (ICU Hans-Hant changes them): P(zh-TW) = P(zh)·(t + ½)/(t + s + 1),
/// P(zh-CN) = P(zh)·(s + ½)/(t + s + 1).</item>
/// <item>P(latin) = words / N, split by a score per language:
/// P(l) = P(latin)·(score_l + 1)/(Σ scores + 3). A function word adds 1/k
/// to each of the k languages whose list has it; a lowercase-initial word
/// with ä ö ü ß adds 1 to German, one with ñ á é í ó ú adds 1 to Spanish
/// (capitalized names do not vote), and each ¿ or ¡ adds 1 to Spanish.</item>
/// </list>
/// <para>The five probabilities sum to 1; the largest is the result.</para>
/// </remarks>
public static class TranscriptTextLanguage
{
    /// <summary>Characters (grapheme clusters, like Swift's <c>prefix</c>) of text the detector looks at; more adds nothing.</summary>
    public const int SampleLength = 4_000;

    /// <summary>The minimum probability for the detected language to be the default.</summary>
    public const double ConfidenceThreshold = 0.6;

    /// <summary>Fewer letters than this and <see cref="Detect"/> returns null.</summary>
    public const int MinimumLetters = 12;

    private static readonly string[] EnglishWords =
    [
        "the", "and", "to", "of", "a", "in", "is", "it", "that", "we", "you", "this", "for", "on", "with", "be",
        "are", "have", "was", "will", "not", "i", "our", "can", "do", "what", "so", "at", "by", "from", "they",
        "there", "just", "need", "about", "it's", "don't", "or", "if", "but", "all", "as", "an", "has", "would",
        "should", "let's", "we'll", "i'm", "your", "their", "them", "which", "who", "how", "when", "then",
        "than", "been", "were", "does", "did", "no", "yes", "okay", "because", "into", "some", "any", "more",
        "also", "very", "like", "get", "going", "think", "know", "out", "up", "here", "now", "before", "after",
        "next", "week", "could", "thanks", "thank", "good", "see", "everyone", "please", "sorry", "sure", "make",
        "these", "those", "its", "my", "me", "us", "he", "she", "his", "her",
    ];

    private static readonly string[] GermanWords =
    [
        "der", "die", "das", "und", "ist", "nicht", "ein", "eine", "einen", "einem", "einer", "zu", "den", "dem",
        "des", "mit", "von", "auf", "für", "sich", "ich", "wir", "sie", "es", "er", "ihr", "uns", "euch",
        "auch", "noch", "bis", "dann", "aber", "oder", "wenn", "kann", "können", "haben", "hat", "habe", "sind",
        "sein", "werden", "wird", "muss", "müssen", "nach", "bei", "aus", "wie", "sehr", "schon", "hier", "jetzt",
        "mal", "also", "doch", "im", "am", "zum", "zur", "vom", "beim", "dass", "weil", "über", "unter", "vor",
        "nur", "so", "was", "an", "in", "man", "kein", "keine", "mehr", "gibt", "gut", "ja", "nein", "diese",
        "dieser", "dieses", "unsere", "unser", "wo", "wer", "heute", "morgen", "gleich", "außerdem", "zusammen",
        "lasst", "lass", "bitte", "danke", "machen", "soll", "sollten", "möchte", "würde", "war", "waren",
    ];

    private static readonly string[] SpanishWords =
    [
        "el", "la", "los", "las", "y", "que", "de", "del", "en", "un", "una", "unos", "unas", "es", "por",
        "para", "con", "no", "se", "lo", "le", "les", "su", "sus", "al", "pero", "más", "como", "muy", "ya",
        "está", "están", "este", "esta", "estos", "estas", "todo", "todos", "toda", "hay", "tenemos", "vamos",
        "puede", "puedes", "nosotros", "porque", "cuando", "también", "sobre", "antes", "después", "entre",
        "hasta", "sin", "desde", "eso", "esto", "bien", "qué", "sí", "a", "o", "e", "ni", "nos", "os", "me",
        "te", "mi", "tu", "yo", "ser", "son", "fue", "era", "han", "ha", "he", "hemos", "tiene", "tienen",
        "hacer", "otro", "otra", "donde", "dónde", "cómo", "pues", "entonces", "ahora", "aquí", "mañana",
        "buenos", "días", "gracias", "vez", "cada", "mismo", "nuestro", "nuestra", "usted", "ustedes",
    ];

    /// <summary>Each function word's vote for English, German and Spanish (1/k when k lists have it).</summary>
    private static readonly Dictionary<string, LatinScores> FunctionWords = BuildFunctionWords();

    private static readonly SearchValues<char> GermanMarks = SearchValues.Create("äöüß");
    private static readonly SearchValues<char> SpanishMarks = SearchValues.Create("ñáéíóú");

    /// <summary>Per ideograph: +1 Traditional-only, -1 Simplified-only, 0 either script.</summary>
    private static readonly ConcurrentDictionary<int, int> ScriptCache = new();

    /// <summary>
    /// The most likely supported language and its probability, or null when
    /// the text has fewer than <see cref="MinimumLetters"/> letters in its
    /// first <see cref="SampleLength"/> characters.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The text has Han characters and icu.dll is missing (<see cref="ChineseScriptConverter"/>).</exception>
    public static (TranscriptLanguage Language, double Probability)? Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var counts = new Counts();
        foreach (Rune rune in Sample(text).EnumerateRunes())
        {
            counts.Add(rune);
        }
        counts.EndWord();
        if (counts.Letters < MinimumLetters)
        {
            return null;
        }
        return counts.Best();
    }

    /// <summary>The detected language when it is at least <see cref="ConfidenceThreshold"/> likely, else null.</summary>
    public static TranscriptLanguage? Confident(string text) =>
        Detect(text) is { } result && result.Probability >= ConfidenceThreshold ? result.Language : null;

    /// <summary>Detection over an SRT's subtitle text (<see cref="Srt.CleanText"/>). Swift's <c>detect(srtText:)</c>.</summary>
    public static (TranscriptLanguage Language, double Probability)? DetectSrt(string srtText) =>
        Detect(Srt.CleanText(srtText));

    /// <summary>The first <see cref="SampleLength"/> grapheme clusters, in NFC so decomposed diacritics match.</summary>
    private static string Sample(string text)
    {
        string sample = text;
        if (text.Length > SampleLength)
        {
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            int count = 0;
            int end = text.Length;
            while (enumerator.MoveNext())
            {
                if (count == SampleLength)
                {
                    end = enumerator.ElementIndex;
                    break;
                }
                count++;
            }
            sample = text[..end];
        }
        try
        {
            return sample.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate cannot be normalized; its letters still count.
            return sample;
        }
    }

    private static bool IsHan(Rune rune)
    {
        int v = rune.Value;
        return (v >= 0x4E00 && v <= 0x9FFF) // CJK Unified Ideographs
            || (v >= 0x3400 && v <= 0x4DBF) // Extension A
            || (v >= 0xF900 && v <= 0xFAFF) // Compatibility Ideographs
            || (v >= 0x20000 && v <= 0x2A6DF) // Extension B
            || (v >= 0x2A700 && v <= 0x2EBEF) // Extensions C to F
            || (v >= 0x2F800 && v <= 0x2FA1F) // Compatibility Supplement
            || (v >= 0x30000 && v <= 0x323AF) // Extensions G and H
            || v == 0x3007; // 〇
    }

    /// <summary>+1 when ICU Hant-Hans changes the ideograph, -1 when Hans-Hant does, else 0.</summary>
    private static int ScriptOf(Rune rune) => ScriptCache.GetOrAdd(rune.Value, static value =>
    {
        string character = char.ConvertFromUtf32(value);
        if (ChineseScriptConverter.Convert(character, ChineseScript.Simplified) != character)
        {
            return 1;
        }
        return ChineseScriptConverter.Convert(character, ChineseScript.Traditional) != character ? -1 : 0;
    });

    private static Dictionary<string, LatinScores> BuildFunctionWords()
    {
        var result = new Dictionary<string, LatinScores>(StringComparer.Ordinal);
        foreach (string word in EnglishWords.Concat(GermanWords).Concat(SpanishWords).Distinct(StringComparer.Ordinal))
        {
            bool en = EnglishWords.Contains(word, StringComparer.Ordinal);
            bool de = GermanWords.Contains(word, StringComparer.Ordinal);
            bool es = SpanishWords.Contains(word, StringComparer.Ordinal);
            double share = 1.0 / ((en ? 1 : 0) + (de ? 1 : 0) + (es ? 1 : 0));
            result[word] = new LatinScores(en ? share : 0, de ? share : 0, es ? share : 0);
        }
        return result;
    }

    private readonly record struct LatinScores(double English, double German, double Spanish);

    /// <summary>Running counts over the sample, one rune at a time.</summary>
    private sealed class Counts
    {
        private readonly StringBuilder word = new();
        private int han;
        private int traditional;
        private int simplified;
        private int words;
        private double english;
        private double german;
        private double spanish;

        public int Letters { get; private set; }

        public void Add(Rune rune)
        {
            bool letter = Rune.IsLetter(rune);
            if (letter || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber)
            {
                Letters++;
            }
            if (IsHan(rune))
            {
                EndWord();
                han++;
                int script = ScriptOf(rune);
                if (script > 0)
                {
                    traditional++;
                }
                else if (script < 0)
                {
                    simplified++;
                }
            }
            else if (letter || Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                word.Append(rune.ToString());
            }
            else if ((rune.Value == '\'' || rune.Value == '’') && word.Length > 0)
            {
                word.Append(rune.ToString());
            }
            else
            {
                EndWord();
                if (rune.Value == '¿' || rune.Value == '¡') // ¿ ¡
                {
                    spanish += 1;
                }
            }
        }

        public void EndWord()
        {
            if (word.Length == 0)
            {
                return;
            }
            string raw = word.ToString().Trim('\'', '’');
            word.Clear();
            if (raw.Length == 0)
            {
                return;
            }
            words++;
#pragma warning disable CA1308 // The function-word lists are lowercase; this is a lookup key, not a display string.
            string key = raw.ToLowerInvariant().Replace('’', '\'');
#pragma warning restore CA1308
            if (FunctionWords.TryGetValue(key, out LatinScores scores))
            {
                english += scores.English;
                german += scores.German;
                spanish += scores.Spanish;
            }
            if (char.IsLower(raw[0]))
            {
                if (key.AsSpan().IndexOfAny(GermanMarks) >= 0)
                {
                    german += 1;
                }
                if (key.AsSpan().IndexOfAny(SpanishMarks) >= 0)
                {
                    spanish += 1;
                }
            }
        }

        /// <summary>
        /// The largest of the five probabilities; ties go to the earlier
        /// language in picker order. Null when there is no token (letters that
        /// are only letter-numbers such as Roman numerals).
        /// </summary>
        public (TranscriptLanguage Language, double Probability)? Best()
        {
            double tokens = han + words;
            if (tokens == 0)
            {
                return null;
            }
            double chinese = han / tokens;
            double latin = words / tokens;
            double scoreSum = english + german + spanish + 3;
            double scriptSum = traditional + simplified + 1;
            (TranscriptLanguage Language, double Probability)? best = null;
            foreach (TranscriptLanguage language in TranscriptLanguages.All)
            {
                double probability = language switch
                {
                    TranscriptLanguage.English => latin * (english + 1) / scoreSum,
                    TranscriptLanguage.ChineseTaiwan => chinese * (traditional + 0.5) / scriptSum,
                    TranscriptLanguage.ChineseMainland => chinese * (simplified + 0.5) / scriptSum,
                    TranscriptLanguage.German => latin * (german + 1) / scoreSum,
                    TranscriptLanguage.Spanish => latin * (spanish + 1) / scoreSum,
                    _ => 0,
                };
                if (best is not { } current || probability > current.Probability)
                {
                    best = (language, probability);
                }
            }
            return best;
        }
    }
}
