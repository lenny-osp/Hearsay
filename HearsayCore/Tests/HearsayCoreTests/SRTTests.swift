import Foundation
import Testing
@testable import HearsayCore

@Suite struct SRTTests {
    // clean_srt_text assertion in test_srt_cleanup_and_prompt_language
    @Test func cleanTextStripsIndicesAndTimestamps() {
        let srt = "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n"
        #expect(SRT.cleanText(srt) == "Hello\nWorld\n")
    }

    @Test func cleanTextKeepsOriginalLineEndings() {
        let srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld"
        #expect(SRT.cleanText(srt) == "Hello\r\nWorld")
        #expect(SRT.cleanText("") == "")
        #expect(SRT.cleanText("1\n\n") == "")
        // Only a line that is all digits is an index; "12 apples" is speech.
        #expect(SRT.cleanText("12 apples\n") == "12 apples\n")
    }

    @Test func renderProducesStandardSRT() {
        let segments = [
            TranscriptSegment(start: 1, end: 2, text: "Hello"),
            TranscriptSegment(start: 3661.5, end: 3662.25, text: "World"),
        ]
        #expect(SRT.render(segments) == """
            1
            00:00:01,000 --> 00:00:02,000
            Hello

            2
            01:01:01,500 --> 01:01:02,250
            World


            """)
        #expect(SRT.render([]) == "")
    }

    @Test func renderThenParseRoundTrips() {
        let segments = [
            TranscriptSegment(start: 0, end: 1.5, text: "First line"),
            TranscriptSegment(start: 1.5, end: 3.25, text: "Two\nlines"),
            TranscriptSegment(start: 3661.001, end: 3700.999, text: "Late"),
        ]
        #expect(SRT.parse(SRT.render(segments)) == segments)
    }

    @Test func parseIsLenient() {
        let text = "\u{FEFF}1\r\n00:00:01.000 --> 00:00:02,5\r\nHello\r\n\r\n00:00:03,000 --> 00:00:04,000\r\nWorld"
        #expect(SRT.parse(text) == [
            TranscriptSegment(start: 1, end: 2.5, text: "Hello"),
            TranscriptSegment(start: 3, end: 4, text: "World"),
        ])
        #expect(SRT.parse("") == [])
        #expect(SRT.parse("just some text\n") == [])
    }

    @Test func cleanTextOfRenderedSegments() {
        let segments = [
            TranscriptSegment(start: 1, end: 2, text: "Hello"),
            TranscriptSegment(start: 3, end: 4, text: "World"),
        ]
        #expect(SRT.cleanText(SRT.render(segments)) == "Hello\nWorld\n")
    }
}
