import Foundation
import HearsayCore
import Observation

/// Drives the notes flow of PLAN.md 4.3 (Python `generate_meeting_notes`,
/// API branch, plus `name_retained_outputs`):
///
/// 1. Confirm send (skipped when `askBeforeSending` is off).
/// 2. Send: generate notes, then the naming sheet prefilled with the
///    sanitized AI suggestion, then `OutputWriter.saveNamed`.
/// 3. Keep local: "Name this meeting yourself?" (default no), then the
///    naming sheet with no suggestion and `OutputWriter.renameRetained`.
///
/// Every failure ends in `.failed` with a message; the SRT stays where it is.
///
/// Regenerating (History, an entry that already has notes): the naming
/// sheet starts from the current meeting name and saving goes through
/// `OutputWriter.replaceNamed`, which moves the current notes to the Trash
/// only once the new ones are saved. "Keep local" leaves every file as is.
@MainActor
@Observable
final class NotesFlowViewModel {
    struct Outcome: Equatable {
        var message: String
        /// Final file URLs, SRT first.
        var files: [URL]
    }

    enum Phase: Equatable {
        case idle
        case confirming
        case generating
        case askingManualNaming
        /// `suggestion` is the sanitized AI name, or nil for manual naming.
        case naming(suggestion: String?)
        case finished(Outcome)
        case failed(message: String, srtURL: URL)
    }

    private(set) var phase: Phase = .idle
    /// The SRT being processed (updated when it is renamed).
    private(set) var srtURL: URL?
    private(set) var transcriptCharacterCount = 0
    /// Retained recordings beside the SRT, shown when asking about naming.
    private(set) var retainedAudio: [URL] = []
    /// Template chosen in the confirm sheet for this run.
    var templateID: UUID
    /// The transcript's language: the default notes language.
    private(set) var transcriptLanguage: TranscriptLanguage = .english
    /// The caller's explanation of where `transcriptLanguage` came from
    /// (History: detected from the text, or assumed), or nil when known.
    private(set) var transcriptLanguageNote: String?
    /// The language the notes and structured transcript are written in,
    /// chosen in the confirm sheet for this run only (not stored).
    var notesLanguage: TranscriptLanguage = .english
    /// True when this run replaces existing notes (History > Regenerate).
    private(set) var isRegenerating = false
    /// The meeting name in the current stem while regenerating, prefilled in
    /// the naming sheet; nil for a plain timestamp stem or a new run.
    private(set) var currentMeetingName: String?

    let store: AIProviderStore
    @ObservationIgnored private let pipeline: NotesPipeline
    @ObservationIgnored private var srtText = ""
    @ObservationIgnored private var timestamp: String?
    @ObservationIgnored private var notes: NotesResponse?
    @ObservationIgnored private var generation: Task<Void, Never>?

    init(store: AIProviderStore, pipeline: NotesPipeline = NotesPipeline()) {
        self.store = store
        self.pipeline = pipeline
        self.templateID = store.configuration.selectedTemplateID
    }

    var isRunning: Bool {
        switch phase {
        case .idle, .finished, .failed: false
        default: true
        }
    }

    var providerName: String { store.configuration.preset.name }

    /// "Transcript language: …" under the confirm sheet's picker.
    var transcriptLanguageCaption: String {
        NotesLanguageCaption.transcriptLine(transcriptLanguage, note: transcriptLanguageNote)
    }

    /// "Notes will be written in …." when the choice differs, else nil.
    var notesLanguageCaption: String? {
        NotesLanguageCaption.notesLine(transcript: transcriptLanguage, notes: notesLanguage)
    }

    // MARK: - Entry point

    /// Starts the flow for a finished SRT. `timestamp` overrides the one in
    /// the SRT filename, as Python's `timestamp=` argument does.
    /// `language` is the transcript's language and the default notes
    /// language; `languageNote` replaces its name in the confirm sheet's
    /// caption (e.g. "Deutsch (detected from the text)").
    /// `replacingNotes` regenerates the notes of an existing meeting.
    func run(
        srtURL: URL,
        language: TranscriptLanguage,
        languageNote: String? = nil,
        timestamp: String? = nil,
        replacingNotes: Bool = false
    ) {
        guard !isRunning else { return }
        self.srtURL = srtURL
        isRegenerating = replacingNotes
        currentMeetingName = replacingNotes
            ? HistoryIndex.meetingName(stem: srtURL.deletingPathExtension().lastPathComponent)
            : nil
        transcriptLanguage = language
        transcriptLanguageNote = languageNote
        notesLanguage = language
        self.timestamp = timestamp
        notes = nil
        retainedAudio = []
        do {
            srtText = try String(contentsOf: srtURL, encoding: .utf8)
        } catch {
            fail(String(localized: "Error: SRT file not found or unreadable (\(srtURL.path)): \(error.localizedDescription)",
                        comment: "Meeting notes error. %1$@ is a file path, %2$@ the system error message."))
            return
        }
        transcriptCharacterCount = srtText.count
        templateID = store.configuration.selectedTemplateID
        if store.configuration.askBeforeSending {
            phase = .confirming
        } else {
            startGeneration()
        }
    }

    // MARK: - Confirm send

    /// "Send" in the confirm sheet (Python: Enter or `y`).
    func send() {
        guard phase == .confirming else { return }
        startGeneration()
    }

