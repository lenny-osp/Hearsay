import AppKit
import Foundation
import HearsayCore
import Observation
import UniformTypeIdentifiers

/// Lists past meetings in the output folder and runs the row actions of the
/// History tab. Owns the security-scoped access to the folder while the tab
/// is visible (and while a notes run it started is still writing).
@MainActor
@Observable
final class HistoryViewModel {
    private(set) var entries: [HistoryEntry] = []
    private(set) var folderURL: URL?
    private(set) var errorMessage: String?
    /// Entry waiting for the delete confirmation.
    var pendingDelete: HistoryEntry?
    /// Entry shown in the rename sheet.
    var pendingRename: HistoryEntry?
    /// Why the last rename failed, shown in an alert.
    var renameError: String?
    /// The selected entry's stem; a renamed entry stays selected.
    var selection: String?
    /// Notes flow started with "Generate notes…" or "Regenerate notes…".
    private(set) var notesModel: NotesFlowViewModel?

    @ObservationIgnored private var folder: ResolvedOutputFolder?
    @ObservationIgnored private var isVisible = false

    // MARK: - Folder access

    /// Resolves the output folder (persisting a refreshed bookmark) and scans
    /// it. Reuses the access still held for a running notes flow unless
    /// `reload` is set (the output folder setting changed).
    func open(settings: AppSettings, reload: Bool = false) {
        isVisible = true
        if folder != nil, !reload {
            rescan()
            return
        }
        releaseFolder()
        do {
            let resolved = try OutputLocation.resolve(bookmark: settings.outputFolderBookmark)
            if let refreshed = resolved.refreshedBookmark {
                settings.outputFolderBookmark = refreshed
            }
            folder = resolved
            folderURL = resolved.url
            errorMessage = nil
        } catch {
            folderURL = nil
            entries = []
            errorMessage = String(localized: "Could not open the output folder: \(error.localizedDescription)",
                                  comment: "Error. %@ is the system error message.")
            return
        }
        rescan()
    }

    /// Stops accessing the folder, or defers that until a running notes flow
    /// has finished writing into it.
    func close() {
        isVisible = false
        if notesModel?.isRunning == true {
            releaseWhenNotesFinish()
        } else {
            releaseFolder()
        }
    }

    private func releaseFolder() {
        folder?.stopAccessing()
        folder = nil
    }

