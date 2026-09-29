# W1 spike, Whisper half: Whisper.net on the dev machine

Date 2026-09-29. Scope: PLAN.md 18.6 row W1, 18.8 rows "Hallucination
filter" and "Language detection". Throwaway console app; not part of
`windows/Hearsay.slnx`. Everything below is reproduced by `run.ps1`
(outputs in `out/`).

## Setup

| Item | Value |
|---|---|
| Machine | Intel Core i5-1235U (2P + 8E cores, 12 threads), 16 GB, Intel UHD iGPU, no NVIDIA; on AC power, ASUS Recommended power plan |
| .NET | SDK 10.0.401, `net10.0`, win-x64, Release |
| `Whisper.net` | **1.9.1** (latest stable on nuget.org; 1.9.2-preview1 exists, not used) |
| `Whisper.net.Runtime` (CPU) | **1.9.1** |
| `Whisper.net.Runtime.Vulkan` | **1.9.1** |
| Bundled whisper.cpp | submodule commit `f24588a272ae8e23280d9c220536437164e6ed28` (CMake version 1.8.5) |
| CPU runtime features | `SSE3 AVX AVX2 F16C FMA BMI2 OPENMP REPACK` (from `WhisperFactory.GetRuntimeInfo()`) |
| Vulkan device | `ggml_vulkan: 0 = Intel(R) UHD Graphics (Intel Corporation) \| uma: 1 \| fp16: 1 \| bf16: 0 \| warp size: 32 \| shared memory: 32768 \| int dot: 1 \| matrix cores: none` |
| `ggml-small.bin` | 487,601,967 bytes, SHA-256 `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b` (both match the Hugging Face `X-Linked-Size` / `X-Linked-ETag`) |
| `ggml-large-v3-turbo-q5_0.bin` | 574,041,195 bytes, SHA-256 `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2` (both match) |

Transcription settings: `factory.CreateBuilder().WithLanguage(lang)` and
nothing else (no prompt, no thread override; whisper.cpp defaults apply,
see the API section). Runtime chosen per process with
`RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu]` or
`[RuntimeLibrary.Vulkan]`; `RuntimeOptions.LoadedLibrary` confirms which one
loaded. Vulkan loaded on the first try; no error.

Timing caveats: other agents were building on the same machine during
some runs (a `VBCSCompiler` was active), so CPU numbers carry a few
percent of noise. Wall times are warm: each process first runs one "cold"
30 s window (reported separately) so graph allocation and Vulkan pipeline
compilation are not charged to the first fixture. Peak working set is
`Process.PeakWorkingSet64` for the whole process (model, all fixtures,
detection, window sweep).

Metrics: similarity = 1 - Levenshtein / max length over Unicode scalars,
after lowercasing, stripping punctuation and symbols, and collapsing
whitespace (zh: whitespace and punctuation stripped, no lowercasing).
Timestamps: for each expected cue, the output cue overlapping it most;
pass if every |Δstart| and |Δend| is at most 0.5 s. "Worst boundary Δ" is
an extra view: the distance from each expected cue boundary to the
nearest output cue boundary (shows whether a failure is only a different
segmentation).

## Results

### ggml-small / cpu

Loaded library `Cpu`; model load 0.57 s; threads 4 (whisper.cpp default min(4, cores)); cold first 30 s window 44.95 s; peak working set 831 MB (whole process, all steps).

| fixture | audio (s) | similarity | ts (every cue within 0.5 s) | worst Δstart / Δend (s) | worst boundary Δ (s) | wall (s) | RTF |
|---|---|---|---|---|---|---|---|
| en | 18.9 | 0.997 | FAIL | 2.58 / 3.58 | 2.34 | 21.65 | 0.87 |
| de | 22.0 | 1.000 | FAIL | 1.48 / 1.16 | 0.38 | 24.41 | 0.90 |
| es | 21.6 | 1.000 | FAIL | 3.48 / 2.82 | 0.26 | 23.46 | 0.92 |
| zh | 29.4 | 0.587 (expected) / 0.750 (truth) | FAIL | 2.34 / 2.34 | 2.34 | 57.99 | 0.51 |

30 s window (zh-30s looped to 30.0 s, language zh), warm, by thread count: 2 threads 41.35 s (RTF 0.73), 4 threads 30.74 s (RTF 0.98), 8 threads 26.45 s (RTF 1.13), 12 threads 28.86 s (RTF 1.04)

### ggml-small / vulkan

Loaded library `Vulkan`; model load 1.69 s; threads 4 (whisper.cpp default min(4, cores)); cold first 30 s window 12.68 s; peak working set 1243 MB (whole process, all steps).

| fixture | audio (s) | similarity | ts (every cue within 0.5 s) | worst Δstart / Δend (s) | worst boundary Δ (s) | wall (s) | RTF |
|---|---|---|---|---|---|---|---|
| en | 18.9 | 0.997 | FAIL | 2.58 / 3.58 | 2.34 | 5.80 | 3.27 |
| de | 22.0 | 1.000 | FAIL | 1.48 / 1.16 | 0.38 | 7.13 | 3.08 |
| es | 21.6 | 1.000 | FAIL | 3.48 / 2.82 | 0.26 | 7.27 | 2.97 |
| zh | 29.4 | 0.587 (expected) / 0.750 (truth) | FAIL | 2.34 / 2.34 | 2.34 | 10.40 | 2.82 |

30 s window (zh-30s looped to 30.0 s, language zh), warm, by thread count: 2 threads 11.00 s (RTF 2.73), 4 threads 10.19 s (RTF 2.94), 8 threads 11.22 s (RTF 2.67), 12 threads 9.85 s (RTF 3.05)

### ggml-large-v3-turbo-q5_0 / cpu

Loaded library `Cpu`; model load 1.08 s; threads 4 (whisper.cpp default min(4, cores)); cold first 30 s window 66.85 s; peak working set 1016 MB (whole process, all steps).

