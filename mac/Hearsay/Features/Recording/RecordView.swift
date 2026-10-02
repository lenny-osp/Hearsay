import AppKit
import HearsayCore
import SwiftUI

/// The Record tab: microphone, system audio, language, controls, level
/// meters, and the recordings being transcribed (PLAN.md 4.1, 4.9). The
/// session lives in the app-level `RecordingController` and the finished
/// recordings in `TranscriptionQueue`, so this view only reflects them and
/// closing the window never stops a recording or a transcription.
///
/// The current session is on top. With no session and one recording in the
/// queue, that recording is shown as the single meeting always was (live
/// preview, progress, "Use live preview instead", the saved files, the
/// notes flow), except under "When I start them" (PLAN.md 4.11), where every
/// recording is a row; otherwise the queue is a list of rows below the
/// session.
struct RecordView: View {
    @Environment(RecordingController.self) private var model
    @Environment(TranscriptionQueue.self) private var queue
    @Environment(AIProviderStore.self) private var aiStore
    @Environment(AppSettings.self) private var settings
    @Environment(PermissionMonitor.self) private var permissions
    /// Switches the main window to the Models tab.
    var onOpenModels: () -> Void = {}
    @State private var notes = NotesHandoff()
    /// The SRT the notes flow started with, to follow its rename.
    @State private var notesSRT: URL?

