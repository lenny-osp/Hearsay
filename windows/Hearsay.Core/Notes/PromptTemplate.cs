using System.Text.Json.Serialization;

namespace Hearsay.Core.Notes;

/// <summary>
/// A named set of note-taking instructions. The instructions replace the notes
/// section of the meeting prompt; <see cref="MeetingPrompt"/> always appends
/// the fixed filename and JSON-shape rules and the transcript after them.
/// The placeholder <c>{output_language}</c> in <see cref="Instructions"/> is
/// replaced with the selected language name (for example "English") when the
/// prompt is built.
/// Port of mac/HearsayCore/Sources/HearsayCore/Notes/PromptTemplate.swift.
/// The JSON property names match the Swift <c>Codable</c> keys.
/// </summary>
public sealed record PromptTemplate
{
    public const string OutputLanguagePlaceholder = "{output_language}";

    /// <summary>Stable id so the built-in template can be referenced from settings.</summary>
    public static readonly Guid GeneralMeetingId = new("6e0b5a10-3c2d-4f51-9a7e-000000000001");

    /// <summary>
    /// Built-in "General meeting" template: the notes section of Python
    /// <c>build_meeting_prompt</c>, verbatim, with <c>{output_language}</c>
    /// placeholders; the text is <c>shared/prompts/general-meeting.txt</c>.
    /// The name is the English one; the app localizes it for display.
    /// </summary>
    public static PromptTemplate GeneralMeeting { get; } = new(
        GeneralMeetingId,
        "General meeting",
        MeetingPrompt.ReadResourceText("general-meeting.txt"),
        isBuiltIn: true);

    /// <summary>A new template with a fresh id.</summary>
    public PromptTemplate(string name, string instructions, bool isBuiltIn = false)
        : this(Guid.NewGuid(), name, instructions, isBuiltIn)
    {
    }

    [JsonConstructor]
    public PromptTemplate(Guid id, string name, string instructions, bool isBuiltIn)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(instructions);
        Id = id;
        Name = name;
        Instructions = instructions;
        IsBuiltIn = isBuiltIn;
    }

    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("instructions")]
    public string Instructions { get; init; }

    [JsonPropertyName("isBuiltIn")]
    public bool IsBuiltIn { get; init; }
}
