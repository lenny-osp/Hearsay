using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace WhisperSpike;

internal static class Cli
{
    public static string Required(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new ArgumentException($"missing --{key}");
}

/// <summary>
/// --native-detect: language detection through whisper.cpp's C API (see NativeDetect),
/// cross-checked against Whisper.net's DetectLanguageWithProbability, on each fixture
/// and on 30 s of digital silence.
/// </summary>
internal static class NativeDetectMode
{
    private static readonly string[] Four = ["en", "zh", "de", "es"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        string modelPath = Cli.Required(opts, "model");
        string runtime = Cli.Required(opts, "runtime").ToLowerInvariant();
        string fixtures = Cli.Required(opts, "fixtures");
        string outDir = Cli.Required(opts, "out");
        Directory.CreateDirectory(outDir);
        RuntimeOptions.RuntimeLibraryOrder = runtime == "cpu" ? [RuntimeLibrary.Cpu] : [RuntimeLibrary.Vulkan];
        int threads = Math.Min(4, Environment.ProcessorCount);

        // Let Whisper.net load its native chain first, then bind to the same whisper.dll.
        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = runtime != "cpu" });
        await using var processor = factory.CreateBuilder().WithLanguage("auto").WithThreads(threads).Build();
        string dll = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime == "cpu" ? "win-x64" : Path.Combine("vulkan", "win-x64"), "whisper.dll");
        using var native = new NativeDetect(dll, modelPath);

        var inputs = new List<(string Name, float[] Samples)>();
        foreach (var lang in Four)
        {
            inputs.Add(($"{lang}-30s", Wav.ReadMono16k(Path.Combine(fixtures, $"{lang}-30s.wav"))));
        }
        inputs.Add(("silence-30s", new float[16000 * 30]));

        var rows = new List<object>();
        var md = new StringBuilder();
        md.Append("| input | en | zh | de | es | top (all 99) | no-speech | one native call (s) | Whisper.net en/zh/de/es raw | max abs diff |\n");
        md.Append("|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var (name, samples) in inputs)
        {
            var w = Stopwatch.StartNew();
            var (probs, noSpeech, top) = native.Detect(samples, threads);
            w.Stop();
            var raw = Four.ToDictionary(c => c, c => probs[native.LangId(c)]);
            float sum = raw.Values.Sum();
            var renorm = raw.ToDictionary(kv => kv.Key, kv => kv.Value / sum);
            var viaNet = Four.ToDictionary(c => c, c => processor.DetectLanguageWithProbability(samples, [c]).probability);
            float diff = Four.Max(c => Math.Abs(viaNet[c] - raw[c]));
            var (netTop, netTopP) = processor.DetectLanguageWithProbability(samples);
            rows.Add(new { input = name, raw, renorm, noSpeech, topId = top, netTop, netTopP, seconds = w.Elapsed.TotalSeconds, viaNet, maxAbsDiff = diff });
            string F(float v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
            md.Append(CultureInfo.InvariantCulture, $"| {name} | {F(renorm["en"])} | {F(renorm["zh"])} | {F(renorm["de"])} | {F(renorm["es"])} | {netTop} {F(netTopP)} | {noSpeech:0.####E+0} | {w.Elapsed.TotalSeconds:F2} | {string.Join(" / ", Four.Select(c => F(viaNet[c])))} | {diff:0.#E+0} |\n");
            Console.WriteLine($"{name}: {string.Join(", ", Four.Select(c => $"{c} {F(renorm[c])}"))}; no-speech {noSpeech:E3}; top {netTop} {F(netTopP)}; {w.Elapsed.TotalSeconds:F2} s; diff vs Whisper.net {diff:E1}");
        }
        string baseName = $"{Path.GetFileNameWithoutExtension(modelPath)}-{runtime}-native-detect";
        File.WriteAllText(Path.Combine(outDir, baseName + ".md"), md.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, baseName + ".json"),
            JsonSerializer.Serialize(rows, JsonOptions), new UTF8Encoding(false));
        return 0;
    }
}
