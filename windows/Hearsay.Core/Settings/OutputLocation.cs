namespace Hearsay.Core.Settings;

/// <summary>
/// A resolved output folder: its full path, and whether it is the fallback
/// because no usable folder was chosen. Windows has no security-scoped
/// access, so there is nothing to stop (the Mac's <c>stopAccessing()</c>)
/// and no bookmark to refresh.
/// </summary>
public sealed record ResolvedOutputFolder(string Path, bool IsFallback);

/// <summary>
/// Resolves where Hearsay writes its outputs (PLAN.md section 8).
/// Port of mac/HearsayCore/Sources/HearsayCore/Settings/OutputLocation.swift.
/// The Mac stores a folder the user picks as a security-scoped bookmark;
/// Windows stores its full path (<see cref="AppSettings.OutputFolder"/>).
/// </summary>
public static class OutputLocation
{
    public const string FolderName = "Hearsay";

    /// <summary>
    /// <c>Documents\Hearsay</c>. Documents comes from the shell
    /// (<c>SpecialFolder.MyDocuments</c>), so a Documents folder redirected
    /// to OneDrive or another drive is followed; only when the shell has none
    /// is it <c>%USERPROFILE%\Documents</c>. Not created here.
    /// </summary>
    public static string DefaultFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            documents = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        }
        return System.IO.Path.Combine(documents, FolderName);
    }

    /// <summary>
    /// The value to store for a folder the user picked (the Mac's
    /// <c>makeBookmark</c>): its full, normalized path without a trailing
    /// separator (a drive root keeps its own).
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, relative, or malformed.</exception>
    public static string MakeStoredPath(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!System.IO.Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException($"The output folder must be a full path: {folder}", nameof(folder));
        }
        var full = System.IO.Path.GetFullPath(folder);
        return System.IO.Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// The chosen folder when it is a full path to an existing folder;
    /// otherwise <paramref name="fallback"/> (default
    /// <see cref="DefaultFolder"/>), created if missing. As on the Mac, a
    /// chosen folder that no longer exists is not recreated.
    /// </summary>
    /// <exception cref="IOException">The fallback folder cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The fallback folder cannot be created.</exception>
    public static ResolvedOutputFolder Resolve(string? chosenFolder, string? fallback = null)
    {
        if (Usable(chosenFolder) is { } chosen)
        {
            return new ResolvedOutputFolder(chosen, IsFallback: false);
        }
        var folder = fallback ?? DefaultFolder();
        Directory.CreateDirectory(folder);
        return new ResolvedOutputFolder(folder, IsFallback: true);
    }

    private static string? Usable(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var stored = MakeStoredPath(folder);
            return Directory.Exists(stored) ? stored : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }
}
