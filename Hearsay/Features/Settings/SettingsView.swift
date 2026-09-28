import AppKit
import HearsayCore
import SwiftUI

struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettingsView()
                .tabItem { Label("General", systemImage: "gearshape") }
            WindowSettingsView()
                .tabItem { Label("Window", systemImage: "macwindow") }
            OutputSettingsView()
                .tabItem { Label("Output", systemImage: "folder") }
        }
        .frame(width: 480, height: 220)
    }
}

private struct GeneralSettingsView: View {
    var body: some View {
        Form {
            Text("Language, input device, model, and AI settings arrive in later phases.")
                .foregroundStyle(.secondary)
        }
        .padding()
    }
}

private struct WindowSettingsView: View {
    @Environment(AppSettings.self) private var settings

    var body: some View {
        @Bindable var settings = settings
        Form {
            Picker("Show Hearsay in:", selection: $settings.windowMode) {
                ForEach(WindowMode.allCases, id: \.self) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .pickerStyle(.radioGroup)
            Text("Changes apply immediately.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding()
    }
}

private struct OutputSettingsView: View {
    @Environment(AppSettings.self) private var settings
    @State private var folderPath = ""
    @State private var errorMessage: String?

    var body: some View {
        Form {
            LabeledContent("Output folder:") {
                Text(folderPath)
                    .textSelection(.enabled)
                    .lineLimit(2)
                    .truncationMode(.middle)
            }
            HStack {
                Button("Choose…", action: chooseFolder)
                Button("Use Default") {
                    settings.outputFolderBookmark = nil
                }
                .disabled(settings.outputFolderBookmark == nil)
            }
            if let errorMessage {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
        .padding()
        .onAppear(perform: refresh)
        .onChange(of: settings.outputFolderBookmark) { refresh() }
    }

    private func refresh() {
        do {
            let resolved = try OutputLocation.resolve(bookmark: settings.outputFolderBookmark)
            defer { resolved.stopAccessing() }
            if let refreshed = resolved.refreshedBookmark {
                settings.outputFolderBookmark = refreshed
            }
            folderPath = resolved.url.path
            errorMessage = nil
        } catch {
            folderPath = OutputLocation.defaultFolder().path
            errorMessage = "Could not create the output folder: \(error.localizedDescription)"
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.message = "Choose where Hearsay saves transcripts and notes."
        if !folderPath.isEmpty {
            panel.directoryURL = URL(fileURLWithPath: folderPath, isDirectory: true)
        }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            settings.outputFolderBookmark = try OutputLocation.makeBookmark(for: url)
            errorMessage = nil
        } catch {
            errorMessage = "Could not remember that folder: \(error.localizedDescription)"
        }
    }
}
