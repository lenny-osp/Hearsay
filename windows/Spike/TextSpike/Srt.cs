namespace TextSpike;

/// <summary>Minimal SRT reading for the spike: cue texts, and SRT.cleanText
/// from mac/HearsayCore/Sources/HearsayCore/.../SRT.swift (lines that are
/// empty, all ASCII digits, or start with "00:00:00" are dropped; the kept
/// lines keep their line endings). Only \n, \r\n and \r split lines here.</summary>
internal static class Srt
{
    public static string SharedDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "shared", "fixtures");
            if (Directory.Exists(candidate)) return Path.Combine(dir, "shared");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("shared/fixtures not found above " + AppContext.BaseDirectory);
    }

    public static string Fixture(string name) => Path.Combine(SharedDir(), "fixtures", name);

    /// <summary>Reads UTF-8 without translating newlines.</summary>
    public static string Read(string path) => new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));

    public static List<string> SplitLinesKeepingEnds(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                int end = i + 1;
                if (c == '\r' && end < text.Length && text[end] == '\n') end++;
                lines.Add(text[start..end]);
                start = end;
                i = end - 1;
            }
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static bool IsClockPrefix(string line) =>
        line.Length >= 8 && char.IsAsciiDigit(line[0]) && char.IsAsciiDigit(line[1]) && line[2] == ':'
        && char.IsAsciiDigit(line[3]) && char.IsAsciiDigit(line[4]) && line[5] == ':'
        && char.IsAsciiDigit(line[6]) && char.IsAsciiDigit(line[7]);

    public static string CleanText(string srt)
    {
        var kept = new System.Text.StringBuilder();
        foreach (string raw in SplitLinesKeepingEnds(srt))
        {
            string line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0) continue;
            if (line.All(char.IsAsciiDigit)) continue;
            if (IsClockPrefix(line)) continue;
            kept.Append(raw);
        }
        return kept.ToString();
    }

    /// <summary>The text of each cue (lines after the timing line, joined by \n).</summary>
    public static List<string> CueTexts(string srt)
    {
        var cues = new List<string>();
        foreach (string block in srt.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string[] lines = block.Split('\n');
            int timing = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0) continue;
            string text = string.Join("\n", lines.Skip(timing + 1)).TrimEnd('\n');
            if (text.Length > 0) cues.Add(text);
        }
        return cues;
    }

    /// <summary>`srt` with each cue's text lines replaced by `convert(line)`;
    /// index and timing lines and every line ending kept byte for byte.</summary>
    public static string MapCueLines(string srt, Func<string, string> convert)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string raw in SplitLinesKeepingEnds(srt))
        {
            string line = raw.TrimEnd('\r', '\n');
            string ending = raw[line.Length..];
            bool isText = line.Length > 0 && !line.All(char.IsAsciiDigit) && !IsClockPrefix(line);
            sb.Append(isText ? convert(line) : line).Append(ending);
        }
        return sb.ToString();
    }
}
