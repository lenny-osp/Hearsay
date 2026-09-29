using Hearsay.App.Features.MenuBar;
using Hearsay.Core.History;
using Hearsay.Core.Naming;
using Hearsay.Core.Settings;

namespace Hearsay.App.Features.History;

/// <summary>
/// Lists past meetings in the output folder and runs the row actions of the
/// History tab (PLAN.md 4.8). Port of
/// mac/Hearsay/Features/History/HistoryViewModel.swift. <see cref="Changed"/>
/// fires after anything the view shows changed. Use from the UI thread.
/// <para>
/// Windows differences: no security-scoped folder access to hold (a plain
/// path); Move to Recycle Bin replaces Move to Trash; the notes flow
/// (Generate / Regenerate Notes…) arrives in W6, so nothing is being
/// generated and Rename is only off for the Record tab's busy recording.
/// </para>
/// </summary>
internal sealed class HistoryViewModel
{
    private readonly Action<string>? recycle;

    /// <param name="recycle">
    /// Moves one file to the Recycle Bin, throwing on failure; null turns
    /// Move to Recycle Bin off (see <see cref="CanMoveToRecycleBin"/>).
    /// </param>
    public HistoryViewModel(Action<string>? recycle)
    {
        this.recycle = recycle;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HistoryEntry> Entries { get; private set; } = [];

    public string? FolderPath { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>The selected entry's stem; a renamed entry stays selected.</summary>
    public string? Selection { get; set; }

    /// <summary>Whether Move to Recycle Bin is available in this build.</summary>
    public bool CanMoveToRecycleBin => recycle is not null;

    /// <summary>
    /// Resolves the output folder (the default is created on demand, as
    /// Settings > Output does) and scans it.
    /// </summary>
    public void Open(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            FolderPath = OutputLocation.Resolve(settings.OutputFolder).Path;
            ErrorMessage = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FolderPath = null;
            Entries = [];
            ErrorMessage = Strings.OutputFolderOpenFailed(error.Message);
            OnChanged();
            return;
        }
        Rescan();
    }

    public void Rescan()
    {
        if (FolderPath is not { } folder) return;
        try
        {
            Entries = HistoryIndex.Scan(folder);
            ErrorMessage = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Entries = [];
            ErrorMessage = Strings.FolderReadFailed(folder, error.Message);
        }
        OnChanged();
    }

    // Row actions

    public void OpenFile(string? path)
    {
        if (path is null) return;
        var failure = ExplorerShell.Open(path);
        Rescan();
        if (failure is not null)
        {
            ErrorMessage = failure;
            OnChanged();
        }
    }

    public void Reveal(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var failure = ExplorerShell.Reveal(entry.Files);
        Rescan();
        if (failure is not null)
        {
            ErrorMessage = Strings.RevealFailed(failure);
            OnChanged();
        }
    }

    public void RevealFolder()
    {
        if (FolderPath is not { } folder) return;
        if (ExplorerShell.Reveal([folder]) is { } failure)
        {
            ErrorMessage = Strings.RevealFailed(failure);
            OnChanged();
        }
    }

    /// <summary>Any entry with an SRT; one that already has notes regenerates them.</summary>
    public static bool CanGenerateNotes(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Srt is not null;
    }

    /// <summary>Existing notes (or a structured transcript) are replaced, not added beside under a <c>-2</c> name.</summary>
    public static bool HasNotes(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Notes is not null || entry.Transcript is not null;
    }

    public static string NotesActionTitle(HistoryEntry entry) =>
        HasNotes(entry) ? Strings.RegenerateNotes : Strings.GenerateNotes;

    // Rename

    /// <summary>
    /// Rename is off for the recording the Record tab is recording or
    /// transcribing (<paramref name="busyStems"/>). The Mac also turns it off
    /// for the meeting whose notes History is generating; that flow is W6.
    /// </summary>
    public static bool CanRename(HistoryEntry entry, IReadOnlySet<string> busyStems)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(busyStems);
        return !busyStems.Contains(entry.Stem);
    }

    /// <summary>Stems of the files the Record tab is capturing or transcribing (the Mac's <c>busyStems(of:)</c>).</summary>
    public static IReadOnlySet<string> BusyStems(RecordingStatus recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return recording.BusyFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renames every file of <paramref name="entry"/>
    /// (<see cref="OutputWriter.RenameEntry(string, string, string)"/>), rescans,
    /// and keeps the entry selected under its new name. Returns null, or the
    /// error text (the files are already rolled back) for the alert.
    /// </summary>
    public string? Rename(HistoryEntry entry, string name)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(name);
        if (FolderPath is not { } folder) return null;
        try
        {
            var result = OutputWriter.RenameEntry(entry.Stem, folder, name);
            Selection = result.Stem;
            Rescan();
            return null;
        }
        catch (OutputWriterException error)
        {
            Rescan();
            return error.Error.Description;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Rescan();
            return error.Message;
        }
    }

    /// <summary>
    /// Moves every file of the confirmed entry to the Recycle Bin; files that
    /// could not be moved are listed in <see cref="ErrorMessage"/>.
    /// </summary>
    public void MoveToRecycleBin(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (recycle is null) return;
        var failures = new List<string>();
        foreach (var path in entry.Files)
        {
            try
            {
                recycle(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(path)}: {error.Message}");
            }
        }
        Rescan();
        if (failures.Count > 0)
        {
            ErrorMessage = Strings.RecycleFailures + "\n" + string.Join("\n", failures);
            OnChanged();
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
