import SwiftUI

/// Main window. Placeholder tabs until the features land (PLAN.md section 10).
struct MainView: View {
    var body: some View {
        TabView {
            RecordView()
                .tabItem { Label("Record", systemImage: "record.circle") }
            placeholder("File", systemImage: "doc.badge.plus",
                        text: "Transcribing an audio file arrives in a later phase.")
                .tabItem { Label("File", systemImage: "doc.badge.plus") }
            ModelManagerView()
                .tabItem { Label("Models", systemImage: "square.and.arrow.down") }
            placeholder("History", systemImage: "clock",
                        text: "Past recordings and transcripts will be listed here.")
                .tabItem { Label("History", systemImage: "clock") }
        }
        .padding()
        .frame(minWidth: 560, minHeight: 360)
    }

    private func placeholder(_ title: String, systemImage: String, text: String) -> some View {
        ContentUnavailableView(title, systemImage: systemImage, description: Text(text))
    }
}

#Preview {
    MainView()
}
