import HearsayCore
import SwiftUI

/// The main window's tabs, in display order.
enum MainTab: Hashable {
    case record, file, models, history, settings
}

/// Which main-window tab is showing. Owned by the app delegate so code
/// outside the window (the Settings… command, the menu bar) can switch tabs.
@MainActor
@Observable
final class MainTabSelection {
    var tab: MainTab = .record
    /// A Settings section to switch to (a help link such as
    /// `hearsay://open/settings-ai`); the Settings tab takes it and clears it.
    var settingsPane: SettingsView.Pane?
}

/// Main window: Record, File, Models, History, Settings (PLAN.md section 4).
struct MainView: View {
    @Environment(AppSettings.self) private var settings
    @Environment(ModelStore.self) private var modelStore
    @Environment(RecordingController.self) private var recording
    @Environment(\.whisperEngine) private var whisperEngine
    @State private var recovery: UnfinishedRecordingQueue?
    @Environment(MainTabSelection.self) private var tabs
    @State private var fileModel: FileViewModel?
    @State private var recoveryError: String?

    var body: some View {
        @Bindable var tabs = tabs
        TabView(selection: $tabs.tab) {
            RecordView(onOpenModels: { tabs.tab = .models })
                .tabItem { Label(String(localized: "Record", comment: "Main window tab"), systemImage: "record.circle") }
                .tag(MainTab.record)
            Group {
                if let fileModel {
                    FileView(model: fileModel, onOpenModels: { tabs.tab = .models })
                } else {
                    ProgressView()
                }
            }
            .tabItem { Label(String(localized: "File", comment: "Main window tab"), systemImage: "doc.badge.plus") }
            .tag(MainTab.file)
            ModelManagerView()
                .tabItem { Label(String(localized: "Models", comment: "Main window tab"), systemImage: "square.and.arrow.down") }
                .tag(MainTab.models)
            HistoryView()
                .tabItem { Label(String(localized: "History", comment: "Main window tab"), systemImage: "clock") }
                .tag(MainTab.history)
            SettingsView()
                .tabItem { Label(String(localized: "Settings", comment: "Main window tab"), systemImage: "gearshape") }
                .tag(MainTab.settings)
        }
        .padding()
        .frame(minWidth: 560, minHeight: 360)
        .onAppear {
            if fileModel == nil {
                fileModel = FileViewModel(
                    settings: settings, modelStore: modelStore, engine: whisperEngine ?? WhisperEngine()
                )
            }
            // The UI snapshots render this view with sample data only.
            if recovery == nil, !UISnapshots.isRunning { recovery = UnfinishedRecordingQueue.checkOnce() }
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
        tabs.tab = .file
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
            tabs.tab = .file
            fileModel?.transcribe(kept)
        } catch {
            recoveryError = String(localized: "\(error.localizedDescription) It stays at \(spoolWAV.path).",
                                   comment: "Recovery error. %1$@ is the system error message, %2$@ a file path.")
        }
    }
}

#Preview {
    MainView()
        .environment(MainTabSelection())
}
