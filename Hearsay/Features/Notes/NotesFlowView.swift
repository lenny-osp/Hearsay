import AppKit
import HearsayCore
import SwiftUI

/// Hosts the notes-flow sheets and shows progress, the resulting files, and
/// errors for a `NotesFlowViewModel`.
struct NotesFlowView: View {
    @Bindable var model: NotesFlowViewModel

    var body: some View {
        content
            .sheet(item: activeSheet) { sheet in
                switch sheet {
                case .confirm:
                    ConfirmSendSheet(model: model)
                case .manualPrompt:
                    ManualNamingPromptSheet(model: model)
                case .naming(let suggestion):
                    NamingSheet(
                        suggestion: suggestion,
                        onSave: { model.saveName($0) },
                        onCancel: { model.cancelNaming() }
                    )
                }
            }
    }

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .idle, .confirming, .askingManualNaming, .naming:
            EmptyView()
        case .generating:
            HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                Text("Generating meeting notes via \(model.providerName) (\(model.store.configuration.model))…")
                Spacer()
                Button("Cancel") { model.cancelGeneration() }
            }
        case .finished(let outcome):
            VStack(alignment: .leading, spacing: 6) {
                Label(outcome.message, systemImage: "checkmark.circle")
                    .foregroundStyle(.green)
                ForEach(outcome.files, id: \.self) { url in
                    Text(url.path)
                        .font(.callout.monospaced())
                        .textSelection(.enabled)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Button("Reveal in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting(outcome.files)
                }
            }
        case let .failed(message, srtURL):
            VStack(alignment: .leading, spacing: 6) {
                Label("Meeting notes were not saved", systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.red)
                Text(message)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                Text("The SRT is kept at \(srtURL.path)")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                Button("Reveal in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([srtURL])
                }
            }
        }
    }

    private enum ActiveSheet: Identifiable {
        case confirm
        case manualPrompt
        case naming(String?)

        var id: String {
            switch self {
            case .confirm: "confirm"
            case .manualPrompt: "manualPrompt"
            case .naming(let suggestion): "naming:\(suggestion ?? "")"
            }
        }
    }

    private var activeSheet: Binding<ActiveSheet?> {
        Binding(
            get: {
                switch model.phase {
                case .confirming: .confirm
                case .askingManualNaming: .manualPrompt
                case .naming(let suggestion): .naming(suggestion)
                default: nil
                }
            },
            // Each sheet ends its own step through the model; nothing to do
            // when SwiftUI reports the dismissal.
            set: { _ in }
        )
    }
}

/// Python `confirm_manual_naming`: "Name this meeting yourself?" with no as
/// the default (Return keeps the timestamp names).
private struct ManualNamingPromptSheet: View {
    let model: NotesFlowViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Name this meeting yourself?")
                .font(.headline)
            Text("No meeting notes were generated. You can rename the SRT and its recording, or keep the timestamp names.")
                .fixedSize(horizontal: false, vertical: true)
            if let srtURL = model.srtURL {
                Text("Transcript kept: \(srtURL.lastPathComponent)")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            ForEach(model.retainedAudio, id: \.self) { url in
                Text("Recording kept: \(url.lastPathComponent)")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button("Name It…") { model.acceptManualNaming() }
                    .keyboardShortcut("y", modifiers: [])
                Button("Keep Timestamp Names") { model.declineManualNaming() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}
