import HearsayCore
import SwiftUI

/// Step 1 of the notes flow (Python `confirm_ai_processing`): the transcript
/// only leaves the Mac after "Send". Enter sends, `n` keeps it local.
struct ConfirmSendSheet: View {
    @Bindable var model: NotesFlowViewModel

    var body: some View {
        @Bindable var store = model.store
        VStack(alignment: .leading, spacing: 14) {
            Text("Send transcript for meeting notes?")
                .font(.headline)

            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 6) {
                GridRow {
                    Text("Provider:").foregroundStyle(.secondary)
                    Text(model.providerName)
                }
                GridRow {
                    Text("Model:").foregroundStyle(.secondary)
                    Text(store.configuration.model.isEmpty ? "(none set)" : store.configuration.model)
                }
                GridRow {
                    Text("Notes language:").foregroundStyle(.secondary)
                    Text(model.languageLine)
                        .fixedSize(horizontal: false, vertical: true)
                }
                GridRow {
                    Text("Transcript:").foregroundStyle(.secondary)
                    Text("\(model.transcriptCharacterCount.formatted()) characters")
                }
                if let srtURL = model.srtURL {
                    GridRow {
                        Text("File:").foregroundStyle(.secondary)
                        Text(srtURL.lastPathComponent)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                }
            }

            Picker("Template:", selection: $model.templateID) {
                ForEach(store.templates) { template in
                    Text(template.name).tag(template.id)
                }
            }

            Text("This sends the whole transcript to \(model.providerName). Nothing leaves this Mac otherwise.")
                .fixedSize(horizontal: false, vertical: true)

            Toggle("Always ask before sending", isOn: $store.configuration.askBeforeSending)

            HStack {
                Spacer()
                Button("Keep local") { model.keepLocal() }
                    .keyboardShortcut("n", modifiers: [])
                Button("Send") { model.send() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 440)
    }
}
