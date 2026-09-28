import HearsayCore
import SwiftUI

/// Port of `select_meeting_name`: an editable name, normalized by
/// `FilenameSanitizer` exactly like the saved file names. Input with no
/// usable characters is rejected inline. Cancel keeps the timestamp names.
struct NamingSheet: View {
    let suggestion: String?
    let onSave: (String) -> Void
    let onCancel: () -> Void

    @State private var name: String

    init(suggestion: String?, onSave: @escaping (String) -> Void, onCancel: @escaping () -> Void) {
        self.suggestion = suggestion
        self.onSave = onSave
        self.onCancel = onCancel
        _name = State(initialValue: suggestion ?? "")
    }

    private var slug: String? { FilenameSanitizer.sanitize(name) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(suggestion == nil ? "Name this meeting" : "Meeting name")
                .font(.headline)
            Text(suggestion == nil
                 ? "The transcript and recording are renamed to <timestamp>_<name>."
                 : "The AI suggested this name. Edit it or press Return to accept it.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Meeting name (in English)", text: $name)
                .textFieldStyle(.roundedBorder)
                .onSubmit(save)

            if let slug {
                LabeledContent("File name:") {
                    Text(slug)
                        .font(.body.monospaced())
                        .textSelection(.enabled)
                }
            } else {
                Text("Enter an English meeting name using letters or digits.")
                    .font(.callout)
                    .foregroundStyle(.red)
            }

            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: onCancel)
                    .keyboardShortcut(.cancelAction)
                Button("Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(slug == nil)
            }
        }
        .padding(20)
        .frame(width: 420)
    }

    private func save() {
        guard slug != nil else { return }
        onSave(name)
    }
}

#Preview {
    NamingSheet(suggestion: "quarterly-planning", onSave: { _ in }, onCancel: {})
}
