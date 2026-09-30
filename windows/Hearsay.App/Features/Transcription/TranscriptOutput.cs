using System.Text;
using Hearsay.Core.Audio;
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

    /// <summary>
    /// A recording's WAV while its transcript is made: still in the spool
    /// (after a recording) or already in the output folder (after a failed
    /// pass or a capture failure).
    /// </summary>
    public sealed record PendingRecording(string Path, bool InSpool);

    /// <summary>Where <see cref="SaveTranscript"/> put the files.</summary>
    /// <param name="Srt">The transcript.</param>
    /// <param name="Wav">The kept recording, or null when "Keep the recording" is off.</param>
    /// <param name="Notice">Set when the WAV could not be moved and stays in the spool.</param>
    public sealed record SavedTranscript(string Srt, string? Wav, string? Notice);

    /// <summary>
    /// The outcome of <see cref="SaveTranscript"/>: <see cref="Saved"/>, or
    /// <see cref="Failure"/> (nothing written; the caller then keeps the
    /// recording with <see cref="KeepAfterFailure"/>).
    /// </summary>
    public sealed record SaveResult(SavedTranscript? Saved, string? Failure);

    /// <summary>
    /// Writes <c>&lt;stem&gt;.srt</c> into the output folder beside the
    /// recording and then keeps or deletes the WAV as
    /// <paramref name="keepRecording"/> says (PLAN.md 4.1 steps 4 and 6). A
    /// recording still in the spool gets a free stem in the output folder; one
    /// already in the output folder (a retry) keeps its stem, and its SRT
    /// (possibly the saved live preview) is replaced. Port of
    /// <c>saveTranscript</c> in mac/Hearsay/Features/Transcription/TranscriptOutput.swift.
    /// </summary>
    public static SaveResult SaveTranscript(
        IReadOnlyList<TranscriptSegment> cues, PendingRecording recording, bool keepRecording, AppSettings settings)
    {
        string folder;
        try
        {
            folder = ResolveFolder(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new SaveResult(null, Strings.OutputFolderOpenFailed(error.Message));
        }
        var source = recording.Path;
        if (!File.Exists(source)) return new SaveResult(null, Strings.RecordingMissing);
        var baseStem = System.IO.Path.GetFileNameWithoutExtension(source);
        var directory = recording.InSpool ? folder : System.IO.Path.GetDirectoryName(source) ?? folder;
        var stem = recording.InSpool ? FreeStem(baseStem, directory, [".srt", ".wav"]) : baseStem;
        var srt = System.IO.Path.Combine(directory, stem + ".srt");
        try
        {
            WriteSrt(cues, srt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new SaveResult(null, Strings.CouldNotWriteTranscript(error.Message));
        }

        string? wav = null;
        string? notice = null;
        if (keepRecording)
        {
            if (recording.InSpool)
            {
                var destination = System.IO.Path.Combine(directory, stem + ".wav");
                try
                {
                    File.Move(source, destination, overwrite: false);
                    wav = destination;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    wav = source;
                    notice = Strings.CouldNotMoveRecordingKeptAt(error.Message, source);
                }
            }
            else
            {
                wav = source;
            }
        }
        else
        {
            TryDelete(source);
        }
        return new SaveResult(new SavedTranscript(srt, wav, notice), null);
    }

    /// <summary>What <see cref="KeepAfterFailure"/> kept.</summary>
    /// <param name="Message">The full message: the reason plus where the files are.</param>
    /// <param name="Recording">The kept WAV (in the output folder unless moving it failed).</param>
    /// <param name="Srt">The saved live preview, or the transcript that was already there.</param>
    public sealed record KeptRecording(string Message, PendingRecording? Recording, string? Srt);

    /// <summary>
    /// PLAN.md 4.1 failure rules: keeps the WAV (moved to the output folder
    /// when it is still in the spool), saves <paramref name="liveSegments"/>
    /// as its SRT when there are any and no transcript exists yet, and
    /// appends the paths to <paramref name="message"/>. Port of
    /// <c>keepAfterFailure</c>.
    /// </summary>
    public static KeptRecording KeepAfterFailure(
        string message, PendingRecording? recording, IReadOnlyList<TranscriptSegment> liveSegments,
        string? existingTranscript, AppSettings settings)
    {
        var lines = new List<string> { message };
        var kept = recording;
        var srt = existingTranscript;
        string? folder = null;
        try
        {
            folder = ResolveFolder(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"recording: output folder: {error.Message}");
        }

        if (recording is { InSpool: true } spoolWav && folder is not null)
        {
            try
            {
                if (RecordingSpool.Finalize(spoolWav.Path, keep: true, folder) is { } moved)
                {
                    kept = new PendingRecording(moved, InSpool: false);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lines.Add(Strings.CouldNotMoveRecording(error.Message));
            }
        }

        if (kept?.Path is { } wav && liveSegments.Count > 0 && existingTranscript is null)
        {
            var live = System.IO.Path.ChangeExtension(wav, ".srt");
            try
            {
                WriteSrt(liveSegments, live);
                srt = live;
                lines.Add(Strings.LivePreviewSavedAs(live));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lines.Add(Strings.LivePreviewNotSaved(error.Message));
            }
        }
        if (kept?.Path is { } keptWav) lines.Add(Strings.RecordingKeptAt(keptWav));
        return new KeptRecording(string.Join("\n", lines), kept, srt);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"recording: could not delete {path}: {error.Message}");
        }
    }

    /// <summary>Two paths name the same folder (full paths, ignoring case and a trailing separator).</summary>
    public static bool SameFolder(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}
