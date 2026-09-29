using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenCCNET;
using TextSpike;

// W1 text spike (PLAN.md 18.8): Chinese script conversion and transcript text
// language. Usage: TextSpike [probe|zh|lang|cost-icu|cost-opencc] [jieba|maxmatch]
Console.OutputEncoding = new UTF8Encoding(false);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string mode = args.Length > 0 ? args[0] : "probe";
SegmentMode segment = args.Length > 1 && args[1] == "maxmatch" ? SegmentMode.MaxMatch : SegmentMode.Jieba;

switch (mode)
{
    case "probe":
        Probe();
        break;
    case "zh":
        Console.WriteLine($"OpenCCNET {typeof(ZhConverter).Assembly.GetName().Version}, segment mode {segment}");
        ChineseCompare.InitOpenCC(segment);
        ChineseCompare.Run();
        break;
    case "lang":
        LanguageEval.Run();
        break;
    case "cost-icu":
        CostIcu();
        break;
    case "cost-opencc":
        CostOpenCC(segment);
        break;
    default:
        Console.Error.WriteLine($"unknown mode '{mode}'");
        return 2;
}
return 0;

static void Probe()
{
    Console.WriteLine($"OS {Environment.OSVersion}, .NET {Environment.Version}");
    Console.WriteLine($"u_getVersion: ICU {Icu.Version()}, loaded from {Icu.LoadedPath()}");
    string[] exports =
    [
        "u_getVersion", "u_errorName", "utrans_openU", "utrans_openInverse", "utrans_close", "utrans_transUChars",
        "utrans_trans", "utrans_getUnicodeID", "utrans_openIDs", "utrans_countAvailableIDs", "uenum_unext",
        "ucol_open", "ubrk_open", "uldn_open", "utrans_open", "utrans_transUChars_72",
    ];
    foreach (var (name, found) in Icu.ProbeExports(exports)) Console.WriteLine($"  export {name,-26} {(found ? "present" : "MISSING")}");
    List<string> ids = Icu.TransliteratorIds();
    Console.WriteLine($"  utrans_openIDs: {ids.Count} transliterator ids; containing Hans/Hant: {string.Join(", ", ids.Where(i => i.Contains("Hans", StringComparison.Ordinal) || i.Contains("Hant", StringComparison.Ordinal)))}");
    using var t = new Icu.Transliterator("Hans-Hant", Icu.Forward);
    using var r = new Icu.Transliterator("Hans-Hant", Icu.Reverse);
    // The Swift tests in ChineseScriptTests.swift.
    (string In, string Want, Icu.Transliterator Tr)[] cases =
    [
        ("那天我来到了中国最冷的城市", "那天我來到了中國最冷的城市", t),
        ("那天我來到了中國最冷的城市", "那天我来到了中国最冷的城市", r),
        ("我们用 Swift 和 MLX 开发软件", "我們用 Swift 和 MLX 開發軟件", t),
        ("我們用 Swift 和 MLX 開發軟件", "我们用 Swift 和 MLX 开发软件", r),
        ("", "", t), ("Hello, world. 123 -> ok!", "Hello, world. 123 -> ok!", t), ("Hello, world. 123 -> ok!", "Hello, world. 123 -> ok!", r),
        ("The quick brown fox jumps over the lazy dog.", "The quick brown fox jumps over the lazy dog.", t),
        ("The quick brown fox jumps over the lazy dog.", "The quick brown fox jumps over the lazy dog.", r),
    ];
    foreach (var (input, want, tr) in cases)
    {
        string got = tr.Transliterate(input);
        Console.WriteLine($"  ChineseScriptTests case {tr.Id}: '{input}' -> '{got}' {(got == want ? "ok" : "FAIL, want '" + want + "'")}");
    }
    Console.WriteLine("  ELS services (elscore.dll MappingGetServices, no filter):");
    foreach (Els.ServiceSummary s in Els.AllServices())
    {
        Console.WriteLine($"    {s.Guid:B} [{s.Category}] {s.Description} v{s.Version} in-scripts={s.InputScripts} out-scripts={s.OutputScripts}");
    }
    Console.WriteLine($"  LCMapStringEx(zh-TW, LCMAP_TRADITIONAL_CHINESE): {Win32.LcMap("那天我来到了中国最冷的城市 我们用 Swift 和 MLX 开发软件", Win32.LcmapTraditionalChinese, "zh-TW")}");
    Console.WriteLine($"  LCMapStringEx(zh-CN, LCMAP_SIMPLIFIED_CHINESE):  {Win32.LcMap("那天我來到了中國最冷的城市", Win32.LcmapSimplifiedChinese, "zh-CN")}");
}

