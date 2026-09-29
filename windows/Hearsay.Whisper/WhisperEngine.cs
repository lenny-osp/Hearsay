using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hearsay.Core.Transcription;
using Hearsay.Whisper.Native;

namespace Hearsay.Whisper;

/// <summary>
/// Owns at most one loaded Whisper model (PLAN.md section 4) and ONE
/// whisper.cpp context for it, used for both language detection and
/// transcription. Every job takes the engine's lock, so jobs are serialized;
/// the model stays loaded between jobs and is released after
/// <see cref="IdleUnloadSeconds"/> without work (PLAN.md section 12, memory).
/// Port of mac/Hearsay/Features/Transcription/WhisperEngine.swift, with the
/// public surface of <c>Transcriber</c> it consumes
/// (mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/Transcriber.swift and
/// LanguageDetection.swift); the MLX decoder itself is replaced by whisper.cpp.
/// </summary>
/// <remarks>
/// <para><b>Design (W5 option b): the whisper.cpp C API is driven directly.</b>
/// Whisper.net 1.9.1 keeps its context handle private
/// (<c>WhisperProcessor</c> exposes no <c>whisper_context*</c>), so its
/// processor cannot share a context with a P/Invoke detector: the W1 spike
/// had to load the model twice (about 0.6 GB more). Whisper.net's detection
/// also returns one language per encoder run and its
/// <c>SegmentData.NoSpeechProbability</c> is a uniform 1/n_vocab
/// (windows/Spike/WhisperSpike/REPORT.md), so detection needs the C API
/// anyway. Driving transcription through the same API costs eleven more
/// exports (<see cref="WhisperNative.ExportNames"/>, twenty-one in all).
/// Whisper.net stays in the build only to pick and load the native runtime
/// (<see cref="WhisperRuntime"/>).</para>
/// <para><b>Silence gate</b> (PLAN.md 18.4): silent 30 s windows are not
/// transcribed and silent cues are dropped (<see cref="SilenceGate"/>).
/// Because of it, automatic language resolution inside
/// <see cref="Transcribe(ReadOnlyMemory{float}, TranscriptionOptions, IProgress{double}?, CancellationToken)"/>
/// detects on the first transcribed window rather than on the first 30 s.</para>
/// <para><b>Cancellation</b> is checked before every speech run and, through
/// whisper.cpp's encoder-begin callback, before every 30 s decoding window
/// (the Mac checks before every window); the CPU backend also polls it
/// during a window through the abort callback. Either way the call throws
/// <see cref="TranscriptionCancelledException"/> with the segments finished so far.</para>
/// </remarks>
public sealed unsafe class WhisperEngine : IDisposable
{
    /// <summary>Seconds without a job before the model is released (the Mac's 600).</summary>
    public const double IdleUnloadSeconds = 600;

    /// <summary>A warm 30 s window slower than this turns live preview off (PLAN.md 18.4, "Speed").</summary>
    public const double LivePreviewMaxWindowSeconds = 15;

    private readonly Lock gate = new();
    private readonly Action<string>? log;
    private readonly TimeSpan idleUnload;
    private readonly Timer idleTimer;
    private int activeJobs;
    private bool disposed;

    // Guarded by gate.
    private WhisperRuntime.LoadedRuntime? runtime;
    private IntPtr context;
    private IntPtr state;
    private string? modelPath;
    private string[] languageCodes = [];
    private bool multilingual;
    private bool warm;

    /// <param name="log">Receives one line per load and per probe (runtime, timings); called on the job's thread.</param>
    /// <param name="idleUnload">Time without a job before the model is released; default <see cref="IdleUnloadSeconds"/>.</param>
    public WhisperEngine(Action<string>? log = null, TimeSpan? idleUnload = null)
    {
        this.log = log;
        this.idleUnload = idleUnload ?? TimeSpan.FromSeconds(IdleUnloadSeconds);
        idleTimer = new Timer(_ => UnloadIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>A model is loaded.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (gate) return context != IntPtr.Zero;
        }
    }

