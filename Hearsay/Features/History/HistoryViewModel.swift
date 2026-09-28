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
    /// Notes flow started with "Generate notes…".
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
            errorMessage = "Could not open the output folder: \(error.localizedDescription)"
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
            errorMessage = "Could not read \(folderURL.path): \(error.localizedDescription)"
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
            errorMessage = "No app is available to open \(url.lastPathComponent)."
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

    static func canGenerateNotes(_ entry: HistoryEntry) -> Bool {
        entry.srt != nil && entry.notes == nil
    }

    var isGeneratingNotes: Bool { notesModel?.isRunning == true }

    func generateNotes(_ entry: HistoryEntry, store: AIProviderStore, languageCode: String) {
        guard Self.canGenerateNotes(entry), let srt = entry.srt, !isGeneratingNotes else { return }
        let model = NotesFlowViewModel(store: store)
        notesModel = model
        model.run(srtURL: srt, languageCode: languageCode)
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
            errorMessage = "Some files could not be moved to the Trash:\n" + failures.joined(separator: "\n")
        }
    }
}
