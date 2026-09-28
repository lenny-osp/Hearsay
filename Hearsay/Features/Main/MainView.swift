import HearsayCore
import SwiftUI

/// Main window: Record, File, Models, History (PLAN.md section 4).
struct MainView: View {
    enum Tab: Hashable {
        case record, file, models, history
    }

    @Environment(AppSettings.self) private var settings
    @Environment(ModelStore.self) private var modelStore
    @Environment(RecordingController.self) private var recording
    @Environment(\.whisperEngine) private var whisperEngine
    @State private var recovery: UnfinishedRecordingQueue?
    @State private var selection: Tab = .record
    @State private var fileModel: FileViewModel?
    @State private var recoveryError: String?

    var body: some View {
        TabView(selection: $selection) {
            RecordView(onOpenModels: { selection = .models })
                .tabItem { Label("Record", systemImage: "record.circle") }
                .tag(Tab.record)
            Group {
                if let fileModel {
                    FileView(model: fileModel, onOpenModels: { selection = .models })
                } else {
                    ProgressView()
                }
            }
            .tabItem { Label("File", systemImage: "doc.badge.plus") }
            .tag(Tab.file)
            ModelManagerView()
                .tabItem { Label("Models", systemImage: "square.and.arrow.down") }
                .tag(Tab.models)
            HistoryView()
                .tabItem { Label("History", systemImage: "clock") }
                .tag(Tab.history)
        }
        .padding()
        .frame(minWidth: 560, minHeight: 360)
        .onAppear {
            if fileModel == nil {
                fileModel = FileViewModel(
                    settings: settings, modelStore: modelStore, engine: whisperEngine ?? WhisperEngine()
                )
            }
            if recovery == nil { recovery = UnfinishedRecordingQueue.checkOnce() }
            takeTranscribeFileRequest()
        }
        .onChange(of: recording.transcribeFileRequest) { takeTranscribeFileRequest() }
        .sheet(isPresented: recoveryShown) {
            if let recovery, let recording = recovery.current {
                UnfinishedRecordingSheet(queue: recovery, recording: recording, onTranscribe: transcribeRecovered)
                    .id(recording)
            }
        }
        .alert("Could not transcribe the recording", isPresented: recoveryErrorShown) {
            Button("OK") { recoveryError = nil }
        } message: {
            Text(recoveryError ?? "")
        }
    }

    /// Shown while the recovery queue still has a recording.
    private var recoveryShown: Binding<Bool> {
        Binding(
            get: { recovery?.current != nil },
            set: { shown in if !shown { recovery = nil } }
        )
    }

    private var recoveryErrorShown: Binding<Bool> {
        Binding(get: { recoveryError != nil }, set: { if !$0 { recoveryError = nil } })
    }

    /// "Transcribe this file" on the Record tab.
    private func takeTranscribeFileRequest() {
        guard let url = recording.transcribeFileRequest, let fileModel, !fileModel.isBusy else { return }
        recording.transcribeFileRequest = nil
        selection = .file
        fileModel.transcribe(url)
    }

    /// Recovery sheet "Transcribe": the sheet has patched the header; the
    /// WAV moves into the output folder (kept) and runs through the File flow.
    private func transcribeRecovered(_ spoolWAV: URL) {
        let spool = recovery?.spool ?? RecordingSpool()
        do {
            let folder = try TranscriptOutput.resolveFolder(settings: settings)
            defer { folder.stopAccessing() }
            guard let kept = try spool.finalize(spoolWAV, keep: true, outputFolder: folder.url) else { return }
            selection = .file
            fileModel?.transcribe(kept)
        } catch {
            recoveryError = "\(error.localizedDescription) It stays at \(spoolWAV.path)."
        }
    }
}

#Preview {
    MainView()
}
