// W1 spike, Whisper half (PLAN.md 18.6 row W1, 18.8 "Hallucination filter" and
// "Language detection"). Throwaway; not part of the solution.
//
// Usage:
//   WhisperSpike --api <out.txt>
//   WhisperSpike --model <ggml-*.bin> --runtime cpu|vulkan --fixtures <dir> --out <dir>
//                [--threads N] [--sweep 2,4,8] [--langs en,de,es,zh]
//
// One process = one model x one runtime, because Whisper.net picks the native
// library once per process (RuntimeOptions.RuntimeLibraryOrder).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;
using WhisperSpike;

var opts = ParseArgs(args);
if (opts.TryGetValue("api", out var apiPath))
{
    using var w = new StreamWriter(apiPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
    ApiDump.Write(w);
    return 0;
}

if (opts.ContainsKey("native-detect"))
{
    return await NativeDetectMode.RunAsync(opts);
}

string modelPath = Required(opts, "model");
string runtime = Required(opts, "runtime").ToLowerInvariant();
string fixtures = Required(opts, "fixtures");
string outDir = Required(opts, "out");
int? threads = opts.TryGetValue("threads", out var th) ? int.Parse(th, CultureInfo.InvariantCulture) : null;
int[] sweep = opts.TryGetValue("sweep", out var sw)
    ? sw.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray()
    : [];
string[] langs = opts.TryGetValue("langs", out var ls) ? ls.Split(',') : ["en", "de", "es", "zh"];
string modelName = Path.GetFileNameWithoutExtension(modelPath);
// --pad N: append N seconds of digital silence to every fixture (hallucination check).
double pad = opts.TryGetValue("pad", out var pd) ? double.Parse(pd, CultureInfo.InvariantCulture) : 0;
string runName = pad > 0 ? $"{modelName}-{runtime}-pad{pad:0}" : $"{modelName}-{runtime}";
Directory.CreateDirectory(outDir);

var nativeLog = new List<string>();
using var logSub = LogProvider.AddLogger((level, msg) =>
{
    lock (nativeLog)
    {
        nativeLog.Add($"[{level}] {msg?.TrimEnd()}");
    }
});

RuntimeOptions.RuntimeLibraryOrder = runtime switch
{
    "cpu" => [RuntimeLibrary.Cpu],
    "vulkan" => [RuntimeLibrary.Vulkan],
    _ => throw new ArgumentException($"unknown runtime {runtime}"),
};

var report = new StringBuilder();
var json = new Dictionary<string, object?>
{
    ["model"] = modelName,
    ["runtime"] = runtime,
    ["modelBytes"] = new FileInfo(modelPath).Length,
    ["logicalProcessors"] = Environment.ProcessorCount,
};

WhisperFactory factory;
var loadWatch = Stopwatch.StartNew();
try
{
    factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = runtime != "cpu" });
    // Force the model into memory now so load time is not charged to the first transcription.
    using var warm = factory.CreateBuilder().WithLanguage("en").Build();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LOAD FAILED ({runName}): {ex.GetType().FullName}: {ex.Message}");
    File.WriteAllText(Path.Combine(outDir, $"{runName}.error.txt"),
        $"{ex}\n\nNative log:\n{string.Join('\n', nativeLog)}\n", new UTF8Encoding(false));
    return 2;
}
loadWatch.Stop();
json["loadSeconds"] = loadWatch.Elapsed.TotalSeconds;
json["loadedLibrary"] = RuntimeOptions.LoadedLibrary?.ToString();
json["runtimeInfo"] = WhisperFactory.GetRuntimeInfo();

Console.WriteLine($"== {runName}: loaded {RuntimeOptions.LoadedLibrary} in {loadWatch.Elapsed.TotalSeconds:F2} s");
Console.WriteLine($"   runtime info: {WhisperFactory.GetRuntimeInfo()}");

// whisper.cpp default: n_threads = min(4, hardware_concurrency).
int effectiveThreads = threads ?? Math.Min(4, Environment.ProcessorCount);
json["threads"] = effectiveThreads;
json["threadsExplicit"] = threads.HasValue;

// 30 s live-preview window: zh-30s looped to exactly 30.0 s (dense speech), language zh.
float[] zhSamples = Wav.ReadMono16k(Path.Combine(fixtures, "zh-30s.wav"));
float[] window = new float[16000 * 30];
for (int i = 0; i < window.Length; i++)
{
    window[i] = zhSamples[i % zhSamples.Length];
}

// Cold first inference (graph allocation, Vulkan pipeline compilation); later timings are warm.
var coldWatch = Stopwatch.StartNew();
await using (var cold = factory.CreateBuilder().WithLanguage("zh").WithThreads(effectiveThreads).Build())
{
    await foreach (var unused in cold.ProcessAsync(window))
    {
        _ = unused;
    }
}
coldWatch.Stop();
json["coldFirstWindowSeconds"] = coldWatch.Elapsed.TotalSeconds;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   cold first 30 s window: {coldWatch.Elapsed.TotalSeconds:F2} s"));