    private func releaseWhenNotesFinish() {
        withObservationTracking {
            _ = notesModel?.isRunning
        } onChange: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self, !self.isVisible else { return }
                if self.notesModel?.isRunning == true {
                    self.releaseWhenNotesFinish()
                } else {
                    self.releaseFolder()
                }
            }
        }
    }

    func rescan() {
        guard let folderURL else { return }
        do {
            entries = try HistoryIndex.scan(folder: folderURL)
            errorMessage = nil
        } catch {
            entries = []
            errorMessage = String(localized: "Could not read \(folderURL.path): \(error.localizedDescription)",
                                  comment: "History error. %1$@ is a folder path, %2$@ the system error message.")
        }
    }

    // MARK: - Row actions

    func open(_ url: URL?) {
        guard let url else { return }
        defer { rescan() }
        if NSWorkspace.shared.urlForApplication(toOpen: url) != nil {
            NSWorkspace.shared.open(url)
            return
        }
        // No app claims the file type (common for .md and .srt): use the
        // default plain-text editor.
        guard let editor = NSWorkspace.shared.urlForApplication(toOpen: .plainText) else {
            errorMessage = String(localized: "No app is available to open \(url.lastPathComponent).",
                                  comment: "History error. %@ is a file name.")
            return
        }
        NSWorkspace.shared.open([url], withApplicationAt: editor, configuration: NSWorkspace.OpenConfiguration())
    }

    func reveal(_ entry: HistoryEntry) {
        NSWorkspace.shared.activateFileViewerSelecting(entry.files)
        rescan()
    }

    func revealFolder() {
        guard let folderURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([folderURL])
    }

    /// Any entry with an SRT; one that already has notes regenerates them.
    static func canGenerateNotes(_ entry: HistoryEntry) -> Bool {
        entry.srt != nil
    }

    /// Existing notes (or a structured transcript) are replaced, not added
    /// beside under a `-2` name.
    static func hasNotes(_ entry: HistoryEntry) -> Bool {
        entry.notes != nil || entry.transcript != nil
    }

    static func notesActionTitle(_ entry: HistoryEntry) -> String {
        hasNotes(entry)
            ? String(localized: "Regenerate Notes…", comment: "History: make new meeting notes, replacing the current ones")
            : String(localized: "Generate Notes…", comment: "History: make meeting notes from the SRT")
    }

    var isGeneratingNotes: Bool { notesModel?.isRunning == true }

    /// An older SRT has no stored language: the default notes language is
    /// detected from the SRT text, or, when detection is unsure, the fixed
    /// language choice or the preferred language for Auto. The confirm
    /// sheet says which (`StoredTranscriptLanguage.resolve`). An unreadable
    /// file skips detection; the notes flow then reports the read error.
    func generateNotes(_ entry: HistoryEntry, store: AIProviderStore, settings: AppSettings) {
        guard Self.canGenerateNotes(entry), let srt = entry.srt, !isGeneratingNotes else { return }
        let srtText = (try? String(contentsOf: srt, encoding: .utf8)) ?? ""
        let resolved = StoredTranscriptLanguage.resolve(
            srtText: srtText, choice: settings.languageChoice, preferred: settings.preferredLanguage
        )
        let model = NotesFlowViewModel(store: store)
        notesModel = model
        model.run(
            srtURL: srt, language: resolved.language, languageNote: resolved.note,
            replacingNotes: Self.hasNotes(entry)
        )
        observeNotesCompletion()
    }

    /// Rescans when the notes flow ends, since it renames and writes files.
    private func observeNotesCompletion() {
        withObservationTracking {
            _ = notesModel?.isRunning
        } onChange: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if self.notesModel?.isRunning == true {
                    self.observeNotesCompletion()
                } else {
                    self.rescan()
                }
            }
        }
    }

    func dismissNotes() {
        guard !isGeneratingNotes else { return }
        notesModel = nil
        rescan()
    }

    // MARK: - Rename

    /// Rename is off for the meeting whose notes are being generated (the
    /// notes flow renames and writes its files) and for the recording that
    /// is being recorded or transcribed (`busyStems`).
    func canRename(_ entry: HistoryEntry, busyStems: Set<String>) -> Bool {
        guard !busyStems.contains(entry.stem) else { return false }
        guard isGeneratingNotes, let srt = notesModel?.srtURL else { return true }
        return srt.deletingPathExtension().lastPathComponent != entry.stem
    }

    /// Stems of the recording the Record tab is capturing, transcribing, or
    /// transcribing again (its WAV and SRT are in the output folder while a
    /// retry or a re-run in another language runs).
    static func busyStems(of recording: RecordingController) -> Set<String> {
        guard recording.isSessionActive || recording.isTranscribing else { return [] }
        return Set([recording.finishedRecording, recording.finishedTranscript]
            .compactMap { $0?.deletingPathExtension().lastPathComponent })
    }

    /// Renames every file of `entry` (`OutputWriter.renameEntry`), rescans,
    /// and keeps the entry selected under its new name. A failure (already
    /// rolled back) is shown in an alert.
    func rename(_ entry: HistoryEntry, to name: String) {
        pendingRename = nil
        guard let folderURL else { return }
        do {
            let result = try OutputWriter.renameEntry(
                stem: entry.stem, directory: folderURL, meetingName: name
            )
            rescan()
            selection = result.stem
        } catch {
            rescan()
            renameError = error.localizedDescription
        }
    }

    /// Moves every file of the confirmed entry to the Trash.
    func confirmDelete() {
        guard let entry = pendingDelete else { return }
        pendingDelete = nil
        var failures: [String] = []
        for url in entry.files {
            do {
                try FileManager.default.trashItem(at: url, resultingItemURL: nil)
            } catch {
                failures.append("\(url.lastPathComponent): \(error.localizedDescription)")
            }
        }
        rescan()
        if !failures.isEmpty {
            errorMessage = String(localized: "Some files could not be moved to the Trash:",
                                  comment: "History error, followed by one line per file")
                + "\n" + failures.joined(separator: "\n")
        }
    }
}
