namespace Hearsay.Core.Audio;

/// <summary>
/// The folder where recordings are spooled while they are made (PLAN.md 4.1
/// step 4 and 4.5). The WAV is always written here first, so a crash never
/// loses audio; afterwards it is moved to the output folder or deleted.
/// Port of mac/HearsayCore/Sources/HearsayCore/Audio/RecordingSpool.swift.
/// </summary>
/// <remarks>
/// The root comes from the caller: the Mac uses
/// <c>~/Library/Application Support/Hearsay/Recording</c>, Windows uses
/// <c>%LOCALAPPDATA%\Hearsay\Recording</c> (PLAN.md 18.3), which
/// <see cref="DefaultRoot"/> returns.
/// </remarks>
public sealed class RecordingSpool
{
    public RecordingSpool(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
    }

    public string Root { get; }

    /// <summary><c>%LOCALAPPDATA%\Hearsay\Recording</c> (PLAN.md 18.3).</summary>
    public static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }
        return Path.Combine(local, "Hearsay", "Recording");
    }

    /// <summary>
    /// <c>&lt;root&gt;\&lt;timestamp&gt;.wav</c>, with <c>-2</c>, <c>-3</c>, ... when
    /// that name is taken. Pass the current output timestamp for a new
    /// recording. The folder is created by <see cref="WavWriter"/> when the
    /// file is opened.
    /// </summary>
    public string NewRecordingPath(string timestamp)
    {
        var stem = Naming.OutputWriter.FreeStem(timestamp, Root, [".wav"]);
        return Path.Combine(Root, stem + ".wav");
    }

    /// <summary>
    /// Spooled WAVs whose header sizes are still 0 or that have no <c>.srt</c>
    /// sibling in the spool, sorted by name (ordinal). Empty when the folder is
    /// missing. Hidden files are skipped.
    /// </summary>
    public IReadOnlyList<string> UnfinishedRecordings()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(Root).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        return entries
            .Where(path => string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsHidden(path))
            .Where(path => IsUnfinishedHeader(path) || !File.Exists(Path.ChangeExtension(path, ".srt")))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Moves the spooled WAV into <paramref name="outputFolder"/> (creating it if
    /// needed, with a numeric suffix on collision) when <paramref name="keep"/>
    /// is true and returns the new location; deletes it and returns null otherwise.
    /// </summary>
    public static string? Finalize(string path, bool keep, string outputFolder)
    {
        if (!keep)
        {
            if (!File.Exists(path))
            {
                // Swift's removeItem fails on a missing file; File.Delete does not.
                throw new FileNotFoundException("The recording to delete does not exist.", path);
            }
            File.Delete(path);
            return null;
        }
        Directory.CreateDirectory(outputFolder);
        var stem = Naming.OutputWriter.FreeStem(Path.GetFileNameWithoutExtension(path), outputFolder, [".wav"]);
        var destination = Path.Combine(outputFolder, stem + ".wav");
        File.Move(path, destination, overwrite: false);
        return destination;
    }

    private static bool IsUnfinishedHeader(string path)
    {
        try
        {
            return WavWriter.HasUnfinishedHeader(path);
        }
        catch (WavException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsHidden(string path)
    {
        if (Path.GetFileName(path).StartsWith('.'))
        {
            return true;
        }
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
