import AppKit
import HearsayCore
import ServiceManagement
import SwiftUI

struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettingsView()
                .frame(minWidth: Self.minimumWidth)
                .tabItem { Label("General", systemImage: "gearshape") }
            WindowSettingsView()
                .frame(minWidth: Self.minimumWidth)
                .tabItem { Label("Window", systemImage: "macwindow") }
            OutputSettingsView()
                .frame(minWidth: Self.minimumWidth)
                .tabItem { Label("Output", systemImage: "folder") }
            AISettingsTab()
                // The grouped Form scrolls, so it has no natural height;
                // give it enough room to show every section at once.
                .frame(minWidth: Self.minimumWidth, idealWidth: 560, minHeight: 560, idealHeight: 640)
                .tabItem { Label("AI", systemImage: "sparkles") }
        }
    }

    /// Each tab sizes to its own content, never narrower than this.
    static let minimumWidth: CGFloat = 520
}

private struct GeneralSettingsView: View {
    var body: some View {
        Form {
            LaunchAtLoginSection()
            Section("Transcription") {
                PreferredLanguagePicker()
                Text("Used when Auto can't tell the language.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ChineseScriptPicker()
                Text("Applies to Chinese transcripts. Other languages are never changed.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            HotkeySettingsSection()
        }
        .padding()
    }
}

/// "Launch Hearsay at login", backed by `SMAppService.mainApp`. The system
/// owns the state (the user can change it in System Settings), so the toggle
/// reads it back after every change and whenever the tab appears.
private struct LaunchAtLoginSection: View {
    @State private var status: SMAppService.Status = SMAppService.mainApp.status
    @State private var errorMessage: String?

    private static let loginItemsURL =
        URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension")

    var body: some View {
        Section("Startup") {
            Toggle("Launch Hearsay at login", isOn: Binding(
                get: { status == .enabled },
                set: { setEnabled($0) }
            ))
            if status == .requiresApproval {
                HStack {
                    Label("Approve in System Settings > General > Login Items",
                          systemImage: "exclamationmark.circle")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Open Login Items", action: openLoginItems)
                }
            }
            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
        .onAppear(perform: refresh)
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refresh()
        }
    }

    private func refresh() {
        status = SMAppService.mainApp.status
    }

    private func setEnabled(_ enabled: Bool) {
        let service = SMAppService.mainApp
        do {
            if enabled {
                try service.register()
            } else {
                try service.unregister()
            }
            errorMessage = nil
        } catch {
            let action = enabled ? "turn on" : "turn off"
            errorMessage = "Could not \(action) launch at login: \(error.localizedDescription)"
        }
        refresh()
    }

    private func openLoginItems() {
        guard let url = Self.loginItemsURL else { return }
        NSWorkspace.shared.open(url)
    }
}

/// "Preferred language" (Settings > General): what Auto falls back to when
/// detection is unsure. The only control that writes
/// `AppSettings.preferredLanguage`.
private struct PreferredLanguagePicker: View {
    @Environment(AppSettings.self) private var settings

    var body: some View {
        @Bindable var settings = settings
        Picker("Preferred language", selection: $settings.preferredLanguage) {
            ForEach(TranscriptLanguage.allCases, id: \.self) { language in
                Text(language.displayName).tag(language)
            }
        }
    }
}

/// "Chinese output" for zh transcripts: 繁體中文 (default) or 简体中文, bound
/// to `AppSettings.chineseScript`. Offers only `ChineseScript.pickerCases`;
/// the internal asIs (used for non-zh sessions) is never shown, and a stored
/// asIs shows as 繁體中文. Shown on the Record tab on its own row below the
/// language picker when ZH is chosen, and in Settings > General.
struct ChineseScriptPicker: View {
    @Environment(AppSettings.self) private var settings
    var label = "Chinese output"

    var body: some View {
        Picker(label, selection: Binding(
            get: { settings.chineseScript.pickerValue },
            set: { settings.chineseScript = $0 }
        )) {
            ForEach(ChineseScript.pickerCases, id: \.self) { script in
                Text(script.displayName).tag(script)
            }
        }
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
        @Bindable var settings = settings
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
            Toggle("Keep the recording (WAV) after a successful transcription",
                   isOn: $settings.keepRecording)
            Text("When off, the WAV is deleted once its SRT is written. A failed transcription always keeps it.")
                .font(.caption)
                .foregroundStyle(.secondary)
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
