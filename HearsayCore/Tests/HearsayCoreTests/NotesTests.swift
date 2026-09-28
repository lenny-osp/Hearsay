import Foundation
import Testing
@testable import HearsayCore

/// Generated once from Python:
/// run_whisper.build_meeting_prompt("hello transcript", "en" | "zh").
/// Python "zh" is Hearsay's "zh-TW".
private let pythonPromptEN = #"""
        You are a professional meeting note-taker. Based on the following meeting transcript, organize a clear and well-structured set of meeting notes.
        The selected output language is English.
        Write the entire meeting note in English, including the section headings, summary, discussion points, decisions, action items, labels, and explanations. Do not mix in another language unless preserving a proper noun, product name, or direct quote from the transcript.

        Please include the following sections, translating each heading into English:
        1. **Meeting Topic and Summary**
        2. **Key Discussion Points and Decisions**
        3. **Action Items (tasks, owners, and follow-up timelines)**

        Also rewrite the SRT content as a polished, structured transcript in English. Preserve useful timestamps, combine fragmented caption lines into readable paragraphs, group the discussion under descriptive topic headings, and add speaker labels only when the speaker can be identified reliably. Do not invent speakers or content.

        Choose a short, descriptive English filename based on the main topic, regardless of the selected output language. Use only lowercase ASCII letters, digits, and hyphens. Do not include a date, time, path, or extension in the filename. Begin each Markdown document with a heading. Return only valid JSON in exactly this shape:
        {"filename": "short-descriptive-name", "markdown": "the complete meeting notes in Markdown", "transcript_markdown": "the complete structured transcript in Markdown"}

        The source SRT is below:
        ---
        hello transcript
        ---
        """#

private let pythonPromptZH = #"""
        You are a professional meeting note-taker. Based on the following meeting transcript, organize a clear and well-structured set of meeting notes.
        The selected output language is Traditional Chinese.
        Write the entire meeting note in Traditional Chinese, including the section headings, summary, discussion points, decisions, action items, labels, and explanations. Do not mix in another language unless preserving a proper noun, product name, or direct quote from the transcript.

        Please include the following sections, translating each heading into Traditional Chinese:
        1. **Meeting Topic and Summary**
        2. **Key Discussion Points and Decisions**
        3. **Action Items (tasks, owners, and follow-up timelines)**

        Also rewrite the SRT content as a polished, structured transcript in Traditional Chinese. Preserve useful timestamps, combine fragmented caption lines into readable paragraphs, group the discussion under descriptive topic headings, and add speaker labels only when the speaker can be identified reliably. Do not invent speakers or content.

        Choose a short, descriptive English filename based on the main topic, regardless of the selected output language. Use only lowercase ASCII letters, digits, and hyphens. Do not include a date, time, path, or extension in the filename. Begin each Markdown document with a heading. Return only valid JSON in exactly this shape:
        {"filename": "short-descriptive-name", "markdown": "the complete meeting notes in Markdown", "transcript_markdown": "the complete structured transcript in Markdown"}

        The source SRT is below:
        ---
        hello transcript
        ---
        """#

@Suite struct NotesTests {
    // MARK: MeetingPrompt

    @Test func defaultPromptIsByteIdenticalToPython() throws {
        #expect(try MeetingPrompt.build(transcript: "hello transcript", languageCode: "en") == pythonPromptEN)
        #expect(try MeetingPrompt.build(transcript: "hello transcript", languageCode: "zh-TW") == pythonPromptZH)
        let zhTW = try MeetingPrompt.build(transcript: "hello transcript", languageCode: "zh-TW")
        #expect(Array(zhTW.utf8) == Array(pythonPromptZH.utf8))
        let en = try MeetingPrompt.build(transcript: "hello transcript", languageCode: "en")
        #expect(Array(en.utf8) == Array(pythonPromptEN.utf8))
    }

    // prompt assertions in test_srt_cleanup_and_prompt_language
    @Test func promptLanguage() throws {
        let english = try MeetingPrompt.build(transcript: "Hello", languageCode: "en")
        #expect(english.contains("The selected output language is English."))
        #expect(english.contains("Write the entire meeting note in English"))
        #expect(english.contains("1. **Meeting Topic and Summary**"))
        #expect(english.contains("3. **Action Items (tasks, owners, and follow-up timelines)**"))

        let chinese = try MeetingPrompt.build(transcript: "Hello", languageCode: "zh-TW")
        #expect(chinese.contains("The selected output language is Traditional Chinese."))
        #expect(chinese.contains("Write the entire meeting note in Traditional Chinese"))
        #expect(chinese.contains("translating each heading into Traditional Chinese"))
        #expect(!chinese.contains("The selected output language is English."))
        #expect(chinese.contains(#""filename": "short-descriptive-name""#))
    }

    @Test func languageNames() {
        #expect(MeetingPrompt.languages == [
            "en": "English", "zh-TW": "Traditional Chinese", "zh-CN": "Simplified Chinese",
            "de": "German", "es": "Spanish",
        ])
        for language in TranscriptLanguage.allCases {
            #expect(MeetingPrompt.languages[language.rawValue] != nil)
        }
    }

    @Test func simplifiedChinesePrompt() throws {
        let prompt = try MeetingPrompt.build(transcript: "hello transcript", languageCode: "zh-CN")
        #expect(prompt == pythonPromptZH.replacingOccurrences(of: "Traditional Chinese", with: "Simplified Chinese"))
        #expect(prompt.contains("The selected output language is Simplified Chinese."))
        #expect(!prompt.contains("Traditional Chinese"))
    }

    @Test(arguments: [("de", "German"), ("es", "Spanish")])
    func germanAndSpanishPrompts(code: String, name: String) throws {
        let prompt = try MeetingPrompt.build(transcript: "hello transcript", languageCode: code)
        // Every {output_language} slot gets the language name: the prompt is
        // the English one with "English" swapped only in those slots.
        let placeholders = PromptTemplate.generalMeeting.instructions
            .components(separatedBy: PromptTemplate.outputLanguagePlaceholder).count - 1
        #expect(placeholders == 4)
        #expect(prompt.components(separatedBy: name).count - 1 == placeholders)
        #expect(prompt.contains("The selected output language is \(name)."))
        #expect(prompt.contains("Write the entire meeting note in \(name), including"))
        #expect(prompt.contains("translating each heading into \(name):"))
        #expect(prompt.contains("structured transcript in \(name). Preserve"))
        #expect(!prompt.contains(PromptTemplate.outputLanguagePlaceholder))
        #expect(!prompt.contains("The selected output language is English."))
        let expected = PromptTemplate.generalMeeting.instructions
            .replacingOccurrences(of: PromptTemplate.outputLanguagePlaceholder, with: name)
        #expect(prompt.hasPrefix(expected + "\n\n" + MeetingPrompt.responseRules))
        #expect(prompt.contains("English filename based on the main topic"))
    }

    @Test func unsupportedLanguageThrows() {
        #expect(throws: MeetingPromptError.unsupportedLanguage("fr")) {
            try MeetingPrompt.build(transcript: "Hello", languageCode: "fr")
        }
        #expect(throws: MeetingPromptError.unsupportedLanguage("zh")) {
            try MeetingPrompt.build(transcript: "Hello", languageCode: "zh")
        }
    }

    @Test func customTemplateKeepsFixedRulesAndTranscript() throws {
        let template = PromptTemplate(name: "Standup", instructions: "Summarize the standup in {output_language}.")
        let prompt = try MeetingPrompt.build(transcript: "T", languageCode: "zh-TW", template: template)
        #expect(prompt.hasPrefix("Summarize the standup in Traditional Chinese.\n\nChoose a short, descriptive English filename"))
        #expect(prompt.contains(#"{"filename": "short-descriptive-name", "markdown": "#))
        #expect(prompt.hasSuffix("\n\nThe source SRT is below:\n---\nT\n---"))
        #expect(!prompt.contains("Meeting Topic and Summary"))
    }

    @Test func builtInTemplate() throws {
        #expect(PromptTemplate.generalMeeting.isBuiltIn)
        #expect(PromptTemplate.generalMeeting.name == "General meeting")
        #expect(PromptTemplate.generalMeeting.id == PromptTemplate.generalMeetingID)
        let data = try JSONEncoder().encode(PromptTemplate.generalMeeting)
        #expect(try JSONDecoder().decode(PromptTemplate.self, from: data) == .generalMeeting)
    }

    @Test func systemMessageMatchesPython() {
        #expect(MeetingPrompt.systemMessage == "You are a professional and efficient meeting-note assistant.")
    }

    // MARK: NotesResponse (parse assertions in test_ai_result_parsing_and_filename_sanitizing)

    @Test func parsesFencedJSON() throws {
        let raw = "```json\n{\"filename\":\"Launch / Plan.md\",\"markdown\":\"# Notes\","
            + "\"transcript_markdown\":\"# Transcript\"}\n```"
        #expect(try NotesResponse.parse(raw) == NotesResponse(
            filename: "Launch / Plan.md", markdown: "# Notes", transcriptMarkdown: "# Transcript"
        ))
    }

    @Test func parsesBareAndUppercaseFencedJSON() throws {
        let body = ##"{"filename":"a","markdown":"# N","transcript_markdown":"# T"}"##
        let expected = NotesResponse(filename: "a", markdown: "# N", transcriptMarkdown: "# T")
        #expect(try NotesResponse.parse("  \(body)\n") == expected)
        #expect(try NotesResponse.parse("```JSON\n\(body)\n```") == expected)
        #expect(try NotesResponse.parse("```\(body)```") == expected)
    }

    @Test func rejectsNonJSON() {
        #expect {
            try NotesResponse.parse("# Notes")
        } throws: { error in
            guard case NotesResponseError.invalidJSON = error else { return false }
            return (error as? NotesResponseError)?.message.hasPrefix("AI output is not valid JSON: ") == true
        }
    }

    @Test func rejectsNonObject() {
        #expect(throws: NotesResponseError.notAnObject) { try NotesResponse.parse("[1, 2]") }
        #expect(throws: NotesResponseError.notAnObject) { try NotesResponse.parse(#""text""#) }
        #expect(NotesResponseError.notAnObject.message == "AI output must be a JSON object.")
    }

    @Test func rejectsMissingOrEmptyFields() {
        #expect(throws: NotesResponseError.missingFilename) {
            try NotesResponse.parse(##"{"markdown":"# N","transcript_markdown":"# T"}"##)
        }
        #expect(throws: NotesResponseError.missingFilename) {
            try NotesResponse.parse(##"{"filename":3,"markdown":"# N","transcript_markdown":"# T"}"##)
        }
        #expect(throws: NotesResponseError.missingMarkdown) {
            try NotesResponse.parse(##"{"filename":"a","markdown":"  ","transcript_markdown":"# T"}"##)
        }
        #expect(throws: NotesResponseError.missingTranscriptMarkdown) {
            try NotesResponse.parse(##"{"filename":"a","markdown":"# N","transcript_markdown":""}"##)
        }
        #expect(NotesResponseError.missingFilename.message == "AI output did not contain a usable filename.")
        #expect(NotesResponseError.missingMarkdown.message == "AI output did not contain usable Markdown notes.")
        #expect(NotesResponseError.missingTranscriptMarkdown.message
            == "AI output did not contain a usable structured transcript.")
    }
}

