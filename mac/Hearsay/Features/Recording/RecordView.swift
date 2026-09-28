import HearsayCore
import SwiftUI

/// The Record tab: microphone, system audio, language, controls, level
/// meters, and the saved recording (PLAN.md 4.1). The session itself lives
/// in the app-level `RecordingController`, so this view only reflects it and
/// closing the window never stops a recording.
struct RecordView: View {
    @Environment(RecordingController.self) private var model
    @Environment(AIProviderStore.self) private var aiStore
    @Environment(AppSettings.self) private var settings
    @Environment(PermissionMonitor.self) private var permissions
    /// Switches the main window to the Models tab.
    var onOpenModels: () -> Void = {}
    @State private var notes = NotesHandoff()

    var body: some View {
        @Bindable var model = model
        Form {
            PermissionsSection()

            Section {
                Picker("Microphone", selection: $model.selectedDeviceUID) {
                    if model.devices.isEmpty {
                        Text("No input device").tag(String?.none)
                    }
                    ForEach(model.devices) { device in
                        Text(device.name).tag(Optional(device.uid))
                    }
                }
                .disabled(model.isSessionActive)

                Toggle("Also capture system audio", isOn: $model.captureSystemAudio)
                    .disabled(model.isSessionActive)

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
                HStack(spacing: 16) {
                    sourceMeter(String(localized: "Mic", comment: "Record tab: microphone level meter (tooltip)"),
                                systemImage: "mic.fill", fraction: model.micLevelFraction)
                    if let system = model.systemLevelFraction {
                        sourceMeter(String(localized: "System", comment: "Record tab: system audio level meter (tooltip)"),
                                    systemImage: "speaker.wave.2.fill", fraction: system)
                    }
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

            if model.isCapturing || model.phase == .stopping || !model.liveSegments.isEmpty
                || model.liveNotice != nil {
                liveTranscript
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

            if let progress = model.transcriptionProgress {
                Section("Transcribing") {
                    ProgressView(value: progress)
                        .progressViewStyle(.linear)
                    HStack {
                        Text(progress, format: .percent.precision(.fractionLength(0)))
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                        Spacer()
                        if model.canUseLivePreview {
                            Button("Use live preview instead") { model.useLivePreviewInstead() }
                                .help("Skip the full pass and save the live preview as the transcript")
                        } else if model.isUsingLivePreview {
                            Text("Saving the live preview…").foregroundStyle(.secondary)
                        }
                    }
                }
            }

            if let error = model.errorMessage {
                Section {
                    Label(error, systemImage: "exclamationmark.octagon.fill")
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                    HStack {
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
            }

            if model.finishedTranscript != nil || model.finishedRecording != nil {
                Section("Saved") {
                    if let srt = model.finishedTranscript {
                        LabeledContent("Transcript") { pathText(srt) }
                    }
                    if let wav = model.finishedRecording {
                        LabeledContent("Recording") { pathText(wav) }
                    }
                    if let language = model.sessionLanguage, model.finishedTranscript != nil {
                        LabeledContent("Language") { Text(language.displayName) }
                    }
                    if let rerunError = model.rerunError {
                        Label(rerunError, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                            .textSelection(.enabled)
                    }
                    Button("Reveal in Finder", systemImage: "folder") {
                        model.revealInFinder()
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
        .onChange(of: model.notesRequest) { takeNotesRequest() }
        .onChange(of: model.phase) {
            if model.phase == .starting { notes.reset() }
        }
    }

    private func pathText(_ url: URL) -> some View {
        Text(url.path)
            .textSelection(.enabled)
            .lineLimit(2)
            .truncationMode(.middle)
    }

    /// PLAN.md 4.3 step 1: the finished SRT goes straight to the notes flow.
    private func takeNotesRequest() {
        guard let srt = model.takeNotesRequest(), let language = model.sessionLanguage else { return }
        notes.start(srtURL: srt, language: language, store: aiStore, settings: settings)
    }

    /// Read-only live preview, newest line kept in view.
    ///
    /// A `ScrollView`, not a `List`: a `List` nested in the grouped `Form`
    /// never scrolled (`scrollTo` had no effect), so only the first rows of
    /// the first chunk were ever visible while later chunks were appended
    /// below the fold.
    private var liveTranscript: some View {
        Section {
            let cues = model.liveSegments.enumerated().filter { !$0.element.text.isEmpty }
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
                        if model.isDetectingLanguage {
                            Label("Detecting language…", systemImage: "globe")
                                .foregroundStyle(.secondary)
                        } else {
                            Text(model.isLivePreviewEnabled
                                 ? String(localized: "The first lines appear after about 10 to 30 s.",
                                          comment: "Live preview placeholder while no line has arrived yet")
                                 : "")
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                .onChange(of: model.liveSegments.count) {
                    // Read the model here, not the `cues` of an older body.
                    guard let last = model.liveSegments.lastIndex(where: { !$0.text.isEmpty }) else { return }
                    proxy.scrollTo(last, anchor: .bottom)
                }
            }
        } header: {
            HStack {
                Text("Live preview")
                Spacer()
                if model.isDetectingLanguage {
                    Label("Detecting language…", systemImage: "globe")
                        .foregroundStyle(.secondary)
                } else if model.isLiveLagging {
                    Label("\(model.liveChunksWaiting) chunks waiting", systemImage: "hourglass")
                        .foregroundStyle(.orange)
                        .help("The preview lags behind the recording but stays complete.")
                } else if model.liveChunksWaiting == 1 {
                    ProgressView().controlSize(.mini)
                }
            }
        } footer: {
            if let notice = model.liveNotice {
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
            Text("Ready").foregroundStyle(.secondary)
        case .transcribing:
            Text("Finalizing…").foregroundStyle(.secondary)
        case .finished:
            Text("Saved").foregroundStyle(.secondary)
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
            case .idle, .starting, .transcribing, .finished, .failed:
                Button("Start", systemImage: "record.circle") { model.start() }
                .keyboardShortcut(.defaultAction)
                .disabled(!model.canStart)
            case .recording:
                Button("Pause", systemImage: "pause.fill") { model.pause() }
                Button("Stop", systemImage: "stop.fill") { Task { await model.stop() } }
                    .keyboardShortcut(.defaultAction)
            case .paused:
                Button("Resume", systemImage: "play.fill") { model.resume() }
                Button("Stop", systemImage: "stop.fill") { Task { await model.stop() } }
                    .keyboardShortcut(.defaultAction)
            case .stopping:
                ProgressView().controlSize(.small)
            }
            Spacer()
        }
    }
}

#Preview {
    let settings = AppSettings()
    RecordView()
        .environment(settings)
        .environment(AIProviderStore())
        .environment(PermissionMonitor(settings: settings, sources: .fixed(microphone: .authorized, screenGranted: false),
                                       allowsReset: false))
        .environment(RecordingController(
            settings: settings, modelStore: ModelStore(settings: settings), engine: WhisperEngine()
        ))
}
