import AppKit
import HearsayCore
import SwiftUI

/// The History tab: past meetings in the output folder with their files.
struct HistoryView: View {
    @Environment(AppSettings.self) private var settings
    @Environment(AIProviderStore.self) private var store
    @Environment(TranscriptionQueue.self) private var queue
    @State private var model = HistoryViewModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            header
            if let message = model.errorMessage {
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            list
            if let notesModel = model.notesModel {
                notesPanel(notesModel)
            }
        }
        .onAppear { model.open(settings: settings) }
        .onDisappear { model.close() }
        .onChange(of: settings.outputFolderBookmark) { model.open(settings: settings, reload: true) }
        .alert(
            "Move this meeting to the Trash?",
            isPresented: deleteAlertShown,
            presenting: model.pendingDelete
        ) { _ in
            // Default action so Return confirms; macOS never makes a
            // destructive alert button the default on its own.
            Button("Move to Trash", role: .destructive) { model.confirmDelete() }
                .keyboardShortcut(.defaultAction)
            Button("Cancel", role: .cancel) { model.pendingDelete = nil }
        } message: { entry in
            Text(entry.files.map(\.lastPathComponent).joined(separator: "\n"))
        }
        .sheet(item: $model.pendingRename) { entry in
            NamingSheet(
                suggestion: nil, currentName: entry.meetingName, renames: true,
                onSave: { name in model.rename(entry, to: name) },
                onCancel: { model.pendingRename = nil }
            )
        }
        .alert(
            Text("Could not rename the meeting", comment: "Alert title when History > Rename failed"),
            isPresented: renameAlertShown,
            presenting: model.renameError
        ) { _ in
            Button("OK", role: .cancel) { model.renameError = nil }
        } message: { message in
            Text(message)
        }
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: "folder")
                .foregroundStyle(.secondary)
            Text(model.folderURL?.path ?? String(localized: "No output folder",
                                                 comment: "History header when the output folder cannot be opened"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .textSelection(.enabled)
                .help(model.folderURL?.path ?? "")
            Button("Reveal in Finder") { model.revealFolder() }
                .disabled(model.folderURL == nil)
            Spacer()
            Button {
                model.rescan()
            } label: {
                Label("Refresh", systemImage: "arrow.clockwise")
            }
            .keyboardShortcut("r", modifiers: .command)
        }
    }

    // MARK: - List

    @ViewBuilder private var list: some View {
        if model.entries.isEmpty {
            ContentUnavailableView(
                "No meetings yet",
                systemImage: "clock",
                description: Text("Recordings, transcripts and meeting notes saved in the output folder appear here.")
            )
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            let busy = HistoryViewModel.busyStems(of: queue)
            ScrollViewReader { proxy in
                List(model.entries, selection: $model.selection) { entry in
                    HistoryRow(
                        entry: entry,
                        canGenerateNotes: HistoryViewModel.canGenerateNotes(entry) && !model.isGeneratingNotes,
                        canRename: model.canRename(entry, busyStems: busy)
                    ) { action in
                        perform(action, on: entry)
                    }
                    .tag(entry.stem)
                    .contextMenu { menuItems(for: entry, busyStems: busy) }
                }
                .onChange(of: model.selection) { _, stem in
                    guard let stem else { return }
                    withAnimation { proxy.scrollTo(stem) }
                }
            }
        }
    }

    @ViewBuilder private func menuItems(for entry: HistoryEntry, busyStems: Set<String>) -> some View {
        Button("Open Notes") { perform(.openNotes, on: entry) }
            .disabled(entry.notes == nil)
        Button("Open Transcript") { perform(.openTranscript, on: entry) }
            .disabled(entry.transcript == nil)
        Button("Open SRT") { perform(.openSRT, on: entry) }
            .disabled(entry.srt == nil)
        Button("Reveal in Finder") { perform(.reveal, on: entry) }
        Divider()
        Button("Rename…") { perform(.rename, on: entry) }
            .disabled(!model.canRename(entry, busyStems: busyStems))
        Button(HistoryViewModel.notesActionTitle(entry)) { perform(.generateNotes, on: entry) }
            .disabled(!HistoryViewModel.canGenerateNotes(entry) || model.isGeneratingNotes)
        Divider()
        Button("Move to Trash…", role: .destructive) { perform(.delete, on: entry) }
    }

    private func perform(_ action: HistoryRow.Action, on entry: HistoryEntry) {
        switch action {
        case .openNotes: model.open(entry.notes)
        case .openTranscript: model.open(entry.transcript)
        case .openSRT: model.open(entry.srt)
        case .reveal: model.reveal(entry)
        case .generateNotes:
            model.generateNotes(entry, store: store, settings: settings)
        case .rename:
            guard model.canRename(entry, busyStems: HistoryViewModel.busyStems(of: queue)) else { return }
            model.pendingRename = entry
        case .delete: model.pendingDelete = entry
        }
    }

    private var renameAlertShown: Binding<Bool> {
        Binding(
            get: { model.renameError != nil },
            set: { shown in if !shown { model.renameError = nil } }
        )
    }

    private var deleteAlertShown: Binding<Bool> {
        Binding(
            get: { model.pendingDelete != nil },
            set: { shown in if !shown { model.pendingDelete = nil } }
        )
    }

    // MARK: - Notes

    private func notesPanel(_ notesModel: NotesFlowViewModel) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Divider()
            HStack(alignment: .top) {
                NotesFlowView(model: notesModel)
                Spacer()
                // While running, `NotesFlowView` shows its own Cancel (sheet
                // steps and generation), so there is always a way out.
                if !notesModel.isRunning {
                    Button("Done") { model.dismissNotes() }
                }
            }
        }
    }
}

