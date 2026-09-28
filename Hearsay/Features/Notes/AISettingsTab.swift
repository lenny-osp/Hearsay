import HearsayCore
import SwiftUI

/// Settings > AI (PLAN.md sections 7 and 8): provider preset, endpoint,
/// model, reasoning effort, temperature, token (Keychain), extra headers, the ask-before-
/// sending switch, a connection test, and the prompt templates. The GitHub
/// Copilot CLI preset shows the binary path, model, and reasoning effort
/// instead, plus "Check Copilot".
struct AISettingsTab: View {
    @Environment(AIProviderStore.self) private var store

    @State private var tokenInput = ""
    @State private var tokenError: String?
    @State private var headersText = ""
    @State private var testState: TestState = .idle
    @State private var editingTemplate: PromptTemplate?
    @State private var selectedTemplateID: UUID?
    @State private var detectedCopilotPath: String?
    @State private var copilotCheck: CopilotCheck = .idle

    private enum CopilotCheck: Equatable {
        case idle
        case running
        case found(path: String, version: String)
        case failed(String)
    }

    private enum TestState: Equatable {
        case idle
        case running
        case succeeded(String)
        case failed(String)
    }

    var body: some View {
        @Bindable var store = store
        let preset = store.configuration.preset
        Form {
            Section("Provider") {
                Picker("Preset:", selection: presetBinding) {
                    ForEach(ProviderPreset.all) { preset in
                        Text(preset.name).tag(preset.id)
                    }
                }
                if preset.kind == .copilotCLI {
                    copilotFields
                } else {
                    httpFields
                }
            }

            if preset.kind == .http {
                tokenSection
            }

            Section("Sending") {
                Toggle("Ask before sending a transcript", isOn: $store.configuration.askBeforeSending)
                HStack {
                    Button("Test connection", action: testConnection)
                        .disabled(testState == .running)
                    switch testState {
                    case .idle:
                        EmptyView()
                    case .running:
                        ProgressView().controlSize(.small)
                    case .succeeded(let reply):
                        Text("Connected. Reply: \(reply)").foregroundStyle(.green)
                    case .failed(let message):
                        Text(message).foregroundStyle(.red).textSelection(.enabled)
                    }
                }
            }

            templatesSection
        }
        .formStyle(.grouped)
        .onAppear {
            loadHeaders()
            detectCopilot()
        }
        .onChange(of: store.configuration.presetID) {
            tokenInput = ""
            tokenError = nil
            testState = .idle
            copilotCheck = .idle
            detectCopilot()
        }
        .sheet(item: $editingTemplate) { template in
            TemplateEditorSheet(template: template) { edited in
                if store.templates.contains(where: { $0.id == edited.id }) {
                    store.updateTemplate(edited)
                } else {
                    let added = store.addTemplate(name: edited.name, instructions: edited.instructions)
                    selectedTemplateID = added.id
                }
                editingTemplate = nil
            } onCancel: {
                editingTemplate = nil
            }
        }
    }

    // MARK: - Provider fields

