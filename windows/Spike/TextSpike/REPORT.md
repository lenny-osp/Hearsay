# W1 text spike: Chinese script conversion and transcript text language

Date 2026-09-29. Machine: Windows 11 Pro 10.0.26200 x64, .NET 10.0.12 (SDK 10.0.401). Covers the PLAN.md 18.8 rows "Chinese script conversion" and "Transcript text language". Throwaway code.

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File windows\Spike\TextSpike\run.ps1 [-Part probe|zh|lang|cost|all]`. It builds (Release, win-x64) and runs the modes `probe`, `zh`, `zh maxmatch`, `lang`, `cost-icu`, `cost-opencc` and `cost-opencc maxmatch`. Each cost mode runs in a fresh process, so its numbers are cold-start numbers.

## Summary

1. **Windows' ICU has the Mac's transform.** icu.dll (ICU 72.1.0.4) exports the whole unversioned `utrans_*` family and lists `Hans-Hant` and `Hant-Hans` among 742 transliterator ids. Run on the model output in `zh-30s.expected.srt`, it gives byte for byte the ZH-TW SRT the Mac writes (494 bytes). All `ChineseScriptTests.swift` string cases pass.
2. **OpenCC would make ZH-TW differ between the platforms.**
   - On zh-30s, every OpenCC variant differs from ICU in one character. ICU keeps 里; s2t and s2hk write 裏; s2tw and s2twp write 裡.
   - Over OpenCC's own table of 3,980 Simplified characters, s2t differs from ICU on 242 characters and s2tw on 231. That is 1.57% and 0.78% of character occurrences, weighted by jieba frequency.
   - The differences include very common characters: 为, 台, 干, 只, 群, 周/週.
   - The two other in-box converters, ELS transliteration and LCMapStringEx, also differ from ICU.
   - OpenCCNET (Jieba mode) costs 1.1 to 1.7 s and about 70 MB at startup, and brings in vulnerable packages.
3. **Transcript text language: use a rule-based detector.** It named the top language correctly on 68/68 labelled samples. At the 0.6 threshold it was never confident and wrong; 3 samples came out not confident, so the caller falls back to the assumed language. ELS Language Detection also ranked 68/68 correctly, but it exposes no confidence. It also returns bare `zh` for script-neutral Chinese, so ZH-TW vs ZH-CN stays undecided.

## A.0 Inputs

- expected (raw model output): 按照北京的老規矩 | 春节差不多在腊月的初旬就开始了 | 腊漆腊八 冻死寒鸭 | 这是一年里最冷的时候 | 在腊八这天 家家都熬腊八粥 | 粥是用各种米 各种豆与各种干果熬成的 | 这不是粥 而是小型的农业展览会
- truth: 按照北京的老規矩 | 春節差不多在臘月的初旬就開始了 | 臘七臘八 凍死寒鴨 | 這是一年裏最冷的時候 | 在臘八這天 家家都熬臘八粥 | 粥是用各種米 各種豆與各種乾果熬成的 | 這不是粥 而是小型的農業展覽會
- Mac ZH-TW reference: the truth file with 臘七→臘漆 and 一年裏→一年里, as `shared/fixtures/README.md` describes. Taken from the README, not from a run on a Mac.

Note: the work item assumed `zh-30s.expected.srt` is Traditional. It is not; it is the raw mlx_whisper output, mostly Simplified, with only 規 in cue 1 Traditional.

## A.1 icu.dll exports and version

- Present: u_getVersion, u_errorName, utrans_openU, utrans_openInverse, utrans_close, utrans_transUChars, utrans_trans, utrans_getUnicodeID, utrans_openIDs, utrans_countAvailableIDs, uenum_unext, ucol_open, ubrk_open, uldn_open.
- Missing: utrans_open (the deprecated char* variant, not needed) and utrans_transUChars_72 (the exports are unversioned).
- `u_getVersion`: ICU 72.1.0.4, loaded from C:\WINDOWS\SYSTEM32\icu.dll (file version 72, 1, 0, 4 (WinBuild.160101.0800)).

Calls that mirror `ChineseScript.swift`:
- Traditional: `utrans_openU(u"Hans-Hant", len, UTRANS_FORWARD, NULL, -1, &parseError, &status)`.
- Simplified: the same id with `UTRANS_REVERSE`, which is what Swift's `reverse: true` does. `utrans_getUnicodeID` reports it as "Hant-Hans", and it gives the same output as opening Hant-Hans forward.
- Conversion: `utrans_transUChars` in place, retried with a larger buffer on U_BUFFER_OVERFLOW_ERROR.

ChineseScriptTests cases:
- 那天我来到了中国最冷的城市 ↔ 那天我來到了中國最冷的城市: ok both ways.
- 我们用 Swift 和 MLX 开发软件 ↔ 我們用 Swift 和 MLX 開發軟件: ok both ways.
- The empty string and the two ASCII sentences are unchanged: ok.

## A.1 ZH-TW pipeline (verbatim)

```
ICU Hans-Hant(expected) vs Mac ZH-TW (truth with 臘漆, 里): identical
ICU Hans-Hant(expected) vs truth: 2 differing position(s)
  cue 3 char 2: 七 (U+4E03) -> 漆 (U+6F06)   in '臘漆臘八 凍死寒鴨'
  cue 4 char 5: 裏 (U+88CF) -> 里 (U+91CC)   in '這是一年里最冷的時候'
