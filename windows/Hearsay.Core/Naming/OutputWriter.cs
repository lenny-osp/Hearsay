using System.Text;

namespace Hearsay.Core.Naming;

/// <summary>
/// Port of mac/HearsayCore/Sources/HearsayCore/Naming/OutputWriter.swift
/// (itself a port of <c>save_named_outputs</c> and
/// <c>rename_transcription_outputs</c> from whisper-tools <c>run_whisper.py</c>),
/// plus the Hearsay-only <c>replaceNamed</c> (History &gt; Regenerate notes) and
/// <c>renameEntry</c> (History &gt; Rename, PLAN.md 4.8).
///
/// Paths are strings; every path the writer returns or reports is a full
/// path (<see cref="Path.GetFullPath(string)"/>, the Swift <c>standardizedFileURL</c>).
/// Failures throw <see cref="OutputWriterException"/>.
/// </summary>
public static class OutputWriter
{
    /// <summary>Recordings kept beside the SRT under the same base name.</summary>
    public static readonly IReadOnlyList<string> RetainedAudioExtensions = ["wav"];

    public const string NotesFallbackHeading = "Meeting Notes";
    public const string TranscriptFallbackHeading = "Structured Transcript";

    internal delegate void Mover(string source, string destination);

    internal delegate void Writer(string text, string destination);

    /// <summary>
    /// Moves a file to the Recycle Bin and returns where it ended up (null
    /// when the system does not say).
    /// </summary>
    internal delegate string? Trasher(string path);

    /// <summary>
    /// Moves a recycled file from <paramref name="location"/> (what the
    /// <see cref="Trasher"/> returned) back to <paramref name="original"/>.
    /// </summary>
    internal delegate void Restorer(string location, string original);

    /// <summary>The files <c>SaveNamed</c> and <c>ReplaceNamed</c> produce.</summary>
    public sealed record NamedOutputs(string Srt, string Markdown, string Transcript, IReadOnlyList<string> Companions);

    /// <summary>A History entry after <see cref="RenameEntry(string, string, string)"/>.</summary>
    /// <param name="Stem">The entry's stem after the rename.</param>
    /// <param name="Files">
    /// The entry's files under <paramref name="Stem"/>, in SRT, notes,
    /// transcript, audio order (only those that exist).
    /// </param>
    public sealed record RenamedEntry(string Stem, IReadOnlyList<string> Files);

    /// <summary>The Markdown documents <c>SaveNamed</c> writes, by suffix.</summary>
    private static readonly string[] NotesSuffixes = [".md", "_transcript.md"];

    private readonly record struct Rename(string Source, string Destination);

    private readonly record struct Recycled(string Original, string? Location);

    // MARK: - save_named_outputs

    /// <summary>
    /// Renames the SRT to <c>&lt;stem&gt;.srt</c> together with its same-basename
    /// retained audio (<c>&lt;stem&gt;.wav</c>), then writes <c>&lt;stem&gt;.md</c> and
    /// <c>&lt;stem&gt;_transcript.md</c> atomically. The stem is
    /// <c>&lt;timestamp&gt;_&lt;meetingName&gt;</c> plus <c>-N</c> when any of those names
    /// is taken. The timestamp is the given one, else the one in the SRT
    /// filename, else now.
    ///
    /// Deliberate departure from Python <c>save_named_outputs</c>, which renames
    /// only the SRT (PLAN.md 4.3 step 7): Hearsay may keep the WAV in the
    /// output folder before naming, and it must follow the SRT's new name.
    ///
    /// If a rename fails, completed renames are rolled back. If a write fails,
    /// the partial Markdown is removed and every rename rolled back. Either
    /// way the error is thrown.
    /// </summary>
    public static NamedOutputs SaveNamed(string srtPath, string meetingName, string markdown,
        string transcriptMarkdown, string? timestamp) =>
        SaveNamed(srtPath, meetingName, markdown, transcriptMarkdown, timestamp, DefaultMove, WriteAtomically);

