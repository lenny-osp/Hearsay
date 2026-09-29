using System.Text;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using Whisper.net.LibraryLoader;

namespace Hearsay.Tests.Whisper;

/// <summary>
/// The Whisper integration tests' model: <c>TEST_RUNNER_HEARSAY_MODEL_DIR</c>
/// is the model path, a whisper.cpp <c>ggml-*.bin</c> file, or a folder that
/// holds one (ggml-large-v3-turbo-q5_0.bin preferred when several). The
/// tests skip at discovery without it, as the Mac's
/// <c>.enabled(if: modelDirectory != nil)</c> does.
/// </summary>
internal static class WhisperTestModel
{
    public const string Variable = "TEST_RUNNER_HEARSAY_MODEL_DIR";

    public const string PreferredFile = "ggml-large-v3-turbo-q5_0.bin";

    /// <summary>The model file, or null when the variable is unset or names nothing usable.</summary>
    public static string? Path { get; } = Resolve(Environment.GetEnvironmentVariable(Variable));

    public static string SkipReason =>
        $"Set {Variable} to a whisper.cpp model file (or a folder holding one) to run the Whisper integration tests.";

    internal static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (File.Exists(value)) return System.IO.Path.GetFullPath(value);
        if (!Directory.Exists(value)) return null;
        var preferred = System.IO.Path.Combine(value, PreferredFile);
        if (File.Exists(preferred)) return System.IO.Path.GetFullPath(preferred);
        var models = Directory.GetFiles(value, "ggml-*.bin");
        return models.Length == 1 ? System.IO.Path.GetFullPath(models[0]) : null;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WhisperModelFactAttribute : FactAttribute
{
    public WhisperModelFactAttribute()
    {
        if (WhisperTestModel.Path is null) Skip = WhisperTestModel.SkipReason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WhisperModelTheoryAttribute : TheoryAttribute
{
    public WhisperModelTheoryAttribute()
    {
        if (WhisperTestModel.Path is null) Skip = WhisperTestModel.SkipReason;
    }
}

/// <summary>One engine, loaded on first use, for every integration test (one model load per run).</summary>
public sealed class WhisperEngineFixture : IDisposable
{
    private readonly Lazy<WhisperEngine> engine;
    private readonly List<string> log = [];

    public WhisperEngineFixture()
    {
        engine = new Lazy<WhisperEngine>(() =>
        {
            var path = WhisperTestModel.Path ?? throw new InvalidOperationException(WhisperTestModel.SkipReason);
            // HEARSAY_TEST_WHISPER_RUNTIME=cpu|vulkan pins the runtime for a
            // measurement run; unset keeps Whisper.net's order (Vulkan, then CPU).
            switch (Environment.GetEnvironmentVariable("HEARSAY_TEST_WHISPER_RUNTIME")?.ToLowerInvariant())
            {
                case "cpu":
                    WhisperRuntime.PreferredOrder = [RuntimeLibrary.Cpu];
                    break;
                case "vulkan":
                    WhisperRuntime.PreferredOrder = [RuntimeLibrary.Vulkan];
                    break;
                default:
                    break;
            }
            var created = new WhisperEngine(line =>
            {
                lock (log) log.Add(line);
            });
            created.Load(path);
            return created;
        });
    }

    public WhisperEngine Engine => engine.Value;

    /// <summary>The engine's log lines so far (load, runtime, probe).</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (log) return [.. log];
        }
    }

    private readonly Dictionary<string, IReadOnlyList<TranscriptSegment>> cues = [];

    /// <summary>Keeps a fixture's cues so the silence test compares against the same run.</summary>
    public void Remember(string code, IReadOnlyList<TranscriptSegment> value)
    {
        lock (cues) cues[code] = value;
    }

    public IReadOnlyList<TranscriptSegment>? Remembered(string code)
    {
        lock (cues) return cues.GetValueOrDefault(code);
    }

    public void Dispose()
    {
        if (engine.IsValueCreated) engine.Value.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class WhisperModelGroup : ICollectionFixture<WhisperEngineFixture>
{
    public const string Name = "Whisper model";
}

/// <summary>
/// PLAN.md 18.5's metrics, as the W1 spike measured them
/// (windows/Spike/WhisperSpike/Metrics.cs): similarity is 1 - Levenshtein /
/// max length over Unicode scalars after normalizing; timestamps match each
/// expected cue with the output cue that overlaps it most.
/// </summary>
internal static class TranscriptMetrics
{
    /// <summary>
    /// en/de/es: lowercase, strip punctuation and symbols, collapse whitespace.
    /// zh: strip whitespace, punctuation and symbols only.
    /// </summary>
    public static string Normalize(string text, bool chinese)
    {
        var output = new StringBuilder();
        if (chinese)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)) continue;
                output.Append(rune.ToString());
            }
            return output.ToString();
        }
        bool space = false;
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)) continue;
            if (Rune.IsWhiteSpace(rune))
            {
                space = output.Length > 0;
                continue;
            }
            if (space)
            {
                output.Append(' ');
                space = false;
            }
            output.Append(rune.ToString());
        }
        return output.ToString();
    }

    public static string Joined(IEnumerable<TranscriptSegment> cues) =>
        string.Join(" ", cues.Select(cue => cue.Text));

    /// <summary>1 - Levenshtein(a, b) / max(|a|, |b|) over Unicode scalar values.</summary>
    public static double Similarity(string a, string b)
    {
        int[] x = [.. a.EnumerateRunes().Select(r => r.Value)];
        int[] y = [.. b.EnumerateRunes().Select(r => r.Value)];
        int n = Math.Max(x.Length, y.Length);
        if (n == 0) return 1.0;
        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) previous[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= y.Length; j++)
            {
                int cost = x[i - 1] == y[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[y.Length] / n;
    }

    public sealed record CueMatch(TranscriptSegment Expected, TranscriptSegment? Output, double DeltaStart, double DeltaEnd);

    /// <summary>For each expected cue, the output cue that overlaps it most (null when none overlaps).</summary>
    public static IReadOnlyList<CueMatch> MatchCues(IReadOnlyList<TranscriptSegment> expected, IReadOnlyList<TranscriptSegment> output)
    {
        var result = new List<CueMatch>();
        foreach (var e in expected)
        {
            TranscriptSegment? best = null;
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
            result.Add(best is { } match
                ? new CueMatch(e, match, Math.Abs(match.Start - e.Start), Math.Abs(match.End - e.End))
                : new CueMatch(e, null, double.PositiveInfinity, double.PositiveInfinity));
        }
        return result;
    }
}
