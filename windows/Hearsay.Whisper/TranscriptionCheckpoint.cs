using Hearsay.Core.Transcription;

namespace Hearsay.Whisper;

/// <summary>
/// What a suspended <see cref="WhisperEngine.TranscribeStep(ReadOnlyMemory{float}, TranscriptionOptions, TranscriptionCheckpoint?, IProgress{double}?, Func{bool}?, CancellationToken)"/>
/// carries so the pass can continue later (PLAN.md 18.10, "Suspend and
/// resume on whisper.cpp"). Port of
/// mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/TranscriptionCheckpoint.swift;
/// whisper.cpp cannot save decoder state, so instead of the Mac's seek and
/// token list this holds the segments so far and the time to restart at.
/// </summary>
/// <param name="Segments">Segments decoded so far, silent cues already dropped, seconds from the start of the input.</param>
/// <param name="ResumeSeconds">Where the next pass starts: the end of the last complete segment, or the start of the next speech run when the pass stopped between runs.</param>
/// <param name="Language">The Whisper code used (given, or detected on the first call).</param>
/// <param name="ModelPath">Full path of the model file that decoded the segments; see <see cref="IsForModel"/>.</param>
/// <param name="SampleCount">Length of the input the checkpoint was made from.</param>
/// <param name="SampleFingerprint">See <see cref="Fingerprint"/>.</param>
/// <param name="Options">The options the checkpoint was made with.</param>
public sealed record TranscriptionCheckpoint(
    IReadOnlyList<TranscriptSegment> Segments,
    double ResumeSeconds,
    string Language,
    string ModelPath,
    int SampleCount,
    ulong SampleFingerprint,
    TranscriptionOptions Options)
{
    /// <summary>
    /// True when <paramref name="modelPath"/> is the model that decoded this
    /// checkpoint. A checkpoint is only valid on its own model (Mac: on
    /// another active model the job starts over); the engine throws
    /// <see cref="TranscriptionCheckpointError.ModelMismatch"/> otherwise.
    /// </summary>
    public bool IsForModel(string? modelPath) =>
        modelPath is not null
        && string.Equals(Path.GetFullPath(modelPath), ModelPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fraction of the input done, 0...1: what a resumed call reports first.</summary>
    public double FractionDone => SampleCount <= 0
        ? 0
        : Math.Clamp(ResumeSeconds * SilenceGate.SampleRate / SampleCount, 0, 1);

    /// <summary>
    /// FNV-1a over the sample count and the bit patterns of every 1600th
    /// sample (one per 0.1 s) and the last one: cheap on hours of audio, and
    /// enough to catch a checkpoint handed to the wrong recording.
    /// </summary>
    public static ulong Fingerprint(ReadOnlySpan<float> samples)
    {
        ulong hash = 0xcbf29ce484222325;
        hash = Mix(hash, (ulong)samples.Length);
        for (int index = 0; index < samples.Length; index += 1600)
        {
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(samples[index]));
        }
        if (!samples.IsEmpty) hash = Mix(hash, BitConverter.SingleToUInt32Bits(samples[^1]));
        return hash;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= value & 0xff;
            hash *= 0x00000100000001b3;
            value >>= 8;
        }
        return hash;
    }

    /// <summary>The decoding options that change the output (threads do not); the same test the Mac does with <c>==</c>.</summary>
    internal static bool SameOptions(TranscriptionOptions a, TranscriptionOptions b) =>
        a.Language == b.Language
        && a.InitialPrompt == b.InitialPrompt
        && a.ConditionOnPreviousText == b.ConditionOnPreviousText
        && a.Temperatures.SequenceEqual(b.Temperatures)
        && a.CompressionRatioThreshold == b.CompressionRatioThreshold
        && a.LogprobThreshold == b.LogprobThreshold
        && a.NoSpeechThreshold == b.NoSpeechThreshold
        && a.BestOf == b.BestOf;
}

/// <summary>
/// The result of one <see cref="WhisperEngine.TranscribeStep(ReadOnlyMemory{float}, TranscriptionOptions, TranscriptionCheckpoint?, IProgress{double}?, Func{bool}?, CancellationToken)"/>:
/// exactly one of <see cref="Finished"/> and <see cref="Suspended"/> is set.
/// Port of <c>TranscriptionStep</c>.
/// </summary>
public sealed record TranscriptionStep
{
    private TranscriptionStep()
    {
    }

    /// <summary>Every window is decoded.</summary>
    public WhisperTranscription? Finished { get; private init; }

    /// <summary><c>shouldYield</c> asked to stop; pass this back as the checkpoint to continue.</summary>
    public TranscriptionCheckpoint? Suspended { get; private init; }

    public static TranscriptionStep Done(WhisperTranscription transcription) => new() { Finished = transcription };

    public static TranscriptionStep Suspend(TranscriptionCheckpoint checkpoint) => new() { Suspended = checkpoint };
}

/// <summary>Why a checkpoint cannot be resumed with the given input.</summary>
public enum TranscriptionCheckpointError
{
    /// <summary>The samples differ (count or content) from the checkpoint's.</summary>
    SamplesMismatch,

    /// <summary>The options differ from the checkpoint's.</summary>
    OptionsMismatch,

    /// <summary>Another model than the checkpoint's is loaded; the caller starts the job over.</summary>
    ModelMismatch,
}

/// <summary>Port of <c>TranscriptionCheckpointError</c>.</summary>
public sealed class TranscriptionCheckpointException : InvalidOperationException
{
    public TranscriptionCheckpointException(TranscriptionCheckpointError error)
        : base(Describe(error))
    {
        Error = error;
    }

    public TranscriptionCheckpointError Error { get; }

    private static string Describe(TranscriptionCheckpointError error) => error switch
    {
        TranscriptionCheckpointError.SamplesMismatch => "The checkpoint was made from different audio samples.",
        TranscriptionCheckpointError.OptionsMismatch => "The checkpoint was made with different transcription options.",
        _ => "The checkpoint was made with a different model.",
    };
}

/// <summary>Joins the segments of a checkpoint with those of the resumed pass.</summary>
public static class CheckpointMerge
{
    private const double Tolerance = 1e-6;

    /// <summary>
    /// <paramref name="prior"/> (the cues that start before
    /// <paramref name="offsetSeconds"/>; a cue starting at or after it would
    /// be decoded again) followed by <paramref name="fresh"/> without its
    /// leading cues that end at or before the offset (PLAN.md 18.10: drop a
    /// first cue that ends before the offset).
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Merge(
        IReadOnlyList<TranscriptSegment> prior, double offsetSeconds, IReadOnlyList<TranscriptSegment> fresh)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(fresh);
        var merged = new List<TranscriptSegment>(prior.Count + fresh.Count);
        foreach (var segment in prior)
        {
            if (segment.Start < offsetSeconds - Tolerance) merged.Add(segment);
        }
        int first = 0;
        while (first < fresh.Count && fresh[first].End <= offsetSeconds + Tolerance) first++;
        for (int i = first; i < fresh.Count; i++) merged.Add(fresh[i]);
        return merged;
    }
}