    internal static NamedOutputs SaveNamed(string srtPath, string meetingName, string markdown,
        string transcriptMarkdown, string? timestamp, Mover move, Writer writeText)
    {
        var safeName = FilenameSanitizer.Sanitize(meetingName)
            ?? throw new OutputWriterException(new OutputWriterError.UnusableMeetingName());
        var notes = MeetingNameInserter.Insert(markdown, safeName, NotesFallbackHeading);
        var transcript = MeetingNameInserter.Insert(transcriptMarkdown, safeName, TranscriptFallbackHeading);

        var srt = Path.GetFullPath(srtPath);
        var directory = DirectoryOf(srt);
        List<string> sources = [srt, .. RetainedAudioFiles(srt)];
        var sourceSuffixes = sources.Select(ExtensionSuffix).ToList();
        var outputTimestamp = timestamp
            ?? Timestamps.ParseFromFilename(Path.GetFileName(srt))
            ?? Timestamps.Now();
        var stem = FreeStem($"{outputTimestamp}_{safeName}", directory, [.. sourceSuffixes, ".md", "_transcript.md"]);

        var finalNotes = Path.Combine(directory, stem + ".md");
        var finalTranscript = Path.Combine(directory, stem + "_transcript.md");
        var renamed = RenameAll(sources, sourceSuffixes, stem, directory, move);

        var failed = finalNotes;
        try
        {
            writeText(notes, finalNotes);
            failed = finalTranscript;
            writeText(transcript, finalTranscript);
        }
        catch (Exception error)
        {
            RemoveQuietly(finalNotes, finalTranscript);
            var unrestored = RollBack(renamed, move);
            throw new OutputWriterException(new OutputWriterError.WriteFailed(failed, Reason(error), unrestored));
        }
        var destinations = renamed.Select(item => item.Destination).ToList();
        return new NamedOutputs(destinations[0], finalNotes, finalTranscript, destinations.Skip(1).ToList());
    }

    // MARK: - Regenerate notes

    /// <summary>
    /// Replaces the notes of an existing meeting <c>&lt;existingStem&gt;.*</c> in
    /// <paramref name="directory"/> (History &gt; Regenerate notes). The new stem
    /// is <c>&lt;timestamp&gt;_&lt;meetingName&gt;</c> plus <c>-N</c> on collision, where the
    /// timestamp is the given one, else the one in <paramref name="existingStem"/>,
    /// else now, and the entry's own current files do not count as
    /// collisions. So the same name regenerates in place.
    ///
    /// Order: the old <c>.md</c> and <c>_transcript.md</c> go to the Recycle Bin,
    /// the SRT and WAV are renamed only when the stem changed, then the new
    /// Markdown is written atomically. On any failure the partial new Markdown
    /// is removed, renames are rolled back and the recycled notes are moved
    /// back; the error lists what could not be restored.
    /// </summary>
    public static NamedOutputs ReplaceNamed(string existingStem, string directory, string meetingName,
        string markdown, string transcriptMarkdown, string? timestamp) =>
        ReplaceNamed(existingStem, directory, meetingName, markdown, transcriptMarkdown, timestamp,
            DefaultMove, WriteAtomically, DefaultTrash, DefaultRestore);

