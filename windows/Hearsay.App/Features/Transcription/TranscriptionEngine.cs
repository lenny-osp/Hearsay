using System.Globalization;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.App.Features.Transcription;

/// <summary>
/// The app's one Whisper engine (the <c>WhisperEngine</c> actor of
/// mac/Hearsay/Features/Transcription/WhisperEngine.swift, which the Mac
/// injects into every tab). Hearsay.Whisper's engine is synchronous and
/// serializes jobs with a lock; this wrapper runs every job on the thread
/// pool, so the UI thread never waits for whisper.cpp, and it remembers the
/// speed probe per model (PLAN.md 18.4, "Speed"). The model path is always
/// passed in, so a job loads the model first when needed, as the Mac's
/// <c>transcribe(samples:location:...)</c> does.
/// </summary>
/// <summary>
/// What the background queue needs from the engine (the Mac's
/// <c>WhisperEngine.transcribeStep</c>, <c>detectLanguage</c> and
/// <c>transcribe</c> as <c>TranscriptionQueue</c> calls them). A fake
/// implements it in the queue tests.
/// </summary>
internal interface IQueueEngine
{
    /// <summary>Foreground calls waiting for or running in the engine (lock-free); a step yields while it is above 0.</summary>
    int ForegroundWaiting { get; }

    /// <summary>
    /// One background, resumable step of a final pass (not counted as
    /// foreground work). <paramref name="shouldYield"/> runs on the engine's
    /// thread: read only volatile or locked state.
    /// </summary>
    Task<TranscriptionStep> TranscribeStepAsync(
        string modelPath, float[] samples, TranscriptLanguage language, TranscriptionCheckpoint? resumeFrom,
        IProgress<double>? progress, Func<bool> shouldYield, CancellationToken cancellationToken);

    /// <summary>Language detection, foreground work.</summary>
    Task<DetectionResult> DetectLanguageAsync(string modelPath, float[] samples, CancellationToken cancellationToken = default);