| fixture | audio (s) | similarity | ts (every cue within 0.5 s) | worst Δstart / Δend (s) | worst boundary Δ (s) | wall (s) | RTF |
|---|---|---|---|---|---|---|---|
| en | 18.9 | 1.000 | pass | 0.00 / 0.00 | 0.00 | 68.64 | 0.28 |
| de | 22.0 | 1.000 | pass | 0.00 / 0.02 | 0.02 | 77.18 | 0.28 |
| es | 21.6 | 1.000 | pass | 0.00 / 0.00 | 0.00 | 56.27 | 0.38 |
| zh | 29.4 | 0.976 (expected) / 0.690 (truth) | pass | 0.00 / 0.46 | 0.46 | 42.80 | 0.69 |

30 s window (zh-30s looped to 30.0 s, language zh), warm, by thread count: 2 threads 62.01 s (RTF 0.48), 4 threads 48.90 s (RTF 0.61), 8 threads 40.98 s (RTF 0.73), 12 threads 37.35 s (RTF 0.80)

### ggml-large-v3-turbo-q5_0 / vulkan

Loaded library `Vulkan`; model load 1.04 s; threads 4 (whisper.cpp default min(4, cores)); cold first 30 s window 12.57 s; peak working set 1247 MB (whole process, all steps).

| fixture | audio (s) | similarity | ts (every cue within 0.5 s) | worst Δstart / Δend (s) | worst boundary Δ (s) | wall (s) | RTF |
|---|---|---|---|---|---|---|---|
| en | 18.9 | 1.000 | pass | 0.00 / 0.00 | 0.00 | 9.45 | 2.00 |
| de | 22.0 | 1.000 | pass | 0.00 / 0.02 | 0.02 | 10.93 | 2.01 |
| es | 21.6 | 1.000 | pass | 0.00 / 0.00 | 0.00 | 10.07 | 2.14 |
| zh | 29.4 | 0.976 (expected) / 0.690 (truth) | pass | 0.02 / 0.46 | 0.46 | 10.18 | 2.88 |

30 s window (zh-30s looped to 30.0 s, language zh), warm, by thread count: 2 threads 8.90 s (RTF 3.37), 4 threads 8.84 s (RTF 3.39), 8 threads 10.41 s (RTF 2.88), 12 threads 9.66 s (RTF 3.11)


Vulkan and CPU output are byte-identical for all four fixtures with
`small` and for en, de, es with turbo; turbo zh differs by 20 ms on one
boundary (cue 2 end 10.020 vs 10.040 s).

### What the numbers say

- **`ggml-large-v3-turbo-q5_0` passes 18.5 for en, de, es**: similarity
  1.000 and every cue within 0.02 s of the MLX reference. The text and
  cue boundaries are the MLX output, apart from normalization-level
  differences.
- **turbo zh** passes the timestamp rule (worst 0.46 s, the last cue
  ends at 29.30 s vs 28.84 s) and scores 0.976 against
  `zh-30s.expected.srt`. whisper.cpp writes **Simplified** characters
  (照北京的老规矩, 春节, 腊月...). Note that `zh-30s.expected.srt` is itself
  Simplified apart from the first cue (按照北京的老規矩), which is what
  mlx_whisper produced; it is not a Traditional file. Against
  `zh-30s.truth.srt` (Traditional, owner-corrected) the raw score is
  0.690, which is the script difference; no conversion was attempted, as
  asked. The one real content error: turbo q5_0 drops the first character
  按 (照北京的老规矩 for 按照北京的老規矩). MLX turbo (f16) gets it, so this
  may be the q5_0 quantization; not tested further (the f16 GGUF is 1.6 GB,
  over the download limit).
- **`ggml-small` fails 18.5**: en/de/es text is near perfect (0.997 to
  1.000), but whisper.cpp small cuts the audio into different cues
  (seven short cues for en vs four), so the most-overlapping cue is off by
  up to 3.6 s. The boundary view shows de/es boundaries within 0.4 s but en
  and zh up to 2.3 s off. zh is poor: small writes **Traditional**
  characters, drops 按照, mishears 臘 as 蠟/辣 (辣氣辣巴, 辣巴粥), and
  repeats the last line (see Hallucination). Similarity 0.587 vs expected
  (mostly the script), 0.750 vs truth. Its first cue ends at 3.40 s where
  the speech runs to 5.28 s, so the zh cues are up to 2.3 s early.
- **Speed on CPU (4 threads, the default)**: small runs at about 0.9x
  realtime and turbo q5_0 at 0.28x to 0.69x. A 60 minute meeting would
  take roughly 70 minutes (small) or 1.5 to 3.5 hours (turbo) for the
  final pass. More threads help turbo (30 s window: 48.9 s at 4 threads,
  41.0 s at 8, 37.4 s at 12) but not enough.
- **Speed on Vulkan (Intel UHD)**: turbo q5_0 runs at 2.0x to 2.9x
  realtime on the fixtures and a warm 30 s window takes 8.8 s (3.4x),
  as fast as small on Vulkan (10.2 s). Thread count barely matters on
  Vulkan (8.8 to 10.4 s from 2 to 12 threads). The first inference
  after load is slower (12.6 s cold), then steady.
- Memory: 0.8 to 1.0 GB peak on CPU, 1.25 GB with Vulkan (the iGPU uses
  shared memory; `uma: 1`).

## Live preview feasibility (30 s chunks while recording)

