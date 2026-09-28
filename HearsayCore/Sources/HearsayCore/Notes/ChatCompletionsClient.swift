import Foundation

/// Why a chat-completions call produced no usable content. Messages follow
/// the Python `_request_api` / `_report_http_error` diagnostics.
public enum ChatCompletionsError: Error, LocalizedError, Equatable {
    /// The auth style needs a token and none is stored.
    case missingToken
    case invalidURL
    case transport(String)
    /// The host could not be resolved or connected to, or there is no network.
    case unreachable(host: String)
    /// Non-2xx response, or a 2xx response carrying only an `error` object.
    case httpStatus(code: Int, bodyExcerpt: String)
    case invalidJSON(bodyExcerpt: String)
    case emptyContent

    /// Python truncates response bodies to 500 characters in diagnostics.
    public static let excerptLength = 500

    public var errorDescription: String? {
        switch self {
        case .missingToken:
            return String(localized: "No API token is set for this provider. Add one in Settings > AI.",
                          bundle: .module, comment: "Meeting notes error. 'Settings > AI' names the Settings tab and section.")
        case .invalidURL:
            return String(localized: "The API URL is not a valid http or https address. Check it in Settings > AI.",
                          bundle: .module, comment: "Meeting notes error")
        case .unreachable(let host):
            return String(
                localized: "Cannot reach \(host). Check the base URL in Settings > AI and your network connection.",
                bundle: .module, comment: "Meeting notes error. %@ is a host name.")
        case .transport(let detail):
            return String(
                localized: "API call failed: A network connection or HTTP client error occurred. \(detail)",
                bundle: .module, comment: "Meeting notes error. %@ is the system error message.")
        case let .httpStatus(code, excerpt):
            if excerpt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return String(localized: "API HTTP Error \(code): The response body is empty.", bundle: .module,
                              comment: "Meeting notes error. %lld is an HTTP status code.")
            }
            let detail = Self.apiErrorMessage(in: excerpt) ?? excerpt
            return String(localized: "API HTTP Error \(code): \(detail)", bundle: .module,
                          comment: "Meeting notes error. %1$lld is an HTTP status code, %2$@ the server's message.")
        case .invalidJSON(let excerpt):
            return String(localized: "Parsing Error: The API response is not valid JSON: \(excerpt)",
                          bundle: .module, comment: "Meeting notes error. %@ is the start of the server's reply.")
        case .emptyContent:
            return String(localized: "Parsing Error: API response did not contain usable message content.",
                          bundle: .module, comment: "Meeting notes error")
        }
    }

    /// `error.message` of an OpenAI-style error body, when present.
    static func apiErrorMessage(in body: String) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: Data(body.utf8)) as? [String: Any],
              let error = object["error"] as? [String: Any] else { return nil }
        if let message = error["message"] as? String { return message }
        return String(describing: error)
    }
}

/// Something that answers a system + user message pair. `NotesPipeline`
/// depends on this so tests can inject a fake.
public protocol ChatCompleting: Sendable {
    func complete(
        systemMessage: String,
        userMessage: String,
        configuration: AIProviderConfiguration,
        token: String?
    ) async throws -> String
}

