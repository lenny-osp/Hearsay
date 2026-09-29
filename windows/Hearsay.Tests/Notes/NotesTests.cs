using System.Text;
using System.Text.Json;
using Hearsay.Core.Notes;
using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Notes;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/NotesTests.swift (MeetingPrompt,
/// PromptTemplate, NotesResponse), plus checks that the prompt text embedded
/// into Hearsay.Core is the bytes of shared/prompts/.
/// </summary>
public class NotesTests
{
    /// <summary>
    /// Generated once from Python:
    /// run_whisper.build_meeting_prompt("hello transcript", "en" | "zh").
    /// Python "zh" is Hearsay's "zh-TW". Kept as literals (not read from
    /// shared/) so a change to the shared files that breaks Python parity fails here.
    /// </summary>
    private const string PythonPromptEN =
        "You are a professional meeting note-taker. Based on the following meeting transcript, organize a clear and well-structured set of meeting notes.\n"
        + "The selected output language is English.\n"
        + "Write the entire meeting note in English, including the section headings, summary, discussion points, decisions, action items, labels, and explanations. Do not mix in another language unless preserving a proper noun, product name, or direct quote from the transcript.\n"
        + "\n"
        + "Please include the following sections, translating each heading into English:\n"
        + "1. **Meeting Topic and Summary**\n"
        + "2. **Key Discussion Points and Decisions**\n"
        + "3. **Action Items (tasks, owners, and follow-up timelines)**\n"
        + "\n"
        + "Also rewrite the SRT content as a polished, structured transcript in English. Preserve useful timestamps, combine fragmented caption lines into readable paragraphs, group the discussion under descriptive topic headings, and add speaker labels only when the speaker can be identified reliably. Do not invent speakers or content.\n"
        + "\n"
        + "Choose a short, descriptive English filename based on the main topic, regardless of the selected output language. Use only lowercase ASCII letters, digits, and hyphens. Do not include a date, time, path, or extension in the filename. Begin each Markdown document with a heading. Return only valid JSON in exactly this shape:\n"
        + "{\"filename\": \"short-descriptive-name\", \"markdown\": \"the complete meeting notes in Markdown\", \"transcript_markdown\": \"the complete structured transcript in Markdown\"}\n"
        + "\n"
        + "The source SRT is below:\n"
        + "---\n"
        + "hello transcript\n"
        + "---";

    private const string PythonPromptZH =
        "You are a professional meeting note-taker. Based on the following meeting transcript, organize a clear and well-structured set of meeting notes.\n"
        + "The selected output language is Traditional Chinese.\n"
        + "Write the entire meeting note in Traditional Chinese, including the section headings, summary, discussion points, decisions, action items, labels, and explanations. Do not mix in another language unless preserving a proper noun, product name, or direct quote from the transcript.\n"
        + "\n"
        + "Please include the following sections, translating each heading into Traditional Chinese:\n"
        + "1. **Meeting Topic and Summary**\n"
        + "2. **Key Discussion Points and Decisions**\n"
        + "3. **Action Items (tasks, owners, and follow-up timelines)**\n"
        + "\n"
        + "Also rewrite the SRT content as a polished, structured transcript in Traditional Chinese. Preserve useful timestamps, combine fragmented caption lines into readable paragraphs, group the discussion under descriptive topic headings, and add speaker labels only when the speaker can be identified reliably. Do not invent speakers or content.\n"
        + "\n"
        + "Choose a short, descriptive English filename based on the main topic, regardless of the selected output language. Use only lowercase ASCII letters, digits, and hyphens. Do not include a date, time, path, or extension in the filename. Begin each Markdown document with a heading. Return only valid JSON in exactly this shape:\n"
        + "{\"filename\": \"short-descriptive-name\", \"markdown\": \"the complete meeting notes in Markdown\", \"transcript_markdown\": \"the complete structured transcript in Markdown\"}\n"
        + "\n"
        + "The source SRT is below:\n"
        + "---\n"
        + "hello transcript\n"
        + "---";

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static int Occurrences(string text, string part) =>
        text.Split(part).Length - 1;

    // MeetingPrompt

    [Fact]
    public void DefaultPromptIsByteIdenticalToPython()
    {
        Assert.Equal(PythonPromptEN, MeetingPrompt.Build("hello transcript", "en"));
        Assert.Equal(PythonPromptZH, MeetingPrompt.Build("hello transcript", "zh-TW"));
        Assert.Equal(Utf8(PythonPromptZH), Utf8(MeetingPrompt.Build("hello transcript", "zh-TW")));
        Assert.Equal(Utf8(PythonPromptEN), Utf8(MeetingPrompt.Build("hello transcript", "en")));
    }

    // prompt assertions in test_srt_cleanup_and_prompt_language
    [Fact]
    public void PromptLanguage()
    {
        var english = MeetingPrompt.Build("Hello", "en");
        Assert.Contains("The selected output language is English.", english, StringComparison.Ordinal);
        Assert.Contains("Write the entire meeting note in English", english, StringComparison.Ordinal);
        Assert.Contains("1. **Meeting Topic and Summary**", english, StringComparison.Ordinal);
        Assert.Contains("3. **Action Items (tasks, owners, and follow-up timelines)**", english, StringComparison.Ordinal);

        var chinese = MeetingPrompt.Build("Hello", "zh-TW");
        Assert.Contains("The selected output language is Traditional Chinese.", chinese, StringComparison.Ordinal);
        Assert.Contains("Write the entire meeting note in Traditional Chinese", chinese, StringComparison.Ordinal);
        Assert.Contains("translating each heading into Traditional Chinese", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("The selected output language is English.", chinese, StringComparison.Ordinal);
        Assert.Contains("\"filename\": \"short-descriptive-name\"", chinese, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageNames()
    {
        var expected = new Dictionary<string, string>
        {
            ["en"] = "English",
            ["zh-TW"] = "Traditional Chinese",
            ["zh-CN"] = "Simplified Chinese",
            ["de"] = "German",
            ["es"] = "Spanish",
        };
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            MeetingPrompt.Languages.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        foreach (var language in TranscriptLanguages.All)
        {
            Assert.True(MeetingPrompt.Languages.ContainsKey(language.Code()), language.Code());
        }
    }

    [Fact]
    public void SimplifiedChinesePrompt()
    {
        var prompt = MeetingPrompt.Build("hello transcript", "zh-CN");
        Assert.Equal(PythonPromptZH.Replace("Traditional Chinese", "Simplified Chinese", StringComparison.Ordinal), prompt);
        Assert.Contains("The selected output language is Simplified Chinese.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Traditional Chinese", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de", "German")]
    [InlineData("es", "Spanish")]
    public void GermanAndSpanishPrompts(string code, string name)
    {
        var prompt = MeetingPrompt.Build("hello transcript", code);
        // Every {output_language} slot gets the language name: the prompt is
        // the English one with "English" swapped only in those slots.
        var placeholders = Occurrences(PromptTemplate.GeneralMeeting.Instructions, PromptTemplate.OutputLanguagePlaceholder);
        Assert.Equal(4, placeholders);
        Assert.Equal(placeholders, Occurrences(prompt, name));
        Assert.Contains($"The selected output language is {name}.", prompt, StringComparison.Ordinal);
        Assert.Contains($"Write the entire meeting note in {name}, including", prompt, StringComparison.Ordinal);
        Assert.Contains($"translating each heading into {name}:", prompt, StringComparison.Ordinal);
        Assert.Contains($"structured transcript in {name}. Preserve", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptTemplate.OutputLanguagePlaceholder, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The selected output language is English.", prompt, StringComparison.Ordinal);
        var expected = PromptTemplate.GeneralMeeting.Instructions
            .Replace(PromptTemplate.OutputLanguagePlaceholder, name, StringComparison.Ordinal);
        Assert.StartsWith(expected + "\n\n" + MeetingPrompt.ResponseRules, prompt, StringComparison.Ordinal);
        Assert.Contains("English filename based on the main topic", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedLanguageThrows()
    {
        var french = Assert.Throws<MeetingPromptException>(() => MeetingPrompt.Build("Hello", "fr"));
        Assert.Equal("fr", french.LanguageCode);
        Assert.Equal("unsupported meeting-note language: fr", french.Message);
        var bareChinese = Assert.Throws<MeetingPromptException>(() => MeetingPrompt.Build("Hello", "zh"));
        Assert.Equal("zh", bareChinese.LanguageCode);
        Assert.Equal("unsupported meeting-note language: zh", bareChinese.Message);
    }

    [Fact]
    public void CustomTemplateKeepsFixedRulesAndTranscript()
    {
        var template = new PromptTemplate("Standup", "Summarize the standup in {output_language}.");
        var prompt = MeetingPrompt.Build("T", "zh-TW", template);
        Assert.StartsWith("Summarize the standup in Traditional Chinese.\n\nChoose a short, descriptive English filename",
            prompt, StringComparison.Ordinal);
        Assert.Contains("{\"filename\": \"short-descriptive-name\", \"markdown\": ", prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n\nThe source SRT is below:\n---\nT\n---", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Meeting Topic and Summary", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInTemplate()
    {
        Assert.True(PromptTemplate.GeneralMeeting.IsBuiltIn);
        Assert.Equal("General meeting", PromptTemplate.GeneralMeeting.Name);
        Assert.Equal(PromptTemplate.GeneralMeetingId, PromptTemplate.GeneralMeeting.Id);
        Assert.Equal(new Guid("6E0B5A10-3C2D-4F51-9A7E-000000000001"), PromptTemplate.GeneralMeetingId);
        var json = JsonSerializer.Serialize(PromptTemplate.GeneralMeeting);
        Assert.Equal(PromptTemplate.GeneralMeeting, JsonSerializer.Deserialize<PromptTemplate>(json));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["id", "name", "instructions", "isBuiltIn"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void NewTemplatesGetFreshIdsAndAreNotBuiltIn()
    {
        var first = new PromptTemplate("A", "x");
        var second = new PromptTemplate("A", "x");
        Assert.NotEqual(first.Id, second.Id);
        Assert.False(first.IsBuiltIn);
    }

    [Fact]
    public void SystemMessageMatchesPython()
    {
        Assert.Equal("You are a professional and efficient meeting-note assistant.", MeetingPrompt.SystemMessage);
    }

    // shared/prompts (the embedded text is the same bytes)

    [Fact]
    public void GeneralMeetingTemplateMatchesSharedFile()
    {
        Assert.Equal(File.ReadAllBytes(SharedFiles.Path("prompts", "general-meeting.txt")),
            Utf8(PromptTemplate.GeneralMeeting.Instructions));
    }

    [Fact]
    public void ResponseRulesMatchSharedFile()
    {
        Assert.Equal(File.ReadAllBytes(SharedFiles.Path("prompts", "response-rules.txt")),
            Utf8(MeetingPrompt.ResponseRules));
    }

    [Fact]
    public void SystemMessageMatchesSharedFile()
    {
        Assert.Equal(File.ReadAllBytes(SharedFiles.Path("prompts", "system-message.txt")),
            Utf8(MeetingPrompt.SystemMessage));
    }

    [Fact]
    public void LanguagesMatchSharedFile()
    {
        var shared = JsonSerializer.Deserialize<Dictionary<string, string>>(SharedFiles.ReadText("prompts", "languages.json"));
        Assert.NotNull(shared);
        Assert.Equal(shared.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            MeetingPrompt.Languages.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every prompt file under shared/prompts is embedded, byte for byte, and
    /// nothing else is: catches a stale build or a newline-translating copy.
    /// </summary>
    [Fact]
    public void EmbeddedPromptResourcesAreByteIdenticalToSharedFiles()
    {
        var folder = SharedFiles.Path("prompts");
        var files = Directory.GetFiles(folder, "*.txt").Append(Path.Combine(folder, "languages.json"))
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();
        Assert.Contains("general-meeting.txt", files);
        Assert.Contains("response-rules.txt", files);
        Assert.Contains("system-message.txt", files);

        var embedded = typeof(MeetingPrompt).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MeetingPrompt.ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[MeetingPrompt.ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(files, embedded);

        foreach (var file in files)
        {
            var onDisk = File.ReadAllBytes(Path.Combine(folder, file));
            var bytes = MeetingPrompt.ReadResourceBytes(file);
            Assert.Equal(onDisk, bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), file);
            Assert.DoesNotContain((byte)'\r', bytes);
        }
    }

    [Fact]
    public void MissingResourceIsAnActionableError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MeetingPrompt.ReadResourceBytes("nope.txt"));
        Assert.Contains("Hearsay.Core.Prompts.nope.txt", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// shared/prompts/assembly.md describes this concatenation; building it
    /// from the shared files gives the same prompt as <see cref="MeetingPrompt.Build"/>.
    /// </summary>
    [Fact]
    public void SharedFilesAssembleToThePythonPrompt()
    {
        var template = SharedFiles.ReadText("prompts", "general-meeting.txt");
        var rules = SharedFiles.ReadText("prompts", "response-rules.txt");
        var names = JsonSerializer.Deserialize<Dictionary<string, string>>(SharedFiles.ReadText("prompts", "languages.json"));
        Assert.NotNull(names);
        foreach (var (code, python) in new[] { ("en", PythonPromptEN), ("zh-TW", PythonPromptZH) })
        {
            var name = names[code];
            var assembled = template.Replace("{output_language}", name, StringComparison.Ordinal)
                + "\n\n" + rules + "\n\nThe source SRT is below:\n---\n" + "hello transcript" + "\n---";
            Assert.Equal(Utf8(python), Utf8(assembled));
            Assert.Equal(Utf8(assembled), Utf8(MeetingPrompt.Build("hello transcript", code)));
        }
    }

    // NotesResponse (parse assertions in test_ai_result_parsing_and_filename_sanitizing)

    [Fact]
    public void ParsesFencedJson()
    {
        var raw = "```json\n{\"filename\":\"Launch / Plan.md\",\"markdown\":\"# Notes\","
            + "\"transcript_markdown\":\"# Transcript\"}\n```";
        Assert.Equal(new NotesResponse("Launch / Plan.md", "# Notes", "# Transcript"), NotesResponse.Parse(raw));
    }

    [Fact]
    public void ParsesBareAndUppercaseFencedJson()
    {
        const string body = "{\"filename\":\"a\",\"markdown\":\"# N\",\"transcript_markdown\":\"# T\"}";
        var expected = new NotesResponse("a", "# N", "# T");
        Assert.Equal(expected, NotesResponse.Parse($"  {body}\n"));
        Assert.Equal(expected, NotesResponse.Parse($"```JSON\n{body}\n```"));
        Assert.Equal(expected, NotesResponse.Parse($"```{body}```"));
    }

    [Fact]
    public void ValuesAreReturnedUntrimmed()
    {
        var parsed = NotesResponse.Parse("{\"filename\":\" a \",\"markdown\":\"# N\\n\",\"transcript_markdown\":\"\\t# T\"}");
        Assert.Equal(new NotesResponse(" a ", "# N\n", "\t# T"), parsed);
    }

    [Fact]
    public void RejectsNonJson()
    {
        var error = Assert.Throws<NotesResponseException>(() => NotesResponse.Parse("# Notes"));
        Assert.Equal(NotesResponseErrorKind.InvalidJson, error.Kind);
        Assert.StartsWith("AI output is not valid JSON: ", error.Reason, StringComparison.Ordinal);
        Assert.StartsWith("Parsing Error: AI output is not valid JSON: ", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(error.Detail));
        Assert.Equal(NotesResponseErrorKind.InvalidJson, Assert.Throws<NotesResponseException>(() => NotesResponse.Parse("")).Kind);
        Assert.Equal(NotesResponseErrorKind.InvalidJson, Assert.Throws<NotesResponseException>(() => NotesResponse.Parse("{} {}")).Kind);
    }

    [Fact]
    public void RejectsNonObject()
    {
        Assert.Equal(NotesResponseErrorKind.NotAnObject, Assert.Throws<NotesResponseException>(() => NotesResponse.Parse("[1, 2]")).Kind);
        Assert.Equal(NotesResponseErrorKind.NotAnObject, Assert.Throws<NotesResponseException>(() => NotesResponse.Parse("\"text\"")).Kind);
        Assert.Equal("AI output must be a JSON object.", NotesResponseException.ReasonFor(NotesResponseErrorKind.NotAnObject));
        Assert.Equal("Parsing Error: AI output must be a JSON object.",
            new NotesResponseException(NotesResponseErrorKind.NotAnObject).Message);
    }

    [Fact]
    public void RejectsMissingOrEmptyFields()
    {
        static NotesResponseErrorKind KindOf(string raw) =>
            Assert.Throws<NotesResponseException>(() => NotesResponse.Parse(raw)).Kind;

        Assert.Equal(NotesResponseErrorKind.MissingFilename, KindOf("{\"markdown\":\"# N\",\"transcript_markdown\":\"# T\"}"));
        Assert.Equal(NotesResponseErrorKind.MissingFilename, KindOf("{\"filename\":3,\"markdown\":\"# N\",\"transcript_markdown\":\"# T\"}"));
        Assert.Equal(NotesResponseErrorKind.MissingMarkdown, KindOf("{\"filename\":\"a\",\"markdown\":\"  \",\"transcript_markdown\":\"# T\"}"));
        Assert.Equal(NotesResponseErrorKind.MissingTranscriptMarkdown, KindOf("{\"filename\":\"a\",\"markdown\":\"# N\",\"transcript_markdown\":\"\"}"));
        // The first failing field wins, in contract order.
        Assert.Equal(NotesResponseErrorKind.MissingFilename, KindOf("{}"));
        Assert.Equal(NotesResponseErrorKind.MissingMarkdown, KindOf("{\"filename\":\"a\",\"markdown\":null}"));

        Assert.Equal("AI output did not contain a usable filename.",
            NotesResponseException.ReasonFor(NotesResponseErrorKind.MissingFilename));
        Assert.Equal("AI output did not contain usable Markdown notes.",
            NotesResponseException.ReasonFor(NotesResponseErrorKind.MissingMarkdown));
        Assert.Equal("AI output did not contain a usable structured transcript.",
            NotesResponseException.ReasonFor(NotesResponseErrorKind.MissingTranscriptMarkdown));
    }
}
