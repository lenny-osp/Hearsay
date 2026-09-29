using System.Globalization;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Naming;

/// <summary>
/// Port of mac/HearsayCore/Sources/HearsayCore/Naming/Timestamps.swift: output
/// timestamps (<c>yyyy-MM-dd_HH-mm-ss</c>) and the source-file timestamp rules
/// from whisper-tools <c>source_file_timestamp</c>.
/// </summary>
public static partial class Timestamps
{
    public const string Format = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>
    /// The earliest time NTFS can store; <see cref="File.GetCreationTimeUtc"/>
    /// returns it when the file system keeps no creation time.
    /// </summary>
    private static readonly DateTime NoFileTime = DateTime.FromFileTimeUtc(0);

    // Same pattern as the Python regex in `source_file_timestamp`.
    [GeneratedRegex(@"(?<!\d)(\d{4})[-_]?(\d{2})[-_]?(\d{2})[T _-]?(\d{2})[-_:]?(\d{2})[-_:]?(\d{2})(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Embedded();

    /// <summary>Formats a date in the given (default: local) time zone with the invariant culture.</summary>
    public static string FromDate(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local);
        return local.ToString(Format, CultureInfo.InvariantCulture);
    }

    /// <summary>The current time as an output timestamp.</summary>
    public static string Now() => FromDate(DateTimeOffset.Now);

    /// <summary>
    /// Finds the first embedded timestamp in a filename and returns it in the
    /// output format, or null when there is none or it is not a real date and
    /// time. Like Python, an invalid first match does not fall through to a
    /// later one.
    /// </summary>
    public static string? ParseFromFilename(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var match = Embedded().Match(filename);
        if (!match.Success)
        {
            return null;
        }
        var parts = new int[6];
        for (var group = 1; group <= 6; group++)
        {
            // Swift `Int(_:)` accepts ASCII digits only, while `\d` also
            // matches other Unicode digits; NumberStyles.None does the same.
            if (!int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out parts[group - 1]))
            {
                return null;
            }
        }
        var (year, month, day, hour, minute, second) = (parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
        if (!IsValid(year, month, day, hour, minute, second))
        {
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{year:D4}-{month:D2}-{day:D2}_{hour:D2}-{minute:D2}-{second:D2}");
    }

    /// <summary>
    /// Python priority: timestamp in the filename, then file birth time, then
    /// modification time. Null when the file cannot be inspected.
    /// On Windows the birth time is the file's creation time; the last write
    /// time is used only when the file system keeps no creation time.
    /// </summary>
    public static string? SourceFileTimestamp(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parsed = ParseFromFilename(Path.GetFileName(path));
        if (parsed is not null)
        {
            return parsed;
        }
        try
        {
            FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
            if (!info.Exists)
            {
                return null;
            }
            var created = info.CreationTimeUtc;
            var date = created > NoFileTime ? created : info.LastWriteTimeUtc;
            if (date <= NoFileTime)
            {
                return null;
            }
            return FromDate(new DateTimeOffset(date, TimeSpan.Zero));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Mirrors the range checks of Python's <c>datetime(...)</c> constructor.</summary>
    private static bool IsValid(int year, int month, int day, int hour, int minute, int second)
    {
        if (year is < 1 or > 9999 || month is < 1 or > 12 || hour is < 0 or > 23 || minute is < 0 or > 59
            || second is < 0 or > 59)
        {
            return false;
        }
        var daysInMonth = month switch
        {
            2 => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };
        return day >= 1 && day <= daysInMonth;
    }
}
