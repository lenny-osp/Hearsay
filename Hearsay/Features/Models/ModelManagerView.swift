import HearsayCore
import SwiftUI

/// Models tab: the catalog grouped by family, with download, use, and
/// delete (PLAN.md section 5).
struct ModelManagerView: View {
    @Environment(ModelStore.self) private var store
    @Environment(\.openURL) private var openURL
    @State private var showOnboarding = false
    @State private var didOfferOnboarding = false

    private var groups: [(family: String, entries: [ModelCatalogEntry])] {
        let byFamily = Dictionary(grouping: store.catalog.entries, by: \.family)
        let known = ModelCatalog.familyOrder.compactMap { family in
            byFamily[family].map { (family: family, entries: $0) }
        }
        let others = byFamily.keys
            .filter { !ModelCatalog.familyOrder.contains($0) }
            .sorted()
            .compactMap { family in byFamily[family].map { (family: family, entries: $0) } }
        return known + others
    }

    private var isDownloadingAnything: Bool {
        store.catalog.entries.contains { entry in
            if case .downloading = store.state(for: entry) { return true }
            return false
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            header
            if let error = store.catalogError {
                Text(error)
                    .foregroundStyle(.red)
                    .font(.callout)
            }
            List {
                ForEach(groups, id: \.family) { group in
                    Section(Self.familyTitle(group.family)) {
                        ForEach(group.entries) { entry in
                            ModelRowView(entry: entry)
                        }
                    }
                }
            }
        }
        .onAppear {
            store.refresh()
            if !didOfferOnboarding, store.installed.isEmpty, !isDownloadingAnything,
               store.catalog.recommended != nil {
                didOfferOnboarding = true
                showOnboarding = true
            }
        }
        .sheet(isPresented: $showOnboarding) {
            OnboardingModelSheet()
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Models are stored in")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(store.rootURL.path)
                    .font(.caption.monospaced())
                    .textSelection(.enabled)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text("\(ByteSize.format(store.totalSizeOnDisk)) on disk")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if FileManager.default.fileExists(atPath: store.rootURL.path) {
                Button("Show in Finder") { openURL(store.rootURL) }
                    .controlSize(.small)
            }
        }
        .padding(.horizontal, 4)
    }

    static func familyTitle(_ family: String) -> String {
        switch family {
        case "tiny": "Tiny"
        case "base": "Base"
        case "small": "Small"
        case "medium": "Medium"
        case "large-v3": "Large v3"
        case "large-v3-turbo": "Large v3 Turbo"
        default: family
        }
    }
}
