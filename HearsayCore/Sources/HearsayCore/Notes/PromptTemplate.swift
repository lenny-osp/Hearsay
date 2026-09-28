import Foundation

/// A named set of note-taking instructions. The instructions replace the notes
/// section of the meeting prompt; `MeetingPrompt` always appends the fixed
/// filename and JSON-shape rules and the transcript after them.
///
/// The placeholder `{output_language}` in `instructions` is replaced with the
/// selected language name (for example "English") when the prompt is built.
public struct PromptTemplate: Codable, Identifiable, Sendable, Equatable {
    public static let outputLanguagePlaceholder = "{output_language}"

    public var id: UUID
    public var name: String
    public var instructions: String
    public var isBuiltIn: Bool

    public init(id: UUID = UUID(), name: String, instructions: String, isBuiltIn: Bool = false) {
        self.id = id
        self.name = name
        self.instructions = instructions
        self.isBuiltIn = isBuiltIn
    }

    /// Stable id so the built-in template can be referenced from settings.
    public static let generalMeetingID = UUID(uuid: (
        0x6E, 0x0B, 0x5A, 0x10, 0x3C, 0x2D, 0x4F, 0x51,
        0x9A, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
    ))

    /// Built-in "General meeting" template: the notes section of Python
    /// `build_meeting_prompt`, verbatim, with `{output_language}` placeholders.
    public static let generalMeeting = PromptTemplate(
        id: generalMeetingID,
        name: "General meeting",
        instructions: """
            You are a professional meeting note-taker. Based on the following meeting transcript, organize a clear and well-structured set of meeting notes.
            The selected output language is {output_language}.
            Write the entire meeting note in {output_language}, including the section headings, summary, discussion points, decisions, action items, labels, and explanations. Do not mix in another language unless preserving a proper noun, product name, or direct quote from the transcript.

            Please include the following sections, translating each heading into {output_language}:
            1. **Meeting Topic and Summary**
            2. **Key Discussion Points and Decisions**
            3. **Action Items (tasks, owners, and follow-up timelines)**

            Also rewrite the SRT content as a polished, structured transcript in {output_language}. Preserve useful timestamps, combine fragmented caption lines into readable paragraphs, group the discussion under descriptive topic headings, and add speaker labels only when the speaker can be identified reliably. Do not invent speakers or content.
            """,
        isBuiltIn: true
    )
}