    @ViewBuilder private var copilotFields: some View {
        @Bindable var store = store
        TextField("Copilot CLI path:", text: copilotPathBinding,
                  prompt: Text(detectedCopilotPath ?? "not found; enter the path to copilot"))
        TextField("Model:", text: $store.configuration.model,
                  prompt: Text(ProviderPreset.defaultOpenAIModel))
        TextField("Reasoning effort:", text: reasoningBinding,
                  prompt: Text(ProviderPreset.defaultReasoningEffort))
        HStack {
            Button("Check Copilot", action: checkCopilot)
                .disabled(copilotCheck == .running)
            switch copilotCheck {
            case .idle:
                EmptyView()
            case .running:
                ProgressView().controlSize(.small)
            case let .found(path, version):
                Text("\(version) at \(path)")
                    .foregroundStyle(.green)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
            case .failed(let message):
                Text(message).foregroundStyle(.red).textSelection(.enabled)
            }
        }
        Text("Uses the Copilot CLI installed on this Mac and its own login. Run `copilot` once in Terminal to log in. \"auto\" lets Copilot pick the model.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder private var httpFields: some View {
        @Bindable var store = store
        let preset = store.configuration.preset
        TextField("Endpoint URL:", text: $store.configuration.baseURL,
                  prompt: Text("https://…/chat/completions"))
        TextField("Model:", text: $store.configuration.model)
        TextField("Reasoning effort:", text: reasoningBinding, prompt: Text("omitted when empty"))
            .disabled(!preset.supportsReasoningEffort)
        TextField("Temperature:", value: $store.configuration.temperature,
                  format: .number.precision(.fractionLength(0...2)),
                  prompt: Text("omitted when empty"))
        if preset.id == ProviderPreset.custom.id {
            Picker("Token header:", selection: $store.configuration.auth) {
                ForEach(AuthHeaderStyle.allCases, id: \.self) { style in
                    Text(style.displayName).tag(style)
                }
            }
        }
        TextField("Extra headers:", text: $headersText, prompt: Text("Name: value, one per line"),
                  axis: .vertical)
            .lineLimit(1...4)
            .onSubmit(commitHeaders)
    }

    private var tokenSection: some View {
        Section("Token") {
            if store.configuration.auth.needsToken {
                SecureField("API token:", text: $tokenInput, prompt: Text("paste and press Return"))
                    .onSubmit(commitToken)
                LabeledContent("Status:") {
                    HStack {
                        Text(store.hasToken ? "Saved" : "Not set")
                            .foregroundStyle(store.hasToken ? Color.primary : Color.secondary)
                        if store.hasToken {
                            Button("Remove") { removeToken() }
                        }
                    }
                }
                if let tokenError {
                    Text(tokenError).font(.caption).foregroundStyle(.red)
                }
            } else {
                Text("This provider needs no token.")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var templatesSection: some View {
        Section("Prompt templates") {
            List(selection: $selectedTemplateID) {
                ForEach(store.templates) { template in
                    HStack {
                        Text(template.name)
                        if template.isBuiltIn {
                            Text("built-in").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if template.id == store.configuration.selectedTemplateID {
                            Text("Default").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .tag(template.id)
                }
            }
            .frame(minHeight: 90)
            HStack {
                Button("Add") {
                    editingTemplate = PromptTemplate(
                        name: "New template",
                        instructions: PromptTemplate.generalMeeting.instructions
                    )
                }
                Button("Edit") {
                    if let selected = selectedTemplate { editingTemplate = selected }
                }
                .disabled(selectedTemplate.map { $0.isBuiltIn } ?? true)
                Button("Delete") {
                    if let selected = selectedTemplate { store.deleteTemplate(id: selected.id) }
                    selectedTemplateID = nil
                }
                .disabled(selectedTemplate.map { $0.isBuiltIn } ?? true)
                Spacer()
                Button("Set as Default") {
                    if let selected = selectedTemplate { store.setDefaultTemplate(id: selected.id) }
                }
                .disabled(selectedTemplate == nil
                          || selectedTemplate?.id == store.configuration.selectedTemplateID)
            }
        }
    }

    private var selectedTemplate: PromptTemplate? {
        selectedTemplateID.flatMap { id in store.templates.first { $0.id == id } }
    }

    private var presetBinding: Binding<String> {
        Binding(
            get: { store.configuration.presetID },
            set: { id in
                if let preset = ProviderPreset.preset(id: id), id != store.configuration.presetID {
                    store.selectPreset(preset)
                }
            }
        )
    }

    private var copilotPathBinding: Binding<String> {
        Binding(
            get: { store.configuration.copilotPath ?? "" },
            set: { value in
                let trimmed = value.trimmingCharacters(in: .whitespaces)
                store.configuration.copilotPath = trimmed.isEmpty ? nil : value
                copilotCheck = .idle
            }
        )
    }

    private var reasoningBinding: Binding<String> {
        Binding(
            get: { store.configuration.reasoningEffort ?? "" },
            set: { value in
                let trimmed = value.trimmingCharacters(in: .whitespaces)
                store.configuration.reasoningEffort = trimmed.isEmpty ? nil : value
            }
        )
    }

    // MARK: - Token

    private func commitToken() {
        guard !tokenInput.isEmpty else { return }
        do {
            try store.setToken(tokenInput)
            tokenError = nil
        } catch {
            tokenError = "Could not save the token: \(NotesFlowViewModel.describe(error))"
        }
        tokenInput = ""
    }

    private func removeToken() {
        do {
            try store.deleteToken()
            tokenError = nil
        } catch {
            tokenError = "Could not remove the token: \(NotesFlowViewModel.describe(error))"
        }
    }

    // MARK: - Extra headers

    private func loadHeaders() {
        headersText = store.configuration.extraHeaders
            .sorted { $0.key < $1.key }
            .map { "\($0.key): \($0.value)" }
            .joined(separator: "\n")
    }

    private func commitHeaders() {
        var headers: [String: String] = [:]
        for line in headersText.split(whereSeparator: \.isNewline) {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces)
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            if !name.isEmpty { headers[name] = value }
        }
        store.configuration.extraHeaders = headers
        loadHeaders()
    }

    // MARK: - Test connection

    private func testConnection() {
        commitHeaders()
        testState = .running
        let configuration = store.configuration
        let token = configuration.auth.needsToken ? store.currentToken : nil
        Task {
            do {
                let reply = try await NotesPipeline().client(for: configuration).complete(
                    systemMessage: MeetingPrompt.systemMessage,
                    userMessage: "Reply with the single word OK.",
                    configuration: configuration,
                    token: token
                )
                testState = .succeeded(String(reply.trimmingCharacters(in: .whitespacesAndNewlines).prefix(60)))
            } catch {
                testState = .failed(NotesFlowViewModel.describe(error))
            }
        }
    }
}

extension AISettingsTab {
    // MARK: - Copilot CLI

    /// Fills the path placeholder with the auto-detected binary.
    fileprivate func detectCopilot() {
        guard store.configuration.preset.kind == .copilotCLI else { return }
        Task {
            detectedCopilotPath = try? await CopilotCLIClient().locate(configuredPath: nil)
        }
    }

    fileprivate func checkCopilot() {
        copilotCheck = .running
        let configuration = store.configuration
        Task {
            do {
                let installation = try await NotesPipeline().checkInstallation(configuration: configuration)
                copilotCheck = .found(path: installation.path, version: installation.version)
            } catch {
                copilotCheck = .failed(NotesFlowViewModel.describe(error))
            }
        }
    }
}

/// Edits a user template's name and instructions.
private struct TemplateEditorSheet: View {
    @State private var template: PromptTemplate
    let onSave: (PromptTemplate) -> Void
    let onCancel: () -> Void

    init(template: PromptTemplate, onSave: @escaping (PromptTemplate) -> Void, onCancel: @escaping () -> Void) {
        _template = State(initialValue: template)
        self.onSave = onSave
        self.onCancel = onCancel
    }

    private var isValid: Bool {
        !template.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !template.instructions.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Prompt template").font(.headline)
            TextField("Name:", text: $template.name)
            Text("Instructions")
            TextEditor(text: $template.instructions)
                .font(.body.monospaced())
                .frame(minHeight: 220)
                .border(Color.secondary.opacity(0.3))
            Text("\(PromptTemplate.outputLanguagePlaceholder) is replaced by the notes language. The filename and JSON rules and the transcript are always appended.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: onCancel)
                    .keyboardShortcut(.cancelAction)
                Button("Save") { onSave(template) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!isValid)
            }
        }
        .padding(20)
        .frame(width: 560)
    }
}
