import Foundation
import Observation

/// The chat-completions provider the user configured (PLAN.md sections 7, 8).
public struct AIProviderConfiguration: Codable, Sendable, Equatable {
    public var presetID: String
    /// Full `/chat/completions` endpoint.
    public var baseURL: String
    public var model: String
    /// Sent as `reasoning_effort` when non-nil and non-empty.
    public var reasoningEffort: String?
    public var auth: AuthHeaderStyle
    public var extraHeaders: [String: String]
    /// Parity with `AI_CONFIRM`: true is `always`, false is `never`.
    public var askBeforeSending: Bool
    public var selectedTemplateID: UUID

    public init(
        presetID: String,
        baseURL: String,
        model: String,
        reasoningEffort: String?,
        auth: AuthHeaderStyle,
        extraHeaders: [String: String] = [:],
        askBeforeSending: Bool = true,
        selectedTemplateID: UUID = PromptTemplate.generalMeetingID
    ) {
        self.presetID = presetID
        self.baseURL = baseURL
        self.model = model
        self.reasoningEffort = reasoningEffort
        self.auth = auth
        self.extraHeaders = extraHeaders
        self.askBeforeSending = askBeforeSending
        self.selectedTemplateID = selectedTemplateID
    }

    /// The preset's defaults: its URL, model, and auth, plus reasoning effort
    /// "max" when the preset supports it.
    public init(preset: ProviderPreset) {
        self.init(
            presetID: preset.id,
            baseURL: preset.baseURL,
            model: preset.defaultModel,
            reasoningEffort: preset.supportsReasoningEffort ? ProviderPreset.defaultReasoningEffort : nil,
            auth: preset.auth
        )
    }

    public static let `default` = AIProviderConfiguration(preset: .openAI)

    public var preset: ProviderPreset {
        ProviderPreset.preset(id: presetID) ?? .custom
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

    public init(defaults: UserDefaults = .standard, secrets: any SecretStore = KeychainSecretStore()) {
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
        var configuration = storedConfiguration ?? .default
        if !([PromptTemplate.generalMeeting] + userTemplates).contains(where: { $0.id == configuration.selectedTemplateID }) {
            configuration.selectedTemplateID = PromptTemplate.generalMeetingID
        }
        self.configuration = configuration
    }

    private func save<Value: Encodable>(_ value: Value, key: String) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        defaults.set(data, forKey: key)
    }

    // MARK: - Provider

    /// Switches to `preset`, resetting URL, model, auth, and reasoning effort
    /// to its defaults. Keeps extra headers, the ask setting, and the template.
    public func selectPreset(_ preset: ProviderPreset) {
        var updated = AIProviderConfiguration(preset: preset)
        updated.extraHeaders = configuration.extraHeaders
        updated.askBeforeSending = configuration.askBeforeSending
        updated.selectedTemplateID = configuration.selectedTemplateID
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