    /// <summary>A whole pass, foreground work ("Transcribe again").</summary>
    Task<WhisperTranscription> TranscribeAsync(
        string modelPath, float[] samples, TranscriptLanguage language,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

internal sealed class TranscriptionEngine : IDisposable, IQueueEngine
{
    private readonly Dictionary<string, Task<SpeedProbeResult>> probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private Task<bool>? cpuCheck;

    public TranscriptionEngine()
    {
        Engine = new WhisperEngine(line => AppLog.Write(line));
    }

    public WhisperEngine Engine { get; }

    /// <summary>A job is waiting or running.</summary>
    public bool IsBusy => Engine.IsBusy;

    /// <inheritdoc />
    public int ForegroundWaiting => Engine.ForegroundWaiting;

    /// <summary>
    /// Transcribes <paramref name="samples"/> in <paramref name="language"/>
    /// with the model at <paramref name="modelPath"/> (the Mac's
    /// <c>TranscriptionOptions.app(language:)</c>). Cancelling stops at the
    /// next 30 s window with <see cref="TranscriptionCancelledException"/>.
    /// Foreground work (PLAN.md 4.9, 18.10): the call is counted from here,
    /// before it waits for the engine, until it ends, so a background step
    /// yields at its next window. Every caller of this method is foreground:
    /// the live chunks, File mode, "Transcribe again".
    /// </summary>
    public async Task<WhisperTranscription> TranscribeAsync(
        string modelPath, float[] samples, TranscriptLanguage language,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var foreground = Engine.EnterForeground();
        return await Engine.TranscribeAsync(modelPath, samples, TranscriptionOptions.App(language), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One background step of a final pass, resumable (PLAN.md 18.10); never
    /// counted as foreground work. Yields (returns a suspended step) when
    /// <paramref name="shouldYield"/> says so at a 30 s window.
    /// </summary>
    public Task<TranscriptionStep> TranscribeStepAsync(
        string modelPath, float[] samples, TranscriptLanguage language, TranscriptionCheckpoint? resumeFrom,
        IProgress<double>? progress, Func<bool> shouldYield, CancellationToken cancellationToken) =>
        Engine.TranscribeStepAsync(
            modelPath, samples, TranscriptionOptions.App(language), resumeFrom, progress, shouldYield, cancellationToken);

    /// <summary>
    /// Detects the language of <paramref name="samples"/> among the four
    /// supported languages (up to three speech windows averaged; the caller
    /// applies <see cref="LanguageDecision.Decide"/>). Foreground work, like
    /// <see cref="TranscribeAsync"/>.
    /// </summary>
    public async Task<DetectionResult> DetectLanguageAsync(string modelPath, float[] samples, CancellationToken cancellationToken = default)
    {
        using var foreground = Engine.EnterForeground();
        return await Task.Run(() => Engine.DetectLanguage(modelPath, samples, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The speed probe of <paramref name="modelPath"/>, measured once per
    /// model and process (PLAN.md 18.4: a warm 30 s window over 15 s turns the
    /// live preview off). A failed probe is not remembered.
    /// </summary>
    public Task<SpeedProbeResult> MeasureSpeedAsync(string modelPath)
    {
        lock (gate)
        {
            if (probes.TryGetValue(modelPath, out var known) && !known.IsFaulted && !known.IsCanceled) return known;
            var probe = Task.Run(() =>
            {
                Engine.Load(modelPath);
                return Engine.MeasureWindowSeconds();
            });
            probes[modelPath] = probe;
            return probe;
        }
    }

    /// <summary>
    /// True when whisper.cpp runs on its CPU runtime (no Vulkan GPU), for the
    /// Record tab's notice before the first recording (PLAN.md 18.4, "Speed").
    /// Loads the runtime library once, on the thread pool, without a model;
    /// false when no runtime loads (the first transcription reports that).
    /// </summary>
    public Task<bool> IsCpuRuntimeAsync()
    {
        lock (gate)
        {
            return cpuCheck ??= Task.Run(() =>
            {
                try
                {
                    var library = WhisperRuntime.EnsureLoaded();
                    AppLog.Write($"whisper: runtime {library}{(WhisperRuntime.IsCpu ? " (CPU: the final pass takes about 1.5 to 3.5x the recording)" : "")}");
                    return WhisperRuntime.IsCpu;
                }
                catch (PlatformNotSupportedException error)
                {
                    AppLog.Write($"whisper: no runtime loaded: {error.Message}");
                    return false;
                }
            });
        }
    }

    /// <summary>The finished probe of <paramref name="modelPath"/>, or null when it has not run (or failed).</summary>
    public SpeedProbeResult? KnownSpeed(string modelPath)
    {
        lock (gate)
        {
            return probes.TryGetValue(modelPath, out var probe) && probe.IsCompletedSuccessfully ? probe.Result : null;
        }
    }

    public void Dispose() => Engine.Dispose();

    /// <summary>The engine stopped because the job was cancelled (the Mac's <c>isTranscriptionCancelled</c>).</summary>
    public static bool IsCancellation(Exception error) => error is OperationCanceledException;

    /// <summary>
    /// The user-facing text of an error (the Mac's <c>RecordingController.describe</c>),
    /// in the interface language when it carries a catalog key (<see cref="Strings.Describe"/>).
    /// </summary>
    public static string Describe(Exception error) => Strings.Describe(error);

    /// <summary>
    /// One line for the debug paths, as the Mac's <c>DetectionResult.debugSummary</c>
    /// in mac/Hearsay/Features/Transcription/TranscriptionOptions+App.swift.
    /// </summary>
    public static string DebugSummary(DetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var windows = result.PerWindow.Select(window => string.Join(", ",
            TranscriptLanguages.WhisperCodes.Select(code =>
                string.Create(CultureInfo.InvariantCulture, $"{code} {(double)(window.TryGetValue(code, out var p) ? p : 0):F4}"))));
        var list = windows.ToList();
        return string.Create(CultureInfo.InvariantCulture,
                $"detected {result.Code ?? "none"} {(double)result.Confidence:F4} over {result.WindowsUsed} speech windows")
            + (list.Count == 0 ? "" : " [" + string.Join("] [", list) + "]");
    }
}
