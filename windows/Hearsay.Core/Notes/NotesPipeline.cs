using System.Text;
using Hearsay.Core.Naming;
using Hearsay.Core.Transcription;

namespace Hearsay.Core.Notes;

/// <summary>Why <see cref="NotesPipeline"/> stopped before asking a provider.</summary>
public abstract record NotesPipelineError
{
    private NotesPipelineError()
    {
    }

    /// <summary>The SRT has no caption text once indexes and timings are removed.</summary>
    public sealed record EmptyTranscript : NotesPipelineError;

    /// <summary>
    /// The SRT file could not be read (<see cref="NotesPipeline.GenerateAndSaveAsync"/>;
    /// the Mac's <c>NotesFlowViewModel</c> reports the same text).
    /// </summary>
    public sealed record SrtUnreadable(string Path, string Reason) : NotesPipelineError;

    /// <summary>The message shown to the user. English until W7.</summary>
    public string Description => this switch
    {
        EmptyTranscript => "Error: The SRT contains no transcript content to summarize.",
        SrtUnreadable e => $"Error: SRT file not found or unreadable ({e.Path}): {e.Reason}",
        _ => throw new InvalidOperationException("Unknown NotesPipelineError."),
    };
}

/// <summary>Thrown by <see cref="NotesPipeline"/>; <see cref="Error"/> says what failed.</summary>
public sealed class NotesPipelineException : Exception
{
    public NotesPipelineException(NotesPipelineError error, Exception? innerException = null)
        : base(error?.Description, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public NotesPipelineError Error { get; }
}

/// <summary>
/// SRT text in, validated <see cref="NotesResponse"/> out: Python
/// <c>generate_meeting_notes</c> without any file handling. The preset kind
/// picks a CLI branch (<see cref="CliClient"/>: Copilot, Claude Code, Codex,
/// Antigravity) or the API branch (<see cref="Client"/>). Port of
/// mac/HearsayCore/Sources/HearsayCore/Notes/NotesPipeline.swift.
/// <para>
/// As in Python, <see cref="Srt.CleanText"/> only guards against a transcript
/// with no caption text; the prompt carries the SRT as is (timings
/// included). The CLI branches strip surrounding whitespace first, as
/// Python's Copilot branch does with <c>srt_text.strip()</c>, and never pass
/// a token.
/// </para>
/// <para>
/// <see cref="GenerateAndSaveAsync"/> adds the file end of PLAN.md 4.3 (steps
/// 2 to 7) that the Mac keeps in its <c>NotesFlowViewModel</c>: read the SRT,
/// generate, name the meeting, and write the three files through
/// <see cref="OutputWriter.SaveNamed(string, string, string, string, string?)"/>.
/// The confirm and naming sheets stay in the app.
/// </para>
/// </summary>
public sealed class NotesPipeline
{
    /// <param name="client">The HTTP client (Ollama, Custom); null makes a <see cref="ChatCompletionsClient"/>.</param>
    /// <param name="cliClient">The CLI client; null makes a <see cref="Notes.CliClient"/> for the current user.</param>
    public NotesPipeline(IChatCompleting? client = null, CliClient? cliClient = null)
    {
        Client = client ?? new ChatCompletionsClient();
        CliClient = cliClient ?? new CliClient();
    }

    public IChatCompleting Client { get; }

    public CliClient CliClient { get; }

    /// <summary>The client that serves <paramref name="configuration"/>'s preset.</summary>
    public IChatCompleting ClientFor(AIProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Preset.Kind.IsCli() ? CliClient : Client;
    }

    /// <summary>
    /// The validated notes for <paramref name="srtText"/>. Throws
    /// <see cref="NotesPipelineException"/> (empty transcript),
    /// <see cref="MeetingPromptException"/> (unsupported language), the
    /// provider's exception (<see cref="ChatCompletionsException"/>,
    /// <see cref="CliProviderException"/>), or <see cref="NotesResponseException"/>;
    /// in the first two cases nothing is sent.
    /// </summary>
    public async Task<NotesResponse> GenerateAsync(string srtText, string languageCode, PromptTemplate template,
        AIProviderConfiguration configuration, string? token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(srtText);
        ArgumentNullException.ThrowIfNull(configuration);
        if (Srt.CleanText(srtText).Trim().Length == 0)
        {
            throw new NotesPipelineException(new NotesPipelineError.EmptyTranscript());
        }
        var isCli = configuration.Preset.Kind.IsCli();
        var transcript = isCli ? srtText.Trim() : srtText;
        var prompt = MeetingPrompt.Build(transcript, languageCode, template);
        var content = await ClientFor(configuration).CompleteAsync(
            MeetingPrompt.SystemMessage, prompt, configuration, isCli ? null : token, cancellationToken).ConfigureAwait(false);
        return NotesResponse.Parse(content);
    }

    /// <summary>
    /// The end-to-end flow for a finished SRT: read it (UTF-8), generate the
    /// notes, then save <c>&lt;stem&gt;.srt</c>, <c>&lt;stem&gt;.md</c> and
    /// <c>&lt;stem&gt;_transcript.md</c> (and rename the same-basename WAV) with
    /// <paramref name="meetingName"/>, or the AI's suggested name when it is
    /// null (<see cref="OutputWriter"/> sanitizes either). <paramref name="timestamp"/>
    /// overrides the one in the SRT filename, as Python's <c>timestamp=</c>
    /// does. On any failure the SRT stays where it is.
    /// </summary>
    public async Task<OutputWriter.NamedOutputs> GenerateAndSaveAsync(string srtPath, string languageCode,
        PromptTemplate template, AIProviderConfiguration configuration, string? token, string? meetingName = null,
        string? timestamp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(srtPath);
        string srtText;
        try
        {
            srtText = await File.ReadAllTextAsync(srtPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new NotesPipelineException(
                new NotesPipelineError.SrtUnreadable(Path.GetFullPath(srtPath), error.Message), error);
        }
        var notes = await GenerateAsync(srtText, languageCode, template, configuration, token, cancellationToken)
            .ConfigureAwait(false);
        return OutputWriter.SaveNamed(srtPath, meetingName ?? notes.Filename, notes.Markdown, notes.TranscriptMarkdown,
            timestamp);
    }

    /// <summary>Where the preset's CLI is, its <c>--version</c> output, and its login state (Settings &gt; AI).</summary>
    public async Task<CliInstallation> CheckInstallationAsync(AIProviderConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var preset = configuration.Preset;
        if (preset.Kind.CliTool() is not { } tool)
        {
            throw new CliProviderException(new CliProviderError.NotACliPreset(preset.Name));
        }
        return await CliClient.CheckInstallationAsync(tool, configuration.CliPath(tool), cancellationToken)
            .ConfigureAwait(false);
    }
}