/// OpenAI-compatible `/chat/completions` client (port of the API branch of
/// Python `generate_meeting_notes` and `_post_chat_completions`).
///
/// Payload `{model, messages, temperature?, reasoning_effort?}`. Python always
/// sends both; here `temperature` is omitted when nil and `reasoning_effort`
/// when empty or unsupported by the preset.
public actor ChatCompletionsClient: ChatCompleting {
    /// Python `urlopen(request, timeout=300)`.
    public static let timeout: TimeInterval = 300

    private let session: URLSession

    public init(sessionConfiguration: URLSessionConfiguration = .ephemeral) {
        let configuration = sessionConfiguration
        configuration.timeoutIntervalForRequest = Self.timeout
        configuration.timeoutIntervalForResource = Self.timeout
        self.session = URLSession(configuration: configuration)
    }

    /// The request `complete` sends. Exposed for tests.
    public static func makeRequest(
        systemMessage: String,
        userMessage: String,
        configuration: AIProviderConfiguration,
        token: String?
    ) throws -> URLRequest {
        let trimmedToken = token?.trimmingCharacters(in: .whitespacesAndNewlines)
        let usableToken = (trimmedToken?.isEmpty ?? true) ? nil : trimmedToken
        if configuration.auth.needsToken && usableToken == nil {
            throw ChatCompletionsError.missingToken
        }
        let address = configuration.baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: address),
              let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = url.host, !host.isEmpty else {
            throw ChatCompletionsError.invalidURL
        }

        var payload: [String: Any] = [
            "model": configuration.model,
            "messages": [
                ["role": "system", "content": systemMessage],
                ["role": "user", "content": userMessage],
            ],
        ]
        if let temperature = configuration.temperature {
            payload["temperature"] = temperature
        }
        if let effort = configuration.effectiveReasoningEffort {
            payload["reasoning_effort"] = effort
        }

        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.httpBody = try JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        switch configuration.auth {
        case .bearer:
            if let usableToken { request.setValue("Bearer \(usableToken)", forHTTPHeaderField: "Authorization") }
        case .apiKey:
            if let usableToken { request.setValue(usableToken, forHTTPHeaderField: "api-key") }
        case .none:
            break
        }
        for (name, value) in configuration.extraHeaders.sorted(by: { $0.key < $1.key }) {
            let trimmedName = name.trimmingCharacters(in: .whitespaces)
            guard !trimmedName.isEmpty else { continue }
            request.setValue(value, forHTTPHeaderField: trimmedName)
        }
        return request
    }

    public func complete(
        systemMessage: String,
        userMessage: String,
        configuration: AIProviderConfiguration,
        token: String?
    ) async throws -> String {
        let request = try Self.makeRequest(
            systemMessage: systemMessage,
            userMessage: userMessage,
            configuration: configuration,
            token: token
        )
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError where Self.unreachableCodes.contains(error.code) {
            throw ChatCompletionsError.unreachable(host: request.url?.host ?? configuration.baseURL)
        } catch {
            throw ChatCompletionsError.transport(error.localizedDescription)
        }
        guard let http = response as? HTTPURLResponse else {
            throw ChatCompletionsError.transport(String(
                localized: "The server did not return an HTTP response.", bundle: .module,
                comment: "Meeting notes error detail, shown after 'API call failed: …'"))
        }
        return try Self.content(status: http.statusCode, body: data)
    }

    /// URL errors that mean the host itself cannot be reached, reported with
    /// the host name instead of the system's generic wording.
    static let unreachableCodes: Set<URLError.Code> = [
        .cannotFindHost, .cannotConnectToHost, .notConnectedToInternet,
    ]

    /// Port of the status and body checks after `_post_chat_completions`.
    static func content(status: Int, body: Data) throws -> String {
        let text = String(decoding: body, as: UTF8.self)
        let excerpt = String(text.prefix(ChatCompletionsError.excerptLength))
        guard (200..<300).contains(status) else {
            throw ChatCompletionsError.httpStatus(code: status, bodyExcerpt: excerpt)
        }
        let object: Any
        do {
            object = try JSONSerialization.jsonObject(with: body, options: [.fragmentsAllowed])
        } catch {
            throw ChatCompletionsError.invalidJSON(bodyExcerpt: excerpt)
        }
        guard let root = object as? [String: Any] else {
            throw ChatCompletionsError.emptyContent
        }
        if let choices = root["choices"] as? [Any], let first = choices.first {
            guard let choice = first as? [String: Any],
                  let message = choice["message"] as? [String: Any],
                  let content = message["content"] as? String,
                  !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw ChatCompletionsError.emptyContent
            }
            return content
        }
        if root["error"] is [String: Any] {
            throw ChatCompletionsError.httpStatus(code: status, bodyExcerpt: excerpt)
        }
        throw ChatCompletionsError.emptyContent
    }
}
