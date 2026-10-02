import AppKit
import HearsayCore
import SwiftUI

/// Content of the menu bar extra window (PLAN.md 4.4, 4.9): the recording
/// state, the level bar while recording, Start / Stop, Pause / Resume and
/// Stop & Start Next, one line for the transcription queue, and the app
/// entry points. Everything is driven by the app-level `RecordingController`
/// and `TranscriptionQueue`, so it works with the main window closed.
struct MenuBarView: View {
    @Environment(MainWindowOpener.self) private var windowOpener
    @Environment(RecordingController.self) private var recording
    @Environment(TranscriptionQueue.self) private var queue
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

            if let line = queueLine {
                Text(line)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }

            if !recording.isSessionActive, let job = queue.activeJob {
                ProgressView(value: job.progress)
                    .progressViewStyle(.linear)
                    .accessibilityLabel("Transcription progress")
            }

            if let caption = lastResultCaption {
                caption
            }

            controls
            stopStartNextRow
            transcribeAllRow

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
        case .idle, .failed:
            if let job = queue.activeJob {
                return String(localized: "Transcribing… \(Int((job.progress * 100).rounded()))%",
                              comment: "Menu bar panel state. %lld is a percentage; keep the % sign after it.")
            }
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
        }
    }

    private var stateColor: Color {
        switch recording.phase {
        case .recording: .red
        case .paused, .starting, .stopping: .orange
        case .idle, .failed: queue.activeJob == nil ? .secondary : .orange
        }
    }

    /// One line for the queue: how many recordings are not transcribed yet
    /// and how far the current one is. Nil when the state line already says
    /// it all (one job, no session) or the queue is empty.
    private var queueLine: String? {
        let pending = queue.pendingCount
        guard pending > 0 else { return nil }
        // With "When I start them" and every job held (PLAN.md 4.11), before
        // the other rules, with or without a session.
        if queue.allPendingHeld {
            return String(localized: "Not transcribed yet · in queue: \(pending)",
                          comment: "Menu bar panel line when every recording in the queue waits for the user to start it. %lld is their number.")
        }
        if !recording.isSessionActive, queue.activeJob != nil {
            // The state line already shows the percentage.
            guard pending > 1 else { return nil }
            return String(localized: "Recordings in queue: \(pending)",
                          comment: "Menu bar panel queue line. %lld is the number of recordings not transcribed yet.")
        }
        if queue.isHeldForSession {
            return String(localized: "Transcription paused while recording · in queue: \(pending)",
                          comment: "Menu bar panel queue line. %lld is the number of recordings not transcribed yet.")
        }
        if let job = queue.activeJob {
            return String(localized: "Transcribing… \(Int((job.progress * 100).rounded()))% · in queue: \(pending)",
                          comment: "Menu bar panel queue line. The first %lld is a percentage (keep the % sign after it), the second the number of recordings not transcribed yet.")
        }
        return String(localized: "Waiting to transcribe · in queue: \(pending)",
                      comment: "Menu bar panel queue line. %lld is the number of recordings not transcribed yet.")
    }

    /// What happened to the last recording, below the state line.
    private var lastResultCaption: AnyView? {
        if case .failed(let message) = recording.phase {
            return AnyView(
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .lineLimit(3)
            )
        }
        guard !recording.isSessionActive, let job = queue.jobs.last else { return nil }
        switch job.state {
        case .done:
            let name = (job.srt ?? job.wav)?.lastPathComponent
                ?? String(localized: "the recording", comment: "Used in 'Saved %@' when there is no file name")
            return AnyView(
                Text("Saved \(name)", comment: "Menu bar panel caption. %@ is a file name.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            )
        case .failed:
            return AnyView(
                Text(job.errorMessage ?? "")
                    .font(.caption)
                    .foregroundStyle(.red)
                    .lineLimit(3)
            )
        case .waiting, .running, .suspended:
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

    /// Stop & Start Next, below Stop and Pause while recording (PLAN.md 4.9).
    @ViewBuilder
    private var stopStartNextRow: some View {
        if recording.isCapturing {
            Button {
                recording.stopAndStartNext()
            } label: {
                Label("Stop & Start Next", systemImage: "forward.end.fill")
                    .frame(maxWidth: .infinity)
            }
            .controlSize(.large)
            .help("Stop & Start Next (\(settings.stopStartNextHotkey.displayString))")
        }
    }

    /// Transcribe All, while a job is held (PLAN.md 4.11). Left out otherwise,
    /// as Stop & Start Next is, because the panel reflows.
    @ViewBuilder
    private var transcribeAllRow: some View {
        if queue.heldCount > 0 {
            Button {
                queue.releaseAll()
            } label: {
                Label(String(localized: "Transcribe All", comment: "Button of the Record tab's queue card and menu bar panel: start the transcription of every recording that waits for the user."), systemImage: "text.bubble")
                    .frame(maxWidth: .infinity)
            }
            .controlSize(.large)
        }
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
/// with the elapsed time while recording, a pause symbol while paused, and
/// with no session the percentage of the recording being transcribed.
/// With "Show recording status in the menu bar" off it is always the
/// waveform.
struct MenuBarLabel: View {
    let recording: RecordingController
    let queue: TranscriptionQueue
    let settings: AppSettings

    var body: some View {
        if settings.menuBarShowsStatus {
            statusLabel
        } else {
            plainLabel
        }
    }

    private var plainLabel: some View {
        Image(systemName: "waveform")
            .accessibilityLabel(Text(verbatim: "Hearsay"))
    }

    @ViewBuilder
    private var statusLabel: some View {
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
        case .idle, .failed:
            // With no session, the job being transcribed shows its percentage.
            if let job = queue.activeJob {
                HStack(spacing: 4) {
                    Image(systemName: "text.bubble")
                    Text("\(Int((job.progress * 100).rounded()))%")
                        .monospacedDigit()
                }
                .accessibilityLabel("Hearsay, transcribing")
            } else {
                plainLabel
            }
        case .starting, .stopping:
            plainLabel
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
