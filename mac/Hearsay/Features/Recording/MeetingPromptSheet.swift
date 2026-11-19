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
        /// The Record tab's microphone choice when the question opened
        /// (PLAN.md 4.10 "Microphone in the question", rule 2).
        let preselectedMicrophone: MicrophoneChoice
    }

    private(set) var pending: Pending?
    /// Set by the coordinator: the user's answer, nil answer for Don't record.
    @ObservationIgnored var onAnswer: (@MainActor (_ id: Int, _ answer: MeetingPromptAnswer?) -> Void)?

    func show(id: Int, preselected: LanguageChoice, microphone: MicrophoneChoice) {
        pending = Pending(id: id, preselected: preselected, preselectedMicrophone: microphone)
    }

    /// Hides the question without an answer (the meeting ended, the setting
    /// went off).
    func close(id: Int) {
        guard pending?.id == id else { return }
        pending = nil
    }

    /// The user answered. Ignored unless `id` is the open question.
    func answer(id: Int, answer: MeetingPromptAnswer?) {
        guard pending?.id == id else { return }
        pending = nil
        onAnswer?(id, answer)
    }

    /// The sheet went away without a button (Escape, the window closing).
    func dismissed(id: Int) {
        answer(id: id, answer: nil)
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

/// The question as the main window shows it: `MeetingPromptSheet` fed with
/// the recording controller's device list and microphone choice, read here
/// so the sheet follows device changes while it is open (PLAN.md 4.10
/// "Microphone in the question", rule 3).
struct LiveMeetingPromptSheet: View {
    let pending: MeetingPromptModel.Pending
    let model: MeetingPromptModel
    @Environment(RecordingController.self) private var recording

    var body: some View {
        MeetingPromptSheet(
            preselected: pending.preselected,
            preselectedMicrophone: pending.preselectedMicrophone,
            devices: recording.devices,
            recordTabMicrophone: recording.microphoneChoice,
            onRecord: { model.answer(id: pending.id, answer: $0) },
            onDontRecord: { model.answer(id: pending.id, answer: nil) }
        )
    }
}

/// "Microsoft Teams meeting started / Record this meeting?" with the
/// microphone and language pickers (PLAN.md 4.10, "Microphone in the
/// question"). Record is the default button; Don't record is the cancel
/// button, so Escape counts as Don't record.
///
/// The device list and the Record tab's microphone choice come in as values
/// (the main window passes the recording controller's, which follow device
/// changes), so the sheet renders with sample devices in the UI snapshots.
/// When the list changes while the sheet is open, the pick follows
/// `MicrophoneChoice.inMeetingQuestion` (rule 3).
struct MeetingPromptSheet: View {
    let preselected: LanguageChoice
    /// The input devices now; live while the sheet is open.
    let devices: [AudioInputDevice]
    /// The Record tab's microphone choice now: the fallback for a picked
    /// device that disappears.
    let recordTabMicrophone: MicrophoneChoice
    let onRecord: (MeetingPromptAnswer) -> Void
    let onDontRecord: () -> Void
    @State private var choice: LanguageChoice
    @State private var microphone: MicrophoneChoice

    init(
        preselected: LanguageChoice,
        preselectedMicrophone: MicrophoneChoice,
        devices: [AudioInputDevice],
        recordTabMicrophone: MicrophoneChoice,
        onRecord: @escaping (MeetingPromptAnswer) -> Void,
        onDontRecord: @escaping () -> Void
    ) {
        self.preselected = preselected
        self.devices = devices
        self.recordTabMicrophone = recordTabMicrophone
        self.onRecord = onRecord
        self.onDontRecord = onDontRecord
        _choice = State(initialValue: preselected)
        _microphone = State(initialValue: preselectedMicrophone)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Microsoft Teams meeting started",
                 comment: "Title of the sheet that asks whether to record a Microsoft Teams meeting and in which language (Settings > General > Meetings, \"Ask which language to use before each automatic recording\"). Keep \"Microsoft Teams\" as is.")
                .font(.headline)
            Text("Record this meeting?",
                 comment: "Text of the \"Microsoft Teams meeting started\" sheet, above its language picker.")
            // Two rows with their labels in one column, so both pop-ups
            // start at the same edge (the pickers' own labels stay for
            // VoiceOver).
            Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 10) {
                GridRow {
                    Text("Microphone")
                        .gridColumnAlignment(.trailing)
                    // The Record tab picker's rows (PLAN.md 4.13). A long
                    // device name truncates instead of widening the sheet.
                    Picker("Microphone", selection: $microphone) {
                        ForEach(devices) { device in
                            Text(device.name).tag(MicrophoneChoice.device(uid: device.uid))
                        }
                        if !devices.isEmpty {
                            Divider()
                        }
                        Text("No microphone (system audio only)",
                             comment: "Record tab: last row of the Microphone picker; records only the sound the Mac plays.")
                            .tag(MicrophoneChoice.noMicrophone)
                    }
                    .labelsHidden()
                    .truncationMode(.tail)
                    .onChange(of: devices) {
                        microphone = microphone.inMeetingQuestion(
                            deviceUIDs: devices.map(\.uid), recordTabChoice: recordTabMicrophone)
                    }
                }
                GridRow {
                    Text("Language")
                    Picker("Language", selection: $choice) {
                        ForEach(LanguageChoice.allCases, id: \.self) { option in
                            Text(option.dropDownName).tag(option)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
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
                       action: { onRecord(MeetingPromptAnswer(language: choice, microphone: microphone)) })
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 380)
    }
}