var fixtureResults = new List<Dictionary<string, object?>>();
foreach (var lang in langs)
{
    string wav = Path.Combine(fixtures, $"{lang}-30s.wav");
    float[] samples = Wav.ReadMono16k(wav);
    if (pad > 0)
    {
        Array.Resize(ref samples, samples.Length + (int)(pad * 16000));
    }
    double audioSeconds = samples.Length / 16000.0;
    bool zh = lang == "zh";

    // 1. Fixed-language transcription, no prompt, otherwise Whisper.net defaults.
    var builder = factory.CreateBuilder().WithLanguage(lang);
    if (threads.HasValue)
    {
        builder = builder.WithThreads(threads.Value);
    }
    var segments = new List<SegmentData>();
    var watch = Stopwatch.StartNew();
    await using (var processor = builder.Build())
    {
        await foreach (var s in processor.ProcessAsync(samples))
        {
            segments.Add(s);
        }
    }
    watch.Stop();

    var cues = segments.Select((s, i) => new Cue(i + 1, s.Start.TotalSeconds, s.End.TotalSeconds, s.Text)).ToList();
    string srt = Metrics.ToSrt(cues);
    File.WriteAllText(Path.Combine(outDir, $"{runName}-{lang}.srt"), srt, new UTF8Encoding(false));

    // 2. Similarity.
    string outText = string.Concat(cues.Select(c => c.Text + " "));
    string expectedPath = Path.Combine(fixtures, $"{lang}-30s.expected.srt");
    var expected = Metrics.ParseSrt(File.ReadAllText(expectedPath, Encoding.UTF8));
    string expText = string.Concat(expected.Select(c => c.Text + " "));
    double sim = Metrics.Similarity(Metrics.Normalize(outText, zh), Metrics.Normalize(expText, zh));
    double? simTruth = null;
    if (zh)
    {
        var truth = Metrics.ParseSrt(File.ReadAllText(Path.Combine(fixtures, "zh-30s.truth.srt"), Encoding.UTF8));
        simTruth = Metrics.Similarity(Metrics.Normalize(outText, true), Metrics.Normalize(string.Concat(truth.Select(c => c.Text)), true));
    }

    // 3. Timestamps.
    var matches = Metrics.MatchCues(expected, cues);
    double worstStart = matches.Max(m => m.DeltaStart);
    double worstEnd = matches.Max(m => m.DeltaEnd);
    bool tsPass = matches.All(m => m.DeltaStart <= 0.5 && m.DeltaEnd <= 0.5);
    // Secondary view: distance from each expected cue boundary to the nearest output boundary.
    var outBounds = cues.SelectMany(c => new[] { c.Start, c.End }).ToList();
    double worstBoundary = expected.SelectMany(c => new[] { c.Start, c.End })
        .Max(b => outBounds.Count == 0 ? double.PositiveInfinity : outBounds.Min(o => Math.Abs(o - b)));

    // 6. Hallucination heuristics.
    var reps = Metrics.Repetitions(Metrics.Normalize(outText, zh), zh);
    var dupSegs = cues.Zip(cues.Skip(1)).Where(p => p.First.Text.Trim() == p.Second.Text.Trim()).Select(p => p.Second.Index).ToList();
    var pastEnd = cues.Where(c => c.Start >= audioSeconds - 0.05).Select(c => c.Index).ToList();

    // 5. Language detection: whisper_lang_auto_detect over the first 30 s.
    // Whisper.net returns only the selected language's probability, so each of the four
    // is read with a one-candidate call (same probs array, one encoder run per call).
    var probs = new Dictionary<string, float>();
    string? topAny;
    float topAnyP;
    double detectSeconds;
    await using (var detector = factory.CreateBuilder().WithLanguage("auto").WithThreads(effectiveThreads).Build())
    {
        var dw = Stopwatch.StartNew();
        (topAny, topAnyP) = detector.DetectLanguageWithProbability(samples);
        dw.Stop();
        detectSeconds = dw.Elapsed.TotalSeconds;
        foreach (var cand in new[] { "en", "zh", "de", "es" })
        {
            var (l, p) = detector.DetectLanguageWithProbability(samples, [cand]);
            probs[cand] = l == cand ? p : float.NaN;
        }
    }
    float sum4 = probs.Values.Sum();
    var renorm = probs.ToDictionary(kv => kv.Key, kv => kv.Value / sum4);
    string top4 = renorm.MaxBy(kv => kv.Value).Key;

    var r = new Dictionary<string, object?>
    {
        ["lang"] = lang,
        ["audioSeconds"] = audioSeconds,
        ["wallSeconds"] = watch.Elapsed.TotalSeconds,
        ["rtf"] = audioSeconds / watch.Elapsed.TotalSeconds,
        ["similarity"] = sim,
        ["similarityTruth"] = simTruth,
        ["timestampsPass"] = tsPass,
        ["worstBoundaryDelta"] = worstBoundary,
        ["worstDeltaStart"] = worstStart,
        ["worstDeltaEnd"] = worstEnd,
        ["cueMatches"] = matches.Select(m => new
        {
            expected = m.Expected.Index,
            output = m.Output?.Index,
            dStart = double.IsFinite(m.DeltaStart) ? m.DeltaStart : -1,
            dEnd = double.IsFinite(m.DeltaEnd) ? m.DeltaEnd : -1,
        }).ToList(),
        ["segments"] = segments.Select(s => new
        {
            start = s.Start.TotalSeconds,
            end = s.End.TotalSeconds,
            text = s.Text,
            noSpeech = s.NoSpeechProbability,
            language = s.Language,
        }).ToList(),
        ["repetitions"] = reps,
        ["duplicateSegments"] = dupSegs,
        ["segmentsPastAudioEnd"] = pastEnd,
        ["detectTopAll"] = topAny,
        ["detectTopAllP"] = topAnyP,
        ["detectRaw"] = probs,
        ["detectRenorm"] = renorm,
        ["detectTop4"] = top4,
        ["detectSeconds"] = detectSeconds,
        ["srt"] = srt,
    };
    fixtureResults.Add(r);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"   {lang}: {audioSeconds:F1} s audio, wall {watch.Elapsed.TotalSeconds:F2} s, RTF {audioSeconds / watch.Elapsed.TotalSeconds:F2}, sim {sim:F3}{(simTruth is double t ? $" (truth {t:F3})" : "")}, ts {(tsPass ? "pass" : "FAIL")} (worst start {worstStart:F2}, end {worstEnd:F2}, boundary {worstBoundary:F2}), detect {topAny} {topAnyP:F3} / top4 {top4} {renorm[top4]:F3}"));
    if (reps.Count > 0 || dupSegs.Count > 0 || pastEnd.Count > 0)
    {
        Console.WriteLine($"      hallucination flags: reps [{string.Join("; ", reps)}], dup segs [{string.Join(",", dupSegs)}], past end [{string.Join(",", pastEnd)}]");
    }
}
json["fixtures"] = fixtureResults;

