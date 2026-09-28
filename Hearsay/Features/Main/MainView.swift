import SwiftUI

/// Main window. Placeholder tabs until the features land (PLAN.md section 10).
struct MainView: View {
    @State private var recovery: UnfinishedRecordingQueue?

    var body: some View {
        TabView {
            RecordView()
                .tabItem { Label("Record", systemImage: "record.circle") }
            placeholder("File", systemImage: "doc.badge.plus",
                        text: "Transcribing an audio file arrives in a later phase.")
                .tabItem { Label("File", systemImage: "doc.badge.plus") }
            ModelManagerView()
                .tabItem { Label("Models", systemImage: "square.and.arrow.down") }
            HistoryView()
                .tabItem { Label("History", systemImage: "clock") }
        }
        .padding()
        .frame(minWidth: 560, minHeight: 360)
        .onAppear {
            if recovery == nil { recovery = UnfinishedRecordingQueue.checkOnce() }
        }
        .sheet(isPresented: recoveryShown) {
            if let recovery, let recording = recovery.current {
                UnfinishedRecordingSheet(queue: recovery, recording: recording, onTranscribe: nil)
                    .id(recording)
            }
        }
    }

    /// Shown while the recovery queue still has a recording.
    private var recoveryShown: Binding<Bool> {
        Binding(
            get: { recovery?.current != nil },
            set: { shown in if !shown { recovery = nil } }
        )
    }

    private func placeholder(_ title: String, systemImage: String, text: String) -> some View {
        ContentUnavailableView(title, systemImage: systemImage, description: Text(text))
    }
}

#Preview {
    MainView()
}