    /// <summary>A job is waiting or running (the update install waits for it, PLAN.md 4.6).</summary>
    public bool IsBusy => Volatile.Read(ref activeJobs) > 0;

    /// <summary>The loaded model file, or null.</summary>
    public string? ModelPath
    {
        get
        {
            lock (gate) return modelPath;
        }
    }

    /// <summary>
    /// Threads a job uses when its options leave them unset:
    /// <see cref="TranscriptionOptions.DefaultThreads"/> for the loaded runtime.
    /// </summary>
    public static int DefaultThreads =>
        TranscriptionOptions.DefaultThreads(WhisperRuntime.IsCpu, Environment.ProcessorCount);

    /// <summary>Loads <paramref name="path"/> (a whisper.cpp GGML/GGUF file) unless it is already loaded.</summary>
    /// <exception cref="WhisperEngineException">
    /// <see cref="WhisperEngineError.LoadFailed"/>: the file does not exist
    /// (inner <see cref="FileNotFoundException"/>), no whisper.cpp runtime
    /// could be loaded (<see cref="PlatformNotSupportedException"/>), or
    /// whisper.cpp could not read the model (<see cref="InvalidOperationException"/>).
    /// </exception>
    public void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        BeginJob();
        try
        {
            lock (gate)
            {
                LoadLocked(path);
            }
        }
        finally
        {
            EndJob();
        }
    }

    /// <summary>Releases the model now.</summary>
    public void Unload()
    {
        lock (gate)
        {
            idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            FreeLocked();
        }
    }

    /// <summary>
    /// Transcribes 16 kHz mono samples with the loaded model.
    /// <paramref name="progress"/> receives the fraction of the audio done,
    /// 0...1, on the calling thread. Cancelling <paramref name="cancellationToken"/>
    /// stops the pass before the next window and throws
    /// <see cref="TranscriptionCancelledException"/>.
    /// </summary>
    /// <exception cref="WhisperEngineException">No model is loaded.</exception>
    /// <exception cref="ArgumentException">The language is not one the model knows, or the temperatures cannot be expressed.</exception>
    public WhisperTranscription Transcribe(
        ReadOnlyMemory<float> samples,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        BeginJob();
        try
        {
            lock (gate)
            {
                RequireLoaded();
                return TranscribeLocked(samples, options, progress, cancellationToken);
            }
        }
        finally
        {
            EndJob();
        }
    }

    /// <summary>Loads <paramref name="path"/> if needed, then transcribes; the load and the job count as one job.</summary>
    public WhisperTranscription Transcribe(
        string path,
        ReadOnlyMemory<float> samples,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        BeginJob();
        try
        {
            lock (gate)
            {
                LoadLocked(path);
                return TranscribeLocked(samples, options, progress, cancellationToken);
            }
        }
        finally
        {
            EndJob();
        }
    }

    /// <summary><see cref="Transcribe(string, ReadOnlyMemory{float}, TranscriptionOptions, IProgress{double}?, CancellationToken)"/> on a thread-pool thread.</summary>
    public Task<WhisperTranscription> TranscribeAsync(
        string path,
        ReadOnlyMemory<float> samples,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Transcribe(path, samples, options, progress, cancellationToken), CancellationToken.None);

    /// <summary>
    /// Detects the language of <paramref name="samples"/> among the supported
    /// Whisper languages (<see cref="TranscriptLanguages.WhisperCodes"/>) with
    /// Core's rules (<see cref="LanguageDetection.Detect"/>: up to three speech
    /// windows averaged, silent and no-speech windows skipped). The caller
    /// applies <see cref="LanguageDecision.Decide"/> to the result.
    /// </summary>
    /// <exception cref="WhisperEngineException">No model is loaded.</exception>
    /// <exception cref="InvalidOperationException">The model is English-only.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before a window.</exception>
    public DetectionResult DetectLanguage(ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
    {
        BeginJob();
        try
        {
            lock (gate)
            {
                RequireLoaded();
                return DetectLanguageLocked(samples, cancellationToken);
            }
        }
        finally
        {
            EndJob();
        }
    }

    /// <summary>Loads <paramref name="path"/> if needed, then <see cref="DetectLanguage(ReadOnlyMemory{float}, CancellationToken)"/>.</summary>
    public DetectionResult DetectLanguage(string path, ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        BeginJob();
        try
        {
            lock (gate)
            {
                LoadLocked(path);
                return DetectLanguageLocked(samples, cancellationToken);
            }
        }
        finally
        {
            EndJob();
        }
    }

    /// <summary>
    /// One window's language distribution over every language the model knows
    /// and its no-speech probability: the callback
    /// <see cref="LanguageDetection.Detect"/> takes. Port of
    /// <c>Transcriber.detectLanguage(samples:window:)</c>: the log-mel of the
    /// window (whisper.cpp pads it to 30 s), one encoder run, one decoder step
    /// from <c>&lt;|startoftranscript|&gt;</c>, softmax over the language
    /// tokens, and softmax over the whole vocabulary at that position for
    /// <c>&lt;|nospeech|&gt;</c>.
    /// </summary>
    public WindowLanguage DetectWindow(ReadOnlyMemory<float> window)
    {
        BeginJob();
        try
        {
            lock (gate)
            {
                RequireLoaded();
                RequireMultilingual();
                return DetectWindowLocked(window.Span, DefaultThreads);
            }
        }
        finally
        {
            EndJob();
        }
    }

    /// <summary>
    /// The speed probe (PLAN.md 18.4, "Speed"): times one warm 30 s window of
    /// speech (shared/fixtures/en-30s.wav, synthesized, embedded, looped to
    /// 30 s, language en; PLAN.md 18.4 "Speed probe audio": no personal audio
    /// in the product) with the loaded model and the default threads. When no
    /// job has run since the load, a short warm-up (the clip's first 4 s)
    /// runs first, so graph allocation and Vulkan pipeline compilation are not
    /// charged to the window. The app turns live preview off when
    /// <see cref="SpeedProbeResult.WindowSeconds"/> is over
    /// <see cref="LivePreviewMaxWindowSeconds"/>.
    /// </summary>
    /// <exception cref="WhisperEngineException">No model is loaded.</exception>
    public SpeedProbeResult MeasureWindowSeconds(CancellationToken cancellationToken = default)
    {
        var window = SpeedProbeWindow();
        var options = TranscriptionOptions.App(TranscriptLanguage.English);
        BeginJob();
        try
        {
            lock (gate)
            {
                RequireLoaded();
                double? warmUp = null;
                if (!warm)
                {
                    var watch = Stopwatch.StartNew();
                    // The clip's speech starts at once, so its first 4 s are never gated.
                    TranscribeLocked(window.AsMemory(0, 4 * SilenceGate.SampleRate), options, null, cancellationToken);
                    warmUp = watch.Elapsed.TotalSeconds;
                }
                var timer = Stopwatch.StartNew();
                TranscribeLocked(window, options, null, cancellationToken);
                double seconds = timer.Elapsed.TotalSeconds;
                var result = new SpeedProbeResult(
                    seconds, warmUp, WhisperRuntime.LibraryName ?? "unknown", DefaultThreads);
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"whisper: speed probe {result.WindowSeconds:F2} s per 30 s window ({result.Runtime}, {result.Threads} threads{(warmUp is { } w ? $", warm-up {w:F2} s" : "")}); live preview {(result.LivePreviewFeasible ? "on" : "off")}"));
                return result;
            }
        }
        finally
        {
            EndJob();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            FreeLocked();
        }
        idleTimer.Dispose();
    }

    // MARK: - Jobs (called with gate held)

    /// <summary>
    /// <see cref="LoadModelLocked"/>, its failures wrapped in
    /// <see cref="WhisperEngineError.LoadFailed"/> for the user (the
    /// technical message stays as the detail and the inner exception).
    /// </summary>
    private void LoadLocked(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            LoadModelLocked(path);
        }
        catch (Exception error) when (error is FileNotFoundException or PlatformNotSupportedException
                                          || (error is InvalidOperationException && error is not ObjectDisposedException))
        {
            throw WhisperEngineException.LoadFailed(error);
        }
    }

    private void LoadModelLocked(string path)
    {
        string full = Path.GetFullPath(path);
        if (context != IntPtr.Zero && string.Equals(modelPath, full, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        FreeLocked();
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Whisper model not found: {full}", full);
        }
        var loaded = WhisperRuntime.Load();
        var api = loaded.Api;
        var watch = Stopwatch.StartNew();

        var defaults = api.ContextDefaultParamsByRef();
        if (defaults is null) throw new InvalidOperationException("whisper_context_default_params_by_ref returned null.");
        WhisperContextParams contextParams = *defaults;
        api.FreeContextParams(defaults);
        // Whisper.net's WhisperFactoryOptions defaults, the configuration the
        // W1 spike measured: GPU on (ignored by the CPU runtime), no flash
        // attention, no DTW.
        contextParams.UseGpu = 1;
        contextParams.FlashAttention = 0;
        contextParams.GpuDevice = 0;
        contextParams.DtwTokenTimestamps = 0;

        IntPtr newContext;
        IntPtr pathUtf8 = Marshal.StringToCoTaskMemUTF8(full);
        try
        {
            newContext = api.InitFromFileWithParamsNoState((byte*)pathUtf8, contextParams);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathUtf8);
        }
        if (newContext == IntPtr.Zero)
        {
            throw new InvalidOperationException($"whisper.cpp could not load the model {full}.");
        }
        IntPtr newState = api.InitState(newContext);
        if (newState == IntPtr.Zero)
        {
            api.Free(newContext);
            throw new InvalidOperationException($"whisper.cpp could not allocate a state for {full} (out of memory?).");
        }

        int maxId = api.LangMaxId();
        var codes = new string[maxId + 1];
        for (int id = 0; id <= maxId; id++)
        {
            codes[id] = WhisperNative.Utf8(api.LangStr(id)) ?? "";
        }

        runtime = loaded;
        context = newContext;
        state = newState;
        modelPath = full;
        languageCodes = codes;
        multilingual = api.IsMultilingual(newContext) != 0;
        warm = false;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"whisper: loaded {Path.GetFileName(full)} in {watch.Elapsed.TotalSeconds:F2} s; runtime {loaded.Library} ({loaded.Path}); default threads {DefaultThreads}; {loaded.SystemInfo}"));
    }

    private void FreeLocked()
    {
        if (runtime is { } loaded)
        {
            if (state != IntPtr.Zero) loaded.Api.FreeState(state);
            if (context != IntPtr.Zero) loaded.Api.Free(context);
        }
        state = IntPtr.Zero;
        context = IntPtr.Zero;
        modelPath = null;
        languageCodes = [];
        warm = false;
    }

    private void RequireLoaded()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (context == IntPtr.Zero) throw new WhisperEngineException(WhisperEngineError.NoActiveModel, null);
    }

    private void RequireMultilingual()
    {
        if (!multilingual)
        {
            throw new InvalidOperationException("This model is English-only; language detection needs a multilingual model.");
        }
    }

    private WhisperNative Api => runtime?.Api ?? throw new WhisperEngineException(WhisperEngineError.NoActiveModel, null);

    private DetectionResult DetectLanguageLocked(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        RequireMultilingual();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var code in TranscriptLanguages.WhisperCodes)
        {
            if (Array.IndexOf(languageCodes, code) < 0) throw new ArgumentException($"The model does not know the language {code}.");
        }
        int threads = DefaultThreads;
        return LanguageDetection.Detect(samples, TranscriptLanguages.WhisperCodes, window =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DetectWindowLocked(window.Span, threads);
        });
    }

    private WindowLanguage DetectWindowLocked(ReadOnlySpan<float> window, int threads)
    {
        var api = Api;
        int status;
        fixed (float* pcm = window)
        {
            status = api.PcmToMelWithState(context, state, pcm, window.Length, threads);
        }
        if (status != 0) throw new InvalidOperationException($"whisper_pcm_to_mel_with_state failed ({status}).");

        var probabilities = new float[languageCodes.Length];
        int top;
        fixed (float* p = probabilities)
        {
            top = api.LangAutoDetectWithState(context, state, 0, threads, p);
        }
        if (top < 0) throw new InvalidOperationException($"whisper_lang_auto_detect_with_state failed ({top}).");
        warm = true;

        float noSpeech = NoSpeechProbability(api.GetLogitsFromState(state), api.NVocab(context), api.TokenNoSpeech(context));
        var distribution = new Dictionary<string, float>(languageCodes.Length);
        for (int id = 0; id < languageCodes.Length; id++)
        {
            if (languageCodes[id].Length > 0) distribution[languageCodes[id]] = probabilities[id];
        }
        return new WindowLanguage(new LanguageProbabilities(distribution), noSpeech);
    }

    /// <summary>softmax(logits)[token], in double; NaN when the logits are missing.</summary>
    internal static float NoSpeechProbability(float* logits, int count, int token)
    {
        if (logits is null || count <= 0 || token < 0 || token >= count) return float.NaN;
        return NoSpeechProbability(new ReadOnlySpan<float>(logits, count), token);
    }

    /// <summary>softmax(<paramref name="logits"/>)[<paramref name="token"/>], computed in double with the max subtracted.</summary>
    internal static float NoSpeechProbability(ReadOnlySpan<float> logits, int token)
    {
        double max = double.NegativeInfinity;
        foreach (var logit in logits) max = Math.Max(max, logit);
        double sum = 0;
        foreach (var logit in logits) sum += Math.Exp(logit - max);
        return (float)(Math.Exp(logits[token] - max) / sum);
    }

    private WhisperTranscription TranscribeLocked(
        ReadOnlyMemory<float> samples,
        TranscriptionOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionCancelledException([], cancellationToken);
        }
        _ = TranscriptionOptions.TemperatureSchedule(options.Temperatures);
        var runs = SilenceGate.SpeechRuns(samples.Span);
        string language = ResolveLanguage(options.Language, samples, runs);
        int threads = options.Threads ?? DefaultThreads;
        var api = Api;

        var job = new RunJob(progress, samples.Length, cancellationToken);
        var handle = GCHandle.Alloc(job);
        IntPtr languageUtf8 = Marshal.StringToCoTaskMemUTF8(language);
        IntPtr promptUtf8 = options.InitialPrompt is { } prompt ? Marshal.StringToCoTaskMemUTF8(prompt) : IntPtr.Zero;
        var segments = new List<TranscriptSegment>();
        try
        {
            var defaults = api.FullDefaultParamsByRef(WhisperSamplingStrategy.Greedy);
            if (defaults is null) throw new InvalidOperationException("whisper_full_default_params_by_ref returned null.");
            WhisperFullParams p = *defaults;
            api.FreeParams(defaults);
            options.Apply(ref p, threads, languageUtf8, promptUtf8);
            IntPtr userData = GCHandle.ToIntPtr(handle);
            p.ProgressCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, void>)&OnProgress;
            p.ProgressCallbackUserData = userData;
            p.EncoderBeginCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&OnEncoderBegin;
            p.EncoderBeginCallbackUserData = userData;
            p.AbortCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte>)&OnAbort;
            p.AbortCallbackUserData = userData;
            p.NewSegmentCallback = IntPtr.Zero;
            p.LogitsFilterCallback = IntPtr.Zero;

            foreach (var run in runs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TranscriptionCancelledException(DropSilent(segments, samples), cancellationToken);
                }
                job.RunStart = run.Start;
                job.RunLength = run.Length;
                int status;
                fixed (float* pcm = samples.Span[run.Start..run.End])
                {
                    status = api.FullWithState(context, state, p, pcm, run.Length);
                }
                warm = true;
                segments.AddRange(ReadSegments(api, run.Start / (double)SilenceGate.SampleRate));
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TranscriptionCancelledException(DropSilent(segments, samples), cancellationToken);
                }
                if (status != 0) throw new InvalidOperationException($"whisper_full failed ({status}).");
                job.Report(run.End);
            }
        }
        finally
        {
            handle.Free();
            Marshal.FreeCoTaskMem(languageUtf8);
            if (promptUtf8 != IntPtr.Zero) Marshal.FreeCoTaskMem(promptUtf8);
        }
        progress?.Report(1.0);
        return new WhisperTranscription(DropSilent(segments, samples), language);
    }

    private static IReadOnlyList<TranscriptSegment> DropSilent(List<TranscriptSegment> segments, ReadOnlyMemory<float> samples) =>
        SilenceGate.DropSilentCues(segments, samples.Span);

    /// <summary>
    /// The language for the whole call: the given code if the model knows it;
    /// for null, "en" for an English-only model, else the most probable
    /// language of the first transcribed window (Mac: of the first 30 s; the
    /// silence gate never transcribes a silent first window), "en" when every
    /// window is silent.
    /// </summary>
    private string ResolveLanguage(string? requested, ReadOnlyMemory<float> samples, IReadOnlyList<SilenceGate.SampleRange> runs)
    {
        if (requested is { } code)
        {
            if (Array.IndexOf(languageCodes, code) < 0 || (!multilingual && code != "en"))
            {
                throw new ArgumentException($"Unsupported language: {code}", nameof(requested));
            }
            return code;
        }
        if (!multilingual || runs.Count == 0) return "en";
        var first = runs[0];
        var window = samples.Span.Slice(first.Start, Math.Min(first.Length, SilenceGate.WindowSamples));
        var probabilities = DetectWindowLocked(window, DefaultThreads).Probabilities.Probabilities;
        string best = "en";
        float bestP = float.NegativeInfinity;
        for (int id = 0; id < languageCodes.Length; id++)
        {
            if (probabilities.TryGetValue(languageCodes[id], out float p) && p > bestP)
            {
                best = languageCodes[id];
                bestP = p;
            }
        }
        return best;
    }

    /// <summary>The state's segments, whisper.cpp centiseconds to seconds, shifted by <paramref name="offsetSeconds"/>.</summary>
    private List<TranscriptSegment> ReadSegments(WhisperNative api, double offsetSeconds)
    {
        int count = api.FullNSegmentsFromState(state);
        var segments = new List<TranscriptSegment>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            segments.Add(ConvertSegment(
                api.FullGetSegmentT0FromState(state, i),
                api.FullGetSegmentT1FromState(state, i),
                WhisperNative.Utf8(api.FullGetSegmentTextFromState(state, i)) ?? "",
                offsetSeconds));
        }
        return segments;
    }

    /// <summary>One whisper.cpp segment (times in centiseconds) as a segment in seconds from the start of the input.</summary>
    internal static TranscriptSegment ConvertSegment(long t0Centiseconds, long t1Centiseconds, string text, double offsetSeconds) =>
        new(offsetSeconds + t0Centiseconds / 100.0, offsetSeconds + t1Centiseconds / 100.0, text);

    private static float[] SpeedProbeWindow()
    {
        using var stream = typeof(WhisperEngine).Assembly.GetManifestResourceStream("Hearsay.Whisper.SpeedProbe.wav")
            ?? throw new InvalidOperationException("The speed probe clip is missing from Hearsay.Whisper.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var clip = PcmWav.ReadMono16k(buffer.ToArray(), "SpeedProbe.wav");
        if (clip.Length == 0) throw new InvalidOperationException("The speed probe clip is empty.");
        var window = new float[SilenceGate.WindowSamples];
        for (int i = 0; i < window.Length; i++) window[i] = clip[i % clip.Length];
        return window;
    }

    // MARK: - Idle unload

    private void BeginJob()
    {
        Interlocked.Increment(ref activeJobs);
        idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void EndJob()
    {
        if (Interlocked.Decrement(ref activeJobs) == 0 && !Volatile.Read(ref disposed))
        {
            try
            {
                idleTimer.Change(idleUnload, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the check and the call.
            }
        }
    }

    private void UnloadIfIdle()
    {
        if (Volatile.Read(ref activeJobs) != 0) return;
        lock (gate)
        {
            if (Volatile.Read(ref activeJobs) != 0 || context == IntPtr.Zero) return;
            FreeLocked();
            Log("whisper: model released after idle time");
        }
    }

    private void Log(string line) => log?.Invoke(line);

    // MARK: - Native callbacks

    /// <summary>State of one Transcribe call, shared with the callbacks through a GCHandle.</summary>
    private sealed class RunJob(IProgress<double>? progress, int total, CancellationToken token)
    {
        private double reported = -1;

        public CancellationToken Token { get; } = token;

        public int RunStart { get; set; }

        public int RunLength { get; set; }

        /// <summary>Reports the fraction done at <paramref name="position"/> samples (silent windows before it count as done); never goes back.</summary>
        public void Report(double position)
        {
            if (progress is null || total <= 0) return;
            double fraction = Math.Clamp(position / total, 0, 1);
            if (fraction <= reported) return;
            reported = fraction;
            progress.Report(fraction);
        }

        public void ReportPercent(int percent) =>
            Report(RunStart + Math.Clamp(percent, 0, 100) / 100.0 * RunLength);
    }

    private static RunJob? Job(IntPtr userData) =>
        userData == IntPtr.Zero ? null : GCHandle.FromIntPtr(userData).Target as RunJob;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProgress(IntPtr context, IntPtr state, int percent, IntPtr userData)
    {
        try
        {
            Job(userData)?.ReportPercent(percent);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // An exception must not unwind into whisper.cpp; progress is best effort.
        }
    }

    /// <summary>Called before every 30 s window's encoder run; false stops whisper_full (cancellation between windows).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnEncoderBegin(IntPtr context, IntPtr state, IntPtr userData) =>
        Job(userData)?.Token.IsCancellationRequested == true ? (byte)0 : (byte)1;

    /// <summary>Polled by the CPU backend during a window; true aborts the computation.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnAbort(IntPtr userData) =>
        Job(userData)?.Token.IsCancellationRequested == true ? (byte)1 : (byte)0;
}

/// <summary>What <see cref="WhisperEngine.MeasureWindowSeconds"/> measured.</summary>
/// <param name="WindowSeconds">Wall time of one warm 30 s window.</param>
/// <param name="WarmUpSeconds">The warm-up's wall time when one ran (the first job after a load).</param>
/// <param name="Runtime">The loaded runtime: "Vulkan", "Cpu", ...</param>
/// <param name="Threads">Threads the window used.</param>
public sealed record SpeedProbeResult(double WindowSeconds, double? WarmUpSeconds, string Runtime, int Threads)
{
    /// <summary>Live preview runs only when a warm window takes at most <see cref="WhisperEngine.LivePreviewMaxWindowSeconds"/>.</summary>
    public bool LivePreviewFeasible => WindowSeconds <= WhisperEngine.LivePreviewMaxWindowSeconds;
}
