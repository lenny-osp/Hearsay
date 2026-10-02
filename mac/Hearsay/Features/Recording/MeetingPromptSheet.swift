import HearsayCore
import Observation
import SwiftUI

/// The open "Record this meeting?" question of `MeetingAutoRecord` (PLAN.md
/// 4.10), shared with the main window, which shows it as a sheet. The
/// coordinator opens and closes it; the sheet answers it. An answer for a
/// question that is no longer open does nothing, so Escape, closing the
/// window and the meeting ending cannot answer twice.
@MainActor
@Observable
final class MeetingPromptModel {
    struct Pending: Identifiable, Equatable {
        let id: Int
        /// The Record tab's current language choice.
        let preselected: LanguageChoice
    }

    private(set) var pending: Pending?
    /// Set by the coordinator: the user's answer, nil choice for Don't record.
    @ObservationIgnored var onAnswer: (@MainActor (_ id: Int, _ choice: LanguageChoice?) -> Void)?

    func show(id: Int, preselected: LanguageChoice) {
        pending = Pending(id: id, preselected: preselected)
    }

    /// Hides the question without an answer (the meeting ended, the setting
    /// went off).
    func close(id: Int) {
        guard pending?.id == id else { return }
        pending = nil
    }

    /// The user answered. Ignored unless `id` is the open question.
    func answer(id: Int, choice: LanguageChoice?) {
        guard pending?.id == id else { return }
        pending = nil
        onAnswer?(id, choice)
    }

    /// The sheet went away without a button (Escape, the window closing).
    func dismissed(id: Int) {
        answer(id: id, choice: nil)
    }
}

extension LanguageChoice {
    /// Name for a drop-down: "Auto", or the language by its own name, as the
    /// Auto mode default language picker in Settings names them (the segmented
    /// Record tab picker's short labels read poorly in a list).
    var dropDownName: String {
        switch self {
        case .auto: shortLabel
        case .fixed(let language): language.displayName
        }
    }
}

/// "Microsoft Teams meeting started / Record this meeting?" with the language
/// picker (PLAN.md 4.10). Record is the default button; Don't record is the
/// cancel button, so Escape counts as Don't record.
struct MeetingPromptSheet: View {
    let preselected: LanguageChoice
    let onRecord: (LanguageChoice) -> Void
    let onDontRecord: () -> Void
    @State private var choice: LanguageChoice

    init(preselected: LanguageChoice, onRecord: @escaping (LanguageChoice) -> Void, onDontRecord: @escaping () -> Void) {
        self.preselected = preselected
        self.onRecord = onRecord
        self.onDontRecord = onDontRecord
        _choice = State(initialValue: preselected)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Microsoft Teams meeting started",
                 comment: "Title of the sheet that asks whether to record a Microsoft Teams meeting and in which language (Settings > General > Meetings, \"Ask which language to use before each automatic recording\"). Keep \"Microsoft Teams\" as is.")
                .font(.headline)
            Text("Record this meeting?",
                 comment: "Text of the \"Microsoft Teams meeting started\" sheet, above its language picker.")
            Picker("Language", selection: $choice) {
                ForEach(LanguageChoice.allCases, id: \.self) { option in
                    Text(option.dropDownName).tag(option)
                }
            }
            HStack {
                Spacer()
                Button(String(localized: "Don't record",
                              comment: "Button of the \"Microsoft Teams meeting started\" sheet: do not record this meeting."),
                       role: .cancel, action: onDontRecord)
                    .keyboardShortcut(.cancelAction)
                Button(String(localized: "Record meeting", defaultValue: "Record",
                              comment: "Button (a verb) of the \"Microsoft Teams meeting started\" sheet: start recording in the chosen language. Not the Record tab's name."),
                       action: { onRecord(choice) })
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 380)
    }
}
