using System.Text;

namespace TextSpike;

/// <summary>Candidate Windows replacement for TranscriptTextLanguage.swift
/// (NLLanguageRecognizer over en, zh-Hans, zh-Hant, de, es). Same contract:
/// sampleLength 4000, minimumLetters 12, confidenceThreshold 0.6, ties break
/// in picker order (en, zh-TW, zh-CN, de, es).
///
/// Tokens: every Han ideograph is one token; every maximal run of other
/// letters (with inner apostrophes) is one word token. N = han + words.
///   P(zh)        = han / N
///   P(zh-TW)     = P(zh) * (t + 0.5) / (t + s + 1)   t, s = Traditional-only / Simplified-only ideographs
///   P(zh-CN)     = P(zh) * (s + 0.5) / (t + s + 1)
///   P(latin)     = words / N
///   P(en|de|es)  = P(latin) * (score + 1) / (sum of scores + 3)
/// score = function words (a word in k lists adds 1/k to each) plus one per
/// lowercase-initial word carrying that language's diacritics (de: ä ö ü ß;
/// es: ñ á é í ó ú) plus one per ¿ or ¡ (es). The five values sum to 1.
/// Capitalized words' diacritics are ignored so names (Müller, José) do not vote.
/// "Traditional-only" means ICU Hant-Hans changes the character; "Simplified-only"
/// means ICU Hans-Hant changes it (the same transform the app already uses).</summary>
internal sealed class RuleDetector
{
    public const int SampleLength = 4000;
    public const int MinimumLetters = 12;
    public const double ConfidenceThreshold = 0.6;
    public static readonly string[] Languages = ["en", "zh-TW", "zh-CN", "de", "es"];

    private static readonly string[] English =
    [
        "the", "and", "to", "of", "a", "in", "is", "it", "that", "we", "you", "this", "for", "on", "with", "be",
        "are", "have", "was", "will", "not", "i", "our", "can", "do", "what", "so", "at", "by", "from", "they",
        "there", "just", "need", "about", "it's", "don't", "or", "if", "but", "all", "as", "an", "has", "would",
        "should", "let's", "we'll", "i'm", "your", "their", "them", "which", "who", "how", "when", "then",
        "than", "been", "were", "does", "did", "no", "yes", "okay", "because", "into", "some", "any", "more",
        "also", "very", "like", "get", "going", "think", "know", "out", "up", "here", "now", "before", "after",
        "next", "week", "could", "thanks", "thank", "good", "see", "everyone", "please", "sorry", "sure", "make", "these", "those", "its", "my", "me", "us", "he", "she", "his", "her",
    ];

    private static readonly string[] German =
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

    private static readonly string[] Spanish =
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

    private static readonly Dictionary<string, (double En, double De, double Es)> Words = BuildWords();

    private static readonly System.Buffers.SearchValues<char> GermanMarks = System.Buffers.SearchValues.Create("äöüß");
    private static readonly System.Buffers.SearchValues<char> SpanishMarks = System.Buffers.SearchValues.Create("ñáéíóú");

    private readonly Func<Rune, int> _scriptOf; // +1 Traditional-only, -1 Simplified-only, 0 neither

    public RuleDetector(Func<Rune, int> scriptOf) => _scriptOf = scriptOf;

    /// <summary>A classifier built on ICU Hans-Hant, cached per character.</summary>
    public static Func<Rune, int> IcuScript(Icu.Transliterator hansHant, Icu.Transliterator hantHans)
    {
        var cache = new Dictionary<Rune, int>();
        return r =>
        {
            if (cache.TryGetValue(r, out int v)) return v;
            string s = r.ToString();
            v = hantHans.Transliterate(s) != s ? 1 : hansHant.Transliterate(s) != s ? -1 : 0;
            cache[r] = v;
            return v;
        };
    }