| Model / runtime | Warm 30 s window | Share of real time | Verdict |
|---|---|---|---|
| turbo q5_0 / Vulkan | 8.8 s | ~30 % | feasible, with headroom for the mixer and the UI |
| small / Vulkan | 9.9 to 11.2 s | ~35 % | feasible, but no reason to prefer it over turbo on Vulkan |
| small / CPU | 26.5 s (8 threads) to 30.7 s (4 threads) | 88 to 100 % | not feasible: no headroom, and it starves the rest of the app |
| turbo q5_0 / CPU | 37.4 s (12 threads) to 48.9 s (4 threads) | 125 to 165 % | not feasible: falls behind |

## Whisper.net API findings (exact names)

From reflection over `Whisper.net.dll` 1.9.1 (`out/whisper-net-api.txt`,
full dump) and the tagged sources (`sandrohanea/whisper.net@1.9.1`,
`WhisperProcessor.cs`; whisper.cpp `src/whisper.cpp` at the submodule
commit).

**Language probabilities.**
- `WhisperProcessor.DetectLanguage(float[] samples)` returns `string?` (top language).
- `WhisperProcessor.DetectLanguageWithProbability(float[] samples)` returns
  `(string? language, float probability)`, the top language over all 99
  and its probability.
- `WhisperProcessor.DetectLanguageWithProbability(ReadOnlySpan<float> samples, params ReadOnlySpan<string> candidateLanguages)`
  returns the best of the candidates and its **raw** (not renormalized)
  probability.
