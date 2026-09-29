using Hearsay.Core.Transcription;

namespace Hearsay.Whisper;

/// <summary>
/// What <see cref="WhisperEngine.Transcribe"/> returns: whisper.cpp's
/// segments (seconds from the start of the samples passed in, text as the
/// model wrote it, silent cues already dropped by <see cref="SilenceGate"/>)
/// and the Whisper language code used. Port of <c>Transcription</c> and its
/// <c>cues(offset:script:)</c> extension in
/// mac/Hearsay/Features/Transcription/WhisperEngine.swift.
/// </summary>
public sealed record WhisperTranscription(IReadOnlyList<TranscriptSegment> Segments, string Language)
{
    /// <summary>
    /// The segments as SRT cues, cleaned like mlx_whisper's <c>WriteSRT</c>
    /// (text stripped, <c>--&gt;</c> replaced with <c>-&gt;</c>), shifted by
    /// <paramref name="offset"/> seconds, and converted to
    /// <paramref name="script"/> (the session language's
    /// <see cref="TranscriptLanguages.ChineseScript(TranscriptLanguage)"/>;
    /// null leaves the text unchanged). Every cue Hearsay shows or writes
    /// comes from here.
    /// </summary>
    public IReadOnlyList<TranscriptSegment> Cues(double offset, ChineseScript? script)
    {
        var cues = new TranscriptSegment[Segments.Count];
        for (int i = 0; i < cues.Length; i++)
        {
            var segment = Segments[i];
            cues[i] = new TranscriptSegment(
                segment.Start + offset,
                segment.End + offset,
                segment.Text.Trim().Replace("-->", "->", StringComparison.Ordinal));
        }
        return ChineseScriptConverter.Convert(cues, script);
    }
}

/// <summary>
/// The engine stopped because the job was cancelled; <see cref="Partial"/>
/// holds the segments finished before that. Port of
/// <c>TranscriptionError.cancelled(partial:)</c>.
/// </summary>
public sealed class TranscriptionCancelledException : OperationCanceledException
{
    public TranscriptionCancelledException()
        : this([], CancellationToken.None)
    {
    }

    public TranscriptionCancelledException(string message)
        : base(message)
    {
        Partial = [];
    }

    public TranscriptionCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
        Partial = [];
    }

    public TranscriptionCancelledException(IReadOnlyList<TranscriptSegment> partial, CancellationToken token)
        : base("The transcription was cancelled.", token)
    {
        Partial = partial;
    }

    /// <summary>Segments decoded before the cancel, in the same frame as <see cref="WhisperTranscription.Segments"/>.</summary>
    public IReadOnlyList<TranscriptSegment> Partial { get; }
}
