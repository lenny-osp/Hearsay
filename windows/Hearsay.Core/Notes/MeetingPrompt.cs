using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Hearsay.Core.Notes;

/// <summary>
/// Thrown by <see cref="MeetingPrompt.Build"/> for a transcript language code
/// that is not in <c>shared/prompts/languages.json</c>; nothing is sent.
/// Port of <c>MeetingPromptError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/MeetingPrompt.swift.
/// </summary>
public sealed class MeetingPromptException : Exception
{
    public MeetingPromptException(string languageCode, Exception? innerException = null)
        : base($"unsupported meeting-note language: {languageCode}", innerException)
    {
        LanguageCode = languageCode;
    }

    /// <summary>The unsupported code, as passed in.</summary>
    public string LanguageCode { get; }
}

/// <summary>
/// Port of mac/HearsayCore/Sources/HearsayCore/Notes/MeetingPrompt.swift
/// (itself a port of <c>build_meeting_prompt</c> in <c>run_whisper.py</c>),
/// with a template slot for the notes section. With
/// <see cref="PromptTemplate.GeneralMeeting"/> the result is byte-identical to
/// the Python prompt. Assembly follows <c>shared/prompts/assembly.md</c>.
/// Unlike the Swift, which compiles the text in, the text here is read from
/// <c>shared/prompts/</c>, embedded into this assembly at build time.
/// </summary>
public static class MeetingPrompt
{
    /// <summary>Logical-name prefix of the embedded <c>shared/prompts/</c> files (see Hearsay.Core.csproj).</summary>
    public const string ResourcePrefix = "Hearsay.Core.Prompts.";

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Transcript language code (<c>TranscriptLanguages.Code</c>) to the name
    /// put into <c>{output_language}</c>; <c>shared/prompts/languages.json</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Languages { get; } = LoadLanguages();

    /// <summary>The system message sent with the prompt; <c>shared/prompts/system-message.txt</c>.</summary>
    public static string SystemMessage { get; } = ReadResourceText("system-message.txt");

    /// <summary>
    /// The fixed filename and JSON-shape rules, always appended after the
    /// template so <see cref="NotesResponse.Parse"/> keeps working;
    /// <c>shared/prompts/response-rules.txt</c>.
    /// </summary>
    public static string ResponseRules { get; } = ReadResourceText("response-rules.txt");

    /// <summary>
    /// Builds the user message. Throws <see cref="MeetingPromptException"/> for a
    /// language code not in <see cref="Languages"/>, matching the Python
    /// <c>ValueError</c>. A null template means <see cref="PromptTemplate.GeneralMeeting"/>.
    /// </summary>
    public static string Build(string transcript, string languageCode, PromptTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(languageCode);
        if (!Languages.TryGetValue(languageCode, out var outputLanguage))
        {
            throw new MeetingPromptException(languageCode);
        }
        var instructions = (template ?? PromptTemplate.GeneralMeeting).Instructions;
        var notes = instructions.Replace(PromptTemplate.OutputLanguagePlaceholder, outputLanguage, StringComparison.Ordinal);
        return notes
            + "\n\n" + ResponseRules
            + "\n\nThe source SRT is below:\n---\n" + transcript + "\n---";
    }

    /// <summary>
    /// The raw bytes of an embedded <c>shared/prompts/</c> file, exactly as on
    /// disk at build time (for example <c>"general-meeting.txt"</c>).
    /// </summary>
    public static byte[] ReadResourceBytes(string fileName)
    {
        var name = ResourcePrefix + fileName;
        using var stream = typeof(MeetingPrompt).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded prompt resource '{name}' is missing; rebuild Hearsay.Core from the repo checkout.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// An embedded <c>shared/prompts/</c> file decoded as UTF-8. Newlines are
    /// kept as they are; a byte-order mark is refused rather than silently
    /// dropped or kept (the shared files have none).
    /// </summary>
    internal static string ReadResourceText(string fileName)
    {
        var bytes = ReadResourceBytes(fileName);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            throw new InvalidOperationException(
                $"shared/prompts/{fileName} starts with a byte-order mark; the prompt files must be UTF-8 without one.");
        }
        return StrictUtf8.GetString(bytes);
    }

    private static ReadOnlyDictionary<string, string> LoadLanguages()
    {
        var names = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResourceText("languages.json"))
            ?? throw new InvalidOperationException("shared/prompts/languages.json is not a JSON object.");
        return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(names, StringComparer.Ordinal));
    }
}
