import Foundation
import Observation

/// The notes provider the user configured (PLAN.md sections 7, 8).
public struct AIProviderConfiguration: Codable, Sendable, Equatable {
    public var presetID: String
    /// Full `/chat/completions` endpoint.
    public var baseURL: String
    public var model: String
    /// Sent as `reasoning_effort` when non-nil and non-empty.
    public var reasoningEffort: String?
    /// Sent as `temperature` when non-nil (Python sends 0.3). Nil omits the
    /// field, for models that reject it.
    public var temperature: Double?
    public var auth: AuthHeaderStyle
    public var extraHeaders: [String: String]
    /// Parity with `AI_CONFIRM`: true is `always`, false is `never`.
    public var askBeforeSending: Bool
    public var selectedTemplateID: UUID
    /// The `copilot` binary to run for the Copilot CLI preset. Nil or empty
    /// means auto-detect (`CLILocator`).
    public var copilotPath: String?
    /// The `claude` binary for the Claude Code CLI preset; nil auto-detects.
    public var claudeCodePath: String?
    /// The `codex` binary for the Codex CLI preset; nil auto-detects.
    public var codexPath: String?
    /// The `agy` binary for the Antigravity CLI preset; nil auto-detects.
    public var antigravityPath: String?

    public init(
        presetID: String,
        baseURL: String,
        model: String,
        reasoningEffort: String?,
        temperature: Double? = AIProviderConfiguration.defaultTemperature,
        auth: AuthHeaderStyle,
        extraHeaders: [String: String] = [:],
        askBeforeSending: Bool = true,
        selectedTemplateID: UUID = PromptTemplate.generalMeetingID,
        copilotPath: String? = nil,
        claudeCodePath: String? = nil,
        codexPath: String? = nil,
        antigravityPath: String? = nil
    ) {
        self.presetID = presetID
        self.baseURL = baseURL
        self.model = model
        self.reasoningEffort = reasoningEffort
        self.temperature = temperature
        self.auth = auth
        self.extraHeaders = extraHeaders
        self.askBeforeSending = askBeforeSending
        self.selectedTemplateID = selectedTemplateID
        self.copilotPath = copilotPath
        self.claudeCodePath = claudeCodePath
        self.codexPath = codexPath
        self.antigravityPath = antigravityPath
    }

    /// The preset's defaults: its URL, model, and auth, its default reasoning
    /// effort ("max" unless the preset says otherwise) when it supports one,
    /// and temperature 0.3 when it supports one (HTTP presets only).
    public init(preset: ProviderPreset) {
        self.init(
            presetID: preset.id,
            baseURL: preset.baseURL,
            model: preset.defaultModel,
            reasoningEffort: preset.supportsReasoningEffort ? preset.defaultEffort : nil,
            temperature: preset.supportsTemperature ? Self.defaultTemperature : nil,
            auth: preset.auth
        )
    }

    /// The configuration used when nothing is stored and no CLI is found:
    /// the GitHub Copilot CLI preset, whose settings explain how to install it.
    public static let `default` = AIProviderConfiguration(preset: .copilotCLI)

    /// The first-launch configuration: the first CLI found, in preset order
    /// (Copilot, Claude Code, Codex, Antigravity), otherwise `default`.
    public static func firstLaunch(installedCLI: CLITool?) -> AIProviderConfiguration {
        installedCLI.map { AIProviderConfiguration(preset: .preset(for: $0)) } ?? .default
    }

    /// Python `generate_meeting_notes` payload `"temperature": 0.3`.
    public static let defaultTemperature: Double = 0.3

    private enum CodingKeys: String, CodingKey {
        case presetID, baseURL, model, reasoningEffort, temperature, auth
        case extraHeaders, askBeforeSending, selectedTemplateID, copilotPath
        case claudeCodePath, codexPath, antigravityPath
    }