static long WorkingSet()
{
    using var p = Process.GetCurrentProcess();
    p.Refresh();
    return p.WorkingSet64;
}

static long PrivateBytes()
{
    using var p = Process.GetCurrentProcess();
    p.Refresh();
    return p.PrivateMemorySize64;
}

static string ZhSample() => Srt.CleanText(Srt.Read(Srt.Fixture("zh-30s.expected.srt")));

static void Report(string what, Stopwatch sw, long ws0, long pb0)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  {what,-52} {sw.Elapsed.TotalMilliseconds,9:F1} ms   working set +{(WorkingSet() - ws0) / 1048576.0,6:F1} MB   private +{(PrivateBytes() - pb0) / 1048576.0,6:F1} MB   managed heap {GC.GetTotalMemory(false) / 1048576.0,6:F1} MB"));
}

static void CostIcu()
{
    string text = ZhSample();
    GC.Collect();
    long ws0 = WorkingSet(), pb0 = PrivateBytes();
    Console.WriteLine($"cost-icu (fresh process; baseline working set {ws0 / 1048576.0:F1} MB)");
    var sw = Stopwatch.StartNew();
    _ = Icu.Version();
    Report("load icu.dll + u_getVersion", sw, ws0, pb0);
    sw.Restart();
    using var t = new Icu.Transliterator("Hans-Hant", Icu.Forward);
    using var r = new Icu.Transliterator("Hans-Hant", Icu.Reverse);
    Report("utrans_openU Hans-Hant forward + reverse", sw, ws0, pb0);
    sw.Restart();
    string once = t.Transliterate(text);
    Report($"first Hans-Hant of zh-30s text ({text.Length} chars)", sw, ws0, pb0);
    sw.Restart();
    for (int i = 0; i < 1000; i++) once = t.Transliterate(text);
    sw.Stop();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  steady state: {sw.Elapsed.TotalMilliseconds / 1000 * 1000:F1} us per zh-30s conversion ({text.Length} chars)"));
    string big = string.Concat(Enumerable.Repeat(text, 400));
    sw.Restart();
    _ = t.Transliterate(big);
    sw.Stop();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  one hour-scale text ({big.Length} chars): {sw.Elapsed.TotalMilliseconds:F1} ms"));
    GC.KeepAlive(once);
}

static void CostOpenCC(SegmentMode segment)
{
    string text = ZhSample();
    GC.Collect();
    long ws0 = WorkingSet(), pb0 = PrivateBytes();
    Console.WriteLine($"cost-opencc {segment} (fresh process; baseline working set {ws0 / 1048576.0:F1} MB)");
    var sw = Stopwatch.StartNew();
    ChineseCompare.InitOpenCC(segment);
    Report("ZhConverter.Initialize", sw, ws0, pb0);
    sw.Restart();
    string once = ZhConverter.HansToHant(text);
    Report($"first HansToHant of zh-30s text ({text.Length} chars)", sw, ws0, pb0);
    sw.Restart();
    once = ZhConverter.HansToTW(text, true);
    Report("first HansToTW(true)", sw, ws0, pb0);
    sw.Restart();
    for (int i = 0; i < 1000; i++) once = ZhConverter.HansToHant(text);
    sw.Stop();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  steady state: {sw.Elapsed.TotalMilliseconds / 1000 * 1000:F1} us per zh-30s conversion"));
    string big = string.Concat(Enumerable.Repeat(text, 400));
    sw.Restart();
    _ = ZhConverter.HansToHant(big);
    sw.Stop();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  one hour-scale text ({big.Length} chars): {sw.Elapsed.TotalMilliseconds:F1} ms"));
    GC.Collect();
    Report("after GC (retained)", new Stopwatch(), ws0, pb0);
    GC.KeepAlive(once);
}