/// One meeting: name, date and time, duration, file badges, action buttons.
private struct HistoryRow: View {
    enum Action {
        case openNotes, openTranscript, openSRT, reveal, rename, generateNotes, delete
    }

    let entry: HistoryEntry
    let canGenerateNotes: Bool
    let canRename: Bool
    let perform: (Action) -> Void

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(entry.meetingName ?? String(localized: "Untitled", comment: "History: a meeting that has no name yet"))
                    .font(.body.weight(.medium))
                    .foregroundStyle(entry.meetingName == nil ? .secondary : .primary)
                HStack(spacing: 8) {
                    Text(dateText)
                    if let duration = entry.audioDuration {
                        Text(Self.format(duration: duration))
                            .monospacedDigit()
                    }
                }
                .font(.callout)
                .foregroundStyle(.secondary)
                HStack(spacing: 4) {
                    if entry.srt != nil { badge("SRT") }
                    if entry.notes != nil { badge(String(localized: "Notes", comment: "History badge: the meeting has meeting notes")) }
                    if entry.transcript != nil { badge(String(localized: "Transcript", comment: "History badge: the meeting has a structured transcript")) }
                    if entry.audio != nil { badge(String(localized: "Audio", comment: "History badge: the meeting has a recording")) }
                }
            }
            Spacer()
            buttons
        }
        .padding(.vertical, 4)
    }

    private var dateText: String {
        guard let timestamp = entry.timestamp else { return entry.stem }
        return timestamp.formatted(
            Date.FormatStyle(date: .abbreviated, time: .shortened).locale(InterfaceLanguageLaunch.applied.locale))
    }

    private var buttons: some View {
        HStack(spacing: 2) {
            iconButton(String(localized: "Open notes", comment: "History row button (tooltip)"),
                       systemImage: "doc.text", enabled: entry.notes != nil, .openNotes)
            iconButton(String(localized: "Open transcript", comment: "History row button (tooltip): the structured Markdown transcript"),
                       systemImage: "text.alignleft", enabled: entry.transcript != nil, .openTranscript)
            iconButton(String(localized: "Open SRT", comment: "History row button (tooltip)"),
                       systemImage: "captions.bubble", enabled: entry.srt != nil, .openSRT)
            iconButton(String(localized: "Reveal in Finder", comment: "Button"),
                       systemImage: "folder", enabled: true, .reveal)
            iconButton(String(localized: "Rename…", comment: "Button: rename the meeting's files"),
                       systemImage: "pencil", enabled: canRename, .rename)
            iconButton(
                HistoryViewModel.notesActionTitle(entry), systemImage: "sparkles",
                enabled: canGenerateNotes, .generateNotes
            )
            iconButton(String(localized: "Move to Trash…", comment: "Button: move the meeting's files to the Trash"),
                       systemImage: "trash", enabled: true, .delete)
        }
        .buttonStyle(.borderless)
    }

    private func iconButton(_ title: String, systemImage: String, enabled: Bool, _ action: Action) -> some View {
        Button {
            perform(action)
        } label: {
            Label(title, systemImage: systemImage)
                .labelStyle(.iconOnly)
                .frame(width: 22, height: 22)
        }
        .help(title)
        .disabled(!enabled)
    }

    private func badge(_ title: String) -> some View {
        Text(title)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .foregroundStyle(Color.accentColor)
            .overlay(
                RoundedRectangle(cornerRadius: 4)
                    .strokeBorder(Color.accentColor.opacity(0.6), lineWidth: 1)
            )
            .accessibilityLabel(Text("\(title) saved", comment: "Accessibility label of a History badge. %@ is SRT, Notes, Transcript, or Audio."))
    }

    /// `H:MM:SS` or `M:SS`.
    static func format(duration: TimeInterval) -> String {
        let total = Int(duration.rounded())
        let hours = total / 3600
        let minutes = total % 3600 / 60
        let seconds = total % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, seconds)
            : String(format: "%d:%02d", minutes, seconds)
    }
}
