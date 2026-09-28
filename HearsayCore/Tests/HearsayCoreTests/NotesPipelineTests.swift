import Foundation
import Testing
@testable import HearsayCore

// MARK: - URLProtocol stub

/// Serves canned responses keyed by request URL, so parallel tests that use
/// distinct hosts never see each other's handlers. Records each request with
/// its body (URLSession moves the body into a stream).
final class ChatStubURLProtocol: URLProtocol, @unchecked Sendable {
    struct Canned: Sendable {
        var status: Int
        var body: Data
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var responses: [String: Canned] = [:]
    nonisolated(unsafe) private static var recorded: [String: [(URLRequest, Data)]] = [:]

    static func register(_ url: String, status: Int, body: Data) {
        lock.withLock { responses[url] = Canned(status: status, body: body) }
    }

    static func requests(for url: String) -> [(URLRequest, Data)] {
        lock.withLock { recorded[url] ?? [] }
    }

    static func session() -> URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ChatStubURLProtocol.self]
        return configuration
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let key = request.url?.absoluteString ?? ""
        let body = Self.readBody(request)
        let canned = Self.lock.withLock { () -> Canned? in
            Self.recorded[key, default: []].append((request, body))
            return Self.responses[key]
        }
        guard let canned, let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: canned.status, httpVersion: "HTTP/1.1",
                                             headerFields: ["Content-Type": "application/json"]) else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: canned.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func readBody(_ request: URLRequest) -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            if count <= 0 { break }
            data.append(buffer, count: count)
        }
        return data
    }
}

private func uniqueURL() -> String {
    "https://\(UUID().uuidString.lowercased()).example.test/chat/completions"
}

private func completion(content: String) -> Data {
    let object: [String: Any] = ["choices": [["message": ["content": content]]]]
    return (try? JSONSerialization.data(withJSONObject: object)) ?? Data()
}

private func configuration(
    url: String,
    auth: AuthHeaderStyle = .bearer,
    reasoningEffort: String? = "max",
    presetID: String = "custom",
    extraHeaders: [String: String] = [:]
) -> AIProviderConfiguration {
    AIProviderConfiguration(
        presetID: presetID,
        baseURL: url,
        model: "test-model",
        reasoningEffort: reasoningEffort,
        auth: auth,
        extraHeaders: extraHeaders
    )
}

private let srt = "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n"

private let goodNotes = ##"{"filename": "Quarterly Planning", "markdown": "# API Notes", "transcript_markdown": "# Transcript\n\nDiscuss launch"}"##

private func pipeline() -> NotesPipeline {
    NotesPipeline(client: ChatCompletionsClient(sessionConfiguration: ChatStubURLProtocol.session()))
}

// MARK: - Client and pipeline