    private func startGeneration() {
        phase = .generating
        let template = store.template(id: templateID)
        let configuration = store.configuration
        let token = configuration.auth.needsToken ? store.currentToken : nil
        let text = srtText
        let language = notesLanguage.code
        let pipeline = pipeline
        generation = Task { [weak self] in
            do {
                let response = try await pipeline.generate(
                    srtText: text, languageCode: language, template: template,
                    configuration: configuration, token: token
                )
                self?.didGenerate(response)
            } catch is CancellationError {
                self?.fail(String(localized: "Meeting-note generation was cancelled; no meeting notes were generated.",
                                  comment: "Meeting notes result"))
            } catch {
                self?.fail(Self.describe(error) + "\n"
                           + String(localized: "No meeting notes were generated.", comment: "Follows a meeting notes error"))
            }
        }
    }

    /// "Keep local" in the confirm sheet (Python: `n`). Not a failure.
    /// When regenerating, the meeting already has a name and notes, so
    /// nothing is renamed.
    func keepLocal() {
        guard phase == .confirming, let srtURL else { return }
        if isRegenerating {
            finish(String(localized: "Nothing was sent; the current notes are unchanged.", comment: "Meeting notes result"),
                   files: [srtURL])
            return
        }
        retainedAudio = OutputWriter.retainedAudioFiles(srtURL: srtURL)
        phase = .askingManualNaming
    }

    func cancelGeneration() {
        generation?.cancel()
    }

    private func didGenerate(_ response: NotesResponse) {
        guard phase == .generating else { return }
        notes = response
        phase = .naming(suggestion: FilenameSanitizer.sanitize(response.filename))
    }

    // MARK: - Manual naming question

    /// "Name this meeting yourself?" answered yes.
    func acceptManualNaming() {
        guard phase == .askingManualNaming else { return }
        phase = .naming(suggestion: nil)
    }

    /// Answered no (the default): keep the timestamp names.
    func declineManualNaming() {
        guard phase == .askingManualNaming, let srtURL else { return }
        finish(String(localized: "Skipped AI processing; no meeting notes were generated.", comment: "Meeting notes result"),
               files: [srtURL] + retainedAudio)
    }

    // MARK: - Naming sheet

    func saveName(_ name: String) {
        guard case .naming = phase, let srtURL else { return }
        guard FilenameSanitizer.sanitize(name) != nil else { return }
        if let notes {
            do {
                let outputs = isRegenerating
                    ? try OutputWriter.replaceNamed(
                        existingStem: srtURL.deletingPathExtension().lastPathComponent,
                        directory: srtURL.deletingLastPathComponent(), meetingName: name,
                        markdown: notes.markdown, transcriptMarkdown: notes.transcriptMarkdown,
                        timestamp: timestamp
                    )
                    : try OutputWriter.saveNamed(
                        srtURL: srtURL, meetingName: name,
                        markdown: notes.markdown, transcriptMarkdown: notes.transcriptMarkdown,
                        timestamp: timestamp
                    )
                self.srtURL = outputs.srt
                finish(String(localized: "Meeting notes generated successfully!", comment: "Meeting notes result"),
                       files: [outputs.srt, outputs.markdown, outputs.transcript] + outputs.companions)
            } catch {
                fail(Self.describe(error))
            }
        } else {
            do {
                let renamed = try OutputWriter.renameRetained(srtURL: srtURL, meetingName: name, timestamp: timestamp)
                if let first = renamed.first { self.srtURL = first }
                finish(String(localized: "Skipped AI processing; renamed the transcript and recording.",
                              comment: "Meeting notes result"), files: renamed)
            } catch {
                fail(Self.describe(error) + "\n"
                     + String(localized: "Keeping the timestamp file names.", comment: "Follows a rename error"))
            }
        }
    }

    /// Cancel in the naming sheet keeps the timestamp names.
    func cancelNaming() {
        guard case .naming = phase, let srtURL else { return }
        if notes != nil {
            // Python: EOF at the name prompt after AI returns 1.
            fail(isRegenerating
                 ? String(localized: "No meeting name selected; the current notes are unchanged.",
                          comment: "Meeting notes result")
                 : String(localized: "No meeting name selected; the SRT has been retained.",
                          comment: "Meeting notes result"))
        } else {
            finish(String(localized: "Skipped AI processing; kept the timestamp file names.", comment: "Meeting notes result"),
                   files: [srtURL] + retainedAudio)
        }
    }

    // MARK: - Sheet dismissed without an answer

    /// A sheet went away without one of its buttons (Escape, window closed,
    /// tab switched, the Cancel button beside the panel). Resolves the step
    /// as Python does when input closes (EOF): the confirm prompt declines,
    /// and the follow-up "Name this meeting yourself?" then also reads EOF
    /// and keeps the timestamp names; the naming prompt cancels. No-op in
    /// other phases.
    func sheetDismissed() {
        switch phase {
        case .confirming:
            keepLocal()
            declineManualNaming()
        case .askingManualNaming:
            declineManualNaming()
        case .naming:
            cancelNaming()
        default:
            break
        }
    }

    func reset() {
        guard !isRunning else { return }
        phase = .idle
    }

    // MARK: - Helpers

    private func finish(_ message: String, files: [URL]) {
        notes = nil
        phase = .finished(Outcome(message: message, files: files))
    }

    private func fail(_ message: String) {
        notes = nil
        guard let srtURL else {
            phase = .idle
            return
        }
        phase = .failed(message: message, srtURL: srtURL)
    }

    static func describe(_ error: Error) -> String {
        if let localized = error as? LocalizedError, let text = localized.errorDescription {
            return text
        }
        return error.localizedDescription
    }
}