// 4b. Warm 30 s window timings, optionally over a thread sweep.
var windowRuns = new List<Dictionary<string, object?>>();
int[] windowThreads = sweep.Length > 0 ? sweep : [effectiveThreads];
foreach (int t in windowThreads)
{
    var times = new List<double>();
    for (int rep = 0; rep < (sweep.Length > 0 ? 1 : 2); rep++)
    {
        var ww = Stopwatch.StartNew();
        await using var p = factory.CreateBuilder().WithLanguage("zh").WithThreads(t).Build();
        await foreach (var unused in p.ProcessAsync(window))
        {
            _ = unused;
        }
        ww.Stop();
        times.Add(ww.Elapsed.TotalSeconds);
    }
    windowRuns.Add(new() { ["threads"] = t, ["seconds"] = times, ["min"] = times.Min() });
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"   30 s window, {t} threads: {string.Join(", ", times.Select(x => x.ToString("F2", CultureInfo.InvariantCulture)))} s (min RTF {30.0 / times.Min():F2})"));
}
json["window30"] = windowRuns;

factory.Dispose();
using (var proc = Process.GetCurrentProcess())
{
    proc.Refresh();
    json["peakWorkingSetMB"] = proc.PeakWorkingSet64 / (1024.0 * 1024.0);
    json["peakPrivateMB"] = proc.PeakVirtualMemorySize64 / (1024.0 * 1024.0);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   peak working set {proc.PeakWorkingSet64 / (1024.0 * 1024.0):F0} MB"));
}
lock (nativeLog)
{
    json["nativeLogSample"] = nativeLog.Where(l => l.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
        || l.Contains("ggml_", StringComparison.OrdinalIgnoreCase) || l.Contains("backend", StringComparison.OrdinalIgnoreCase)
        || l.Contains("system_info", StringComparison.OrdinalIgnoreCase) || l.Contains("model size", StringComparison.OrdinalIgnoreCase))
        .Distinct().Take(80).ToList();
    File.WriteAllText(Path.Combine(outDir, $"{runName}.native.log"), string.Join('\n', nativeLog) + "\n", new UTF8Encoding(false));
}
File.WriteAllText(Path.Combine(outDir, $"{runName}.json"),
    JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }),
    new UTF8Encoding(false));
return 0;

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < a.Length; i++)
    {
        string key = a[i].TrimStart('-');
        bool hasValue = i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal);
        d[key] = hasValue ? a[++i] : "";
    }
    return d;
}

static string Required(Dictionary<string, string> d, string key) => Cli.Required(d, key);