- Internally each call runs `whisper_pcm_to_mel_with_state` and
  `whisper_lang_auto_detect_with_state(ctx, state, 0, threads, probs)`,
  which fills a `float[whisper_lang_max_id() + 1]` with the softmax over
  every language token (the same quantity as Python's `detect_language`).
  **Whisper.net throws that array away** and returns one entry. The only
  way to get the four probabilities through the public API is four calls
  with one candidate each (what this spike does): four encoder runs.
  One call costs about one encoder pass, measured for the unrestricted
  call: turbo q5_0 42 to 66 s on CPU (4 threads) and 6.8 to 8.0 s on
  Vulkan; small 12 to 18 s on CPU and 2.0 to 2.4 s on Vulkan.
- Detection always uses offset 0 of the samples passed (the first 30 s,
  zero-padded by `whisper_pcm_to_mel`). To detect on window N, pass that
  slice.

**No-speech probability.**
- Detection returns none.
- `SegmentData.NoSpeechProbability` (`float`, getter) exists and comes from
  `whisper_full_get_segment_no_speech_prob_from_state`. **It is not
  usable.** It measured 1.927e-5 to 1.929e-5 on every segment of every run,
  which is 1/51,866, that is 1/n_vocab: a uniform softmax. whisper.cpp
  computes it from `state->logits` right after the prompt decode, but the
  row it reads holds no real logits for the sot position. Consequence:
  whisper.cpp's own no-speech skip (`no_speech_prob > no_speech_thold &&
  avg_logprob < logprob_thold`) can never fire at the default 0.6.
- Workaround proven in this spike (`NativeDetect.cs`, `--native-detect`):
  call the C API of the same `whisper.dll` that Whisper.net ships:
  `whisper_init_from_file`, `whisper_init_state`,
  `whisper_pcm_to_mel_with_state`, `whisper_lang_auto_detect_with_state`,
  then `whisper_get_logits_from_state` (the logits of the single-`sot`
  decode the detection just ran), `whisper_token_nosp`, `whisper_n_vocab`.
  One encoder run gives **all language probabilities plus the no-speech
  probability**, exactly what `LanguageDetection.swift` uses. Its
  per-language values agree with Whisper.net's to within 1.7e-4 on speech
  (8e-3 on silence). Cost: a second `whisper_context`, so a second model
  load (~0.6 GB). A W5 implementation could avoid that by owning the
  context, that is, a thin P/Invoke layer instead of `WhisperFactory`
  for detection.

**Token probabilities.**
- `SegmentData.Tokens` is `WhisperToken[]`, with public fields `Id`,
  `TimestampId`, `Probability`, `ProbabilityLog`, `TimestampProbability`,
  `TimestampProbabilitySum`, `Start`, `End` (both `long`, centiseconds),
  `DtwTimestamp`, `VoiceLen`, `Text`.
- `SegmentData.Probability`, `MinProbability` and `MaxProbability` are
  filled only with `WhisperProcessorBuilder.WithProbabilities()`.
- `SegmentData.Language` is the language whisper_full used for that run.
- There is no average logprob, no compression ratio, and no per-segment
  temperature.

**Builder options** (`WhisperProcessorBuilder`, all return the builder):

| Need | Exists | Name / note |
|---|---|---|
| no-speech threshold | yes | `WithNoSpeechThreshold(float)`, marked [EXPERIMENTAL], default 0.6 (ineffective, see above) |
| entropy threshold | yes | `WithEntropyThreshold(float)`, default 2.4. whisper.cpp uses token entropy where Python and the Mac use `compression_ratio_threshold` 2.4 (gzip ratio); same number, different test |
| logprob threshold | yes | `WithLogProbThreshold(float)`, default -1.0 |
| temperature / fallback | yes | `WithTemperature(float)` (whisper.cpp default 0.0; the XML doc's "0.2" is wrong), `WithTemperatureInc(float)` (default 0.2, gives 0, 0.2, ... 1.0 like Python). Greedy `best_of` via `WithGreedySamplingStrategy(b => b.WithBestOf(n))`, default 5; beam via `WithBeamSearchSamplingStrategy(b => b.WithBeamSize(n).WithPatience(p))` |
| condition on previous text | only "off" | `WithNoContext()` sets `no_context = true`, and there is no way to set it false. whisper.cpp's default is already `no_context = true`, which matches the Mac's `condition_on_previous_text False`. The XML doc saying past text is used when unset is wrong for this whisper.cpp. `WithMaxLastTextTokens(int)` caps it |
| max segment length | yes | `WithMaxSegmentLength(int)` (characters; needs token timestamps), `SplitOnWord()`, `WithMaxTokensPerSegment(int)` |
| single segment | yes | `WithSingleSegment()` |
| timestamps / timestamp-token rules | partial | `WithTokenTimestamps()`, `WithTokenTimestampsThreshold(float)` (0.01), `WithTokenTimestampsSumThreshold(float)` (0.01), `WithMaxInitialTs(float)` (1.0, Python's `max_initial_timestamp`), `WithPrintTimestamps(bool)`. The timestamp-token logit rules are built in and not configurable; no public `no_timestamps`; DTW via `WhisperFactoryOptions.UseDtwTimeStamps` / `HeadsPreset` (`WhisperAlignmentHeadsPreset.LargeV3Turbo` exists) |
| prompt | yes | `WithPrompt(string)`, `WithCarryInitialPrompt(bool)` |
| other | | `WithLanguage(string)` ("auto" allowed), `WithLanguageDetection()`, `WithThreads(int)` (default min(4, cores)), `WithOffset/WithDuration(TimeSpan)`, `WithAudioContextSize(int)`, `WithSuppressRegex(string)`, `WithoutSuppressBlank()`, `WithLengthPenalty(float)`, `WithTranslate()`, handlers `WithSegmentEventHandler`, `WithProgressHandler`, `WithEncoderBeginHandler`, `WithOpenVinoEncoder(...)` |
| hallucination_silence_threshold | no | not in whisper.cpp |
| VAD | separate | `WhisperVadFactory.FromPath(...)` / `WhisperVadProcessorBuilder` (`WithThreshold`, `WithMinSpeechDuration`, `WithMinSilenceDuration`, `WithSpeechPadding`...) → `WhisperVadProcessor.DetectSpeech(float[])` returns `VadSegmentData(Start, End)`; needs a Silero GGML model (`WhisperGgmlDownloader.GetGgmlSileroVadModelAsync`), not downloaded here |

Factory and runtime: `WhisperFactory.FromPath(string, WhisperFactoryOptions)`
with `UseGpu` (default true), `GpuDevice`, `UseFlashAttention`,
`DelayInitialization`; `WhisperFactory.GetRuntimeInfo()`,
`WhisperFactory.GetSupportedLanguages()`;
`RuntimeOptions.RuntimeLibraryOrder` (`List<RuntimeLibrary>`, default
Cuda, Cuda12, Vulkan, CoreML, OpenVino, Cpu, CpuNoAvx),
`RuntimeOptions.LoadedLibrary`, `RuntimeOptions.LibraryPath`; logging
through `LogProvider.AddLogger(Action<WhisperLogLevel, string>)`.

## Language detection

Whisper.net path (`DetectLanguageWithProbability`, one call per candidate), renormalized over en/zh/de/es; "top (all 99)" is the unrestricted call; "one call" is its wall time (each candidate call costs the same). Single window (every fixture is under 30 s).

| run | fixture | en | zh | de | es | top of 4 | top (all 99) | one call (s) |
|---|---|---|---|---|---|---|---|---|
| ggml-small-cpu | en | 0.9997 | 0.0001 | 0.0000 | 0.0002 | en 0.9997 | en 0.9969 | 12.95 |
| ggml-small-cpu | de | 0.0037 | 0.0001 | 0.9957 | 0.0005 | de 0.9957 | de 0.9944 | 16.18 |
| ggml-small-cpu | es | 0.0015 | 0.0000 | 0.0000 | 0.9984 | es 0.9984 | es 0.9890 | 12.32 |
| ggml-small-cpu | zh | 0.0000 | 1.0000 | 0.0000 | 0.0000 | zh 1.0000 | zh 0.9998 | 17.89 |
| ggml-small-vulkan | en | 0.9997 | 0.0001 | 0.0000 | 0.0002 | en 0.9997 | en 0.9970 | 2.43 |
| ggml-small-vulkan | de | 0.0037 | 0.0001 | 0.9957 | 0.0005 | de 0.9957 | de 0.9944 | 2.02 |
| ggml-small-vulkan | es | 0.0016 | 0.0000 | 0.0000 | 0.9984 | es 0.9984 | es 0.9891 | 1.99 |
| ggml-small-vulkan | zh | 0.0000 | 0.9999 | 0.0000 | 0.0000 | zh 0.9999 | zh 0.9998 | 1.97 |
| ggml-large-v3-turbo-q5_0-cpu | en | 0.9999 | 0.0000 | 0.0000 | 0.0000 | en 0.9999 | en 0.9999 | 65.91 |
| ggml-large-v3-turbo-q5_0-cpu | de | 0.0003 | 0.0000 | 0.9996 | 0.0000 | de 0.9996 | de 0.9994 | 60.50 |
| ggml-large-v3-turbo-q5_0-cpu | es | 0.0002 | 0.0000 | 0.0000 | 0.9998 | es 0.9998 | es 0.9995 | 46.75 |
| ggml-large-v3-turbo-q5_0-cpu | zh | 0.0008 | 0.9991 | 0.0000 | 0.0000 | zh 0.9991 | zh 0.9985 | 41.72 |
| ggml-large-v3-turbo-q5_0-vulkan | en | 0.9999 | 0.0000 | 0.0000 | 0.0000 | en 0.9999 | en 0.9998 | 7.10 |
| ggml-large-v3-turbo-q5_0-vulkan | de | 0.0003 | 0.0000 | 0.9996 | 0.0000 | de 0.9996 | de 0.9995 | 8.03 |
| ggml-large-v3-turbo-q5_0-vulkan | es | 0.0002 | 0.0000 | 0.0000 | 0.9998 | es 0.9998 | es 0.9995 | 6.77 |
| ggml-large-v3-turbo-q5_0-vulkan | zh | 0.0008 | 0.9991 | 0.0000 | 0.0000 | zh 0.9991 | zh 0.9985 | 6.80 |

whisper.cpp C API path (`--native-detect`, NativeDetect.cs), ggml-large-v3-turbo-q5_0-vulkan. Probabilities renormalized over the four; no-speech is softmax(sot logits)[<|nospeech|>]; last columns cross-check against Whisper.net's raw per-candidate values.

| input | en | zh | de | es | top (all 99) | no-speech | one native call (s) | Whisper.net en/zh/de/es raw | max abs diff |
|---|---|---|---|---|---|---|---|---|---|
| en-30s | 0.9999 | 0.0000 | 0.0000 | 0.0000 | en 0.9998 | 9.7198E-13 | 8.73 | 0.9998 / 0.0000 / 0.0000 / 0.0000 | 1.2E-5 |
| zh-30s | 0.0009 | 0.9990 | 0.0000 | 0.0001 | zh 0.9985 | 1.4596E-11 | 6.44 | 0.0008 / 0.9985 / 0.0000 / 0.0000 | 1.4E-4 |
| de-30s | 0.0003 | 0.0000 | 0.9996 | 0.0000 | de 0.9995 | 6.4966E-12 | 5.29 | 0.0003 / 0.0000 / 0.9995 / 0.0000 | 3.1E-5 |
| es-30s | 0.0002 | 0.0000 | 0.0000 | 0.9998 | es 0.9995 | 3.7515E-12 | 4.81 | 0.0002 / 0.0000 / 0.0000 / 0.9995 | 2.4E-5 |
| silence-30s | 0.7740 | 0.0277 | 0.0809 | 0.1174 | en 0.3685 | 1.678E-10 | 4.00 | 0.3685 / 0.0131 / 0.0396 / 0.0531 | 8.3E-3 |

whisper.cpp C API path (`--native-detect`, NativeDetect.cs), ggml-large-v3-turbo-q5_0-cpu. Probabilities renormalized over the four; no-speech is softmax(sot logits)[<|nospeech|>]; last columns cross-check against Whisper.net's raw per-candidate values.

| input | en | zh | de | es | top (all 99) | no-speech | one native call (s) | Whisper.net en/zh/de/es raw | max abs diff |
|---|---|---|---|---|---|---|---|---|---|
| en-30s | 0.9999 | 0.0000 | 0.0000 | 0.0000 | en 0.9999 | 1.0039E-12 | 39.41 | 0.9999 / 0.0000 / 0.0000 / 0.0000 | 1.6E-5 |
| zh-30s | 0.0009 | 0.9990 | 0.0000 | 0.0000 | zh 0.9985 | 1.4397E-11 | 41.92 | 0.0008 / 0.9985 / 0.0000 / 0.0000 | 1.7E-4 |
| de-30s | 0.0004 | 0.0000 | 0.9996 | 0.0000 | de 0.9994 | 5.885E-12 | 41.82 | 0.0003 / 0.0000 / 0.9994 / 0.0000 | 1.4E-5 |
| es-30s | 0.0002 | 0.0000 | 0.0000 | 0.9998 | es 0.9995 | 3.586E-12 | 42.74 | 0.0002 / 0.0000 / 0.0000 / 0.9995 | 5.2E-5 |
| silence-30s | 0.7845 | 0.0268 | 0.0774 | 0.1114 | en 0.3889 | 1.5849E-10 | 39.42 | 0.3889 / 0.0126 / 0.0383 / 0.0514 | 4.1E-3 |


Every fixture picks the right language with confidence above 0.99 after
renormalization over the four, on both models and both runtimes, which
meets 18.5's "above 0.9". Turbo q5_0's top-over-all values (en 0.9998,
zh 0.9985, de 0.9995, es 0.9995) are within 0.001 of the Python references
in `IntegrationTests.pythonDetection` (0.999725, 0.997967, 0.999425,
0.999430). On 30 s of digital silence the four-way distribution is
en 0.77 to 0.78 / es 0.11 to 0.12 / de 0.08 / zh 0.03 (Vulkan, CPU), and the no-speech probability is
1.6e-10 to 1.7e-10. That matches the Mac's note that turbo's no-speech does not flag
silence, so the Mac's RMS gate (0.001) must be ported as well.

## Hallucination

On the fixtures as they are (speech to within a second of the end):
- turbo q5_0, CPU and Vulkan: none. No repeated n-grams, no duplicate
  cues, no invented text in en, de, es or zh.
- small: **zh repeats its last line**. Cue 8 (26.0 to 29.0 s,
  "而是小型的農業展覽會") repeats the end of cue 7 in a second decoding
  window. en, de, es are clean.

The Mac side, checked for this row: `TranscriptionOptions.swift` keeps
`hallucinationSilenceThreshold` 2.0 only for parity. In mlx_whisper 0.4.3
every use of it sits inside `if word_timestamps:`, and word timestamps are
off, so **it has no effect on the Mac or in the Python CLI**. There is
nothing to port for the 18.8 "Hallucination filter" row as written. What
does differ is the no-speech skip: the Mac skips a window when no-speech
is above 0.6 and avg logprob is below -1.0. whisper.cpp has the same rule,
but it never fires because of the broken no-speech value above.

Extra check (`--pad 45`, turbo q5_0 on Vulkan): each fixture with 45 s of
digital silence appended. whisper.cpp **invents text in every silent
window**: "Thank you." ×2 (en), "Vielen Dank." ×2 (de), "Gracias." ×2
(es), and for zh the well-known subtitle-credit hallucinations
"优优独播剧场——YoYo Television Series Exclusive" and "欢迎订阅我的频道".
The invented cues run to 1:29.98, past the end of the 64 to 74 s audio. The
speech part is unchanged (timestamps still pass). Whether the Mac produces
the same on this input is not known here (with turbo the no-speech value
is near zero on silence, so its skip may not fire either); it is worth
running the same padded WAV through `HEARSAY_TRANSCRIBE_FILE` on the Mac.
Real meetings have long pauses and the live preview gets silent chunks, so
Windows needs a gate before or after whisper_full: the RMS window gate and
the native no-speech value (both already Mac rules), or Silero VAD through
`WhisperVadProcessor`, or dropping segments whose tokens fall past the
audio end.

#### ggml-large-v3-turbo-q5_0 / vulkan / en + 45 s silence (63.9 s)

```text
1
00:00:00,000 --> 00:00:04,480
Welcome to the Hearsay Planning Meeting. Today we will review the model download flow,

2
00:00:04,780 --> 00:00:10,700
the live transcript preview, and the system audio capture. The first action item is to install Xcode.

3
00:00:10,980 --> 00:00:15,620
The second action item is to record a 60-minute test call and check for drift between the

4
00:00:15,620 --> 00:00:18,440
microphone and the system audio. Thank you everyone.

5
00:00:30,000 --> 00:00:59,980
Thank you.

6
00:01:00,000 --> 00:01:29,980
Thank you.

```

#### ggml-large-v3-turbo-q5_0 / vulkan / de + 45 s silence (67.0 s)

```text
1
00:00:00,000 --> 00:00:03,880
Guten Morgen zusammen, lasst uns mit dem Stand des Projekts beginnen.

2
00:00:04,280 --> 00:00:10,600
Das neue Release ist für nächsten Donnerstag geplant, aber wir haben noch zwei offene Fehler im Anmeldebereich.

3
00:00:10,980 --> 00:00:14,100
Markus, kannst du bis Mittwoch eine Lösung vorschlagen?

4
00:00:14,500 --> 00:00:17,820
Außerdem müssen wir das Budget für das dritte Quartal besprechen.

5
00:00:18,220 --> 00:00:21,680
Ich schicke euch nach dem Meeting eine Zusammenfassung per E-Mail.

6
00:00:30,000 --> 00:00:59,980
Vielen Dank.

7
00:01:00,000 --> 00:01:29,980
Vielen Dank.

```

#### ggml-large-v3-turbo-q5_0 / vulkan / es + 45 s silence (66.6 s)

```text
1
00:00:00,000 --> 00:00:10,580
Buenos días a todos. Empecemos con el estado del proyecto. La nueva versión está prevista para el próximo jueves, pero todavía tenemos dos errores abiertos en la pantalla de inicio de sesión.

2
00:00:11,040 --> 00:00:21,120
Lucía, ¿puedes proponer una solución antes del miércoles? Además, tenemos que revisar el presupuesto del tercer trimestre. Os enviaré un resumen por correo después de la reunión.

3
00:00:30,000 --> 00:00:31,000
Gracias.

4
00:01:00,000 --> 00:01:01,000
Gracias.

```

#### ggml-large-v3-turbo-q5_0 / vulkan / zh + 45 s silence (74.4 s)

```text
1
00:00:00,000 --> 00:00:05,280
照北京的老规矩

2
00:00:05,280 --> 00:00:10,040
春节差不多在腊月的初旬就开始了

3
00:00:10,040 --> 00:00:12,600
腊漆腊八 冻死寒鸭

4
00:00:12,600 --> 00:00:15,480
这是一年里最冷的时候

5
00:00:15,480 --> 00:00:19,660
在腊八这天 家家都熬腊八粥

6
00:00:19,660 --> 00:00:24,740
粥是用各种米 各种豆 与各种干果熬成的

7
00:00:24,740 --> 00:00:29,300
这不是粥 而是小型的农业展览会

8
00:00:30,000 --> 00:00:59,980
优优独播剧场——YoYo Television Series Exclusive

9
00:01:00,000 --> 00:01:29,980
欢迎订阅我的频道

```


## Recommendation

- **Default model on this machine class: `ggml-large-v3-turbo-q5_0`
  (574 MB) with the Vulkan runtime.** It is the only one of the two that
  meets 18.5 (en, de, es exact; zh 0.976 raw with one dropped character,
  pending script conversion), and on an Intel UHD iGPU it is as fast as
  `small` (about 3x realtime). Ship `Whisper.net.Runtime` and
  `Whisper.net.Runtime.Vulkan`, keep Whisper.net's default order (Vulkan
  before Cpu), and log `RuntimeOptions.LoadedLibrary`.
- **CPU-only (no working Vulkan device):** keep turbo q5_0 for the final
  pass, because `small` fails the timestamp rule on all four fixtures and
  is poor on zh. Say that the final pass takes about 1.5 to 3.5x the
  recording length. Use `WithThreads` above the default 4 (8 to 12 gave
  about 20 to 25 % on turbo). This replaces PLAN 18.4's "default to a
  smaller quantized model on CPU-only machines": the measured smaller
  model is not acceptable. A `medium` or q8 turbo was not measured (both
  over 500 MB, not approved).
- **Live preview:** feasible on Vulkan (a 30 s chunk in about 9 s). Not
  feasible on CPU with either model; disable it below a measured speed
  (18.7's mitigation), for example when a warm 30 s window takes more
  than about 15 s.
- **Language detection:** use the whisper.cpp C API path
  (`NativeDetect.cs` pattern) rather than `DetectLanguageWithProbability`:
  one encoder run instead of four, and it gives the no-speech probability
  the shared language-decision rules need. Port the Mac's RMS gate with it.
- **Before W5:** add a silence gate (see Hallucination) and decide
  whether zh on Windows needs a leading-character check (the dropped 按).

## Not measured

- CUDA: no NVIDIA GPU here; the CUDA runtime was not installed or run.
- The f16 `large-v3-turbo` GGUF (1.6 GB), q8_0, `medium`, `base` and
  `tiny`: over the download limit or not approved. Whether q5_0 causes the
  dropped 按 is open.
- Chinese script conversion (OpenCC vs ICU): out of scope here, and the zh
  numbers above are raw.
- The Mac on the silence-padded input, and Silero VAD (model not
  downloaded).
- Fixture wall times with more than 4 threads: only the 30 s window was
  swept.
- Multi-window detection (the Mac averages up to three speech windows):
  every fixture is under 30 s, so one window.
- `--native-detect` on CPU: measured; table above.

## Raw SRT output (verbatim)

### ggml-small-cpu / en

```text
1
00:00:00,000 --> 00:00:02,200
Welcome to the hearsay planning meeting.

2
00:00:02,200 --> 00:00:07,120
Today we will review the model download flow, the live transcript preview, and the system

3
00:00:07,120 --> 00:00:08,440
audio capture.

4
00:00:08,440 --> 00:00:11,040
The first action item is to install Xcode.

5
00:00:11,040 --> 00:00:15,440
The second action item is to record a 60 minute test call and check for drift between

6
00:00:15,440 --> 00:00:17,680
the microphone and the system audio.

7
00:00:17,680 --> 00:00:18,720
Thank you everyone.

```

### ggml-small-cpu / de

```text
1
00:00:00,000 --> 00:00:01,480
Guten Morgen zusammen.

2
00:00:01,480 --> 00:00:04,120
Lasst uns mit dem Stand des Projekts beginnen.

3
00:00:04,120 --> 00:00:09,440
Das neue Release ist für nächsten Donnerstag geplant, aber wir haben noch zwei offene Fehler

4
00:00:09,440 --> 00:00:10,960
im Anmeldebereich.

5
00:00:10,960 --> 00:00:14,480
Markus, kannst du bis Mittwoch eine Lösung vorschlagen?

6
00:00:14,480 --> 00:00:18,160
Außerdem müssen wir das Budget für das dritte Quartal besprechen.

7
00:00:18,160 --> 00:00:21,760
Ich schicke euch nach dem Meeting eine Zusammenfassung per E-Mail.

```

### ggml-small-cpu / es

```text
1
00:00:00,000 --> 00:00:03,480
Buenos días a todos. Empecemos con el estado del proyecto.

2
00:00:03,480 --> 00:00:10,780
La nueva versión está prevista para el próximo jueves, pero todavía tenemos dos errores abiertos en la pantalla de inicio de sesión.

3
00:00:10,780 --> 00:00:14,340
Lucía, ¿puedes proponer una solución antes del miércoles?

4
00:00:14,340 --> 00:00:18,300
Además, tenemos que revisar el presupuesto del tercer trimestre.

5
00:00:18,300 --> 00:00:21,300
Os enviaré un resumen por correo después de la reunión.

```

### ggml-small-cpu / zh

```text
1
00:00:00,000 --> 00:00:03,400
北京的老規矩

2
00:00:03,400 --> 00:00:07,000
春節差不多在蠟月的初旬就開始了

3
00:00:07,000 --> 00:00:10,000
辣氣辣巴 凍死寒鴨

4
00:00:10,000 --> 00:00:13,000
這是一年裡最冷的時候

5
00:00:13,000 --> 00:00:17,000
在辣巴這天 家家都熬辣巴粥

6
00:00:17,000 --> 00:00:22,000
粥是用各種米 各種豆 與各種干果熬成的

7
00:00:22,000 --> 00:00:26,000
這不是粥 而是小型的農業展覽會

8
00:00:26,000 --> 00:00:29,000
而是小型的農業展覽會

```

### ggml-small-vulkan / en

```text
1
00:00:00,000 --> 00:00:02,200
Welcome to the hearsay planning meeting.

2
00:00:02,200 --> 00:00:07,120
Today we will review the model download flow, the live transcript preview, and the system

3
00:00:07,120 --> 00:00:08,440
audio capture.

4
00:00:08,440 --> 00:00:11,040
The first action item is to install Xcode.

5
00:00:11,040 --> 00:00:15,440
The second action item is to record a 60 minute test call and check for drift between

6
00:00:15,440 --> 00:00:17,680
the microphone and the system audio.

7
00:00:17,680 --> 00:00:18,720
Thank you everyone.

```

### ggml-small-vulkan / de

```text
1
00:00:00,000 --> 00:00:01,480
Guten Morgen zusammen.

2
00:00:01,480 --> 00:00:04,120
Lasst uns mit dem Stand des Projekts beginnen.

3
00:00:04,120 --> 00:00:09,440
Das neue Release ist für nächsten Donnerstag geplant, aber wir haben noch zwei offene Fehler

4
00:00:09,440 --> 00:00:10,960
im Anmeldebereich.

5
00:00:10,960 --> 00:00:14,480
Markus, kannst du bis Mittwoch eine Lösung vorschlagen?

6
00:00:14,480 --> 00:00:18,160
Außerdem müssen wir das Budget für das dritte Quartal besprechen.

7
00:00:18,160 --> 00:00:21,760
Ich schicke euch nach dem Meeting eine Zusammenfassung per E-Mail.

```

### ggml-small-vulkan / es

```text
1
00:00:00,000 --> 00:00:03,480
Buenos días a todos. Empecemos con el estado del proyecto.

2
00:00:03,480 --> 00:00:10,780
La nueva versión está prevista para el próximo jueves, pero todavía tenemos dos errores abiertos en la pantalla de inicio de sesión.

3
00:00:10,780 --> 00:00:14,340
Lucía, ¿puedes proponer una solución antes del miércoles?

4
00:00:14,340 --> 00:00:18,300
Además, tenemos que revisar el presupuesto del tercer trimestre.

5
00:00:18,300 --> 00:00:21,300
Os enviaré un resumen por correo después de la reunión.

```

### ggml-small-vulkan / zh

```text
1
00:00:00,000 --> 00:00:03,400
北京的老規矩

2
00:00:03,400 --> 00:00:07,000
春節差不多在蠟月的初旬就開始了

3
00:00:07,000 --> 00:00:10,000
辣氣辣巴 凍死寒鴨

4
00:00:10,000 --> 00:00:13,000
這是一年裡最冷的時候

5
00:00:13,000 --> 00:00:17,000
在辣巴這天 家家都熬辣巴粥

6
00:00:17,000 --> 00:00:22,000
粥是用各種米 各種豆 與各種干果熬成的

7
00:00:22,000 --> 00:00:26,000
這不是粥 而是小型的農業展覽會

8
00:00:26,000 --> 00:00:29,000
而是小型的農業展覽會

```

### ggml-large-v3-turbo-q5_0-cpu / en

```text
1
00:00:00,000 --> 00:00:04,480
Welcome to the Hearsay Planning Meeting. Today we will review the model download flow,

2
00:00:04,780 --> 00:00:10,700
the live transcript preview, and the system audio capture. The first action item is to install Xcode.

3
00:00:10,980 --> 00:00:15,620
The second action item is to record a 60-minute test call and check for drift between the

4
00:00:15,620 --> 00:00:18,440
microphone and the system audio. Thank you everyone.

```

### ggml-large-v3-turbo-q5_0-cpu / de

```text
1
00:00:00,000 --> 00:00:03,880
Guten Morgen zusammen, lasst uns mit dem Stand des Projekts beginnen.

2
00:00:04,280 --> 00:00:10,600
Das neue Release ist für nächsten Donnerstag geplant, aber wir haben noch zwei offene Fehler im Anmeldebereich.

3
00:00:10,980 --> 00:00:14,100
Markus, kannst du bis Mittwoch eine Lösung vorschlagen?

4
00:00:14,500 --> 00:00:17,820
Außerdem müssen wir das Budget für das dritte Quartal besprechen.

5
00:00:18,220 --> 00:00:21,680
Ich schicke euch nach dem Meeting eine Zusammenfassung per E-Mail.

```

### ggml-large-v3-turbo-q5_0-cpu / es

```text
1
00:00:00,000 --> 00:00:10,580
Buenos días a todos. Empecemos con el estado del proyecto. La nueva versión está prevista para el próximo jueves, pero todavía tenemos dos errores abiertos en la pantalla de inicio de sesión.

2
00:00:11,040 --> 00:00:21,120
Lucía, ¿puedes proponer una solución antes del miércoles? Además, tenemos que revisar el presupuesto del tercer trimestre. Os enviaré un resumen por correo después de la reunión.

```

### ggml-large-v3-turbo-q5_0-cpu / zh

```text
1
00:00:00,000 --> 00:00:05,280
照北京的老规矩

2
00:00:05,280 --> 00:00:10,020
春节差不多在腊月的初旬就开始了

3
00:00:10,020 --> 00:00:12,600
腊漆腊八 冻死寒鸭

4
00:00:12,600 --> 00:00:15,480
这是一年里最冷的时候

5
00:00:15,480 --> 00:00:19,660
在腊八这天 家家都熬腊八粥

6
00:00:19,660 --> 00:00:24,740
粥是用各种米 各种豆 与各种干果熬成的

7
00:00:24,740 --> 00:00:29,300
这不是粥 而是小型的农业展览会

```

### ggml-large-v3-turbo-q5_0-vulkan / en

```text
1
00:00:00,000 --> 00:00:04,480
Welcome to the Hearsay Planning Meeting. Today we will review the model download flow,

2
00:00:04,780 --> 00:00:10,700
the live transcript preview, and the system audio capture. The first action item is to install Xcode.

3
00:00:10,980 --> 00:00:15,620
The second action item is to record a 60-minute test call and check for drift between the

4
00:00:15,620 --> 00:00:18,440
microphone and the system audio. Thank you everyone.

```

### ggml-large-v3-turbo-q5_0-vulkan / de

```text
1
00:00:00,000 --> 00:00:03,880
Guten Morgen zusammen, lasst uns mit dem Stand des Projekts beginnen.

2
00:00:04,280 --> 00:00:10,600
Das neue Release ist für nächsten Donnerstag geplant, aber wir haben noch zwei offene Fehler im Anmeldebereich.

3
00:00:10,980 --> 00:00:14,100
Markus, kannst du bis Mittwoch eine Lösung vorschlagen?

4
00:00:14,500 --> 00:00:17,820
Außerdem müssen wir das Budget für das dritte Quartal besprechen.

5
00:00:18,220 --> 00:00:21,680
Ich schicke euch nach dem Meeting eine Zusammenfassung per E-Mail.

```

### ggml-large-v3-turbo-q5_0-vulkan / es

```text
1
00:00:00,000 --> 00:00:10,580
Buenos días a todos. Empecemos con el estado del proyecto. La nueva versión está prevista para el próximo jueves, pero todavía tenemos dos errores abiertos en la pantalla de inicio de sesión.

2
00:00:11,040 --> 00:00:21,120
Lucía, ¿puedes proponer una solución antes del miércoles? Además, tenemos que revisar el presupuesto del tercer trimestre. Os enviaré un resumen por correo después de la reunión.

```

### ggml-large-v3-turbo-q5_0-vulkan / zh

```text
1
00:00:00,000 --> 00:00:05,280
照北京的老规矩

2
00:00:05,280 --> 00:00:10,040
春节差不多在腊月的初旬就开始了

3
00:00:10,040 --> 00:00:12,600
腊漆腊八 冻死寒鸭

4
00:00:12,600 --> 00:00:15,480
这是一年里最冷的时候

5
00:00:15,480 --> 00:00:19,660
在腊八这天 家家都熬腊八粥

6
00:00:19,660 --> 00:00:24,740
粥是用各种米 各种豆 与各种干果熬成的

7
00:00:24,740 --> 00:00:29,300
这不是粥 而是小型的农业展览会

```


