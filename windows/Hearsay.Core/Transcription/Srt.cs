using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Transcription;

/// <summary>
/// SubRip rendering, lenient parsing, and the transcript cleanup used for the
/// AI prompt (port of <c>clean_srt_text</c> in <c>run_whisper.py</c>).
/// Port of mac/HearsayCore/Sources/HearsayCore/Transcription/SRT.swift.
/// </summary>
public static partial class Srt
{
    // Render

    /// <summary>
    /// Standard SRT: 1-based index, <c>HH:MM:SS,mmm --&gt; HH:MM:SS,mmm</c>,
    /// text, and a blank line after every block (so the output ends with a
    /// newline). Newlines are always LF.
    /// </summary>
    public static string Render(IEnumerable<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var output = new StringBuilder();
        var index = 0;
        foreach (var segment in segments)
        {
            index += 1;
            output.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            output.Append(Timestamp(segment.Start)).Append(" --> ").Append(Timestamp(segment.End)).Append('\n');
            output.Append(segment.Text).Append("\n\n");
        }
        return output.ToString();
    }

    /// <summary><c>HH:MM:SS,mmm</c>, rounded to the nearest millisecond (half away from zero), never negative.</summary>
    public static string Timestamp(double seconds)
    {
        long totalMilliseconds = double.IsFinite(seconds) && seconds > 0
            ? (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)
            : 0;
        var milliseconds = totalMilliseconds % 1000;
        var totalSeconds = totalMilliseconds / 1000;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            totalSeconds / 3600, totalSeconds / 60 % 60, totalSeconds % 60, milliseconds);
    }

    // Parse

    /// <summary>
    /// Lenient parse: tolerates CRLF or CR line endings, a byte-order mark, a
    /// missing final blank line, a missing index line, and either <c>,</c> or
    /// <c>.</c> as the millisecond separator. Blocks without a timing line are skipped.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.StartsWith('\uFEFF'))
        {
            normalized = normalized[1..];
        }

        var segments = new List<TranscriptSegment>();
        var block = new List<string>();

        void Flush()
        {
            foreach (var (line, position) in block.Select((line, position) => (line, position)))
            {
                if (ParseTiming(line) is { } timing)
                {
                    var body = string.Join('\n', block.Skip(position + 1));
                    segments.Add(new TranscriptSegment(timing.Start, timing.End, body));
                    break;
                }
            }
            block.Clear();
        }

        foreach (var line in normalized.Split('\n'))
        {
            if (IsBlank(line))
            {
                Flush();
            }
            else
            {
                block.Add(line);
            }
        }
        Flush();
        return segments;
    }

    /// <summary>
    /// Swift <c>trimmingCharacters(in: .whitespaces).isEmpty</c>: only tab and
    /// Unicode space separators (Zs) count, not other line or page breaks.
    /// </summary>
    private static bool IsBlank(string line)
    {
        foreach (var character in line)
        {
            if (character != '\t' && char.GetUnicodeCategory(character) != UnicodeCategory.SpaceSeparator)
            {
                return false;
            }
        }
        return true;
    }

    [GeneratedRegex(@"^\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimingPattern();

    private static (double Start, double End)? ParseTiming(string line)
    {
        var match = TimingPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }
        var numbers = new long[8];
        for (var group = 1; group <= 8; group++)
        {
            var digits = match.Groups[group].Value;
            if (group == 4 || group == 8)
            {
                // "5" means 500 ms and "05" means 50 ms, as in decimal fractions.
                digits = digits.PadRight(3, '0');
            }
            // Only ASCII digits parse, as with Swift Int(_:); the regex's \d
            // also matches other scripts' digits.
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }
            numbers[group - 1] = value;
        }
        static double Seconds(long h, long m, long s, long ms) => (((h * 60 + m) * 60 + s) * 1000 + ms) / 1000.0;
        return (
            Seconds(numbers[0], numbers[1], numbers[2], numbers[3]),
            Seconds(numbers[4], numbers[5], numbers[6], numbers[7]));
    }

    // Clean

    /// <summary>
    /// Port of <c>clean_srt_text</c>: drops index lines, timing lines, and
    /// blank lines, keeping every other line with its original line ending.
    /// Line splitting follows Python's <c>str.splitlines(keepends=True)</c>,
    /// and only <c>\r</c> and <c>\n</c> are stripped before the checks, as in Python.
    /// </summary>
    public static string CleanText(string srtText)
    {
        ArgumentNullException.ThrowIfNull(srtText);
        var kept = new StringBuilder();
        foreach (var rawLine in PythonSplitLinesKeepingEnds(srtText))
        {
            var line = rawLine.TrimEnd('\r', '\n');
            if (line.Length == 0)
            {
                continue;
            }
            if (line.All(IsAsciiDigit))
            {
                continue;
            }
            if (StartsWithClockPrefix(line))
            {
                continue;
            }
            kept.Append(rawLine);
        }
        return kept.ToString();
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';

    /// <summary>
    /// Mirrors <c>re.match(r"^[0-9]{2}:[0-9]{2}:[0-9]{2}", line)</c>. Every
    /// accepted character is ASCII, so checking UTF-16 units equals checking scalars.
    /// </summary>
    private static bool StartsWithClockPrefix(string line)
    {
        if (line.Length < 8)
        {
            return false;
        }
        for (var offset = 0; offset < 8; offset++)
        {
            var character = line[offset];
            if (offset == 2 || offset == 5)
            {
                if (character != ':')
                {
                    return false;
                }
            }
            else if (!IsAsciiDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The line boundaries recognized by Python's <c>str.splitlines</c> (all in the BMP).</summary>
    private static bool IsPythonLineBoundary(char character) => (int)character switch
    {
        0x0A or 0x0B or 0x0C or 0x0D or 0x1C or 0x1D or 0x1E or 0x85 or 0x2028 or 0x2029 => true,
        _ => false,
    };

    private static List<string> PythonSplitLinesKeepingEnds(string text)
    {
        var lines = new List<string>();
        var lineStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (!IsPythonLineBoundary(character))
            {
                index += 1;
                continue;
            }
            var next = index + 1;
            if (character == '\r' && next < text.Length && text[next] == '\n')
            {
                next += 1;
            }
            lines.Add(text[lineStart..next]);
            lineStart = next;
            index = next;
        }
        if (lineStart < text.Length)
        {
            lines.Add(text[lineStart..]);
        }
        return lines;
    }
}
