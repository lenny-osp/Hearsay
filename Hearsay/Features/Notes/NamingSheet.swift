import HearsayCore
import SwiftUI

/// Port of `select_meeting_name`: an editable name, normalized by
/// `FilenameSanitizer` exactly like the saved file names. Input with no
/// usable characters is rejected inline. Cancel keeps the timestamp names.
///
/// When regenerating notes, the field starts from the meeting's current
/// name (`currentName`) and the AI suggestion is a one-click alternative.
struct NamingSheet: View {
    let suggestion: String?
    let currentName: String?
    /// Saving moves the meeting's current notes to the Trash.
    let replacesNotes: Bool
    let onSave: (String) -> Void
    let onCancel: () -> Void

    @State private var name: String

    init(
        suggestion: String?,
        currentName: String? = nil,
        replacesNotes: Bool = false,
        onSave: @escaping (String) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.suggestion = suggestion
        self.currentName = currentName
        self.replacesNotes = replacesNotes
        self.onSave = onSave
        self.onCancel = onCancel
        _name = State(initialValue: currentName ?? suggestion ?? "")
    }

    private var slug: String? { FilenameSanitizer.sanitize(name) }

    /// The AI suggestion offered beside a prefilled current name.
    private var alternative: String? {
        guard currentName != nil, let suggestion,
              suggestion != currentName.flatMap(FilenameSanitizer.sanitize)
        else { return nil }
        return suggestion
    }

    private var explanation: String {
        if currentName != nil {
            return alternative == nil
                ? String(localized: "This is the meeting's current name. Edit it or press Return to keep it.",
                         comment: "Naming sheet explanation when regenerating notes")
                : String(localized: "This is the meeting's current name. Edit it, keep it, or use the AI's suggestion.",
                         comment: "Naming sheet explanation when regenerating notes")
        }
        return suggestion == nil
            ? String(localized: "The transcript and recording are renamed to <timestamp>_<name>.",
                     comment: "Naming sheet explanation. Translate the words inside <timestamp>_<name> but keep the angle brackets and underscore.")
            : String(localized: "The AI suggested this name. Edit it or press Return to accept it.",
                     comment: "Naming sheet explanation")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(suggestion == nil && currentName == nil
                 ? String(localized: "Name this meeting", comment: "Naming sheet title")
                 : String(localized: "Meeting name", comment: "Naming sheet title"))
                .font(.headline)
            Text(explanation)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("Meeting name (in English)", text: $name)
                .textFieldStyle(.roundedBorder)
                .onSubmit(save)

            if let alternative {
                Button("Use suggested name: \(alternative)") { name = alternative }
                    .disabled(slug == alternative)
            }

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

            if replacesNotes {
                Text("Saving moves the current notes to the Trash.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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

#Preview("Regenerate") {
    NamingSheet(
        suggestion: "genhe-road-trip", currentName: "trip-to-genhe", replacesNotes: true,
        onSave: { _ in }, onCancel: {}
    )
}