    /// <summary>
    /// <paramref name="restore"/> puts a recycled file back; without one the
    /// file is moved back with <paramref name="move"/> (the Swift code, where
    /// the Trash is an ordinary folder, and the tests' fake Recycle Bin).
    /// </summary>
    internal static NamedOutputs ReplaceNamed(string existingStem, string directory, string meetingName,
        string markdown, string transcriptMarkdown, string? timestamp, Mover move, Writer writeText, Trasher trash,
        Restorer? restore = null)
    {
        var putBack = restore ?? ((location, original) => move(location, original));
        var safeName = FilenameSanitizer.Sanitize(meetingName)
            ?? throw new OutputWriterException(new OutputWriterError.UnusableMeetingName());
        var notes = MeetingNameInserter.Insert(markdown, safeName, NotesFallbackHeading);
        var transcript = MeetingNameInserter.Insert(transcriptMarkdown, safeName, TranscriptFallbackHeading);

        var folder = Path.GetFullPath(directory);
        var srt = Path.Combine(folder, existingStem + ".srt");
        List<string> sources = [srt, .. RetainedAudioFiles(srt)];
        var sourceSuffixes = sources.Select(ExtensionSuffix).ToList();
        var oldNotes = NotesSuffixes
            .Select(suffix => Path.Combine(folder, existingStem + suffix))
            .Where(Exists)
            .ToList();
        var ownFiles = sources.Concat(oldNotes).Select(FileIdentityOf).OfType<FileIdentity>().ToList();

        var outputTimestamp = timestamp
            ?? Timestamps.ParseFromFilename(existingStem)
            ?? Timestamps.Now();
        var stem = FreeStem($"{outputTimestamp}_{safeName}", folder,
            [.. sourceSuffixes, ".md", "_transcript.md"], ownFiles);
        var finalNotes = Path.Combine(folder, stem + ".md");
        var finalTranscript = Path.Combine(folder, stem + "_transcript.md");

        // 1. Old notes to the Recycle Bin.
        var trashed = new List<Recycled>();
        foreach (var path in oldNotes)
        {
            try
            {
                trashed.Add(new Recycled(path, trash(path)));
            }
            catch (Exception error)
            {
                var unrestored = RestoreFromTrash(trashed, putBack);
                throw new OutputWriterException(new OutputWriterError.TrashFailed(path, Reason(error), unrestored));
            }
        }

        // 2. SRT and WAV follow a changed name.
        List<Rename> renamed = [];
        if (!string.Equals(stem, existingStem, StringComparison.Ordinal))
        {
            try
            {
                renamed = RenameAll(sources, sourceSuffixes, stem, folder, move);
            }
            catch (OutputWriterException exception)
                when (exception.Error is OutputWriterError.RenameFailed failure)
            {
                List<string> notRestored = [.. failure.Unrestored, .. RestoreFromTrash(trashed, putBack)];
                throw new OutputWriterException(failure with { Unrestored = notRestored });
            }
        }

        // 3. New Markdown.
        var failedWrite = finalNotes;
        try
        {
            writeText(notes, finalNotes);
            failedWrite = finalTranscript;
            writeText(transcript, finalTranscript);
        }
        catch (Exception error)
        {
            RemoveQuietly(finalNotes, finalTranscript);
            List<string> unrestored = [.. RollBack(renamed, move), .. RestoreFromTrash(trashed, putBack)];
            throw new OutputWriterException(new OutputWriterError.WriteFailed(failedWrite, Reason(error), unrestored));
        }
        var srtResult = renamed.Count > 0 ? renamed[0].Destination : srt;
        IReadOnlyList<string> companions = renamed.Count == 0
            ? sources.Skip(1).ToList()
            : renamed.Skip(1).Select(item => item.Destination).ToList();
        return new NamedOutputs(srtResult, finalNotes, finalTranscript, companions);
    }

    /// <summary>
    /// Moves recycled notes back to their original paths, newest first.
    /// Returns the current paths of files that could not be restored (the
    /// original path when the Recycle Bin location is unknown).
    /// </summary>
    private static List<string> RestoreFromTrash(List<Recycled> trashed, Restorer restore)
    {
        var unrestored = new List<string>();
        for (var index = trashed.Count - 1; index >= 0; index--)
        {
            var item = trashed[index];
            if (item.Location is not { } location)
            {
                unrestored.Add(item.Original);
                continue;
            }
            try
            {
                restore(location, item.Original);
            }
            catch (Exception)
            {
                unrestored.Add(location);
            }
        }
        return unrestored;
    }

    // MARK: - rename_transcription_outputs

    /// <summary>
    /// Renames the SRT and its same-basename retained audio to
    /// <c>&lt;timestamp&gt;_&lt;meetingName&gt;</c> (plus <c>-N</c> on collision). The
    /// timestamp is the given one, else the SRT's own (name, birth time,
    /// modification time), else now. Returns the new SRT path first, then the
    /// companions. If any rename fails, every completed rename is rolled back.
    /// </summary>
    public static IReadOnlyList<string> RenameRetained(string srtPath, string meetingName, string? timestamp) =>
        RenameRetained(srtPath, meetingName, timestamp, DefaultMove);

    internal static IReadOnlyList<string> RenameRetained(string srtPath, string meetingName, string? timestamp,
        Mover move)
    {
        var safeName = FilenameSanitizer.Sanitize(meetingName)
            ?? throw new OutputWriterException(new OutputWriterError.UnusableMeetingName());
        var srt = Path.GetFullPath(srtPath);
        var directory = DirectoryOf(srt);
        List<string> sources = [srt, .. RetainedAudioFiles(srt)];
        var suffixes = sources.Select(ExtensionSuffix).ToList();

        var outputTimestamp = timestamp
            ?? Timestamps.SourceFileTimestamp(srt)
            ?? Timestamps.Now();
        var stem = FreeStem($"{outputTimestamp}_{safeName}", directory, suffixes);
        return RenameAll(sources, suffixes, stem, directory, move).Select(item => item.Destination).ToList();
    }

    // MARK: - Rename a History entry

