import Foundation

/// Port of `insert_meeting_name` from whisper-tools `run_whisper.py`.
///
/// Puts `**Meeting Name:** <name>` directly below the first Markdown heading,
/// removing any meeting-name line the AI already put in that first section.
/// Without a heading, `# <fallbackHeading>` is prepended first.
///
/// Python's MULTILINE `^` and `$` only treat `\n` as a line break, while ICU
/// also honors `\r`, U+2028 and others, so the anchors are spelled out as
/// `(?<![^\n])` and `(?![^\n])` to keep the behavior identical.
public enum MeetingNameInserter {
    private static let lineStart = #"(?<![^\n])"#
    private static let lineEnd = #"(?![^\n])"#

    // Force-try is avoided: the patterns are constants, and a nil regex falls
    // back to "no match", which the tests would catch immediately.
    private static let headingLine = try? NSRegularExpression(
        pattern: lineStart + #" {0,3}#{1,6}[ \t]+[^\n]+"#
    )
    private static let fallbackHeadingLine = try? NSRegularExpression(pattern: #"^#[^\n]+"#)
    private static let headingStart = try? NSRegularExpression(
        pattern: lineStart + #" {0,3}#{1,6}[ \t]+"#
    )
    private static let meetingNameLine = try? NSRegularExpression(
        pattern: lineStart + #"[ \t]*\.?\*\*Meeting Name:\*\*[^\n]*(?:\n|"# + lineEnd + ")",
        options: [.caseInsensitive]
    )

    public static func insert(into markdown: String, meetingName: String, fallbackHeading: String) -> String {
        var text = markdown as NSString
        var heading = firstMatch(headingLine, in: text)
        if heading == nil {
            text = "# \(fallbackHeading)\n\n\(lstripWhitespace(markdown))" as NSString
            heading = firstMatch(fallbackHeadingLine, in: text)
        }
        let headingEnd = heading.map { $0.location + $0.length } ?? 0

        let body = text.substring(from: headingEnd) as NSString
        let sectionEnd = firstMatch(headingStart, in: body)?.location ?? body.length
        var section = body.substring(to: sectionEnd)
        if let meetingNameLine {
            section = meetingNameLine.stringByReplacingMatches(
                in: section,
                range: NSRange(location: 0, length: (section as NSString).length),
                withTemplate: ""
            )
        }
        let remaining = lstripLineBreaks(section + body.substring(from: sectionEnd))
        let headingText = rstripWhitespace(text.substring(to: headingEnd))
        let result = "\(headingText)\n\n**Meeting Name:** \(meetingName)\n"
        return result + (remaining.isEmpty ? "" : "\n\(remaining)")
    }

    /// True when the first section (below the first heading) holds a
    /// `**Meeting Name:**` line, the line `insert` replaces. A document
    /// without a heading, or with the line only in a later section, has none.
    /// History > Rename rewrites only notes for which this is true.
    public static func hasMeetingName(_ markdown: String) -> Bool {
        let text = markdown as NSString
        guard let heading = firstMatch(headingLine, in: text) else { return false }
        let body = text.substring(from: heading.location + heading.length) as NSString
        let sectionEnd = firstMatch(headingStart, in: body)?.location ?? body.length
        let section = body.substring(to: sectionEnd) as NSString
        return firstMatch(meetingNameLine, in: section) != nil
    }

    private static func firstMatch(_ regex: NSRegularExpression?, in text: NSString) -> NSRange? {
        guard let regex,
              let match = regex.firstMatch(in: text as String, range: NSRange(location: 0, length: text.length))
        else { return nil }
        return match.range
    }

    private static func lstripWhitespace(_ value: String) -> String {
        String(value.drop(while: { $0.isWhitespace }))
    }

    private static func rstripWhitespace(_ value: String) -> String {
        var slice = Substring(value)
        while let last = slice.last, last.isWhitespace { slice = slice.dropLast() }
        return String(slice)
    }

    /// Python `lstrip("\r\n")`: drops leading CR and LF scalars only.
    private static func lstripLineBreaks(_ value: String) -> String {
        var scalars = Substring(value).unicodeScalars[...]
        while let first = scalars.first, first == "\r" || first == "\n" {
            scalars = scalars.dropFirst()
        }
        return String(String.UnicodeScalarView(scalars))
    }
}