    private static Dictionary<string, (double, double, double)> BuildWords()
    {
        var sets = new Dictionary<string, (bool En, bool De, bool Es)>(StringComparer.Ordinal);
        foreach (string w in English) sets[w] = sets.GetValueOrDefault(w) with { En = true };
        foreach (string w in German) sets[w] = sets.GetValueOrDefault(w) with { De = true };
        foreach (string w in Spanish) sets[w] = sets.GetValueOrDefault(w) with { Es = true };
        var result = new Dictionary<string, (double, double, double)>(StringComparer.Ordinal);
        foreach (var (w, (en, de, es)) in sets)
        {
            int k = (en ? 1 : 0) + (de ? 1 : 0) + (es ? 1 : 0);
            result[w] = (en ? 1.0 / k : 0, de ? 1.0 / k : 0, es ? 1.0 / k : 0);
        }
        return result;
    }

    public static bool IsHan(Rune r)
    {
        int v = r.Value;
        return (v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF) || (v >= 0xF900 && v <= 0xFAFF)
            || (v >= 0x20000 && v <= 0x2A6DF) || (v >= 0x2A700 && v <= 0x2EBEF) || (v >= 0x2F800 && v <= 0x2FA1F)
            || (v >= 0x30000 && v <= 0x323AF) || v == 0x3007;
    }

    public sealed record Result(string Language, double Confidence, IReadOnlyDictionary<string, double> All, string Evidence);

    /// <summary>The most likely language and its probability, or null below
    /// MinimumLetters letters.</summary>
    public Result? Detect(string text)
    {
        string sample = text.Length > SampleLength ? text[..SampleLength] : text;
        int letters = 0, han = 0, trad = 0, simp = 0, words = 0;
        double en = 0, de = 0, es = 0;
        var word = new StringBuilder();

        void EndWord()
        {
            if (word.Length == 0) return;
            string raw = word.ToString().Trim('\'', '’');
            word.Clear();
            if (raw.Length == 0) return;
            words++;
            string w = raw.ToLowerInvariant().Replace('’', '\'');
            if (Words.TryGetValue(w, out var s))
            {
                en += s.En;
                de += s.De;
                es += s.Es;
            }
            if (char.IsLower(raw[0]))
            {
                if (w.AsSpan().IndexOfAny(GermanMarks) >= 0) de += 1;
                if (w.AsSpan().IndexOfAny(SpanishMarks) >= 0) es += 1;
            }
        }

        foreach (Rune r in sample.EnumerateRunes())
        {
            if (Rune.IsLetter(r)) letters++;
            if (IsHan(r))
            {
                EndWord();
                han++;
                int sc = _scriptOf(r);
                if (sc > 0) trad++;
                else if (sc < 0) simp++;
            }
            else if (Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                word.Append(r.ToString());
            }
            else if ((r.Value == '\'' || r.Value == 0x2019) && word.Length > 0)
            {
                word.Append(r.ToString());
            }
            else
            {
                EndWord();
                if (r.Value == 0xBF || r.Value == 0xA1) es += 1; // ¿ ¡
            }
        }
        EndWord();
        if (letters < MinimumLetters) return null;

        double n = han + words;
        double pZh = han / n, pLatin = words / n;
        double sum = en + de + es;
        var all = new Dictionary<string, double>
        {
            ["en"] = pLatin * (en + 1) / (sum + 3),
            ["zh-TW"] = pZh * (trad + 0.5) / (trad + simp + 1),
            ["zh-CN"] = pZh * (simp + 0.5) / (trad + simp + 1),
            ["de"] = pLatin * (de + 1) / (sum + 3),
            ["es"] = pLatin * (es + 1) / (sum + 3),
        };
        string best = Languages[0];
        foreach (string l in Languages)
        {
            if (all[l] > all[best]) best = l;
        }
        string evidence = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"han {han} (trad {trad}, simp {simp}), words {words}, scores en {en:F2} de {de:F2} es {es:F2}");
        return new Result(best, all[best], all, evidence);
    }
}
