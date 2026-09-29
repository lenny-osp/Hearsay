import AppKit
import HearsayCore
import ServiceManagement
import SwiftUI

/// The Settings tab of the main window (PLAN.md section 8): General,
/// Window, Output and AI, switched with a segmented picker. Every section is
/// a grouped form, so it scrolls inside the tab at any window height.
struct SettingsView: View {
    enum Pane: String, CaseIterable, Hashable {
        case general
        case window
        case output
        case ai

        var title: String {
            switch self {
            case .general: String(localized: "General", comment: "Settings section (segmented picker)")
            case .window: String(localized: "Window", comment: "Settings section (segmented picker)")
            case .output: String(localized: "Output", comment: "Settings section (segmented picker)")
            case .ai: String(localized: "AI", comment: "Settings section (segmented picker): meeting-notes provider")
            }
        }
    }

    @State private var pane: Pane
    @Environment(MainTabSelection.self) private var tabs: MainTabSelection?

    /// `initialPane` is for the UI snapshots; the app always opens on General
    /// (or on the section a help link asks for).
    init(initialPane: Pane = .general) {
        _pane = State(initialValue: initialPane)
    }

    var body: some View {
        VStack(spacing: 0) {
            Picker(String(localized: "Settings section", comment: "Accessibility label of the Settings section picker"),
                   selection: $pane) {
                ForEach(Pane.allCases, id: \.self) { pane in
                    Text(pane.title).tag(pane)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .padding(.top, 8)
            Group {
                switch pane {
                case .general: GeneralSettingsView()
                case .window: WindowSettingsView()
                case .output: OutputSettingsView()
                case .ai: AISettingsTab()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .onAppear(perform: takeRequestedPane)
        .onChange(of: tabs?.settingsPane) { takeRequestedPane() }
    }

    /// A help link asked for a section.
    private func takeRequestedPane() {
        guard let tabs, let requested = tabs.settingsPane else { return }
        pane = requested
        tabs.settingsPane = nil
    }
}

private struct GeneralSettingsView: View {
    var body: some View {
        Form {
            Section {
                InterfaceLanguagePicker()
                Text("Menus and windows. A change applies after Hearsay restarts.",
                     comment: "Settings > General caption under Interface language")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } header: {
                Text("Interface", comment: "Settings > General section header: the app's own language")
            }
            LaunchAtLoginSection()
            Section("Transcription") {
                PreferredLanguagePicker()
                Text("Used when Auto can't tell the language. When Auto hears Chinese, it writes 简体中文 if that is chosen here, otherwise 繁體中文.",
                     comment: "Settings > General caption under Auto mode default language. 简体中文 and 繁體中文 are language names; keep them as they are.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            HotkeySettingsSection()
            SoftwareUpdatesSection()
            AcknowledgementsSection()
        }
        .formStyle(.grouped)
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
            errorMessage = enabled
                ? String(localized: "Could not turn on launch at login: \(error.localizedDescription)",
                         comment: "Settings > General error. %@ is the system error message.")
                : String(localized: "Could not turn off launch at login: \(error.localizedDescription)",
                         comment: "Settings > General error. %@ is the system error message.")
        }
        refresh()
    }

    private func openLoginItems() {
        guard let url = Self.loginItemsURL else { return }
        NSWorkspace.shared.open(url)
    }
}

/// "Auto mode default language" (Settings > General; renamed from "Preferred
/// language" 2026-09-29, owner request): what Auto falls back to when
/// detection is unsure, and which Chinese variant Auto uses when it detects
/// Chinese. Lists the five languages by autonym. The only control that
/// writes `AppSettings.preferredLanguage`.
private struct PreferredLanguagePicker: View {
    @Environment(AppSettings.self) private var settings

    var body: some View {
        @Bindable var settings = settings
        Picker("Auto mode default language", selection: $settings.preferredLanguage) {
            ForEach(TranscriptLanguage.allCases, id: \.self) { language in
                Text(language.displayName).tag(language)
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
        .formStyle(.grouped)
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
        .formStyle(.grouped)
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
            errorMessage = String(localized: "Could not create the output folder: \(error.localizedDescription)",
                                  comment: "Settings > Output error. %@ is the system error message.")
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = String(localized: "Choose", comment: "Open panel button: use the selected folder")
        panel.message = String(localized: "Choose where Hearsay saves transcripts and notes.",
                               comment: "Open panel message for the output folder")
        if !folderPath.isEmpty {
            panel.directoryURL = URL(fileURLWithPath: folderPath, isDirectory: true)
        }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            settings.outputFolderBookmark = try OutputLocation.makeBookmark(for: url)
            errorMessage = nil
        } catch {
            errorMessage = String(localized: "Could not remember that folder: \(error.localizedDescription)",
                                  comment: "Settings > Output error. %@ is the system error message.")
        }
    }
}
