using System.Globalization;
using Hearsay.Core.Naming;

namespace Hearsay.Core.History;

/// <summary>
/// One meeting in the output folder: every file sharing a stem
/// (<c>&lt;stem&gt;.srt</c>, <c>&lt;stem&gt;.md</c>, <c>&lt;stem&gt;_transcript.md</c>,
/// <c>&lt;stem&gt;.wav</c>). Port of <c>HistoryEntry</c> in
/// mac/HearsayCore/Sources/HearsayCore/History/HistoryIndex.swift.
/// </summary>
/// <param name="Stem">The stem; unique within a folder.</param>
/// <param name="Timestamp">Parsed from the timestamp embedded in the stem, in the local time zone.</param>
/// <param name="MeetingName">The part after <c>&lt;timestamp&gt;_</c>, null for plain timestamp names.</param>
/// <param name="Srt">Full path of the SRT, if any.</param>
/// <param name="Notes">Full path of the notes, if any.</param>
/// <param name="Transcript">Full path of the structured transcript, if any.</param>
/// <param name="Audio">Full path of the WAV, if any.</param>
/// <param name="AudioDuration">Duration of <paramref name="Audio"/> in seconds, null when there is none or it is unreadable.</param>
public sealed record HistoryEntry(
    string Stem,
    DateTimeOffset? Timestamp,
    string? MeetingName,
    string? Srt,
    string? Notes,
    string? Transcript,
    string? Audio,
    double? AudioDuration)
{
    /// <summary>The stem; unique within a folder.</summary>
    public string Id => Stem;

    /// <summary>Every existing file of the entry, in SRT, notes, transcript, audio order.</summary>
    public IReadOnlyList<string> Files =>
        new[] { Srt, Notes, Transcript, Audio }.OfType<string>().ToList();
}

/// <summary>
/// Builds the History list from the output folder (PLAN.md 4.8). Port of
/// mac/HearsayCore/Sources/HearsayCore/History/HistoryIndex.swift.
/// </summary>
public static class HistoryIndex
{
    private enum Kind
    {
        Srt,
        Notes,
        Transcript,
        Audio,
    }

    /// <summary>Suffixes in match order: <c>_transcript.md</c> before <c>.md</c>.</summary>
    private static readonly (string Suffix, Kind Kind)[] Suffixes =
    [
        ("_transcript.md", Kind.Transcript),
        (".md", Kind.Notes),
        (".srt", Kind.Srt),
        (".wav", Kind.Audio),
    ];

    /// <summary>
    /// Groups the regular files directly inside <paramref name="folder"/> by
    /// stem, newest first (entries without a timestamp last, then by stem
    /// descending). Hidden files (a leading dot, or the Windows hidden
    /// attribute, the counterpart of the Mac's <c>skipsHiddenFiles</c>),
    /// <c>.partial</c>/<c>.tmp</c> files and unrelated extensions are ignored.
    /// Throws when the folder cannot be read.
    /// </summary>
    public static IReadOnlyList<HistoryEntry> Scan(string folder)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(folder));
        var groups = new Dictionary<string, Dictionary<Kind, string>>(StringComparer.Ordinal);
        foreach (var file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            var name = file.Name;
            if (name.StartsWith('.') || file.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var match = Suffixes.FirstOrDefault(item => name.EndsWith(item.Suffix, StringComparison.OrdinalIgnoreCase));
            if (match.Suffix is null)
            {
                continue;
            }
            var stem = name[..^match.Suffix.Length];
            if (stem.Length == 0)
            {
                continue;
            }
            if (!groups.TryGetValue(stem, out var files))
            {
                files = [];
                groups[stem] = files;
            }
            files[match.Kind] = file.FullName;
        }

        var entries = groups
            .Select(group =>
            {
                var audio = group.Value.GetValueOrDefault(Kind.Audio);
                return new HistoryEntry(
                    Stem: group.Key,
                    Timestamp: TimestampDate(group.Key),
                    MeetingName: MeetingName(group.Key),
                    Srt: group.Value.GetValueOrDefault(Kind.Srt),
                    Notes: group.Value.GetValueOrDefault(Kind.Notes),
                    Transcript: group.Value.GetValueOrDefault(Kind.Transcript),
                    Audio: audio,
                    AudioDuration: audio is null ? null : WavDuration(audio));
            })
            .ToList();
        entries.Sort(Compare);
        return entries;
    }

    /// <summary>
    /// The meeting name in <c>&lt;yyyy-MM-dd_HH-mm-ss&gt;_&lt;name&gt;</c>; null when the
    /// stem does not start with a timestamp followed by <c>_</c> and a name.
    /// </summary>
    public static string? MeetingName(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var prefixLength = Timestamps.Format.Length;
        if (stem.Length <= prefixLength + 1)
        {
            return null;
        }
        var prefix = stem[..prefixLength];
        if (!string.Equals(Timestamps.ParseFromFilename(prefix), prefix, StringComparison.Ordinal))
        {
            return null;
        }
        if (stem[prefixLength] != '_')
        {
            return null;
        }
        var name = stem[(prefixLength + 1)..];
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// The first embedded timestamp in <paramref name="stem"/> (the
    /// <see cref="Timestamps"/> rules) as a date in <paramref name="timeZone"/>
    /// (default: local).
    /// </summary>
    public static DateTimeOffset? TimestampDate(string stem, TimeZoneInfo? timeZone = null)
    {
        if (Timestamps.ParseFromFilename(stem) is not { } text)
        {
            return null;
        }
        if (!DateTime.TryParseExact(text, Timestamps.Format, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var clock))
        {
            return null;
        }
        var zone = timeZone ?? TimeZoneInfo.Local;
        return new DateTimeOffset(clock, zone.GetUtcOffset(clock));
    }

    private static int Compare(HistoryEntry lhs, HistoryEntry rhs)
    {
        if (lhs.Timestamp is { } left && rhs.Timestamp is { } right && left != right)
        {
            return right.CompareTo(left);
        }
        if (lhs.Timestamp is not null && rhs.Timestamp is null)
        {
            return -1;
        }
        if (lhs.Timestamp is null && rhs.Timestamp is not null)
        {
            return 1;
        }
        return string.CompareOrdinal(rhs.Stem, lhs.Stem);
    }

    /// <summary>
    /// Duration in seconds of the WAV at <paramref name="path"/>, null when it
    /// is unreadable (Swift: <c>try? WavWriter.duration(of:)</c>).
    /// </summary>
    internal static double? WavDuration(string path)
    {
        try
        {
            return Audio.WavWriter.DurationOf(path);
        }
        catch (Exception error) when (error is Audio.WavException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
