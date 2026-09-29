using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperSpike;

internal sealed record Cue(int Index, double Start, double End, string Text);

/// <summary>SRT parsing and writing, text normalization, similarity and timestamp matching.</summary>
internal static partial class Metrics
{
    [GeneratedRegex(@"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)")]
    private static partial Regex TimeLine();

    public static List<Cue> ParseSrt(string text)
    {
        var cues = new List<Cue>();
        var blocks = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int t = Array.FindIndex(lines, l => TimeLine().IsMatch(l));
            if (t < 0)
            {
                continue;
            }
            var m = TimeLine().Match(lines[t]);
            double S(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            double start = S(1) * 3600 + S(2) * 60 + S(3) + S(4) / 1000.0;
            double end = S(5) * 3600 + S(6) * 60 + S(7) + S(8) / 1000.0;
            cues.Add(new Cue(cues.Count + 1, start, end, string.Join("\n", lines.Skip(t + 1))));
        }
        return cues;
    }

    public static string Stamp(double seconds)
    {
        long ms = (long)Math.Round(seconds * 1000.0);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    public static string ToSrt(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder();
        foreach (var c in cues)
        {
            sb.Append(c.Index.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append(Stamp(c.Start)).Append(" --> ").Append(Stamp(c.End)).Append('\n')
              .Append(c.Text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// en/de/es: lowercase, strip punctuation and symbols, collapse whitespace.
    /// zh: strip whitespace and punctuation only.
    /// </summary>
    public static string Normalize(string text, bool chinese)
    {
        var sb = new StringBuilder();
        if (chinese)
        {
            foreach (var r in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(r) || Rune.IsPunctuation(r) || Rune.IsSymbol(r))
                {
                    continue;
                }
                sb.Append(r.ToString());
            }
            return sb.ToString();
        }
        bool space = false;
        foreach (var r in text.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsPunctuation(r) || Rune.IsSymbol(r))
            {
                continue;
            }
            if (Rune.IsWhiteSpace(r))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>1 - Levenshtein(a, b) / max(|a|, |b|), over Unicode scalar values.</summary>
    public static double Similarity(string a, string b)
    {
        int[] x = a.EnumerateRunes().Select(r => r.Value).ToArray();
        int[] y = b.EnumerateRunes().Select(r => r.Value).ToArray();
        int n = Math.Max(x.Length, y.Length);
        if (n == 0)
        {
            return 1.0;
        }
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++)
        {
            prev[j] = j;
        }
        for (int i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= y.Length; j++)
            {
                int cost = x[i - 1] == y[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[y.Length] / n;
    }

    public sealed record CueMatch(Cue Expected, Cue? Output, double DeltaStart, double DeltaEnd);

    /// <summary>For each expected cue, the output cue that overlaps it most.</summary>
    public static List<CueMatch> MatchCues(IReadOnlyList<Cue> expected, IReadOnlyList<Cue> output)
    {
        var result = new List<CueMatch>();
        foreach (var e in expected)
        {
            Cue? best = null;
            double bestOverlap = 0;
            foreach (var o in output)
            {
                double overlap = Math.Min(e.End, o.End) - Math.Max(e.Start, o.Start);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = o;
                }
            }
            result.Add(best is null
                ? new CueMatch(e, null, double.PositiveInfinity, double.PositiveInfinity)
                : new CueMatch(e, best, Math.Abs(best.Start - e.Start), Math.Abs(best.End - e.End)));
        }
        return result;
    }

    /// <summary>Word 3-grams (character 4-grams for zh) that occur three times or more.</summary>
    public static List<string> Repetitions(string normalized, bool chinese)
    {
        var units = chinese
            ? normalized.EnumerateRunes().Select(r => r.ToString()).ToArray()
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = chinese ? 4 : 3;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i + n <= units.Length; i++)
        {
            string key = string.Join(chinese ? "" : " ", units.Skip(i).Take(n));
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts.Where(kv => kv.Value >= 3).Select(kv => $"\"{kv.Key}\" x{kv.Value}").ToList();
    }
}
