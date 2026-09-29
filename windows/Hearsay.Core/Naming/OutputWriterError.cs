namespace Hearsay.Core.Naming;

/// <summary>
/// Port of <c>OutputWriterError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Naming/OutputWriter.swift. The Swift
/// enum cases are records; <see cref="OutputWriterException"/> carries one.
/// Paths are full paths. The English text matches the Swift string catalog
/// keys so the shared translations can be reused (localization is W7).
/// </summary>
public abstract record OutputWriterError
{
    private OutputWriterError()
    {
    }

    /// <summary>The meeting name has no letters or digits left after sanitizing.</summary>
    public sealed record UnusableMeetingName : OutputWriterError;

    /// <summary>
    /// A rename failed. <paramref name="Unrestored"/> lists files whose rollback
    /// also failed, by their current path.
    /// </summary>
    public sealed record RenameFailed(string Source, string Destination, string Reason, IReadOnlyList<string> Unrestored)
        : OutputWriterError;

    /// <summary>
    /// Writing a Markdown document failed. The partial Markdown was removed
    /// and every rename (SRT and retained audio) rolled back;
    /// <paramref name="Unrestored"/> lists files whose rollback failed, by
    /// their current path.
    /// </summary>
    public sealed record WriteFailed(string Path, string Reason, IReadOnlyList<string> Unrestored) : OutputWriterError;

    /// <summary>
    /// Moving the previous notes to the Recycle Bin failed (<c>ReplaceNamed</c>).
    /// Notes already recycled were put back; <paramref name="Unrestored"/> lists
    /// files whose restore failed, by their current path.
    /// </summary>
    public sealed record TrashFailed(string Path, string Reason, IReadOnlyList<string> Unrestored) : OutputWriterError;

    /// <summary>The message shown to the user (Swift <c>errorDescription</c>).</summary>
    public string Description => this switch
    {
        UnusableMeetingName =>
            "The meeting name contains no usable characters. Use English letters or digits.",
        RenameFailed failure =>
            $"Unable to rename {failure.Source} to {System.IO.Path.GetFileName(failure.Destination)}: {failure.Reason}."
            + " " + RestoreSentence(failure.Unrestored, "All files keep their original names."),
        WriteFailed failure =>
            $"Unable to write meeting notes ({failure.Path}): {failure.Reason}."
            + " " + RestoreSentence(failure.Unrestored,
                "The transcript, recording and any earlier notes keep their original names."),
        TrashFailed failure =>
            $"Unable to move the current notes ({failure.Path}) to the Trash: {failure.Reason}."
            + " " + RestoreSentence(failure.Unrestored, "The current notes are unchanged."),
        _ => throw new InvalidOperationException("Unknown OutputWriterError."),
    };

    /// <summary>
    /// <paramref name="otherwise"/> when every file was restored, else the list
    /// of the files that were not.
    /// </summary>
    private static string RestoreSentence(IReadOnlyList<string> unrestored, string otherwise) =>
        unrestored.Count == 0 ? otherwise : $"These files could not be restored: {string.Join(", ", unrestored)}.";
}

/// <summary>Thrown by <see cref="OutputWriter"/>; <see cref="Error"/> says what failed.</summary>
public sealed class OutputWriterException : Exception
{
    public OutputWriterException(OutputWriterError error)
        : base(error?.Description)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public OutputWriterError Error { get; }
}
