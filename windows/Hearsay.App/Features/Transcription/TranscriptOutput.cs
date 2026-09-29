using System.Text;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Transcription;

/// <summary>
/// Writing transcripts into the output folder (PLAN.md 4.1 step 6, 4.2 step 3).
/// Port of <c>TranscriptOutput</c> in
/// mac/Hearsay/Features/Transcription/TranscriptOutput.swift. Windows has no
/// security scope, so <see cref="ResolveFolder"/> returns a plain path and
/// there is nothing to stop.
/// </summary>
internal static class TranscriptOutput
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// First of <paramref name="baseStem"/>, <c>base-2</c>, <c>base-3</c>, ...
    /// for which no <c>stem + suffix</c> exists in <paramref name="directory"/>
    /// (the same rule as <c>OutputWriter</c> and <c>RecordingSpool</c>).
    /// </summary>
    public static string FreeStem(string baseStem, string directory, IReadOnlyList<string> suffixes)
    {
        var candidate = baseStem;
        var sequence = 2;
        while (suffixes.Any(suffix => File.Exists(Path.Combine(directory, candidate + suffix))))
        {
            candidate = $"{baseStem}-{sequence}";
            sequence++;
        }
        return candidate;
    }

    /// <summary>
    /// Renders <paramref name="cues"/> with <see cref="Srt.Render"/> (LF
    /// newlines, UTF-8 without a BOM) and writes them to <paramref name="path"/>
    /// atomically, replacing a file that is already there.
    /// </summary>
    public static void WriteSrt(IEnumerable<TranscriptSegment> cues, string path)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException($"No folder in {path}", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, Utf8NoBom.GetBytes(Srt.Render(cues)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The output folder from Settings, else the default (created on demand).</summary>
    /// <exception cref="IOException">The default folder cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The default folder cannot be created.</exception>
    public static string ResolveFolder(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return OutputLocation.Resolve(settings.OutputFolder).Path;
    }

    /// <summary>Two paths name the same folder (full paths, ignoring case and a trailing separator).</summary>
    public static bool SameFolder(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}