    var body: some View {
        @Bindable var model = model
        Form {
            PermissionsSection()

            Section {
                Picker("Microphone", selection: $model.microphoneChoice) {
                    ForEach(model.devices) { device in
                        Text(device.name).tag(MicrophoneChoice.device(uid: device.uid))
                    }
                    // PLAN.md 4.13; also the choice when no input device exists.
                    if !model.devices.isEmpty {
                        Divider()
                    }
                    Text("No microphone (system audio only)",
                         comment: "Record tab: last row of the Microphone picker; records only the sound the Mac plays.")
                        .tag(MicrophoneChoice.noMicrophone)
                }
                .disabled(model.isSessionActive)

                // Without a microphone system audio is the recording: shown
                // on and locked, the stored choice unchanged (PLAN.md 4.13).
                Toggle("Also capture system audio", isOn: model.microphone.recordsMicrophone
                       ? $model.captureSystemAudio : .constant(true))
                    .disabled(model.isSessionActive || !model.microphone.recordsMicrophone)

                // The script row follows on its own row for ZH and Auto.
                LanguageChoicePicker(isDisabled: model.isSessionActive)
            }

            Section {
                HStack(alignment: .firstTextBaseline) {
                    Text(LevelMeter.formatElapsed(model.elapsed))
                        .font(.system(.largeTitle, design: .monospaced))
                        .monospacedDigit()
                    Spacer()
                    statusLabel
                }
                ProgressView(value: model.levelFraction)
                    .progressViewStyle(.linear)
                    .tint(model.silenceWarning == nil ? .green : .orange)
                    .accessibilityLabel("Input level")
                if showsMicMeter || model.systemLevelFraction != nil {
                    HStack(spacing: 16) {
                        if showsMicMeter {
                            sourceMeter(String(localized: "Mic", comment: "Record tab: microphone level meter (tooltip)"),
                                        systemImage: "mic.fill", fraction: model.micLevelFraction)
                        }
                        if let system = model.systemLevelFraction {
                            sourceMeter(String(localized: "System", comment: "Record tab: system audio level meter (tooltip)"),
                                        systemImage: "speaker.wave.2.fill", fraction: system)
                        }
                    }
                }
                // A recording Hearsay started for a Microsoft Teams meeting (PLAN.md 4.10).
                if let notice = model.automaticStartNotice {
                    Label(notice, systemImage: "video.fill")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let notice = model.systemAudioNotice {
                    HStack {
                        Label(notice, systemImage: "speaker.slash.fill")
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                        Spacer()
                        if model.systemAudioDenied {
                            Button("Open System Settings") { model.openScreenCaptureSettings() }
                        }
                    }
                }
                if let warning = model.silenceWarning {
                    Label(warning, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                }
                controls
            }

            if model.isLivePreviewTurnedOff, let notice = model.liveNotice {
                // Settings turned the preview off (PLAN.md 4.12): no empty
                // transcript box, only the notice.
                Section("Live preview") {
                    Label(notice, systemImage: "text.bubble")
                        .foregroundStyle(.secondary)
                }
            } else if model.isCapturing || model.phase == .stopping || !model.liveSegments.isEmpty
                || model.liveNotice != nil {
                liveTranscript(
                    segments: model.liveSegments, waiting: model.liveChunksWaiting,
                    lagging: model.isLiveLagging, detecting: model.isDetectingLanguage,
                    enabled: model.isLivePreviewEnabled, notice: model.liveNotice
                )
            }

            if let notice = model.languageNotice {
                Section {
                    LanguageNoticeView(
                        notice: notice,
                        isEnabled: model.canChangeSessionLanguage,
                        onRerun: { model.transcribeAgain(in: $0) },
                        onDismiss: { model.dismissLanguageNotice() }
                    )
                }
            }

            if let error = model.errorMessage {
                Section {
                    Label(error, systemImage: "exclamationmark.octagon.fill")
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                    HStack {
                        if model.failureOpensScreenCaptureSettings {
                            Button("Open System Settings") { model.openScreenCaptureSettings() }
                        }
                        if model.needsModel {
                            Button("Open Models", systemImage: "square.and.arrow.down", action: onOpenModels)
                        }
                        if model.canRetryTranscription {
                            Button("Try Again", systemImage: "arrow.clockwise") { model.retryTranscription() }
                        }
                        if model.finishedRecording != nil {
                            Button("Transcribe this file", systemImage: "doc.badge.plus") {
                                model.requestTranscribeFile()
                            }
                        }
                    }
                }
                if model.finishedTranscript != nil || model.finishedRecording != nil {
                    Section("Saved") {
                        if let srt = model.finishedTranscript {
                            LabeledContent("Transcript") { pathText(srt) }
                        }
                        if let wav = model.finishedRecording {
                            LabeledContent("Recording") { pathText(wav) }
                        }
                        Button("Reveal in Finder", systemImage: "folder") {
                            model.revealInFinder()
                        }
                    }
                }
            }

            if let job = queue.featuredJob {
                featuredSections(job)
            } else if !queue.jobs.isEmpty {
                Section {
                    ForEach(queue.jobs) { job in
                        QueueRow(
                            job: job,
                            isPausedForSession: queue.isPausedForSession(job),
                            canGenerateNotes: notes.notes?.isRunning != true,
                            onGenerateNotes: { startNotes(for: job) }
                        )
                    }
                } header: {
                    // "When I start them" (PLAN.md 4.11): Transcribe All while a job is held.
                    HStack {
                        Text("Transcription queue")
                        Spacer()
                        if queue.heldCount > 0 {
                            Button(String(localized: "Transcribe All", comment: "Button of the Record tab's queue card and menu bar panel: start the transcription of every recording that waits for the user.")) { queue.releaseAll() }
                                .buttonStyle(.borderedProminent)
                                .controlSize(.small)
                        }
                    }
                }
            }

            if let notesModel = notes.notes {
                Section("Meeting notes") {
                    NotesFlowView(model: notesModel)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            model.activate()
            permissions.refresh()
            takeNotesRequest()
        }
        .onChange(of: queue.notesRequest) { takeNotesRequest() }
        .onChange(of: model.phase) {
            if model.phase == .starting { notes.reset() }
        }
        .onChange(of: notes.notes?.phase) {
            // The notes flow renames the SRT and its WAV (PLAN.md 4.3 step 7).
            if case .finished(let outcome) = notes.notes?.phase, let notesSRT {
                queue.filesRenamed(from: notesSRT, to: outcome.files)
                self.notesSRT = nil
            }
        }
    }

    /// No Mic meter for a session without a microphone, or before one
    /// (PLAN.md 4.13).
    private var showsMicMeter: Bool {
        model.isSessionActive ? model.sessionRecordsMicrophone : model.microphone.recordsMicrophone
    }

    /// Tooltip of Hold (PLAN.md 4.11).
    fileprivate static var holdHelp: String {
        String(localized: "Stops transcribing for now. Transcribe continues where it stopped.",
               comment: "Tooltip of the Hold button. \"Transcribe\" is the translated button of that name.")
    }

    private func pathText(_ url: URL) -> some View {
        Text(url.path)
            .textSelection(.enabled)
            .lineLimit(2)
            .truncationMode(.middle)
    }

    /// PLAN.md 4.3 step 1: the finished SRT goes straight to the notes flow
    /// (when the queue decides it may, PLAN.md 4.9 item 4; it checks again
    /// here and otherwise leaves "Generate Notes…" on the job's row).
    private func takeNotesRequest() {
        guard let request = queue.takeNotesRequest() else { return }
        notesSRT = request.srt
        notes.start(srtURL: request.srt, language: request.language, store: aiStore, settings: settings)
    }

    /// "Generate Notes…" on a queue row.
    private func startNotes(for job: TranscriptionJob) {
        guard let srt = job.srt, let language = job.language else { return }
        queue.notesStarted(for: job)
        notesSRT = srt
        notes.start(srtURL: srt, language: language, store: aiStore, settings: settings)
    }

    // MARK: - The single recording

    /// One recording and no session: the sections the Record tab always
    /// showed after Stop.
    @ViewBuilder
    private func featuredSections(_ job: TranscriptionJob) -> some View {
        if !job.liveSegments.isEmpty || job.liveNotice != nil {
            liveTranscript(
                segments: job.liveSegments, waiting: job.liveChunksWaiting,
                lagging: job.liveChunksWaiting > 1, detecting: job.isDetectingLanguage,
                enabled: job.liveEnabled, notice: job.liveNotice
            )
        }

        if let notice = job.languageNotice {
            Section {
                LanguageNoticeView(
                    notice: notice,
                    isEnabled: job.canChangeLanguage,
                    onRerun: { queue.transcribeAgain(job, in: $0) },
                    onDismiss: { queue.dismissLanguageNotice(job) }
                )
            }
        }

        if let progress = job.displayProgress {
            // Never held: under "When I start them" (PLAN.md 4.11) there is
            // no single meeting, every recording is a queue row.
            Section {
                ProgressView(value: progress)
                    .progressViewStyle(.linear)
                HStack {
                    Text(progress, format: .percent.precision(.fractionLength(0)))
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                    Spacer()
                    if job.canUseLivePreview {
                        Button("Use live preview instead") { queue.useLivePreviewInstead(job) }
                            .help("Skip the full pass and save the live preview as the transcript")
                    } else if job.isUsingLivePreview {
                        Text("Saving the live preview…").foregroundStyle(.secondary)
                    }
                }
            } header: {
                Text("Transcribing")
            }
        }

        if job.state == .failed, let error = job.errorMessage {
            Section {
                Label(error, systemImage: "exclamationmark.octagon.fill")
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                HStack {
                    if job.needsModel {
                        Button("Open Models", systemImage: "square.and.arrow.down", action: onOpenModels)
                    }
                    Button("Try Again", systemImage: "arrow.clockwise") { queue.retry(job) }
                    if let wav = job.wav {
                        Button("Transcribe this file", systemImage: "doc.badge.plus") {
                            model.transcribeFileRequest = wav
                        }
                    }
                }
            }
        }

        if !job.isPending, job.srt != nil || job.wav != nil {
            Section("Saved") {
                if let srt = job.srt {
                    LabeledContent("Transcript") { pathText(srt) }
                }
                if let wav = job.wav {
                    LabeledContent("Recording") { pathText(wav) }
                }
                if let language = job.language, job.state == .done {
                    LabeledContent("Language") { Text(language.displayName) }
                }
                if let rerunError = job.rerunError {
                    Label(rerunError, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                        .textSelection(.enabled)
                }
                HStack {
                    Button("Reveal in Finder", systemImage: "folder") {
                        queue.revealInFinder(job)
                    }
                    if job.offersNotes, job.state == .done {
                        Button("Generate Notes…", systemImage: "sparkles") { startNotes(for: job) }
                            .disabled(notes.notes?.isRunning == true)
                    }
                }
            }
        }
    }

    // MARK: - Live preview

    /// Read-only live preview, newest line kept in view.
    ///
    /// A `ScrollView`, not a `List`: a `List` nested in the grouped `Form`
    /// never scrolled (`scrollTo` had no effect), so only the first rows of
    /// the first chunk were ever visible while later chunks were appended
    /// below the fold.
    private func liveTranscript(
        segments: [CoreSegment], waiting: Int, lagging: Bool, detecting: Bool, enabled: Bool, notice: String?
    ) -> some View {
        Section {
            let cues = segments.enumerated().filter { !$0.element.text.isEmpty }
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 6) {
                        ForEach(cues, id: \.offset) { item in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                Text(LevelMeter.formatElapsed(item.element.start))
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(.secondary)
                                Text(item.element.text)
                                    .textSelection(.enabled)
                                Spacer(minLength: 0)
                            }
                            .id(item.offset)
                        }
                    }
                    .padding(.vertical, 4)
                }
                .defaultScrollAnchor(.bottom)
                .frame(height: 200)
                .overlay {
                    if cues.isEmpty {
                        if detecting {
                            Label("Detecting language…", systemImage: "globe")
                                .foregroundStyle(.secondary)
                        } else {
                            Text(enabled
                                 ? String(localized: "The first lines appear after about 10 to 30 s.",
                                          comment: "Live preview placeholder while no line has arrived yet")
                                 : "")
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                .onChange(of: segments.count) {
                    guard let last = segments.lastIndex(where: { !$0.text.isEmpty }) else { return }
                    proxy.scrollTo(last, anchor: .bottom)
                }
            }
        } header: {
            HStack {
                Text("Live preview")
                Spacer()
                if detecting {
                    Label("Detecting language…", systemImage: "globe")
                        .foregroundStyle(.secondary)
                } else if lagging {
                    Label("\(waiting) chunks waiting", systemImage: "hourglass")
                        .foregroundStyle(.orange)
                        .help("The preview lags behind the recording but stays complete.")
                } else if waiting == 1 {
                    ProgressView().controlSize(.mini)
                }
            }
        } footer: {
            if let notice {
                Text(notice).foregroundStyle(.secondary)
            }
        }
    }

    private func sourceMeter(_ title: String, systemImage: String, fraction: Double) -> some View {
        HStack(spacing: 6) {
            Image(systemName: systemImage)
                .foregroundStyle(.secondary)
                .frame(width: 16)
            ProgressView(value: fraction)
                .progressViewStyle(.linear)
                .controlSize(.small)
                .tint(.green)
                .frame(maxWidth: 120)
        }
        .help(title)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("\(title) level", comment: "Accessibility label. %@ is Mic or System."))
        .accessibilityValue(Text(fraction, format: .percent.precision(.fractionLength(0))))
    }

    @ViewBuilder
    private var statusLabel: some View {
        switch model.phase {
        case .idle, .failed:
            // Under "When I start them" there is no single meeting (PLAN.md
            // 4.11): the rows explain themselves, and this reads "Ready".
            if let job = queue.featuredJob, model.errorMessage == nil {
                if job.isPending || job.isRerunning {
                    Text("Finalizing…").foregroundStyle(.secondary)
                } else if job.state == .done {
                    Text("Saved").foregroundStyle(.secondary)
                } else {
                    Text("Ready").foregroundStyle(.secondary)
                }
            } else {
                Text("Ready").foregroundStyle(.secondary)
            }
        case .starting:
            Text("Starting…").foregroundStyle(.secondary)
        case .recording:
            Label("Recording", systemImage: "record.circle.fill").foregroundStyle(.red)
        case .paused:
            Label("Paused", systemImage: "pause.circle.fill").foregroundStyle(.secondary)
        case .stopping:
            Text("Saving…").foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private var controls: some View {
        HStack {
            switch model.phase {
            case .idle, .starting, .failed:
                Button("Start", systemImage: "record.circle") { model.start() }
                .keyboardShortcut(.defaultAction)
                .disabled(!model.canStart)
            case .recording:
                Button("Pause", systemImage: "pause.fill") { model.pause() }
                Button("Stop", systemImage: "stop.fill") { Task { await model.stop() } }
                    .keyboardShortcut(.defaultAction)
                stopStartNextButton
            case .paused:
                Button("Resume", systemImage: "play.fill") { model.resume() }
                Button("Stop", systemImage: "stop.fill") { Task { await model.stop() } }
                    .keyboardShortcut(.defaultAction)
                stopStartNextButton
            case .stopping:
                ProgressView().controlSize(.small)
            }
            Spacer()
        }
    }

    private var stopStartNextButton: some View {
        Button("Stop & Start Next", systemImage: "forward.end.fill") { model.stopAndStartNext() }
            .help(String(localized: "Save this recording and start the next one at once (\(settings.stopStartNextHotkey.displayString)). This one is transcribed in the background.",
                         comment: "Record tab tooltip of Stop & Start Next. %@ is a shortcut such as ⌃⌥⌘N."))
    }
}

/// The state text of a held job (PLAN.md 4.11): "Not transcribed yet", or
/// "On hold · N%" once its pass had started.
@MainActor
private func heldStateText(_ job: TranscriptionJob) -> String {
    if job.state == .waiting {
        return String(localized: "Not transcribed yet",
                      comment: "Record tab queue row state: the recording waits until the user starts its transcription (\"When I start them\").")
    }
    return String(localized: "On hold · \(Int((job.progress * 100).rounded()))%",
                  comment: "Record tab queue row state: the user put a partly transcribed recording on hold. %lld is a percentage; keep the % sign after it.")
}

/// One recording in the Record tab's queue list: its name, its state, and
/// what can be done with it (PLAN.md 4.9 "UI").
private struct QueueRow: View {
    @Environment(TranscriptionQueue.self) private var queue
    let job: TranscriptionJob
    let isPausedForSession: Bool
    let canGenerateNotes: Bool
    let onGenerateNotes: () -> Void
    /// "Move to Trash…" was clicked; the alert asks first (PLAN.md 4.11).
    @State private var confirmsTrash = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(job.title)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(stateText)
                        .font(.caption)
                        .foregroundStyle(stateColor)
                        .monospacedDigit()
                }
                Spacer()
                actions
                    .controlSize(.small)
            }
            if let progress = job.displayProgress, job.state != .waiting || job.isRerunning, !isPausedForSession {
                ProgressView(value: progress)
                    .progressViewStyle(.linear)
                    .controlSize(.small)
            }
            if job.state == .failed, let error = job.errorMessage {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .lineLimit(4)
            }
            if let notice = job.languageNotice, job.state == .done || job.state == .waiting {
                LanguageNoticeView(
                    notice: notice,
                    isEnabled: job.canChangeLanguage,
                    onRerun: { queue.transcribeAgain(job, in: $0) },
                    onDismiss: { queue.dismissLanguageNotice(job) }
                )
                .font(.caption)
            }
            if let rerunError = job.rerunError {
                Label(rerunError, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            if let trashError = job.trashError {
                Text(trashError)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .lineLimit(4)
            }
        }
        .alert(
            String(localized: "Move this recording to the Trash?",
                   comment: "Alert title after Move to Trash… on a Record tab queue row of a recording that waits (\"When I start them\")."),
            isPresented: $confirmsTrash
        ) {
            // Default action so Return confirms, as in History.
            Button("Move to Trash", role: .destructive) { queue.trash(job) }
                .keyboardShortcut(.defaultAction)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("It is not transcribed, and its live preview is discarded.",
                 comment: "Message of the alert that asks before a recording that waits in the Record tab's queue is moved to the Trash.")
        }
    }

    private var stateText: String {
        if queue.isHeld(job) { return heldStateText(job) }
        if job.isRerunning {
            return String(localized: "Transcribing \(Int((job.rerunProgress * 100).rounded()))%",
                          comment: "Record tab queue row state. %lld is a percentage; keep the % sign after it.")
        }
        switch job.state {
        case .waiting:
            if isPausedForSession {
                return String(localized: "Paused while recording",
                              comment: "Record tab queue row state: the transcription waits until the recording stops")
            }
            return String(localized: "Waiting", comment: "Record tab queue row state: not started yet")
        case .running:
            return String(localized: "Transcribing \(Int((job.progress * 100).rounded()))%",
                          comment: "Record tab queue row state. %lld is a percentage; keep the % sign after it.")
        case .suspended:
            if isPausedForSession {
                return String(localized: "Paused while recording",
                              comment: "Record tab queue row state: the transcription waits until the recording stops")
            }
            return String(localized: "Transcribing \(Int((job.progress * 100).rounded()))%",
                          comment: "Record tab queue row state. %lld is a percentage; keep the % sign after it.")
        case .done:
            return String(localized: "queue.state.done", defaultValue: "Done",
                          comment: "Record tab queue row state: the transcript is written")
        case .failed:
            return String(localized: "Failed", comment: "Record tab queue row state: the transcription failed")
        }
    }

    private var stateColor: Color {
        switch job.state {
        case .failed: .red
        default: .secondary
        }
    }

    @ViewBuilder
    private var actions: some View {
        HStack(spacing: 6) {
            if queue.isHeld(job) {
                Button("Transcribe") { queue.release(job) }
                    .buttonStyle(.borderedProminent)
            }
            if queue.canHold(job) {
                Button(String(localized: "Hold", comment: "Button on a queue row being transcribed: stop transcribing it for now.")) { queue.hold(job) }
                    .help(RecordView.holdHelp)
            }
            if job.canUseLivePreview {
                Button("Use live preview instead") { queue.useLivePreviewInstead(job) }
                    .help("Skip the full pass and save the live preview as the transcript")
            } else if job.isUsingLivePreview, job.isPending {
                Text("Saving the live preview…").font(.caption).foregroundStyle(.secondary)
            }
            if job.state == .done, job.srt != nil {
                Button("Open Transcript") { queue.openTranscript(job) }
                if job.offersNotes {
                    Button("Generate Notes…", action: onGenerateNotes)
                        .disabled(!canGenerateNotes)
                }
            }
            if job.state == .failed {
                Button("Try Again") { queue.retry(job) }
            }
            if !job.isPending {
                Button {
                    queue.revealInFinder(job)
                } label: {
                    Image(systemName: "folder")
                }
                .help("Reveal in Finder")
                .accessibilityLabel(Text("Reveal in Finder"))
            }
            if queue.canTrash(job) {
                Button("Move to Trash…", role: .destructive) { confirmsTrash = true }
            }
            if job.canDismiss {
                Button {
                    queue.dismiss(job)
                } label: {
                    Image(systemName: "xmark")
                }
                .buttonStyle(.borderless)
                .help("Dismiss")
                .accessibilityLabel(Text("Dismiss"))
            }
        }
    }
}

#Preview {
    let settings = AppSettings()
    let modelStore = ModelStore(settings: settings)
    let engine = WhisperEngine()
    let queue = TranscriptionQueue(settings: settings, modelStore: modelStore, engine: engine, drives: false)
    RecordView()
        .environment(settings)
        .environment(AIProviderStore())
        .environment(PermissionMonitor(settings: settings, sources: .fixed(microphone: .authorized, screenGranted: false),
                                       allowsReset: false))
        .environment(queue)
        .environment(RecordingController(settings: settings, modelStore: modelStore, engine: engine, queue: queue))
}