    /// <summary>Every file suffix a History entry can have, in <c>HistoryEntry.Files</c> order.</summary>
    internal static readonly IReadOnlyList<string> EntrySuffixes =
        [".srt", ".md", "_transcript.md", .. RetainedAudioExtensions.Select(extension => "." + extension)];

    /// <summary>
    /// Renames every existing file of the History entry <c>&lt;stem&gt;.*</c> in
    /// <paramref name="directory"/> (SRT, notes, structured transcript, WAV) to
    /// <c>&lt;timestamp&gt;_&lt;meetingName&gt;</c> (History &gt; Rename, PLAN.md 4.8). The
    /// name is sanitized like every saved name. The timestamp is the one in
    /// <paramref name="stem"/>, else the first file's birth or modification
    /// time (the <c>RenameRetained</c> rule), else now. <c>-N</c> is added when
    /// any of the entry's names (all four, so a foreign file never joins the
    /// entry) is taken by a file that is not the entry's own. Renaming to the
    /// current name changes nothing.
    ///
    /// Notes and structured transcript whose first section has a
    /// <c>**Meeting Name:**</c> line get it rewritten to the new sanitized name
    /// (the value <c>SaveNamed</c> writes), atomically after the moves; other
    /// Markdown is only renamed. On a failed move, completed moves are rolled
    /// back; on a failed write, rewritten files get their old text back and
    /// every move is rolled back. Either way the error lists what could not
    /// be restored.
    /// </summary>
    public static RenamedEntry RenameEntry(string stem, string directory, string meetingName) =>
        RenameEntry(stem, directory, meetingName, DefaultMove, WriteReplacing);

