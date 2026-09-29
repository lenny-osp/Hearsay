using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Notes;

/// <summary>Which rule of the reply contract (<c>shared/prompts/assembly.md</c>) a reply broke.</summary>
public enum NotesResponseErrorKind
{
    InvalidJson,
    NotAnObject,
    MissingFilename,
    MissingMarkdown,
    MissingTranscriptMarkdown,
}

/// <summary>
/// Why an AI response could not be used. <see cref="Reason"/> matches the
/// Python <c>parse_ai_result</c> diagnostics (without the "Parsing Error: "
/// prefix); <see cref="Exception.Message"/> is "Parsing Error: " + reason.
/// Port of <c>NotesResponseError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/NotesResponse.swift. The texts
/// are the English ones; the app localizes them for display.
/// </summary>
public sealed class NotesResponseException : Exception
{
    public const string ParsingErrorPrefix = "Parsing Error: ";

    /// <summary>
    /// A contract failure. <paramref name="detail"/> is the JSON parser's
    /// message for <see cref="NotesResponseErrorKind.InvalidJson"/>, ignored otherwise.
    /// </summary>
    public NotesResponseException(NotesResponseErrorKind kind, string? detail = null, Exception? innerException = null)
        : base(ParsingErrorPrefix + ReasonFor(kind, detail), innerException)
    {
        Kind = kind;
        Detail = kind == NotesResponseErrorKind.InvalidJson ? detail ?? string.Empty : null;
        Reason = ReasonFor(kind, detail);
    }

    public NotesResponseErrorKind Kind { get; }

    /// <summary>The JSON parser's message, for <see cref="NotesResponseErrorKind.InvalidJson"/> only.</summary>
    public string? Detail { get; }

    /// <summary>The reason without the "Parsing Error: " prefix (Swift <c>message</c>).</summary>
    public string Reason { get; }

    /// <summary>The English reason text for <paramref name="kind"/>.</summary>
    public static string ReasonFor(NotesResponseErrorKind kind, string? detail = null) => kind switch
    {
        NotesResponseErrorKind.InvalidJson => $"AI output is not valid JSON: {detail}",
        NotesResponseErrorKind.NotAnObject => "AI output must be a JSON object.",
        NotesResponseErrorKind.MissingFilename => "AI output did not contain a usable filename.",
        NotesResponseErrorKind.MissingMarkdown => "AI output did not contain usable Markdown notes.",
        NotesResponseErrorKind.MissingTranscriptMarkdown => "AI output did not contain a usable structured transcript.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// The validated AI result. Port of mac/HearsayCore/Sources/HearsayCore/Notes/NotesResponse.swift
/// (itself a port of <c>parse_ai_result</c> in <c>run_whisper.py</c>); the
/// rules are the "Reply contract" in <c>shared/prompts/assembly.md</c>.
/// </summary>
public sealed partial record NotesResponse(string Filename, string Markdown, string TranscriptMarkdown)
{
    // Python: re.fullmatch(r"```(?:json)?\s*(.*?)\s*```", text, DOTALL | IGNORECASE)
    [GeneratedRegex(@"\A```(?:json)?\s*(.*?)\s*```\z",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FencePattern();

    /// <summary>
    /// Parses and validates a reply. Throws <see cref="NotesResponseException"/>
    /// for the first rule it breaks, in the order of assembly.md.
    /// </summary>
    public static NotesResponse Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = raw.Trim();
        var fence = FencePattern().Match(text);
        if (fence.Success)
        {
            text = fence.Groups[1].Value;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException error)
        {
            throw new NotesResponseException(NotesResponseErrorKind.InvalidJson, error.Message, error);
        }
        catch (ArgumentException error)
        {
            // Text that cannot be transcoded to UTF-8 (a lone surrogate).
            throw new NotesResponseException(NotesResponseErrorKind.InvalidJson, error.Message, error);
        }

        using (document)
        {
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new NotesResponseException(NotesResponseErrorKind.NotAnObject);
            }
            var filename = UsableString(result, "filename")
                ?? throw new NotesResponseException(NotesResponseErrorKind.MissingFilename);
            var markdown = UsableString(result, "markdown")
                ?? throw new NotesResponseException(NotesResponseErrorKind.MissingMarkdown);
            var transcriptMarkdown = UsableString(result, "transcript_markdown")
                ?? throw new NotesResponseException(NotesResponseErrorKind.MissingTranscriptMarkdown);
            return new NotesResponse(filename, markdown, transcriptMarkdown);
        }
    }

    /// <summary>
    /// A string value with at least one non-whitespace character, returned
    /// unmodified (Python returns the original, unstripped value).
    /// </summary>
    private static string? UsableString(JsonElement result, string name)
    {
        if (!result.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
