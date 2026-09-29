using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TextSpike;

/// <summary>Part B: transcript text language (TranscriptTextLanguage.swift)
/// with ELS Language Detection and with RuleDetector.</summary>
internal static class LanguageEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string? MapEls(string tag) => tag switch
    {
        "en" => "en",
        "de" => "de",
        "es" => "es",
        "zh-Hans" => "zh-CN",
        "zh-Hant" => "zh-TW",
        _ => null,
    };

    private sealed record Sample(string Group, string Want, string Text);

    public static void Run()
    {
        using var hansHant = new Icu.Transliterator("Hans-Hant", Icu.Forward);
        using var hantHans = new Icu.Transliterator("Hans-Hant", Icu.Reverse);
        var rule = new RuleDetector(RuleDetector.IcuScript(hansHant, hantHans));
        using var els = new Els.Service(Els.LanguageDetection);

        var samples = new List<Sample>();
        foreach (var (file, want) in new[] { ("en-30s.expected.srt", "en"), ("de-30s.expected.srt", "de"), ("es-30s.expected.srt", "es"), ("zh-30s.expected.srt", "zh-CN"), ("zh-30s.truth.srt", "zh-TW") })
        {
            string srt = Srt.Read(Srt.Fixture(file));
            samples.Add(new Sample("fixture whole", want, Srt.CleanText(srt)));
            foreach (string cue in Srt.CueTexts(srt))
            {
                // Cue 1 of the model output is already Traditional (老規矩, no Simplified-only character).
                string cueWant = file == "zh-30s.expected.srt" && cue.Contains('規', StringComparison.Ordinal) ? "zh-TW" : want;
                samples.Add(new Sample($"fixture cue {file[..6]}", cueWant, cue));
            }
        }
        // The Mac app's own ZH-TW rendering of the zh fixture.
        samples.Add(new Sample("fixture whole", "zh-TW", hansHant.Transliterate(Srt.CleanText(Srt.Read(Srt.Fixture("zh-30s.expected.srt"))))));

        // TranscriptTextLanguageTests.swift
        samples.AddRange(
        [
            new("mac test", "en", "Let's start with the budget review. We need to finish the report before Friday and send it to the whole team."),
            new("mac test", "de", "Wir fangen mit dem Budget an. Der Bericht muss bis Freitag fertig sein, dann schicken wir ihn an das ganze Team."),
            new("mac test", "es", "Empezamos con el presupuesto. Tenemos que terminar el informe antes del viernes y enviarlo a todo el equipo."),
            new("mac test", "zh-TW", "我們先從預算開始討論。報告必須在星期五之前完成，然後寄給整個團隊。"),
            new("mac test", "zh-CN", "我们先从预算开始讨论。报告必须在星期五之前完成，然后发给整个团队。"),
            new("mac test", "nil", ""),
            new("mac test", "nil", "12 34 !?"),
            new("mac test", "nil", "ok"),
            new("mac test", "nil", "OK 好"),
            new("mac test", "nil", string.Concat(Enumerable.Repeat("a ", RuleDetector.MinimumLetters - 1))),
            new("mac test", "de?", "Guten Morgen zusammen, willkommen"),
            new("mac test", "de", Srt.CleanText("1\n00:00:00,000 --> 00:00:04,000\nWir fangen mit dem Budget an und besprechen dann den Bericht.\n\n2\n00:00:04,000 --> 00:00:08,000\nDer Bericht muss bis Freitag fertig sein.\n")),
        ]);

        // Hand-written, including tricky ones.
        samples.AddRange(
        [
            new("hand", "en", "Markus Müller will send the report to Jürgen before Friday."),
            new("hand", "en", "José and Lucía will join the call at noon."),
            new("hand", "en", "Can you share your screen? I can't see the slides yet."),
            new("hand", "en", "The Straße in the address was wrong, so the package came back."),
            new("hand", "en", "OK, sounds good, thanks everyone, see you tomorrow."),
            new("hand", "en", "Deployment blocked. Waiting for approval from security review."),
            new("hand", "de", "Wir müssen das Meeting auf Montag verschieben, weil das Deployment noch nicht fertig ist."),
            new("hand", "de", "Kannst du mir bitte die Folien schicken?"),
            new("hand", "de", "Das Release ist für Donnerstag geplant."),
            new("hand", "de", "Ich habe keine Zeit, sorry."),
            new("hand", "de", "Die Tickets im Backlog sind priorisiert."),
            new("hand", "es", "Tenemos que terminar el informe antes del viernes y enviarlo a todo el equipo"),
            new("hand", "es", "Vamos a revisar el backlog del sprint y el deployment con el cliente."),
            new("hand", "es", "Lucia, puedes mandar la presentacion hoy?"),
            new("hand", "es", "¿Alguien tiene preguntas sobre el presupuesto?"),
            new("hand", "es", "Buenos dias a todos, empezamos ya"),
            new("hand", "es", "Perfecto, nos vemos mañana"),
            new("hand", "en", "He said hasta la vista and left the meeting early."),
            new("hand", "es", "El meeting de hoy es sobre el roadmap"),
            new("hand", "de", "Die Pipeline ist rot, der Build failed wieder"),
            new("hand", "en", "Die hard fans will love the new release."),
            new("hand", "zh-CN", "我们用 Swift 和 MLX 开发软件"),
            new("hand", "zh-CN", "好的，我们明天再讨论这个 bug 的 fix，然后发布新版本"),
            new("hand", "zh-TW", "好的，我們明天再討論這個 bug 的 fix，然後發佈新版本"),
            new("hand", "zh-TW", "請大家把會議記錄寄給我，謝謝"),
            new("hand", "zh-CN", "我在北京开会，大家好，今天天气不错"),
            new("hand", "zh-?", "你好，我在北京工作，大家都很好"), // no character that differs between the scripts
        ]);

        Console.WriteLine("== B.1 ELS Language Detection (elscore.dll, service {CF7E00B1-909B-4D95-A8F4-611F7C377702}) and B.2 rule detector");
        Console.WriteLine("   columns: want | ELS ranked tags (first 8) -> first supported | rule: language confidence [confident?] evidence | text");
        int n = 0, elsOk = 0, ruleOk = 0, ruleConfOk = 0, ruleConfWrong = 0;
        var elsFails = new List<string>();
        var ruleFails = new List<string>();
        var ruleNotConfident = new List<string>();
        int floorNil = 0;
        var swEls = new Stopwatch();
        var swRule = new Stopwatch();
        foreach (Sample s in samples)
        {
            swEls.Start();
            List<string> ranked;
            string elsError = "";
            try
            {
                ranked = els.DetectLanguages(s.Text);
            }
            catch (InvalidOperationException e)
            {
                ranked = [];
                elsError = e.Message;
            }
            swEls.Stop();
            string? elsPick = ranked.Select(MapEls).FirstOrDefault(m => m is not null);
            swRule.Start();
            RuleDetector.Result? r = rule.Detect(s.Text);
            swRule.Stop();

            string oneLine = s.Text.Replace("\n", " / ", StringComparison.Ordinal);
            if (oneLine.Length > 70) oneLine = oneLine[..70] + "...";
            string ruleText = r is null ? "nil" : string.Create(Inv, $"{r.Language} {r.Confidence:F2} [{(r.Confidence >= RuleDetector.ConfidenceThreshold ? "yes" : "no")}] {r.Evidence}");
            Console.WriteLine($"  {s.Group,-18} want {s.Want,-5} | ELS {(elsError.Length > 0 ? elsError : string.Join(",", ranked.Take(8)))} -> {elsPick ?? "none"} | rule {ruleText} | {oneLine}");

            bool scorable = !s.Want.Contains('?', StringComparison.Ordinal);
            if (!scorable) continue;
            n++;
            string wantEls = s.Want == "nil" ? "none" : s.Want;
            // ELS has no minimum; for "nil" rows count it right when it names no supported language.
            if ((elsPick ?? "none") == wantEls) elsOk++;
            else elsFails.Add($"{s.Group}: want {s.Want}, ELS {elsPick ?? "none"} ({string.Join(",", ranked.Take(5))}) '{oneLine}'");
            // Below MinimumLetters the Mac returns nil too (TranscriptTextLanguage.minimumLetters).
            int letterCount = s.Text.EnumerateRunes().Count(Rune.IsLetter);
            string ruleWant = letterCount < RuleDetector.MinimumLetters ? "nil" : s.Want;
            if (ruleWant == "nil" && s.Want != "nil") floorNil++;
            string ruleTop = r?.Language ?? "nil";
            if (ruleTop == ruleWant) ruleOk++;
            else ruleFails.Add($"{s.Group}: want {ruleWant}, rule {ruleText} '{oneLine}'");
            string ruleConfident = r is not null && r.Confidence >= RuleDetector.ConfidenceThreshold ? r.Language : "nil";
            if (ruleConfident == "nil" && ruleWant != "nil") ruleNotConfident.Add($"{s.Group}: want {ruleWant}, rule {ruleText} '{oneLine}'");
            if (ruleConfident == ruleWant) ruleConfOk++;
            else if (ruleConfident != "nil") ruleConfWrong++;
        }
        Console.WriteLine();
        Console.WriteLine($"== B summary over {n} labelled samples (rows marked '?' are not scored)");
        Console.WriteLine($"  ELS first supported tag correct: {elsOk}/{n}");
        foreach (string f in elsFails) Console.WriteLine("    ELS miss: " + f);
        Console.WriteLine($"  rule top language correct (nil counted as a language; {floorNil} samples with a language label have fewer than {RuleDetector.MinimumLetters} letters, so nil is the Mac contract's answer for them): {ruleOk}/{n}");
        foreach (string f in ruleFails) Console.WriteLine("    rule miss: " + f);
        Console.WriteLine($"  rule with the 0.6 rule: {ruleConfOk}/{n} correct (confident and right, or nil where nil is wanted), {ruleConfWrong} confident and wrong, the rest not confident (caller falls back to the assumed language)");
        foreach (string f in ruleNotConfident) Console.WriteLine("    rule not confident: " + f);
        Console.WriteLine(string.Create(Inv, $"  time: ELS {swEls.Elapsed.TotalMilliseconds:F1} ms for {samples.Count} calls (includes first-call load), rule {swRule.Elapsed.TotalMilliseconds:F1} ms"));

        var sw = Stopwatch.StartNew();
        using (var els2 = new Els.Service(Els.LanguageDetection))
        {
            _ = els2.DetectLanguages("warm up");
        }
        Console.WriteLine(string.Create(Inv, $"  ELS open + first call on an already-loaded elscore: {sw.Elapsed.TotalMilliseconds:F1} ms"));
    }
}
