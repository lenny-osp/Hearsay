using System.Text.RegularExpressions;

namespace Hearsay.Core.Naming;

/// <summary>
/// Port of mac/HearsayCore/Sources/HearsayCore/Naming/MeetingNameInserter.swift
/// (itself a port of <c>insert_meeting_name</c> from whisper-tools <c>run_whisper.py</c>).
///
/// Puts <c>**Meeting Name:** &lt;name&gt;</c> directly below the first Markdown
/// heading, removing any meeting-name line the AI already put in that first
/// section. Without a heading, <c># &lt;fallbackHeading&gt;</c> is prepended first.
///
/// Python's MULTILINE <c>^</c> and <c>$</c> only treat <c>\n</c> as a line break;
/// the anchors are spelled out as <c>(?&lt;![^\n])</c> and <c>(?![^\n])</c>, as in
/// the Swift code, so the behavior stays identical. Every pattern is matched
/// against a fresh substring (never with a start offset) so the look-behind
/// sees the substring's start as a line start, as NSRegularExpression does.
/// </summary>
public static partial class MeetingNameInserter
{
    private const string LineStart = @"(?<![^\n])";
    private const string LineEnd = @"(?![^\n])";

    [GeneratedRegex(LineStart + @" {0,3}#{1,6}[ \t]+[^\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^#[^\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex FallbackHeadingLine();

    [GeneratedRegex(LineStart + @" {0,3}#{1,6}[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingStart();

    [GeneratedRegex(LineStart + @"[ \t]*\.?\*\*Meeting Name:\*\*[^\n]*(?:\n|" + LineEnd + ")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeetingNameLine();

    public static string Insert(string markdown, string meetingName, string fallbackHeading)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = markdown;
        var heading = HeadingLine().Match(text);
        if (!heading.Success)
        {
            text = $"# {fallbackHeading}\n\n{markdown.TrimStart()}";
            heading = FallbackHeadingLine().Match(text);
        }
        var headingEnd = heading.Success ? heading.Index + heading.Length : 0;

        var body = text[headingEnd..];
        var start = HeadingStart().Match(body);
        var sectionEnd = start.Success ? start.Index : body.Length;
        var section = MeetingNameLine().Replace(body[..sectionEnd], "");
        var remaining = (section + body[sectionEnd..]).TrimStart('\r', '\n');
        var headingText = text[..headingEnd].TrimEnd();
        var result = $"{headingText}\n\n**Meeting Name:** {meetingName}\n";
        return result + (remaining.Length == 0 ? "" : "\n" + remaining);
    }

    /// <summary>
    /// True when the first section (below the first heading) holds a
    /// <c>**Meeting Name:**</c> line, the line <see cref="Insert"/> replaces. A
    /// document without a heading, or with the line only in a later section,
    /// has none. History &gt; Rename rewrites only notes for which this is true.
    /// </summary>
    public static bool HasMeetingName(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var heading = HeadingLine().Match(markdown);
        if (!heading.Success)
        {
            return false;
        }
        var body = markdown[(heading.Index + heading.Length)..];
        var start = HeadingStart().Match(body);
        var sectionEnd = start.Success ? start.Index : body.Length;
        return MeetingNameLine().IsMatch(body[..sectionEnd]);
    }
}