OpenCC s2t   (ZhConverter.HansToHant)(expected) vs ICU: cue 4 char 5: 里 (U+91CC) -> 裏 (U+88CF)
OpenCC s2tw  (ZhConverter.HansToTW(false))(expected) vs ICU: cue 4 char 5: 里 (U+91CC) -> 裡 (U+88E1)
OpenCC s2twp (ZhConverter.HansToTW(true))(expected) vs ICU: cue 4 char 5: 里 (U+91CC) -> 裡 (U+88E1)
OpenCC s2hk  (ZhConverter.HansToHK)(expected) vs ICU: cue 4 char 5: 里 (U+91CC) -> 裏 (U+88CF)
ICU Hans-Hant over expected.srt == Mac ZH-TW SRT bytes: True (494 vs 494 bytes)
```

## A.1 Round trips of the truth (verbatim)

```
ICU Hant-Hans(truth) = OpenCC t2s(truth) = tw2s = tw2sp: 按照北京的老规矩 | 春节差不多在腊月的初旬就开始了 | 腊七腊八 冻死寒鸭 | 这是一年里最冷的时候 | 在腊八这天 家家都熬腊八粥 | 粥是用各种米 各种豆与各种干果熬成的 | 这不是粥 而是小型的农业展览会
ICU Hans-Hant(ICU Hant-Hans(truth)) vs truth: cue 4 char 5: 裏 (U+88CF) -> 里 (U+91CC)
OpenCC s2t(OpenCC t2s(truth)) vs truth: identical
OpenCC s2tw(OpenCC t2s(truth)) vs truth: cue 4 char 5: 裏 (U+88CF) -> 裡 (U+88E1)
ICU Hans-Hant(OpenCC t2s(truth)) vs truth: cue 4 char 5: 裏 -> 里
OpenCC s2t(ICU Hant-Hans(truth)) vs truth: identical
ICU round trip of truth.srt byte-identical: False
```

ICU's round trip fails only on 里/裏. The Mac fails in the same place, because Hans-Hant keeps 里.

## A.1 ZH-CN pipeline (verbatim)

```
ICU Hant-Hans(expected) vs expected: cue 1 char 7: 規 (U+898F) -> 规 (U+89C4)
OpenCC t2s(expected) vs ICU Hant-Hans(expected): identical
ICU Hans-Hant(ICU Hant-Hans(expected)) vs Mac ZH-TW: identical
OpenCC s2t(OpenCC t2s(expected)) vs Mac ZH-TW: cue 4 char 5: 里 (U+91CC) -> 裏 (U+88CF)
```

## A.2 Parity: the same Simplified text through ICU and OpenCC

For all three Simplified inputs (the model output, ICU Hant-Hans(truth), OpenCC t2s(truth)), each OpenCC variant differs from ICU only at cue 4 char 5: s2t and s2hk write 裏, s2tw and s2twp write 裡, ICU keeps 里.

Hand-written sentences (Jieba mode), ICU vs OpenCC:
- 下周一开会…发到群里…打印: ICU 下週/群里/打印. s2t 周, 羣, 裏. s2tw 周, 裡. s2twp also changes 打印→列印.
- 后台…发布…头发: ICU 後台. s2t and s2tw 後臺. s2twp also 釋出. s2hk identical to ICU.
- 几只虾, 面条: ICU 幾只, 麵條. s2t 隻, 麪. s2tw 隻.
- …文件夹里: s2t 裏, s2tw 裡. s2twp rewrites the vocabulary (影片, 記憶體, 網路, 軟體, 設定, 儲存, 資料夾).
- 一台服务器: s2t and s2tw 臺. s2twp 臺, 伺服器.
- 我们用 Swift…, 那天我来到了…: identical in every variant.

Whole table, 3,980 STCharacters keys converted one at a time:
- s2t: 242 differ (4.26% of STCharacters occurrences, 1.574% of all character occurrences). Most frequent: 为 為/爲, 里 里/裏, 干 乾/幹, 台 台/臺, 游 游/遊, 众 眾/衆, 准 准/準, 吃 吃/喫, 群 群/羣, 划, 余, 钟 鐘/鍾, …
- s2tw: 231 differ (2.10%, 0.775%). Most frequent: 里/裡, 干/幹, 台/臺, 游/遊, 准/準, 划/劃, 余/餘, 钟/鍾, 托/託, 启 啓/啟, …
- ICU Hans-Hant changes 2,549 of the 3,980 characters and leaves the rest as they are.
- These counts are an upper bound, because OpenCC resolves some characters per word in running text (乾脆 vs 天干).
- MaxMatch vs Jieba agree on common characters. MaxMatch differs on 1,266 rare CJK Ext-A characters that jieba's dictionary never sees.

## A.3 Other in-box converters

ELS services found by `MappingGetServices` with no filter:
- {3caccdc8-5590-42dc-9a7b-b5a6b5b3b63b} Simplified→Traditional transliteration
- {a3a8333b-f4fc-42f6-a0c4-0462fe7317cb} Traditional→Simplified transliteration
- {cf7e00b1-909b-4d95-a8f4-611f7c377702} Language Detection
- {2d64b439-…} Script Detection
- Cyrillic, Devanagari, Malayalam and Bengali to Latin; Hangul decomposition

How they compare with ICU:
- **ELS transliteration:** on the fixture it writes 裡. Toward Simplified it equals ICU. On the extra sentences there are 8 differences; it converts to Taiwan vocabulary (軟體, 列印, 伺服器, 天乾). 191 table differences (于/於, 党, 历, 复, …).
- **LCMapStringEx:** on the fixture it keeps 干果 in Traditional and 乾果 in Simplified. On the extra sentences there are 10 differences (后, 發 for 髮, 面 for 麵, 云, 周). 425 table differences.
- **Windows.Globalization:** it has no documented script transform. Not probed at runtime.

## A.4 OpenCCNET

- Version 1.1.0 (CosineG, Apache-2.0, netstandard2.0), the latest on nuget.org.
- Dependencies: jieba.NET 0.42.2, which brings Newtonsoft.Json 12.0.3 (NU1903, high) and System.Configuration.ConfigurationManager 4.7.0, which brings System.Drawing.Common 4.7.0 (NU1904, critical). With warnings as errors, restore fails; the spike suppresses both advisories so the comparison could run. The product should not take this package.
- Data it ships: Dictionary 1.1 MB and JiebaResource 18 MB; the spike's output folder is 21 MB.
- Variants (ZhConverter static methods and their OpenCC configs): HansToHant = s2t, HansToTW(false) = s2tw, HansToTW(true) = s2twp, HansToHK = s2hk, HantToHans = t2s, TWToHans(false/true) = tw2s/tw2sp. Also HantToTW, HantToHK, TWToHant, HKToHans, HKToHant, Kyuu/Shin.
- Segment modes: SegmentMode.Jieba (default), MaxMatch, Custom. The mode changes results: MaxMatch writes 下週, 繫好, 天乾.
- Closest to ICU: s2tw overall; s2hk on this fixture. No variant matches ICU. Toward Simplified, t2s, tw2s and tw2sp all equal ICU on the fixture.

## A.5 Cost (cold process)

| Approach | Startup | Memory | Steady state, 95 chars | 38,000 chars |
|---|---|---|---|---|
| ICU (open forward + reverse) | 70–106 ms | +6.3 MB working set | 80–120 µs | 38–53 ms |
| OpenCCNET, Jieba | 1,150–1,700 ms | +70 MB, 77 MB after GC | 520–810 µs | 240–360 ms |
| OpenCCNET, MaxMatch | 134–156 ms | +18 MB, 25 MB after GC | 220–300 µs | 44–66 ms |

## B.1 ELS Language Detection

- Works on this machine. Calls: `MappingGetServices` (pGuid = ELS_GUID_LANGUAGE_DETECTION), `MappingRecognizeText(svc, text, len, 0, NULL, &bag)`, `MappingFreePropertyBag`, `MappingFreeServices`.
- Result: one `MAPPING_DATA_RANGE`. Its `pData` is a double-NUL-terminated list of BCP-47 tags, most likely first. **There is no score or confidence.**
- Empty input returns E_INVALIDARG (0x80070057).
- Cost: 6–18 ms to open the service; about 70 calls take 10–25 ms.

Results:
- en whole: en,da,yo,nb,nn,pl,hr,lv. All 4 en cues → en.
- de whole: de,gl,sv,ca,…. All 5 de cues → de.
- es whole: es,ca,pt,ro,…. Both es cues → es.
- zh expected whole: zh-Hans,zh-Hant,zh. Cue 1 (老規矩) → zh-Hant,zh; the other 6 cues → zh-Hans,zh.
- zh truth whole and every cue: zh-Hant,zh.
- "OK 好" → zh. "ok", "12 34 !?", "a a …" and "Guten Morgen zusammen, willkommen" → no tags.
- Script-neutral "你好，我在北京工作，大家都很好" → bare zh.
- Score: 68/68 on the first supported tag.

## B.2 Rule detector (RuleDetector.cs)

Mac constants kept: sample 4,000 characters; return nil below 12 letters; ties break in picker order en, zh-TW, zh-CN, de, es.

Confidence:
- Tokens: each Han ideograph is one token; each run of other letters is one word token. N = han + words.
- zh vs Latin: P(zh) = han/N, P(latin) = words/N.
- Script split: t = characters that ICU Hant-Hans changes (Traditional-only); s = characters that ICU Hans-Hant changes (Simplified-only). P(zh-TW) = P(zh)·(t+½)/(t+s+1), P(zh-CN) = P(zh)·(s+½)/(t+s+1).
- en/de/es scores: about 100 function words per language (a word in k lists adds 1/k to each). Plus 1 per lowercase-initial word containing ä ö ü ß (de) or ñ á é í ó ú (es), plus 1 per ¿ or ¡ (es). Capitalized names do not vote. P(l) = P(latin)·(score_l+1)/(Σ+3).
- The five values sum to 1. The detector returns the largest, and that value is the confidence.

Results over 68 labelled samples (5 fixture files whole plus the Mac ZH-TW rendering, 30 fixture cues, 12 inputs from TranscriptTextLanguageTests.swift, 26 hand-written):
- Top language 68/68 right. This counts nil as correct for the 6 fixture cues under 12 letters, which the Mac also returns nil for.
- With the 0.6 rule: 65/68 correct and 0 confident-and-wrong. Three came out not confident:
  - de 0.57 "Ich habe keine Zeit, sorry."
  - en 0.44 "He said hasta la vista and left the meeting early."
  - en 0.50 "Die hard fans will love the new release."
- Script-neutral Chinese gives zh-TW 0.50, not confident.

Selected results:
- Fixtures whole: en 0.90, de 0.92, es 0.95, zh expected zh-CN 0.94 (trad 1, simp 22), truth zh-TW 0.98.
- "Markus Müller will send … Jürgen …": en 0.71. "José and Lucía …": en 0.71. "The Straße in the address …": en 0.61.
- Spanish without diacritics: "Tenemos que terminar …" es 0.79; "Lucia, puedes mandar la presentacion hoy?" es 0.60; "Buenos dias a todos, empezamos ya" es 0.64.
- English jargon inside: "El meeting de hoy es sobre el roadmap" es 0.69; "Die Pipeline ist rot, der Build failed wieder" de 0.67.
- Chinese with English: "我们用 Swift 和 MLX 开发软件" zh-CN 0.72.
- Short inputs: "", "12 34 !?", "ok", "OK 好" and "a "×11 → nil. "Guten Morgen zusammen, willkommen" → de 0.60 (the Mac test only requires non-nil).

Known weaknesses: short sentences with one foreign word, two languages in one sentence, and Chinese with no script-specific character. Portuguese, Italian or French would come out low-confidence es or en, not nil.

## B.3 Recommendation

- Use the rule detector alone. It is deterministic, needs only the icu.dll the app already uses for script conversion, and gives the same answer on every Windows build.
- Keep the Mac constants: threshold 0.6, sample 4,000, minimum 12 letters.
- Tests should assert the language and whether it is confident, never the number (the Mac tests only assert ≥ 0.6).
- ELS agreed with the rule on every sample and has no score, so skip it. At most it could later serve as a veto that pushes the confidence below 0.6 when ELS disagrees.

## Open questions

1. The Mac reference comes from the README; confirm it with one Mac run or with shared vectors.
2. icu.dll changes with Windows updates, just as the Mac's ICU changes with macOS; shared vectors would catch a change on either side.
3. Before W6, test the rule detector on longer synthetic transcripts (not the owner's files).