struct NotesPipelineTests {
    /// Mirrors `test_api_success_payload_and_token_precedence` (payload
    /// shape): model, the system and user messages, temperature 0.3,
    /// reasoning_effort, the
    /// 300 s timeout, and the Bearer header.
    @Test func successPayloadShapeAndBearerHeader() async throws {
        let url = uniqueURL()
        ChatStubURLProtocol.register(url, status: 200, body: completion(content: goodNotes))

        let result = try await pipeline().generate(
            srtText: srt, languageCode: "zh", template: .generalMeeting,
            configuration: configuration(url: url), token: "ai-token"
        )
        #expect(result == NotesResponse(filename: "Quarterly Planning", markdown: "# API Notes",
                                        transcriptMarkdown: "# Transcript\n\nDiscuss launch"))

        let recorded = ChatStubURLProtocol.requests(for: url)
        try #require(recorded.count == 1)
        let (request, body) = recorded[0]
        #expect(request.httpMethod == "POST")
        #expect(request.timeoutInterval == 300)
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer ai-token")
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "application/json")
        #expect(request.value(forHTTPHeaderField: "api-key") == nil)

        let payload = try #require(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        #expect(Set(payload.keys) == ["model", "messages", "temperature", "reasoning_effort"])
        #expect(payload["model"] as? String == "test-model")
        #expect(payload["temperature"] as? Double == 0.3)
        #expect(payload["reasoning_effort"] as? String == "max")
        let messages = try #require(payload["messages"] as? [[String: String]])
        #expect(messages.count == 2)
        #expect(messages[0] == ["role": "system", "content": "You are a professional and efficient meeting-note assistant."])
        #expect(messages[1]["role"] == "user")
        let user = try #require(messages[1]["content"])
        #expect(user.components(separatedBy: "Discuss launch").count - 1 == 1)
        #expect(user.contains("Write the entire meeting note in Traditional Chinese"))
        #expect(user == (try MeetingPrompt.build(transcript: srt, languageCode: "zh")))
    }

    @Test func reasoningEffortIsAbsentWhenNilOrEmptyOrUnsupported() throws {
        for config in [
            configuration(url: uniqueURL(), reasoningEffort: nil),
            configuration(url: uniqueURL(), reasoningEffort: "  "),
            configuration(url: uniqueURL(), reasoningEffort: "max", presetID: "ollama"),
        ] {
            let request = try ChatCompletionsClient.makeRequest(
                systemMessage: "s", userMessage: "u", configuration: config, token: "t"
            )
            let payload = try #require(
                try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any]
            )
            #expect(Set(payload.keys) == ["model", "messages", "temperature"])
        }
    }

    @Test func temperatureIsSentWhenSetAndAbsentWhenNil() throws {
        var config = configuration(url: uniqueURL())
        config.temperature = 0.7
        let set = try ChatCompletionsClient.makeRequest(
            systemMessage: "s", userMessage: "u", configuration: config, token: "t"
        )
        let setPayload = try #require(
            try JSONSerialization.jsonObject(with: set.httpBody ?? Data()) as? [String: Any]
        )
        #expect(setPayload["temperature"] as? Double == 0.7)

        config.temperature = nil
        let omitted = try ChatCompletionsClient.makeRequest(
            systemMessage: "s", userMessage: "u", configuration: config, token: "t"
        )
        let omittedPayload = try #require(
            try JSONSerialization.jsonObject(with: omitted.httpBody ?? Data()) as? [String: Any]
        )
        #expect(Set(omittedPayload.keys) == ["model", "messages", "reasoning_effort"])
    }

    @Test func temperatureNilSurvivesAReloadAndMissingKeyMeansDefault() throws {
        var config = AIProviderConfiguration.default
        config.temperature = nil
        let decoded = try JSONDecoder().decode(AIProviderConfiguration.self, from: JSONEncoder().encode(config))
        #expect(decoded.temperature == nil)
        #expect(decoded == config)

        let legacy = #"{"presetID":"openai","baseURL":"https://api.openai.com/v1/chat/completions","model":"m","auth":"bearer","extraHeaders":{},"askBeforeSending":true,"selectedTemplateID":"6E0B5A10-3C2D-4F51-9A7E-000000000001"}"#
        let old = try JSONDecoder().decode(AIProviderConfiguration.self, from: Data(legacy.utf8))
        #expect(old.temperature == 0.3)
        #expect(old.reasoningEffort == nil)
    }

    @Test func authHeaderPerStyleAndExtraHeaders() throws {
        let apiKey = try ChatCompletionsClient.makeRequest(
            systemMessage: "s", userMessage: "u",
            configuration: configuration(url: uniqueURL(), auth: .apiKey, extraHeaders: ["X-Team": "notes"]),
            token: "azure-key"
        )
        #expect(apiKey.value(forHTTPHeaderField: "api-key") == "azure-key")
        #expect(apiKey.value(forHTTPHeaderField: "Authorization") == nil)
        #expect(apiKey.value(forHTTPHeaderField: "X-Team") == "notes")

        let none = try ChatCompletionsClient.makeRequest(
            systemMessage: "s", userMessage: "u",
            configuration: configuration(url: uniqueURL(), auth: .none), token: nil
        )
        #expect(none.value(forHTTPHeaderField: "Authorization") == nil)
        #expect(none.value(forHTTPHeaderField: "api-key") == nil)
        #expect(none.value(forHTTPHeaderField: "Content-Type") == "application/json")
    }

    @Test func missingTokenAndInvalidURL() {
        for token in [nil, "", "   "] as [String?] {
            #expect(throws: ChatCompletionsError.missingToken) {
                try ChatCompletionsClient.makeRequest(
                    systemMessage: "s", userMessage: "u",
                    configuration: configuration(url: uniqueURL(), auth: .bearer), token: token
                )
            }
        }
        for url in ["", "not a url", "ftp://example.test/x", "file:///tmp/x"] {
            #expect(throws: ChatCompletionsError.invalidURL) {
                try ChatCompletionsClient.makeRequest(
                    systemMessage: "s", userMessage: "u",
                    configuration: configuration(url: url, auth: .none), token: nil
                )
            }
        }
    }

    /// Mirrors `test_api_http_json_and_empty_content_failures_are_atomic`:
    /// HTTP error, non-JSON body, and empty content all fail.
    @Test func httpErrorKeepsAFiveHundredCharacterExcerpt() async throws {
        let url = uniqueURL()
        let longBody = "not json " + String(repeating: "x", count: 900)
        ChatStubURLProtocol.register(url, status: 500, body: Data(longBody.utf8))
        do {
            _ = try await pipeline().generate(srtText: srt, languageCode: "en", template: .generalMeeting,
                                              configuration: configuration(url: url), token: "t")
            Issue.record("expected an error")
        } catch let error as ChatCompletionsError {
            #expect(error == .httpStatus(code: 500, bodyExcerpt: String(longBody.prefix(500))))
            #expect(error.errorDescription?.hasPrefix("API HTTP Error 500: not json xxx") == true)
        }
    }

    @Test func httpErrorMessagesReadLikePython() {
        #expect(ChatCompletionsError.httpStatus(code: 401, bodyExcerpt: #"{"error": {"message": "Bad credentials"}}"#)
            .errorDescription == "API HTTP Error 401: Bad credentials")
        #expect(ChatCompletionsError.httpStatus(code: 502, bodyExcerpt: "  ")
            .errorDescription == "API HTTP Error 502: The response body is empty.")
    }

    @Test func nonJSONBodyAndEmptyContentFail() async throws {
        let cases: [(Int, String, ChatCompletionsError)] = [
            (200, "{", .invalidJSON(bodyExcerpt: "{")),
            (200, #"{"choices":[{"message":{"content":"   "}}]}"#, .emptyContent),
            (200, #"{"choices":[{"message":{}}]}"#, .emptyContent),
            (200, #"{"choices":[]}"#, .emptyContent),
            (200, #"{"error":{"message":"quota"}}"#, .httpStatus(code: 200, bodyExcerpt: #"{"error":{"message":"quota"}}"#)),
        ]
        for (status, body, expected) in cases {
            let url = uniqueURL()
            ChatStubURLProtocol.register(url, status: status, body: Data(body.utf8))
            await #expect(throws: expected) {
                _ = try await pipeline().generate(srtText: srt, languageCode: "en", template: .generalMeeting,
                                                  configuration: configuration(url: url), token: "t")
            }
        }
    }

    @Test func transportFailureIsReported() async {
        // No canned response registered: the stub fails the connection.
        let url = uniqueURL()
        do {
            _ = try await ChatCompletionsClient(sessionConfiguration: ChatStubURLProtocol.session())
                .complete(systemMessage: "s", userMessage: "u", configuration: configuration(url: url), token: "t")
            Issue.record("expected an error")
        } catch let ChatCompletionsError.transport(detail) {
            #expect(!detail.isEmpty)
        } catch {
            Issue.record("unexpected error \(error)")
        }
    }

    @Test func fencedJSONReplyParses() async throws {
        let url = uniqueURL()
        ChatStubURLProtocol.register(url, status: 200, body: completion(content: "```json\n" + goodNotes + "\n```"))
        let result = try await pipeline().generate(srtText: srt, languageCode: "en", template: .generalMeeting,
                                                   configuration: configuration(url: url, auth: .none), token: nil)
        #expect(result.filename == "Quarterly Planning")
    }

    @Test func invalidNotesJSONSurfacesTheParseError() async {
        let url = uniqueURL()
        ChatStubURLProtocol.register(url, status: 200, body: completion(content: #"{"filename": "x"}"#))
        await #expect(throws: NotesResponseError.missingMarkdown) {
            _ = try await pipeline().generate(srtText: srt, languageCode: "en", template: .generalMeeting,
                                              configuration: configuration(url: url), token: "t")
        }
    }

    /// Python: "The SRT contains no transcript content to summarize." and no
    /// request is sent.
    @Test func emptyTranscriptNeverCallsTheProvider() async {
        let url = uniqueURL()
        ChatStubURLProtocol.register(url, status: 200, body: completion(content: goodNotes))
        await #expect(throws: NotesPipelineError.emptyTranscript) {
            _ = try await pipeline().generate(srtText: "1\n00:00:01,000 --> 00:00:02,000\n\n",
                                              languageCode: "en", template: .generalMeeting,
                                              configuration: configuration(url: url), token: "t")
        }
        #expect(ChatStubURLProtocol.requests(for: url).isEmpty)
    }

    @Test func unsupportedLanguageNeverCallsTheProvider() async {
        let url = uniqueURL()
        await #expect(throws: MeetingPromptError.unsupportedLanguage("fr")) {
            _ = try await pipeline().generate(srtText: srt, languageCode: "fr", template: .generalMeeting,
                                              configuration: configuration(url: url), token: "t")
        }
        #expect(ChatStubURLProtocol.requests(for: url).isEmpty)
    }

    @Test func customTemplateReplacesTheNotesSection() async throws {
        let url = uniqueURL()
        ChatStubURLProtocol.register(url, status: 200, body: completion(content: goodNotes))
        let template = PromptTemplate(name: "Standup", instructions: "Summarize the standup in {output_language}.")
        _ = try await pipeline().generate(srtText: srt, languageCode: "en", template: template,
                                          configuration: configuration(url: url), token: "t")
        let body = try #require(ChatStubURLProtocol.requests(for: url).first?.1)
        let payload = try #require(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        let messages = try #require(payload["messages"] as? [[String: String]])
        #expect(messages[1]["content"]?.hasPrefix("Summarize the standup in English.\n\n") == true)
        #expect(messages[1]["content"]?.contains(MeetingPrompt.responseRules) == true)
    }
}

// MARK: - Presets, store, secrets

@MainActor
struct AIProviderStoreTests {
    private static func freshDefaults() -> UserDefaults {
        let suite = "tw.og1o.hearsay.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func presetTable() {
        #expect(ProviderPreset.all.map(\.id) == ["openai", "githubModels", "azureOpenAI", "anthropic", "ollama", "custom"])
        #expect(ProviderPreset.openAI.baseURL == "https://api.openai.com/v1/chat/completions")
        #expect(ProviderPreset.githubModels.baseURL == "https://models.inference.ai.github.com/chat/completions")
        #expect(ProviderPreset.azureOpenAI.baseURL.isEmpty)
        #expect(ProviderPreset.azureOpenAI.auth == .apiKey)
        #expect(ProviderPreset.anthropic.defaultModel == "claude-sonnet-5")
        #expect(ProviderPreset.ollama.auth == AuthHeaderStyle.none)
        #expect(ProviderPreset.ollama.defaultModel.isEmpty)
        #expect(AIProviderConfiguration(preset: .openAI).reasoningEffort == "max")
        #expect(AIProviderConfiguration(preset: .openAI).model == "gpt-5.6-luna")
        #expect(AIProviderConfiguration(preset: .ollama).reasoningEffort == nil)
        #expect(AIProviderConfiguration(preset: .openAI).temperature == 0.3)
        #expect(AIProviderConfiguration(preset: .ollama).temperature == 0.3)
    }

    @Test func defaultsOnFirstLaunch() {
        let store = AIProviderStore(defaults: Self.freshDefaults(), secrets: InMemorySecretStore())
        #expect(store.configuration == .default)
        #expect(store.configuration.askBeforeSending)
        #expect(store.templates == [.generalMeeting])
        #expect(store.selectedTemplate == .generalMeeting)
        #expect(!store.hasToken)
    }

    @Test func configurationAndTemplatesRoundTrip() {
        let defaults = Self.freshDefaults()
        let store = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore())
        store.selectPreset(.azureOpenAI)
        store.configuration.baseURL = "https://me.openai.azure.com/openai/deployments/x/chat/completions"
        store.configuration.extraHeaders = ["X-A": "b"]
        store.configuration.askBeforeSending = false
        let standup = store.addTemplate(name: "Standup", instructions: "Short.")
        store.setDefaultTemplate(id: standup.id)

        let reloaded = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore())
        #expect(reloaded.configuration == store.configuration)
        #expect(reloaded.configuration.presetID == "azureOpenAI")
        #expect(reloaded.configuration.auth == .apiKey)
        #expect(reloaded.templates == [.generalMeeting, standup])
        #expect(reloaded.selectedTemplate == standup)
    }

    @Test func builtInTemplateCannotBeEditedOrDeleted() {
        let store = AIProviderStore(defaults: Self.freshDefaults(), secrets: InMemorySecretStore())
        var edited = PromptTemplate.generalMeeting
        edited.name = "Changed"
        store.updateTemplate(edited)
        store.deleteTemplate(id: PromptTemplate.generalMeetingID)
        #expect(store.templates == [.generalMeeting])
    }

    @Test func editAndDeleteUserTemplate() {
        let defaults = Self.freshDefaults()
        let store = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore())
        var template = store.addTemplate(name: "A", instructions: "a")
        template.name = "B"
        template.instructions = "b"
        store.updateTemplate(template)
        #expect(store.templates.last?.name == "B")
        store.setDefaultTemplate(id: template.id)
        store.deleteTemplate(id: template.id)
        #expect(store.templates == [.generalMeeting])
        #expect(store.configuration.selectedTemplateID == PromptTemplate.generalMeetingID)
        #expect(AIProviderStore(defaults: defaults, secrets: InMemorySecretStore()).templates == [.generalMeeting])
    }

    @Test func storedTemplatesAlwaysStartWithTheBuiltIn() throws {
        let defaults = Self.freshDefaults()
        var tampered = PromptTemplate.generalMeeting
        tampered.instructions = "tampered"
        let user = PromptTemplate(name: "U", instructions: "u")
        defaults.set(try JSONEncoder().encode([user, tampered]), forKey: AIProviderStore.Key.templates)
        var config = AIProviderConfiguration.default
        config.selectedTemplateID = UUID()
        defaults.set(try JSONEncoder().encode(config), forKey: AIProviderStore.Key.configuration)

        let store = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore())
        #expect(store.templates == [.generalMeeting, user])
        #expect(store.configuration.selectedTemplateID == PromptTemplate.generalMeetingID)
    }

    @Test func selectPresetResetsProviderFieldsButKeepsPreferences() {
        let store = AIProviderStore(defaults: Self.freshDefaults(), secrets: InMemorySecretStore())
        store.configuration.askBeforeSending = false
        store.configuration.model = "other"
        store.selectPreset(.ollama)
        #expect(store.configuration.baseURL == "http://localhost:11434/v1/chat/completions")
        #expect(store.configuration.model.isEmpty)
        #expect(store.configuration.reasoningEffort == nil)
        #expect(store.configuration.auth == AuthHeaderStyle.none)
        #expect(!store.configuration.askBeforeSending)
    }

    @Test func tokensArePerPreset() throws {
        let secrets = InMemorySecretStore()
        let store = AIProviderStore(defaults: Self.freshDefaults(), secrets: secrets)
        try store.setToken("  sk-openai \n")
        #expect(store.currentToken == "sk-openai")
        store.selectPreset(.githubModels)
        #expect(!store.hasToken)
        try store.setToken("ghp")
        #expect(try secrets.read(account: "openai") == "sk-openai")
        #expect(try secrets.read(account: "githubModels") == "ghp")
        try store.setToken("")
        #expect(!store.hasToken)
        store.selectPreset(.openAI)
        try store.deleteToken()
        #expect(try secrets.read(account: "openai") == nil)
    }
}

struct InMemorySecretStoreTests {
    @Test func readWriteDelete() throws {
        let store = InMemorySecretStore()
        #expect(try store.read(account: "a") == nil)
        try store.write("one", account: "a")
        try store.write("two", account: "a")
        try store.write("other", account: "b")
        #expect(try store.read(account: "a") == "two")
        try store.delete(account: "a")
        try store.delete(account: "missing")
        #expect(try store.read(account: "a") == nil)
        #expect(try store.read(account: "b") == "other")
    }

    @Test func concurrentAccessIsSafe() async throws {
        let store = InMemorySecretStore()
        await withTaskGroup(of: Void.self) { group in
            for index in 0..<200 {
                group.addTask { try? store.write("\(index)", account: "k\(index % 7)") }
            }
        }
        for index in 0..<7 {
            #expect(try store.read(account: "k\(index)") != nil)
        }
    }
}
