import AppKit
import Foundation
import HearsayCore
import Observation
import SwiftUI

/// Queue of unfinished spool recordings found at launch (PLAN.md 4.5). The
/// sheet shows the first one; each answer moves on to the next.
@MainActor
@Observable
final class UnfinishedRecordingQueue {
    /// Only the first window of a launch checks the spool.
    private static var hasChecked = false

    private(set) var pending: [URL]
    let spool: RecordingSpool

    init(recordings: [URL], spool: RecordingSpool = RecordingSpool()) {
        self.pending = recordings
        self.spool = spool
    }

    /// The spool's unfinished recordings the first time this is called in a
    /// launch, nil afterwards or when there are none. Spool files created
    /// after this process launched belong to a recording of this session
    /// (started from the menu bar or a hotkey before the window opened), so
    /// they are left alone.
    static func checkOnce(spool: RecordingSpool = RecordingSpool()) -> UnfinishedRecordingQueue? {
        guard !hasChecked else { return nil }
        hasChecked = true
        let launchDate = NSRunningApplication.current.launchDate
        let recordings = spool.unfinishedRecordings().filter { url in
            guard let launchDate,
                  let created = (try? url.resourceValues(forKeys: [.creationDateKey]))?.creationDate
            else { return true }
            return created < launchDate
        }
        return recordings.isEmpty ? nil : UnfinishedRecordingQueue(recordings: recordings, spool: spool)
    }

    var current: URL? { pending.first }

    func advance() {
        if !pending.isEmpty { pending.removeFirst() }
    }
}

/// "A recording from <date> was not finished (<duration>). What do you want
/// to do?" with Keep, Delete and Transcribe.
struct UnfinishedRecordingSheet: View {
    @Environment(AppSettings.self) private var settings
    let queue: UnfinishedRecordingQueue
    let recording: URL
    /// Runs the File flow on the recording. Nil until transcription lands,
    /// which disables the Transcribe button.
    var onTranscribe: ((URL) -> Void)?

    @State private var errorMessage: String?
    @State private var isWorking = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Unfinished recording", systemImage: "exclamationmark.triangle")
                .font(.headline)
            Text("A recording from \(dateText) was not finished (\(durationText)). What do you want to do?")
                .fixedSize(horizontal: false, vertical: true)
            Text(recording.lastPathComponent)
                .font(.callout.monospaced())
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            if queue.pending.count > 1 {
                Text("\(queue.pending.count - 1) more after this one.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if let errorMessage {
                Text(errorMessage)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Button("Delete", role: .destructive, action: delete)
                Spacer()
                VStack(alignment: .trailing, spacing: 2) {
                    Button("Transcribe", action: transcribe)
                        .disabled(onTranscribe == nil)
                    if onTranscribe == nil {
                        Text("Available once transcription lands")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                Button("Keep", action: keep)
                    .keyboardShortcut(.defaultAction)
            }
            .disabled(isWorking)
        }
        .padding(20)
        .frame(width: 460)
        .interactiveDismissDisabled()
    }

    private var dateText: String {
        guard let date = HistoryIndex.timestampDate(stem: recording.deletingPathExtension().lastPathComponent)
        else { return "an unknown time" }
        return date.formatted(date: .long, time: .shortened)
    }

    private var durationText: String {
        guard let duration = try? WavWriter.duration(of: recording) else { return "unknown length" }
        let total = Int(duration.rounded())
        let hours = total / 3600
        let minutes = total % 3600 / 60
        let seconds = total % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, seconds)
            : String(format: "%d:%02d", minutes, seconds)
    }

    /// Patches the header from the file size and moves the WAV into the
    /// output folder.
    private func keep() {
        isWorking = true
        defer { isWorking = false }
        do {
            try WavWriter.patchHeader(at: recording)
            let folder = try OutputLocation.resolve(bookmark: settings.outputFolderBookmark)
            defer { folder.stopAccessing() }
            if let refreshed = folder.refreshedBookmark {
                settings.outputFolderBookmark = refreshed
            }
            try queue.spool.finalize(recording, keep: true, outputFolder: folder.url)
            errorMessage = nil
            queue.advance()
        } catch {
            errorMessage = "Could not keep the recording: \(error.localizedDescription) It stays at \(recording.path)."
        }
    }

    private func delete() {
        do {
            try FileManager.default.removeItem(at: recording)
            errorMessage = nil
            queue.advance()
        } catch {
            errorMessage = "Could not delete the recording: \(error.localizedDescription)"
        }
    }

    private func transcribe() {
        guard let onTranscribe else { return }
        do {
            try WavWriter.patchHeader(at: recording)
        } catch {
            errorMessage = "Could not repair the recording: \(error.localizedDescription)"
            return
        }
        onTranscribe(recording)
        queue.advance()
    }
}