    /// A missing `temperature` key (configurations saved before the field
    /// existed) means the default 0.3; an explicit null means omit it.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        presetID = try container.decode(String.self, forKey: .presetID)
        baseURL = try container.decode(String.self, forKey: .baseURL)
        model = try container.decode(String.self, forKey: .model)
        reasoningEffort = try container.decodeIfPresent(String.self, forKey: .reasoningEffort)
        if container.contains(.temperature) {
            temperature = try container.decodeIfPresent(Double.self, forKey: .temperature)
        } else {
            temperature = Self.defaultTemperature
        }
        auth = try container.decode(AuthHeaderStyle.self, forKey: .auth)
        extraHeaders = try container.decodeIfPresent([String: String].self, forKey: .extraHeaders) ?? [:]
        askBeforeSending = try container.decodeIfPresent(Bool.self, forKey: .askBeforeSending) ?? true
        selectedTemplateID = try container.decodeIfPresent(UUID.self, forKey: .selectedTemplateID)
            ?? PromptTemplate.generalMeetingID
        copilotPath = try container.decodeIfPresent(String.self, forKey: .copilotPath)
        claudeCodePath = try container.decodeIfPresent(String.self, forKey: .claudeCodePath)
        codexPath = try container.decodeIfPresent(String.self, forKey: .codexPath)
        antigravityPath = try container.decodeIfPresent(String.self, forKey: .antigravityPath)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(presetID, forKey: .presetID)
        try container.encode(baseURL, forKey: .baseURL)
        try container.encode(model, forKey: .model)
        try container.encodeIfPresent(reasoningEffort, forKey: .reasoningEffort)
        // Written as null when nil so "omit" survives a reload.
        try container.encode(temperature, forKey: .temperature)
        try container.encode(auth, forKey: .auth)
        try container.encode(extraHeaders, forKey: .extraHeaders)
        try container.encode(askBeforeSending, forKey: .askBeforeSending)
        try container.encode(selectedTemplateID, forKey: .selectedTemplateID)
        try container.encodeIfPresent(copilotPath, forKey: .copilotPath)
        try container.encodeIfPresent(claudeCodePath, forKey: .claudeCodePath)
        try container.encodeIfPresent(codexPath, forKey: .codexPath)
        try container.encodeIfPresent(antigravityPath, forKey: .antigravityPath)
    }

    public var preset: ProviderPreset {
        ProviderPreset.preset(id: presetID) ?? .custom
    }

    /// This configuration with a retired preset id replaced by `custom`, or
    /// nil when the preset is still offered. URL, model, auth, and every other
    /// field are kept so nothing the user entered is lost.
    public func migratingRetiredPreset() -> AIProviderConfiguration? {
        guard ProviderPreset.retiredIDs.contains(presetID) else { return nil }
        var migrated = self
        migrated.presetID = ProviderPreset.custom.id
        return migrated
    }

    /// The configured binary path for `tool`; nil or empty auto-detects.
    public func cliPath(for tool: CLITool) -> String? {
        switch tool {
        case .copilot: copilotPath
        case .claudeCode: claudeCodePath
        case .codex: codexPath
        case .antigravity: antigravityPath
        }
    }

    public mutating func setCLIPath(_ path: String?, for tool: CLITool) {
        switch tool {
        case .copilot: copilotPath = path
        case .claudeCode: claudeCodePath = path
        case .codex: codexPath = path
        case .antigravity: antigravityPath = path
        }
    }

    /// The temperature to send, or nil to omit it. Always nil for CLI presets.
    public var effectiveTemperature: Double? {
        preset.supportsTemperature ? temperature : nil
    }

    /// The model as the confirm sheet shows it. An empty model is what the
    /// provider then uses: the Copilot default (Python `or`), the CLI's own
    /// default for Claude Code, Codex, and Antigravity, or nothing for HTTP.
    public var modelDescription: String {
        let trimmed = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { return trimmed }
        switch preset.kind {
        case .copilotCLI: return ProviderPreset.defaultOpenAIModel
        case .claudeCodeCLI, .codexCLI, .antigravityCLI:
            return String(localized: "CLI default", bundle: .module,
                          comment: "Confirm sheet 'Model:' when empty: the command-line tool picks its own model")
        case .http:
            return String(localized: "(none set)", bundle: .module, comment: "Confirm sheet 'Model:' when empty")
        }
    }

    /// The reasoning effort to send, or nil to omit the field.
    public var effectiveReasoningEffort: String? {
        guard preset.supportsReasoningEffort,
              let value = reasoningEffort?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else { return nil }
        return value
    }
}

/// Persists the provider configuration and the prompt templates as JSON in
/// `UserDefaults`, and the per-preset API tokens in a `SecretStore`.
@MainActor
@Observable
public final class AIProviderStore {
    public enum Key {
        public static let configuration = "aiProviderConfiguration"
        public static let templates = "promptTemplates"
    }

    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored public let secrets: any SecretStore

    public var configuration: AIProviderConfiguration {
        didSet { save(configuration, key: Key.configuration) }
    }

    /// Always starts with `PromptTemplate.generalMeeting`.
    public private(set) var templates: [PromptTemplate] {
        didSet { save(templates.filter { !$0.isBuiltIn }, key: Key.templates) }
    }

    /// Bumped on every token change so views re-read `hasToken`.
    public private(set) var tokenRevision = 0

