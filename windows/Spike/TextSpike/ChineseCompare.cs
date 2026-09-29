using System.Globalization;
using System.Text;
using OpenCCNET;

namespace TextSpike;

/// <summary>Part A: ICU Hans-Hant (as on the Mac, ChineseScript.swift) against
/// OpenCC on shared/fixtures/zh-30s.*.srt.</summary>
internal static class ChineseCompare
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void InitOpenCC(SegmentMode mode)
    {
        ZhConverter.Initialize(
            Path.Combine(AppContext.BaseDirectory, "Dictionary"),
            Path.Combine(AppContext.BaseDirectory, "JiebaResource"),
            false, mode);
    }

    /// <summary>OpenCCNET entry points and the OpenCC config each corresponds to.</summary>
    public static readonly (string Name, Func<string, string> Convert)[] OpenCCToTraditional =
    [
        ("OpenCC s2t   (ZhConverter.HansToHant)", ZhConverter.HansToHant),
        ("OpenCC s2tw  (ZhConverter.HansToTW(false))", s => ZhConverter.HansToTW(s, false)),
        ("OpenCC s2twp (ZhConverter.HansToTW(true))", s => ZhConverter.HansToTW(s, true)),
        ("OpenCC s2hk  (ZhConverter.HansToHK)", ZhConverter.HansToHK),
    ];

    public static readonly (string Name, Func<string, string> Convert)[] OpenCCToSimplified =
    [
        ("OpenCC t2s   (ZhConverter.HantToHans)", ZhConverter.HantToHans),
        ("OpenCC tw2s  (ZhConverter.TWToHans(false))", s => ZhConverter.TWToHans(s, false)),
        ("OpenCC tw2sp (ZhConverter.TWToHans(true))", s => ZhConverter.TWToHans(s, true)),
    ];

    /// <summary>Prints every rune position where `got` differs from `reference`,
    /// cue by cue; returns the number of differing positions.</summary>
    public static int Diff(string label, IReadOnlyList<string> reference, IReadOnlyList<string> got)
    {
        int count = 0;
        var lines = new List<string>();
        for (int c = 0; c < Math.Max(reference.Count, got.Count); c++)
        {
            string a = c < reference.Count ? reference[c] : "";
            string b = c < got.Count ? got[c] : "";
            Rune[] ra = [.. a.EnumerateRunes()];
            Rune[] rb = [.. b.EnumerateRunes()];
            if (ra.Length != rb.Length)
            {
                lines.Add($"    cue {c + 1}: length differs: '{a}' vs '{b}'");
                count++;
                continue;
            }
            for (int i = 0; i < ra.Length; i++)
            {
                if (ra[i] != rb[i])
                {
                    lines.Add(string.Create(Inv, $"    cue {c + 1} char {i + 1}: {ra[i]} (U+{ra[i].Value:X4}) -> {rb[i]} (U+{rb[i].Value:X4})   in '{b}'"));
                    count++;
                }
            }
        }
        Console.WriteLine($"  {label}: {(count == 0 ? "identical" : count.ToString(Inv) + " differing position(s)")}");
        foreach (string l in lines) Console.WriteLine(l);
        return count;
    }

    public static void Run()
    {
        using var hansHant = new Icu.Transliterator("Hans-Hant", Icu.Forward);
        using var hantHans = new Icu.Transliterator("Hans-Hant", Icu.Reverse); // the Mac's reverse: true
        using var hantHansFwd = new Icu.Transliterator("Hant-Hans", Icu.Forward);
        Func<string, string> icuT = hansHant.Transliterate;
        Func<string, string> icuS = hantHans.Transliterate;

        string expectedSrt = Srt.Read(Srt.Fixture("zh-30s.expected.srt"));
        string truthSrt = Srt.Read(Srt.Fixture("zh-30s.truth.srt"));
        List<string> expected = Srt.CueTexts(expectedSrt);
        List<string> truth = Srt.CueTexts(truthSrt);

        // What the Mac writes for ZH-TW from this model output: the truth file
        // except the two characters shared/fixtures/README.md names (臘漆 for
        // 臘七, 里 for 裏), i.e. ICU Hans-Hant leaves 漆 and 里 alone.
        string macTwSrt = truthSrt.Replace("臘七", "臘漆", StringComparison.Ordinal).Replace("這是一年裏", "這是一年里", StringComparison.Ordinal);
        List<string> macTw = Srt.CueTexts(macTwSrt);

        Console.WriteLine("== A.0 inputs");
        Console.WriteLine($"  expected cues (raw model output, mixed script): {string.Join(" | ", expected)}");
        Console.WriteLine($"  truth cues (owner's Traditional):               {string.Join(" | ", truth)}");
        Console.WriteLine($"  ICU ids: forward '{hansHant.Id}', reverse '{hantHans.Id}', Hant-Hans forward '{hantHansFwd.Id}'");
        Console.WriteLine($"  Hans-Hant reverse == Hant-Hans forward on truth and expected: {truth.Concat(expected).All(t => icuS(t) == hantHansFwd.Transliterate(t))}");

        Console.WriteLine();
        Console.WriteLine("== A.1 the ZH-TW pipeline: Traditional from the model output (expected.srt)");
        Diff("ICU Hans-Hant(expected) vs Mac ZH-TW (truth with 臘漆, 里)", macTw, [.. expected.Select(icuT)]);
        Diff("ICU Hans-Hant(expected) vs truth", truth, [.. expected.Select(icuT)]);
        foreach (var (name, f) in OpenCCToTraditional)
        {
            Diff($"{name}(expected) vs ICU Hans-Hant(expected)", [.. expected.Select(icuT)], [.. expected.Select(f)]);
        }
        string icuTwSrt = Srt.MapCueLines(expectedSrt, icuT);
        byte[] a = Encoding.UTF8.GetBytes(icuTwSrt), b = Encoding.UTF8.GetBytes(macTwSrt);
        Console.WriteLine($"  ICU Hans-Hant over expected.srt == Mac ZH-TW SRT bytes: {a.AsSpan().SequenceEqual(b)} ({a.Length} vs {b.Length} bytes)");

        Console.WriteLine();
        Console.WriteLine("== A.1 round trips of the Traditional truth: to Simplified and back");
        List<string> sIcu = [.. truth.Select(icuS)];
        List<string> sOcc = [.. truth.Select(ZhConverter.HantToHans)];
        Console.WriteLine($"  ICU Hant-Hans(truth):  {string.Join(" | ", sIcu)}");
        Console.WriteLine($"  OpenCC t2s(truth):     {string.Join(" | ", sOcc)}");
        Diff("OpenCC t2s(truth) vs ICU Hant-Hans(truth)", sIcu, sOcc);
        foreach (var (name, f) in OpenCCToSimplified.Skip(1))
        {
            Diff($"{name}(truth) vs ICU Hant-Hans(truth)", sIcu, [.. truth.Select(f)]);
        }
        Diff("ICU Hans-Hant(ICU Hant-Hans(truth)) vs truth", truth, [.. sIcu.Select(icuT)]);
        Diff("OpenCC s2t(OpenCC t2s(truth)) vs truth", truth, [.. sOcc.Select(ZhConverter.HansToHant)]);
        Diff("OpenCC s2tw(OpenCC t2s(truth)) vs truth", truth, [.. sOcc.Select(s => ZhConverter.HansToTW(s, false))]);
        Diff("ICU Hans-Hant(OpenCC t2s(truth)) vs truth", truth, [.. sOcc.Select(icuT)]);
        Diff("OpenCC s2t(ICU Hant-Hans(truth)) vs truth", truth, [.. sIcu.Select(ZhConverter.HansToHant)]);
        string rt = Srt.MapCueLines(Srt.MapCueLines(truthSrt, icuS), icuT);
        Console.WriteLine($"  ICU round trip of truth.srt byte-identical: {rt == truthSrt}");

        Console.WriteLine();
        Console.WriteLine("== A.1 the expected (mixed) text to Simplified (the ZH-CN pipeline) and back");
        List<string> eIcuS = [.. expected.Select(icuS)];
        List<string> eOccS = [.. expected.Select(ZhConverter.HantToHans)];
        Console.WriteLine($"  ICU Hant-Hans(expected): {string.Join(" | ", eIcuS)}");
        Diff("ICU Hant-Hans(expected) vs expected (what ZH-CN changes)", expected, eIcuS);
        Diff("OpenCC t2s(expected) vs ICU Hant-Hans(expected)", eIcuS, eOccS);
        Diff("ICU Hans-Hant(ICU Hant-Hans(expected)) vs Mac ZH-TW", macTw, [.. eIcuS.Select(icuT)]);
        Diff("OpenCC s2t(OpenCC t2s(expected)) vs Mac ZH-TW", macTw, [.. eOccS.Select(ZhConverter.HansToHant)]);

        Console.WriteLine();
        Console.WriteLine("== A.2 same Simplified input, ICU Hans-Hant vs each OpenCC variant");
        var simplifiedInputs = new (string Name, List<string> Text)[]
        {
            ("model output (expected.srt)", expected),
            ("ICU Hant-Hans(truth)", sIcu),
            ("OpenCC t2s(truth)", sOcc),
        };
        foreach (var (inName, input) in simplifiedInputs)
        {
            Console.WriteLine($"  input: {inName}");
            List<string> icu = [.. input.Select(icuT)];
            foreach (var (name, f) in OpenCCToTraditional) Diff("  " + name + " vs ICU", icu, [.. input.Select(f)]);
        }

        Console.WriteLine();
        Console.WriteLine("== A.2 extra meeting sentences (hand-written Simplified), ICU vs OpenCC");
        string[] extra =
        [
            "我们用 Swift 和 MLX 开发软件",
            "那天我来到了中国最冷的城市",
            "下周一开会讨论预算，会议记录发到群里，别忘了带电脑和打印的报告。",
            "这个功能的后台服务在发布前需要修复两个问题，头发都快掉光了。",
            "他系好鞋带后出发去面试，面条已经凉了，只剩几只虾。",
            "请把视频、内存、网络和软件的设置保存在云端的文件夹里。",
            "我们只有一台服务器，干脆把它升级吧，天干物燥注意防火。",
        ];
        foreach (string s in extra)
        {
            string icu = icuT(s);
            Console.WriteLine($"  src:   {s}");
            Console.WriteLine($"  ICU:   {icu}");
            foreach (var (name, f) in OpenCCToTraditional)
            {
                string o = f(s);
                string marks = MarkDiff(icu, o);
                Console.WriteLine($"  {name[7..12].Trim(),-5}: {o}{(marks.Length > 0 ? "   differs at " + marks : "")}");
            }
        }

        Console.WriteLine();
        using var elsT = new Els.Service(new Guid("3CACCDC8-5590-42DC-9A7B-B5A6B5B3B63B"));
        using var elsS = new Els.Service(new Guid("A3A8333B-F4FC-42F6-A0C4-0462FE7317CB"));
        var inbox = new (string Name, Func<string, string> ToT, Func<string, string> ToS)[]
        {
            ("ELS Microsoft Simplified/Traditional Chinese Transliteration", elsT.Transliterate, elsS.Transliterate),
            ("LCMapStringEx LCMAP_TRADITIONAL/SIMPLIFIED_CHINESE", s => Win32.LcMap(s, Win32.LcmapTraditionalChinese, "zh-TW"), s => Win32.LcMap(s, Win32.LcmapSimplifiedChinese, "zh-CN")),
        };
        Console.WriteLine("== A.3 other in-box converters vs ICU");
        foreach (var (name, toT, toS) in inbox)
        {
            Diff($"{name}: to Traditional(expected) vs ICU Hans-Hant(expected)", [.. expected.Select(icuT)], [.. expected.Select(toT)]);
            Diff($"{name}: to Simplified(truth) vs ICU Hant-Hans(truth)", sIcu, [.. truth.Select(toS)]);
            Diff($"{name}: to Traditional(extra sentences) vs ICU", [.. extra.Select(icuT)], [.. extra.Select(toT)]);
        }

        Console.WriteLine();
        CharacterTable(hansHant, [.. OpenCCToTraditional.Take(2), .. inbox.Select(i => (i.Name, i.ToT))]);
    }

    private static string MarkDiff(string a, string b)
    {
        Rune[] ra = [.. a.EnumerateRunes()], rb = [.. b.EnumerateRunes()];
        if (ra.Length != rb.Length) return "(length differs)";
        var parts = new List<string>();
        for (int i = 0; i < ra.Length; i++)
        {
            if (ra[i] != rb[i]) parts.Add($"{ra[i]}->{rb[i]}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Whole-table comparison: every key of OpenCC's STCharacters.txt
    /// through ICU Hans-Hant and OpenCC s2t / s2tw, weighted by character
    /// frequency from jieba's dict.txt (word frequency counts, a proxy for
    /// running text).</summary>
    private static void CharacterTable(Icu.Transliterator hansHant, (string Name, Func<string, string> Convert)[] others)
    {
        var freq = new Dictionary<Rune, long>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "JiebaResource", "dict.txt")))
        {
            string[] p = line.Split(' ');
            if (p.Length < 2 || !long.TryParse(p[1], NumberStyles.Integer, Inv, out long f)) continue;
            foreach (Rune r in p[0].EnumerateRunes()) freq[r] = freq.GetValueOrDefault(r) + f;
        }
        long totalFreq = freq.Values.Sum();

        var keys = new List<string>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Dictionary", "STCharacters.txt")))
        {
            string k = line.Split('\t')[0];
            if (k.Length > 0) keys.Add(k);
        }
        Console.WriteLine($"== A.2 whole table: {keys.Count} Simplified characters from OpenCC STCharacters.txt, one at a time");
        long allFreq = keys.Sum(k => k.EnumerateRunes().Sum(r => freq.GetValueOrDefault(r)));
        foreach (var (name, f) in others)
        {
            int diffs = 0;
            long diffFreq = 0;
            var top = new List<(long F, string Line)>();
            foreach (string k in keys)
            {
                string icu = hansHant.Transliterate(k);
                string occ = f(k);
                if (icu == occ) continue;
                diffs++;
                long fr = k.EnumerateRunes().Sum(r => freq.GetValueOrDefault(r));
                diffFreq += fr;
                top.Add((fr, $"{k} ICU {icu} / other {occ}"));
            }
            Console.WriteLine(string.Create(Inv, $"  {name}: {diffs} of {keys.Count} characters differ from ICU; by jieba frequency {100.0 * diffFreq / Math.Max(1, allFreq):F2}% of STCharacters occurrences, {100.0 * diffFreq / Math.Max(1, totalFreq):F3}% of all character occurrences"));
            Console.WriteLine("    most frequent differences: " + string.Join("; ", top.OrderByDescending(t => t.F).Take(40).Select(t => t.Line)));
            Console.WriteLine($"    differences on characters jieba never sees: {top.Count(t => t.F == 0)}, e.g. " + string.Join("; ", top.Where(t => t.F == 0).Take(8).Select(t => t.Line)));
        }
        int icuChanges = keys.Count(k => hansHant.Transliterate(k) != k);
        Console.WriteLine($"  ICU Hans-Hant changes {icuChanges} of those {keys.Count} characters (the rest it leaves as is)");
    }
}
