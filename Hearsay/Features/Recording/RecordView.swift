import HearsayCore
import SwiftUI

/// The Record tab: microphone, system audio, language, controls, level
/// meters, and the saved recording (PLAN.md 4.1).
struct RecordView: View {
    @Environment(AppSettings.self) private var settings
    @State private var model = RecordViewModel()

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section {
                Picker("Microphone", selection: $model.selectedDeviceUID) {
                    if model.devices.isEmpty {
                        Text("No input device").tag(String?.none)
                    }
                    ForEach(model.devices) { device in
                        Text(device.name).tag(Optional(device.uid))
                    }
                }
                .disabled(model.isBusy)

                Toggle("Also capture system audio", isOn: $settings.captureSystemAudio)
                    .disabled(model.isBusy)

                Picker("Language", selection: $settings.defaultLanguageCode) {
                    ForEach(RecordViewModel.languages, id: \.code) { language in
                        Text(language.label).tag(language.code)
                    }
                }
                .pickerStyle(.segmented)
                .disabled(model.isBusy)
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
                    sourceMeter("Mic", systemImage: "mic.fill", fraction: model.micLevelFraction)
                    if let system = model.systemLevelFraction {
                        sourceMeter("System", systemImage: "speaker.wave.2.fill", fraction: system)
                    }
                }
                if let notice = model.systemAudioNotice {
                    HStack {
                        Label(notice, systemImage: "speaker.slash.fill")
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                        Spacer()
                        if model.systemAudioPermissionDenied {
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

            if let error = model.errorMessage {
                Section {
                    Label(error, systemImage: "exclamationmark.octagon.fill")
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                }
            }

            if let saved = model.savedRecording {
                Section("Saved recording") {
                    Text(saved.path)
                        .textSelection(.enabled)
                        .lineLimit(2)
                        .truncationMode(.middle)
                    Button("Reveal in Finder", systemImage: "folder") {
                        model.revealInFinder()
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { model.activate() }
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
        .accessibilityLabel("\(title) level")
        .accessibilityValue(Text(fraction, format: .percent.precision(.fractionLength(0))))
    }

    @ViewBuilder
    private var statusLabel: some View {
        switch model.phase {
        case .idle:
            Text("Ready").foregroundStyle(.secondary)
        case .starting:
            Text("Starting…").foregroundStyle(.secondary)
        case .recording:
            Label("Recording", systemImage: "record.circle.fill").foregroundStyle(.red)
        case .paused:
            Label("Paused", systemImage: "pause.circle.fill").foregroundStyle(.secondary)
        case .saving:
            Text("Saving…").foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private var controls: some View {
        HStack {
            switch model.phase {
            case .idle, .starting:
                Button("Start", systemImage: "record.circle") {
                    Task { await model.start(settings: settings) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(model.phase == .starting)
            case .recording:
                Button("Pause", systemImage: "pause.fill") { model.pause() }
                Button("Stop", systemImage: "stop.fill") { model.stop() }
                    .keyboardShortcut(.defaultAction)
            case .paused:
                Button("Resume", systemImage: "play.fill") { model.resume() }
                Button("Stop", systemImage: "stop.fill") { model.stop() }
                    .keyboardShortcut(.defaultAction)
            case .saving:
                ProgressView().controlSize(.small)
            }
            Spacer()
        }
    }
}

#Preview {
    RecordView()
        .environment(AppSettings())
}
