using System.Text;
using Hearsay.App.Features.MenuBar;
using Hearsay.App.Features.Notes;
using Hearsay.Core.History;
using Hearsay.Core.Naming;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.History;

/// <summary>
/// Lists past meetings in the output folder and runs the row actions of the
/// History tab (PLAN.md 4.8). Port of
/// mac/Hearsay/Features/History/HistoryViewModel.swift. <see cref="Changed"/>
/// fires after anything the view shows changed. Use from the UI thread.
/// <para>
/// Windows differences: no security-scoped folder access to hold (a plain
/// path); Move to Recycle Bin replaces Move to Trash; stems compare
/// ignoring case (PLAN.md 18.4, file identity).
/// </para>
/// </summary>
internal sealed class HistoryViewModel
{
    private readonly Action<string>? recycle;
    private bool notesWereRunning;

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

    /// <summary>Notes flow started with "Generate Notes…" or "Regenerate Notes…".</summary>
    public NotesFlowViewModel? NotesModel { get; private set; }

    public bool IsGeneratingNotes => NotesModel?.IsRunning == true;

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

    /// <summary>
    /// Starts the notes flow for <paramref name="entry"/>'s SRT. An older SRT
    /// has no stored language: the default notes language is detected from
    /// the SRT text, or, when detection is unsure, the fixed language choice
    /// or the preferred language for Auto; the confirm sheet says which
    /// (<see cref="StoredTranscriptLanguage.Resolve"/>). An unreadable file
    /// skips detection; the notes flow then reports the read error. An entry
    /// with notes regenerates them (<see cref="OutputWriter.ReplaceNamed(string, string, string, string, string, string?)"/>).
    /// <paramref name="generate"/> replaces the pipeline (the UI snapshots).
    /// </summary>
    public NotesFlowViewModel? GenerateNotes(HistoryEntry entry, AIProviderStore store, AppSettings settings,
        NotesGenerator? generate = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanGenerateNotes(entry) || entry.Srt is not { } srt || IsGeneratingNotes) return null;
        string srtText;
        try
        {
            srtText = File.ReadAllText(srt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            srtText = "";
        }
        var (language, note) = StoredTranscriptLanguage.Resolve(srtText, settings.LanguageChoice, settings.PreferredLanguage);
        if (NotesModel is { } previous) previous.Changed -= OnNotesChanged;
        var model = new NotesFlowViewModel(store, generate);
        NotesModel = model;
        notesWereRunning = true;
        model.Changed += OnNotesChanged;
        model.Run(srt, language, note, replacingNotes: HasNotes(entry));
        OnNotesChanged(model, EventArgs.Empty);
        return model;
    }

    /// <summary>"Done" under a finished notes flow: removes the panel and rescans.</summary>
    public void DismissNotes()
    {
        if (IsGeneratingNotes) return;
        if (NotesModel is { } model) model.Changed -= OnNotesChanged;
        NotesModel = null;
        Rescan();
    }

    /// <summary>Rescans when the notes flow ends, since it renames and writes files; always re-renders (Rename's state).</summary>
    private void OnNotesChanged(object? sender, EventArgs e)
    {
        var running = IsGeneratingNotes;
        var ended = notesWereRunning && !running;
        notesWereRunning = running;
        if (ended)
        {
            Rescan();
        }
        else
        {
            OnChanged();
        }
    }

    // Rename

    /// <summary>
    /// Rename is off for the meeting whose notes are being generated (the
    /// notes flow renames and writes its files) and for the recording the
    /// Record tab is recording or transcribing (<paramref name="busyStems"/>).
    /// </summary>
    public bool CanRename(HistoryEntry entry, IReadOnlySet<string> busyStems)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(busyStems);
        if (busyStems.Contains(entry.Stem)) return false;
        if (!IsGeneratingNotes || NotesModel?.SrtPath is not { } srt) return true;
        return !string.Equals(Path.GetFileNameWithoutExtension(srt), entry.Stem, StringComparison.OrdinalIgnoreCase);
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
