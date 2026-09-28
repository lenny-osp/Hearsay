import HearsayCore
import SwiftUI

/// One catalog entry: name, quantization, size, state, and one primary
/// action. Delete lives in the context menu.
struct ModelRowView: View {
    let entry: ModelCatalogEntry

    @Environment(ModelStore.self) private var store
    @State private var deleteError: String?

    private var state: DownloadState { store.state(for: entry) }
    private var isActive: Bool { store.activeModelRepo == entry.repo }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(entry.displayName)
                        .font(.body.weight(.medium))
                    Text(entry.quantization)
                        .font(.caption.monospaced())
                        .padding(.horizontal, 5)
                        .padding(.vertical, 1)
                        .background(.quaternary, in: RoundedRectangle(cornerRadius: 4))
                    if entry.recommended {
                        Text("Recommended")
                            .font(.caption)
                            .foregroundStyle(.tint)
                    }
                }
                detail
            }
            Spacer(minLength: 8)
            primaryAction
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .contextMenu {
            Button("Delete", role: .destructive, action: delete)
                .disabled(!store.hasLocalFiles(entry))
        }
        .alert("Could not delete the model", isPresented: Binding(
            get: { deleteError != nil },
            set: { if !$0 { deleteError = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(deleteError ?? "")
        }
    }

    @ViewBuilder private var detail: some View {
        switch state {
        case let .downloading(progress):
            VStack(alignment: .leading, spacing: 2) {
                ProgressView(value: progress.fractionCompleted)
                    .frame(maxWidth: 260)
                Text("\(ByteSize.format(progress.bytesReceived)) of \(ByteSize.format(progress.totalBytes))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
        case let .failed(message):
            Text(message)
                .font(.caption)
                .foregroundStyle(.red)
                .lineLimit(2)
        case .installed:
            Text(isActive
                 ? String(localized: "\(ByteSize.format(entry.sizeBytes)), in use",
                          comment: "Models tab: the active model. %@ is a size such as 1.6 GB.")
                 : String(localized: "\(ByteSize.format(entry.sizeBytes)), installed",
                          comment: "Models tab: a downloaded model. %@ is a size such as 1.6 GB."))
                .font(.caption)
                .foregroundStyle(.secondary)
        case .notInstalled:
            Text(ByteSize.format(entry.sizeBytes))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private var primaryAction: some View {
        switch state {
        case .notInstalled:
            Button("Download") { store.download(entry) }
        case .failed:
            Button("Retry") { store.download(entry) }
        case .downloading:
            Button("Cancel") { store.cancelDownload(entry) }
        case .installed:
            if isActive {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                    .font(.title3)
                    .help("Active model")
                    .accessibilityLabel("Active model")
            } else {
                Button("Use") { store.use(entry) }
            }
        }
    }

    private func delete() {
        do {
            try store.delete(entry)
        } catch {
            deleteError = error.localizedDescription
        }
    }
}

@MainActor
enum ByteSize {
    static func format(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    /// One decimal place, for button labels like "1.6 GB".
    static func short(_ bytes: Int64) -> String {
        let value = Double(bytes)
        let locale = InterfaceLanguageLaunch.applied.locale
        if value >= 1_000_000_000 {
            return String(format: "%.1f GB", locale: locale, value / 1_000_000_000)
        }
        return String(format: "%.0f MB", locale: locale, value / 1_000_000)
    }
}
