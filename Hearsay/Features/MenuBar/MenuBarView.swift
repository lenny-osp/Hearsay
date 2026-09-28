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
    @Environment(\.openSettings) private var openSettings

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
            }

            if let caption = lastResultCaption {
                caption
            }

            controls

            Divider()

            menuRow("Open Hearsay") {
                windowOpener.show()
            }
            menuRow("Settings…") {
                NSApp.activate()
                openSettings()
            }
            .keyboardShortcut(",", modifiers: .command)

            Divider()

            menuRow("Quit Hearsay") {
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
        case .idle, .finished, .failed: return "Idle"
        case .starting: return "Starting…"
        case .recording: return "Recording \(elapsed)"
        case .paused: return "Paused \(elapsed)"
        case .stopping: return "Saving…"
        }
    }

    private var stateColor: Color {
        switch recording.phase {
        case .recording: .red
        case .paused, .starting, .stopping: .orange
        case .idle, .finished, .failed: .secondary
        }
    }

    /// What happened to the last recording, below the state line.
    private var lastResultCaption: AnyView? {
        switch recording.phase {
        case .finished(let url):
            return AnyView(
                Text("Saved \(url.lastPathComponent)")
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
                    Label(recording.phase == .paused ? "Resume" : "Pause",
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
                .disabled(recording.phase == .starting)
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
        default:
            Image(systemName: "waveform")
                .accessibilityLabel("Hearsay")
        }
    }

    /// Status item images are drawn as templates (monochrome) unless the
    /// image says otherwise, so the red variant is a non-template image.
    private static let recordingImage: NSImage = {
        let configuration = NSImage.SymbolConfiguration(pointSize: 14, weight: .regular)
            .applying(.preferringMulticolor())
        let image = NSImage(systemSymbolName: "record.circle.fill", accessibilityDescription: "Recording")?
            .withSymbolConfiguration(configuration) ?? NSImage()
        image.isTemplate = false
        return image
    }()
}