    internal static RenamedEntry RenameEntry(string stem, string directory, string meetingName, Mover move,
        Writer writeText)
    {
        var safeName = FilenameSanitizer.Sanitize(meetingName)
            ?? throw new OutputWriterException(new OutputWriterError.UnusableMeetingName());
        var folder = Path.GetFullPath(directory);
        var present = EntrySuffixes
            .Select(suffix => (Path: Path.Combine(folder, stem + suffix), Suffix: suffix))
            .Where(item => File.Exists(item.Path))
            .ToList();
        var sources = present.Select(item => item.Path).ToList();
        if (sources.Count == 0)
        {
            return new RenamedEntry(stem, []);
        }

        var outputTimestamp = Timestamps.ParseFromFilename(stem)
            ?? Timestamps.SourceFileTimestamp(sources[0])
            ?? Timestamps.Now();
        var newStem = FreeStem($"{outputTimestamp}_{safeName}", folder, EntrySuffixes,
            sources.Select(FileIdentityOf).OfType<FileIdentity>().ToList());
        if (string.Equals(newStem, stem, StringComparison.Ordinal))
        {
            return new RenamedEntry(stem, sources);
        }

        // Meeting-name lines to rewrite, read before anything moves.
        var headings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".md"] = NotesFallbackHeading,
            ["_transcript.md"] = TranscriptFallbackHeading,
        };
        var rewrites = new List<(string Suffix, string Old, string New)>();
        foreach (var (path, suffix) in present)
        {
            if (!headings.TryGetValue(suffix, out var heading) || ReadUtf8(path) is not { } old
                || !MeetingNameInserter.HasMeetingName(old))
            {
                continue;
            }
            var updated = MeetingNameInserter.Insert(old, safeName, heading);
            if (!string.Equals(updated, old, StringComparison.Ordinal))
            {
                rewrites.Add((suffix, old, updated));
            }
        }

        var renamed = RenameAll(sources, present.Select(item => item.Suffix).ToList(), newStem, folder, move);

        var written = new List<(string Path, string Old)>();
        foreach (var rewrite in rewrites)
        {
            var path = Path.Combine(folder, newStem + rewrite.Suffix);
            try
            {
                writeText(rewrite.New, path);
                written.Add((path, rewrite.Old));
            }
            catch (Exception error)
            {
                var oldText = new List<string>();
                for (var index = written.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        writeText(written[index].Old, written[index].Path);
                    }
                    catch (Exception)
                    {
                        oldText.Add(written[index].Path);
                    }
                }
                // Every file still moves back so the entry stays together; a
                // file that kept the new meeting-name line is reported by
                // the path it ends up at.
                var unmoved = RollBack(renamed, move);
                var unrestored = new List<string>(unmoved);
                foreach (var kept in oldText)
                {
                    if (unmoved.Contains(kept, StringComparer.Ordinal))
                    {
                        continue;
                    }
                    var source = renamed
                        .Where(item => string.Equals(item.Destination, kept, StringComparison.Ordinal))
                        .Select(item => item.Source)
                        .FirstOrDefault();
                    unrestored.Add(source ?? kept);
                }
                throw new OutputWriterException(new OutputWriterError.WriteFailed(path, Reason(error), unrestored));
            }
        }
        return new RenamedEntry(newStem, renamed.Select(item => item.Destination).ToList());
    }

    /// <summary>
    /// Renames each source to <c>stem + suffix</c> in order. On the first
    /// failure, rolls back the completed renames in reverse and throws
    /// <see cref="OutputWriterError.RenameFailed"/>.
    /// </summary>
    private static List<Rename> RenameAll(List<string> sources, List<string> suffixes, string stem,
        string directory, Mover move)
    {
        var renamed = new List<Rename>();
        for (var index = 0; index < sources.Count && index < suffixes.Count; index++)
        {
            var source = sources[index];
            var destination = Path.Combine(directory, stem + suffixes[index]);
            try
            {
                move(source, destination);
            }
            catch (Exception error)
            {
                var unrestored = RollBack(renamed, move);
                throw new OutputWriterException(
                    new OutputWriterError.RenameFailed(source, destination, Reason(error), unrestored));
            }
            renamed.Add(new Rename(source, destination));
        }
        return renamed;
    }

    /// <summary>
    /// Moves every renamed file back, newest first. Returns the current paths
    /// of files that could not be restored.
    /// </summary>
    private static List<string> RollBack(List<Rename> renamed, Mover move)
    {
        var unrestored = new List<string>();
        for (var index = renamed.Count - 1; index >= 0; index--)
        {
            try
            {
                move(renamed[index].Destination, renamed[index].Source);
            }
            catch (Exception)
            {
                unrestored.Add(renamed[index].Destination);
            }
        }
        return unrestored;
    }

    private static string ExtensionSuffix(string path) => Path.GetExtension(path);

    private static string DirectoryOf(string fullPath) =>
        Path.GetDirectoryName(fullPath) ?? Path.GetPathRoot(fullPath) ?? fullPath;

    /// <summary>
    /// Port of <c>retained_audio_files</c>: regular files beside the SRT sharing
    /// its base name, one per extension in <see cref="RetainedAudioExtensions"/>.
    /// </summary>
    public static IReadOnlyList<string> RetainedAudioFiles(string srtPath)
    {
        var srt = Path.GetFullPath(srtPath);
        var directory = DirectoryOf(srt);
        var stem = Path.GetFileNameWithoutExtension(srt);
        return RetainedAudioExtensions
            .Select(extension => Path.Combine(directory, $"{stem}.{extension}"))
            .Where(File.Exists)
            .ToList();
    }

    // MARK: - Helpers

    /// <summary>
    /// First of <c>base</c>, <c>base-2</c>, <c>base-3</c>, ... for which no
    /// <c>stem + suffix</c> exists in <paramref name="directory"/>. Files in
    /// <paramref name="ignoring"/> (by file identity, so a case-only difference
    /// still matches) do not count as taken.
    /// </summary>
    internal static string FreeStem(string baseStem, string directory, IReadOnlyList<string> suffixes,
        IReadOnlyList<FileIdentity>? ignoring = null)
    {
        bool Taken(string path)
        {
            if (!Exists(path))
            {
                return false;
            }
            if (ignoring is not { Count: > 0 } || FileIdentityOf(path) is not { } identity)
            {
                return true;
            }
            return !ignoring.Contains(identity);
        }

        var candidate = baseStem;
        var sequence = 2;
        while (suffixes.Any(suffix => Taken(Path.Combine(directory, candidate + suffix))))
        {
            candidate = $"{baseStem}-{sequence}";
            sequence++;
        }
        return candidate;
    }

    /// <summary>
    /// Which file a path names. The Swift code compares (device, inode). On
    /// Windows the equivalent (volume serial number, file index) needs an open
    /// handle per file, and every path compared here is built from the same
    /// folder path, so the identity is the normalized full path compared
    /// case-insensitively: NTFS, FAT and exFAT match names without regard to
    /// case, so <c>A.srt</c> and <c>a.srt</c> are one file, the same case-only
    /// match the Swift identity gives on a case-insensitive APFS volume.
    /// Two different paths reaching one file (hard links, junctions, 8.3 short
    /// names) are not recognized; the paths here never take those forms.
    /// Folders with per-directory case sensitivity turned on are not
    /// supported.
    /// </summary>
    internal readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        private readonly string key;

        public FileIdentity(string path)
        {
            key = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public bool Equals(FileIdentity other) => string.Equals(key, other.key, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is FileIdentity other && Equals(other);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(key ?? "");

        public static bool operator ==(FileIdentity left, FileIdentity right) => left.Equals(right);

        public static bool operator !=(FileIdentity left, FileIdentity right) => !left.Equals(right);
    }

    private static FileIdentity? FileIdentityOf(string path) => Exists(path) ? new FileIdentity(path) : null;

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    internal static void DefaultMove(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

    /// <summary>
    /// Sends one file to the Recycle Bin (History's "Move to Recycle Bin…").
    /// Same operation <see cref="ReplaceNamed"/> uses for the old notes.
    /// </summary>
    public static void MoveToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = DefaultTrash(path);
    }

    /// <summary>
    /// Like <see cref="MoveToRecycleBin"/>, and returns the recycled item's handle
    /// (null when the file was deleted instead). For the queue's "Move to Recycle
    /// Bin…" (PLAN.md 4.11), whose debug replay removes its own copy again with
    /// <see cref="PurgeFromRecycleBin"/>.
    /// </summary>
    public static string? RecycleFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return DefaultTrash(path);
    }

    /// <summary>
    /// Removes a file <see cref="RecycleFile"/> recycled from the bin for good, only when
    /// the bin's record names <paramref name="originalPath"/>; throws <see cref="IOException"/>
    /// otherwise. Debug replay only.
    /// </summary>
    public static void PurgeFromRecycleBin(string handle, string originalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Recycle Bin is available on Windows only.");
        }
        RecycleBin.Purge(handle, originalPath);
    }

    /// <summary>
    /// Sends the file to the Recycle Bin (<see cref="RecycleBin.Recycle"/>)
    /// and returns the recycled item's handle, which <see cref="DefaultRestore"/>
    /// takes to move it back, as the Mac moves notes back from the Trash. A
    /// file that cannot be recycled (for example on a network share) asks
    /// before it is deleted instead (<c>FOF_WANTNUKEWARNING</c>) and returns
    /// null, so a later rollback reports it as not restored.
    /// </summary>
    internal static string? DefaultTrash(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Recycle Bin is available on Windows only.");
        }
        return RecycleBin.Recycle(path);
    }

    /// <summary>Moves a file <see cref="DefaultTrash"/> recycled back to its original path.</summary>
    internal static void DefaultRestore(string location, string original)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Recycle Bin is available on Windows only.");
        }
        RecycleBin.Restore(location, original);
    }

    /// <summary>
    /// Writes to a temp file (<c>.&lt;name&gt;.&lt;guid&gt;.tmp</c>) in the destination
    /// folder, flushes it to disk, then moves it into place without replacing
    /// an existing file. The temp file is removed on failure.
    /// </summary>
    internal static void WriteAtomically(string text, string destination) =>
        WriteThroughTemporary(text, destination, temporary => File.Move(temporary, destination, overwrite: false));

    /// <summary>
    /// Like <see cref="WriteAtomically"/>, but atomically replaces a file
    /// already at <paramref name="destination"/> (<c>MoveFileEx</c> with
    /// <c>MOVEFILE_REPLACE_EXISTING</c>, the Windows <c>rename(2)</c>), so a
    /// reader sees the old or the new text, never a partial one.
    /// </summary>
    internal static void WriteReplacing(string text, string destination) =>
        WriteThroughTemporary(text, destination, temporary => File.Move(temporary, destination, overwrite: true));

    private static void WriteThroughTemporary(string text, string destination, Action<string> place)
    {
        var directory = DirectoryOf(Path.GetFullPath(destination));
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():D}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // UTF-8 without a BOM and without newline translation, as Swift's `Data(text.utf8)`.
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            place(temporary);
        }
        catch
        {
            RemoveQuietly(temporary);
            throw;
        }
    }

    private static string? ReadUtf8(string path)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void RemoveQuietly(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best effort, like Swift's `try? removeItem`.
            }
        }
    }

    /// <summary>
    /// The system message for the error text. .NET messages end with a
    /// period and the sentence adds its own, so a trailing one is dropped.
    /// </summary>
    private static string Reason(Exception error)
    {
        var message = error is OutputWriterException writerError ? writerError.Error.Description : error.Message;
        return message.TrimEnd().TrimEnd('.');
    }
}