    /// `installedCLI` is asked only on first launch (nothing stored) to pick
    /// the default preset. The default checks the well-known install
    /// locations and never starts a shell, so it is safe on the main thread.
    public init(
        defaults: UserDefaults = .standard,
        secrets: any SecretStore = KeychainSecretStore(),
        installedCLI: () -> CLITool? = { CLILocator().firstKnownTool() }
    ) {
        self.defaults = defaults
        self.secrets = secrets
        let decoder = JSONDecoder()
        let storedConfiguration = defaults.data(forKey: Key.configuration)
            .flatMap { try? decoder.decode(AIProviderConfiguration.self, from: $0) }
        let storedTemplates = defaults.data(forKey: Key.templates)
            .flatMap { try? decoder.decode([PromptTemplate].self, from: $0) } ?? []
        let userTemplates = storedTemplates.filter {
            !$0.isBuiltIn && $0.id != PromptTemplate.generalMeetingID
        }
        self.templates = [.generalMeeting] + userTemplates
        var needsSave = storedConfiguration == nil
        var configuration = storedConfiguration ?? .firstLaunch(installedCLI: installedCLI())
        if let migrated = configuration.migratingRetiredPreset() {
            Self.moveToken(from: configuration.presetID, to: migrated.presetID, in: secrets)
            configuration = migrated
            needsSave = true
        }
        if !([PromptTemplate.generalMeeting] + userTemplates).contains(where: { $0.id == configuration.selectedTemplateID }) {
            configuration.selectedTemplateID = PromptTemplate.generalMeetingID
        }
        self.configuration = configuration
        if needsSave { save(configuration, key: Key.configuration) }
    }

    /// Moves the Keychain token of a retired preset to its replacement, once.
    /// A token already stored under the replacement is never overwritten (the
    /// old one is then left in place), and the old one is deleted only after
    /// a successful copy.
    private static func moveToken(from oldID: String, to newID: String, in secrets: any SecretStore) {
        guard let token = (try? secrets.read(account: oldID)) ?? nil, !token.isEmpty else { return }
        let existing = (try? secrets.read(account: newID)) ?? nil
        guard existing?.isEmpty ?? true else { return }
        do { try secrets.write(token, account: newID) } catch { return }
        try? secrets.delete(account: oldID)
    }

    private func save<Value: Encodable>(_ value: Value, key: String) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        defaults.set(data, forKey: key)
    }

    // MARK: - Provider

    /// Switches to `preset`, resetting URL, model, auth, reasoning effort, and
    /// temperature to its defaults. Keeps extra headers, the ask setting, the
    /// template, and the CLI paths.
    public func selectPreset(_ preset: ProviderPreset) {
        var updated = AIProviderConfiguration(preset: preset)
        updated.extraHeaders = configuration.extraHeaders
        updated.askBeforeSending = configuration.askBeforeSending
        updated.selectedTemplateID = configuration.selectedTemplateID
        updated.copilotPath = configuration.copilotPath
        updated.claudeCodePath = configuration.claudeCodePath
        updated.codexPath = configuration.codexPath
        updated.antigravityPath = configuration.antigravityPath
        configuration = updated
    }

    // MARK: - Tokens

    public func token(for presetID: String) throws -> String? {
        try secrets.read(account: presetID)
    }

    /// The token for the current preset, or nil when unset or unreadable.
    public var currentToken: String? {
        _ = tokenRevision
        let value = (try? secrets.read(account: configuration.presetID)) ?? nil
        guard let value, !value.isEmpty else { return nil }
        return value
    }

    public var hasToken: Bool { currentToken != nil }

    /// Stores `token` for the current preset; an empty token deletes it.
    public func setToken(_ token: String) throws {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        defer { tokenRevision += 1 }
        if trimmed.isEmpty {
            try secrets.delete(account: configuration.presetID)
        } else {
            try secrets.write(trimmed, account: configuration.presetID)
        }
    }

    public func deleteToken() throws {
        defer { tokenRevision += 1 }
        try secrets.delete(account: configuration.presetID)
    }

    // MARK: - Templates

    public var selectedTemplate: PromptTemplate {
        template(id: configuration.selectedTemplateID)
    }

    /// The template with `id`, or the built-in one when it no longer exists.
    public func template(id: UUID) -> PromptTemplate {
        templates.first { $0.id == id } ?? .generalMeeting
    }

    @discardableResult
    public func addTemplate(name: String, instructions: String) -> PromptTemplate {
        let template = PromptTemplate(name: name, instructions: instructions)
        templates.append(template)
        return template
    }

    /// Replaces a user template's name and instructions. The built-in
    /// template is never changed.
    public func updateTemplate(_ template: PromptTemplate) {
        guard let index = templates.firstIndex(where: { $0.id == template.id }),
              !templates[index].isBuiltIn else { return }
        templates[index].name = template.name
        templates[index].instructions = template.instructions
    }

    /// Deletes a user template. The built-in template cannot be deleted. If
    /// the deleted template was the default, the built-in one becomes it.
    public func deleteTemplate(id: UUID) {
        guard let index = templates.firstIndex(where: { $0.id == id }),
              !templates[index].isBuiltIn else { return }
        templates.remove(at: index)
        if configuration.selectedTemplateID == id {
            configuration.selectedTemplateID = PromptTemplate.generalMeetingID
        }
    }

    public func setDefaultTemplate(id: UUID) {
        guard templates.contains(where: { $0.id == id }) else { return }
        configuration.selectedTemplateID = id
    }
}
