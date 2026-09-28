import AppKit
import HearsayCore
import SwiftUI

/// Content of the menu bar extra window (PLAN.md 4.4): the recording state,
/// the level bar while recording, Start / Stop and Pause / Resume, and the
/// app entry points. Everything is driven by the app-level
/// `RecordingController`, so it works with the main window closed.
struct MenuBarView: View {
    @Environment(MainWindowOpener.self) private var windowOpener
    @Environment(RecordingController.self) private var recording
    @Environment(AppSettings.self) private var settings

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            stateLine

            if recording.isCapturing {
                ProgressView(value: recording.levelFraction)
                    .progressViewStyle(.linear)
                    .tint(recording.silenceWarning == nil ? .green : .orange)
                    .accessibilityLabel("Input level")
                if let warning = recording.silenceWarning {
                    Label(warning, systemImage: "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                if recording.isDetectingLanguage {
                    Label("Detecting language…", systemImage: "globe")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else if let line = recording.latestLiveLine {
                    Text(line)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.head)
                        .help(line)
                }
            }

            if let progress = recording.transcriptionProgress {
                ProgressView(value: progress)
                    .progressViewStyle(.linear)
                    .accessibilityLabel("Transcription progress")
            }

            if let caption = lastResultCaption {
                caption
            }

            controls

            Divider()

            menuRow(String(localized: "Open Hearsay", comment: "Menu bar panel: bring up the main window")) {
                windowOpener.show()
            }

            Divider()

            menuRow(String(localized: "Quit Hearsay", comment: "Menu bar panel")) {
                NSApp.terminate(nil)
            }
            .keyboardShortcut("q", modifiers: .command)
        }
        .padding(12)
        .frame(width: 260)
        .onAppear { recording.activate() }
    }

    // MARK: - State

    private var stateLine: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(stateColor)
                .frame(width: 8, height: 8)
            Text(stateText)
                .font(.headline)
                .monospacedDigit()
        }
        .accessibilityElement(children: .combine)
    }

    private var stateText: String {
        let elapsed = LevelMeter.formatElapsed(recording.elapsed)
        switch recording.phase {
        case .idle, .finished, .failed:
            return String(localized: "Idle", comment: "Menu bar panel state: not recording")
        case .starting:
            return String(localized: "Starting…", comment: "Recording state")
        case .recording:
            return String(localized: "Recording \(elapsed)",
                          comment: "Menu bar panel state. %@ is the elapsed time, for example 00:12:34.")
        case .paused:
            return String(localized: "Paused \(elapsed)",
                          comment: "Menu bar panel state. %@ is the elapsed time, for example 00:12:34.")
        case .stopping:
            return String(localized: "Saving…", comment: "Recording state: the recording is being saved")
        case .transcribing(let progress):
            return String(localized: "Transcribing… \(Int((progress * 100).rounded()))%",
                          comment: "Menu bar panel state. %lld is a percentage; keep the % sign after it.")
        }
    }

    private var stateColor: Color {
        switch recording.phase {
        case .recording: .red
        case .paused, .starting, .stopping, .transcribing: .orange
        case .idle, .finished, .failed: .secondary
        }
    }

    /// What happened to the last recording, below the state line.
    private var lastResultCaption: AnyView? {
        switch recording.phase {
        case .finished(let srt, let wav):
            let name = (srt ?? wav)?.lastPathComponent
                ?? String(localized: "the recording", comment: "Used in 'Saved %@' when there is no file name")
            return AnyView(
                Text("Saved \(name)", comment: "Menu bar panel caption. %@ is a file name.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            )
        case .failed(let message):
            return AnyView(
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .lineLimit(3)
            )
        default:
            return nil
        }
    }

    // MARK: - Controls

    private var controls: some View {
        HStack(spacing: 8) {
            if recording.isCapturing || recording.phase == .stopping {
                Button {
                    Task { await recording.stop() }
                } label: {
                    Label("Stop", systemImage: "stop.fill")
                        .frame(maxWidth: .infinity)
                }
                .disabled(recording.phase == .stopping)
                .help("Stop (\(settings.startStopHotkey.displayString))")

                Button {
                    recording.togglePause()
                } label: {
                    Label(recording.phase == .paused
                          ? String(localized: "Resume", comment: "Button: resume the paused recording")
                          : String(localized: "Pause", comment: "Button: pause the recording"),
                          systemImage: recording.phase == .paused ? "play.fill" : "pause.fill")
                        .frame(maxWidth: .infinity)
                }
                .disabled(recording.phase == .stopping)
                .help("Pause / Resume (\(settings.pauseHotkey.displayString))")
            } else {
                Button {
                    recording.start()
                } label: {
                    Label("Start", systemImage: "record.circle")
                        .frame(maxWidth: .infinity)
                }
                .disabled(!recording.canStart)
                .help("Start (\(settings.startStopHotkey.displayString))")
            }
        }
        .controlSize(.large)
    }

    private func menuRow(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .foregroundStyle(.primary)
    }
}

/// The menu bar item itself: `waveform` when idle, a red record symbol
/// with the elapsed time while recording, and a pause symbol while paused.
struct MenuBarLabel: View {
    let recording: RecordingController

    var body: some View {
        switch recording.phase {
        case .recording:
            HStack(spacing: 4) {
                Image(nsImage: Self.recordingImage)
                Text(LevelMeter.formatElapsed(recording.elapsed))
                    .monospacedDigit()
            }
            .accessibilityLabel("Hearsay, recording")
        case .paused:
            Image(systemName: "pause.circle")
                .accessibilityLabel("Hearsay, paused")
        case .transcribing(let progress):
            HStack(spacing: 4) {
                Image(systemName: "text.bubble")
                Text("\(Int((progress * 100).rounded()))%")
                    .monospacedDigit()
            }
            .accessibilityLabel("Hearsay, transcribing")
        default:
            Image(systemName: "waveform")
                .accessibilityLabel(Text(verbatim: "Hearsay"))
        }
    }

    /// Status item images are drawn as templates (monochrome) unless the
    /// image says otherwise, so the red variant is a non-template image.
    private static let recordingImage: NSImage = {
        let configuration = NSImage.SymbolConfiguration(pointSize: 14, weight: .regular)
            .applying(.preferringMulticolor())
        let image = NSImage(systemSymbolName: "record.circle.fill",
                            accessibilityDescription: String(localized: "Recording", comment: "Menu bar icon while recording"))?
            .withSymbolConfiguration(configuration) ?? NSImage()
        image.isTemplate = false
        return image
    }()
}
