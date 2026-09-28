import Foundation

/// The argv of one notes request for each CLI, with the resolved binary as
/// argv[0].
public enum CLIArguments {
    // MARK: - GitHub Copilot CLI

    /// Python `copilot_argv`. An empty model or effort falls back to the
    /// defaults, as Python's `or` does; model `auto` omits `--model`.
    public static func copilot(
        executable: String,
        prompt: String,
        model: String,
        reasoningEffort: String?
    ) -> [String] {
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        let targetModel = trimmedModel.isEmpty ? ProviderPreset.defaultOpenAIModel : trimmedModel
        let trimmedEffort = reasoningEffort?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let effort = trimmedEffort.isEmpty ? ProviderPreset.defaultReasoningEffort : trimmedEffort
        var argv = [
            executable,
            "--prompt", prompt,
            "--silent",
            "--no-color",
            "--no-ask-user",
            "--no-auto-update",
            "--output-format", "text",
            "--reasoning-effort", effort,
        ]
        if targetModel != "auto" {
            argv += ["--model", targetModel]
        }
        return argv
    }

    // MARK: - Claude Code

    /// `claude --print` with no tools, none of the user's settings, hooks,
    /// CLAUDE.md, MCP servers, or skills, and no saved session. The system
    /// message replaces Claude Code's agent prompt. The prompt follows `--`
    /// so neither a leading dash nor the variadic `--tools` can swallow it.
    /// An empty model omits `--model` (the CLI's default); the effort goes
    /// through `claudeCodeEffort`.
    public static func claudeCode(
        executable: String,
        prompt: String,
        systemMessage: String,
        model: String,
        reasoningEffort: String?
    ) -> [String] {
        var argv = [
            executable,
            "--print",
            "--output-format", "text",
            "--tools", "",
            "--setting-sources", "",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--permission-mode", "dontAsk",
            "--disable-slash-commands",
            "--no-chrome",
        ]
        let system = systemMessage.trimmingCharacters(in: .whitespacesAndNewlines)
        if !system.isEmpty {
            argv += ["--system-prompt", systemMessage]
        }
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedModel.isEmpty {
            argv += ["--model", trimmedModel]
        }
        if let effort = claudeCodeEffort(reasoningEffort) {
            argv += ["--effort", effort]
        }
        return argv + ["--", prompt]
    }

    /// `claude --effort` accepts low, medium, high, xhigh, max. Hearsay's
    /// "none" and "minimal" (OpenAI levels) become "low"; anything else, or
    /// an empty value, omits the flag so the CLI's default applies.
    public static func claudeCodeEffort(_ value: String?) -> String? {
        switch normalized(value) {
        case "none", "minimal", "low": "low"
        case "medium": "medium"
        case "high": "high"
        case "xhigh": "xhigh"
        case "max": "max"
        default: nil
        }
    }

    // MARK: - Codex

    /// The file `codex exec -o` writes the final reply to, inside the run's
    /// temporary folder.
    public static let codexReplyFileName = "last-message.txt"

    /// `codex exec` in a read-only sandbox rooted at the temporary folder,
    /// without `~/.codex/config.toml` (MCP servers, notify programs, the
    /// user's model) or `.rules` files, and without saving the session. The
    /// reply is read from `outputFile`, not stdout. An empty model omits
    /// `--model` (the CLI's default); the effort goes through `codexEffort`.
    public static func codex(
        executable: String,
        prompt: String,
        model: String,
        reasoningEffort: String?,
        workingDirectory: String,
        outputFile: String
    ) -> [String] {
        var argv = [
            executable,
            "exec",
            "--sandbox", "read-only",
            "--cd", workingDirectory,
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--color", "never",
            "--output-last-message", outputFile,
        ]
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedModel.isEmpty {
            argv += ["--model", trimmedModel]
        }
        if let effort = codexEffort(reasoningEffort) {
            argv += ["-c", "model_reasoning_effort=\"\(effort)\""]
        }
        return argv + ["--", prompt]
    }

    /// `model_reasoning_effort` accepts none, minimal, low, medium, high,
    /// xhigh, max (the OpenAI Responses API list), so Hearsay's values pass
    /// through. Anything else, or an empty value, omits the override.
    public static func codexEffort(_ value: String?) -> String? {
        let effort = normalized(value)
        return ["none", "minimal", "low", "medium", "high", "xhigh", "max"].contains(effort) ? effort : nil
    }

    // MARK: - Antigravity

    /// Files of one `agy` run, at the root of its temporary folder, outside
    /// the empty working folder the agent sees.
    public static let antigravitySchemaFileName = "reply-schema.json"
    public static let antigravityLogFileName = "agy.log"
    /// The agy project every Hearsay run belongs to, and the name of the
    /// empty working folder: `agy --new-project` names the project after it.
    public static let antigravityProjectName = "hearsay-notes"
    /// `--print-timeout`, a little below `CLIClient.timeout` (600 s) so agy
    /// stops on its own first.
    public static let antigravityPrintTimeout = "570s"
    /// The reply shape `MeetingPrompt.responseRules` asks for, enforced with
    /// `agy --json-schema`.
    public static let notesReplySchema = #"{"type":"object","required":["filename","markdown","transcript_markdown"],"properties":{"filename":{"type":"string"},"markdown":{"type":"string"},"transcript_markdown":{"type":"string"}},"additionalProperties":false}"#

    /// agy is an agent that explores its workspace and writes files unless
    /// told otherwise; in print mode a denied file write ends the run with
    /// no reply. Sent before the user message.
    public static let antigravityInstructions = "Answer in your reply only. Do not run commands, and do not read, search, or write files: everything you need is in this message."

    /// The `--print` text: the system message (agy has no system prompt
    /// option), `antigravityInstructions`, then the user message.
    public static func antigravityPrompt(systemMessage: String, userMessage: String) -> String {
        let system = systemMessage.trimmingCharacters(in: .whitespacesAndNewlines)
        let parts = (system.isEmpty ? [] : [system]) + [antigravityInstructions, userMessage]
        return parts.joined(separator: "\n\n")
    }

    /// `agy --print` with the reply schema enforced (which needs
    /// `--output-format json`), slash commands and skills off, the terminal
    /// sandbox on, a log file inside the run's folder, and every run in the
    /// `hearsay-notes` project: `--project` once it exists, else
    /// `--new-project` (which creates it, named after the working folder).
    /// agy has no flag to turn tools off; in print mode a tool call that
    /// needs a permission is denied at once, but commands in the user's own
    /// `permissions.allow` list still run. The prompt is attached to
    /// `--print=`, because agy takes the next argument as its value and
    /// rejects a prompt after `--`. `prompt` is `antigravityPrompt`'s
    /// result. An empty model
    /// omits `--model`; the effort goes through `antigravityEffort`.
    public static func antigravity(
        executable: String,
        prompt: String,
        model: String,
        reasoningEffort: String?,
        schemaFile: String,
        logFile: String,
        projectExists: Bool
    ) -> [String] {
        var argv = [
            executable,
            "--output-format", "json",
            "--json-schema", schemaFile,
            "--disable-slash-commands",
            "--sandbox",
            "--print-timeout", antigravityPrintTimeout,
            "--log-file", logFile,
        ]
        argv += projectExists ? ["--project", antigravityProjectName] : ["--new-project"]
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmedModel.isEmpty {
            argv += ["--model", trimmedModel]
        }
        if let effort = antigravityEffort(reasoningEffort, model: trimmedModel) {
            argv += ["--effort", effort]
        }
        return argv + ["--print=" + prompt]
    }

    /// `agy --effort` accepts low, medium, high, max. Hearsay's "none" and
    /// "minimal" become "low", "xhigh" becomes "max"; anything else, or an
    /// empty value, omits the flag. A model id that already names its effort
    /// (`gemini-3.8-flash-high`, `gpt-oss-120b-medium`) also omits it: agy
    /// rejects any other effort for such an id ("conflicts with
    /// --effort=low") and needs none for the matching one.
    public static func antigravityEffort(_ value: String?, model: String) -> String? {
        let suffix = model.lowercased().split(separator: "-").last.map(String.init) ?? ""
        if ["low", "medium", "high", "max"].contains(suffix) { return nil }
        switch normalized(value) {
        case "none", "minimal", "low": return "low"
        case "medium": return "medium"
        case "high": return "high"
        case "xhigh", "max": return "max"
        default: return nil
        }
    }

    /// Whether `~/.gemini/config/projects` has a project named
    /// `antigravityProjectName`. Only reads; a missing or unreadable folder
    /// counts as no.
    public static func antigravityProjectExists(homeDirectory: String = NSHomeDirectory()) -> Bool {
        let folder = (homeDirectory as NSString).appendingPathComponent(".gemini/config/projects")
        let files = (try? FileManager.default.contentsOfDirectory(atPath: folder)) ?? []
        return files.contains { file in
            guard file.hasSuffix(".json"),
                  let data = FileManager.default.contents(atPath: (folder as NSString).appendingPathComponent(file)),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
            return object["name"] as? String == antigravityProjectName
        }
    }

    private static func normalized(_ value: String?) -> String {
        value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? ""
    }
}

/// Meeting notes through a locally installed CLI that uses its own login:
/// GitHub Copilot CLI (port of the Copilot branch of Python
/// `generate_meeting_notes`), Claude Code, Codex, or Antigravity. The preset's kind picks
/// the tool. No token is sent; each run happens in a fresh temporary folder.
public actor CLIClient: ChatCompleting {
    /// Generous: a long meeting at reasoning effort "max" takes minutes.
    public static let timeout: TimeInterval = 600
    public static let versionTimeout: TimeInterval = 15

    private let runner: CLIRunner
    private let locator: CLILocator
    private let antigravityProjectExists: @Sendable () -> Bool

    public init(
        runner: @escaping CLIRunner = CLIProcessRunner.run,
        locator: CLILocator = CLILocator(),
        antigravityProjectExists: @escaping @Sendable () -> Bool = { CLIArguments.antigravityProjectExists() }
    ) {
        self.runner = runner
        self.locator = locator
        self.antigravityProjectExists = antigravityProjectExists
    }

    /// The inherited environment with the binary's folder first on PATH, so
    /// `#!/usr/bin/env node` finds the Node that installed it. For Claude
    /// Code, Codex, and Antigravity, API-key variables are removed so the
    /// subscription (or Google account) login is used, and so are Claude Code's own session variables (set
    /// when Hearsay was started from inside a Claude Code session).
    public static func environment(
        for tool: CLITool,
        executable: String,
        base: [String: String] = ProcessInfo.processInfo.environment
    ) -> [String: String] {
        var environment = base
        switch tool {
        case .copilot:
            break
        case .claudeCode:
            environment = environment.filter { key, _ in
                !(key == "ANTHROPIC_API_KEY" || key == "ANTHROPIC_AUTH_TOKEN"
                  || key == "CLAUDECODE" || key.hasPrefix("CLAUDE_CODE_"))
            }
        case .codex:
            environment["OPENAI_API_KEY"] = nil
            environment["CODEX_API_KEY"] = nil
        case .antigravity:
            environment["GEMINI_API_KEY"] = nil
            environment["GOOGLE_API_KEY"] = nil
            environment["GOOGLE_APPLICATION_CREDENTIALS"] = nil
        }
        let directory = (executable as NSString).deletingLastPathComponent
        let path = base["PATH"].flatMap { $0.isEmpty ? nil : $0 } ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        environment["PATH"] = directory + ":" + path
        return environment
    }

    /// The part of a failed run worth showing, at most
    /// `ChatCompletionsError.excerptLength` characters. Copilot: the start of
    /// stderr (Python parity). Claude Code prints its errors on stdout in
    /// `--print` mode, so stdout is used when stderr is empty. Codex starts
    /// stderr with a banner and the prompt, so its last `ERROR:` line (or the
    /// end of stderr) is used. Antigravity: its last `Error:`, `error:`,
    /// `jetski:`, or `AGY_ERROR` line on stderr (so the login URL it prints
    /// first is not shown), else the end of stderr, else stdout.
    public static func excerpt(tool: CLITool, result: CLIRunResult) -> String {
        let limit = ChatCompletionsError.excerptLength
        switch tool {
        case .copilot:
            return String(result.stderr.prefix(limit))
        case .claudeCode:
            let stderr = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            return String((stderr.isEmpty ? result.stdout : result.stderr).prefix(limit))
        case .codex:
            let errorLine = result.stderr
                .split(whereSeparator: \.isNewline)
                .last { $0.hasPrefix("ERROR:") }
            if let errorLine { return String(errorLine.prefix(limit)) }
            return String(result.stderr.suffix(limit))
        case .antigravity:
            let errorLine = result.stderr
                .split(whereSeparator: \.isNewline)
                .last { line in
                    let lowered = line.lowercased()
                    return lowered.hasPrefix("error") || lowered.hasPrefix("jetski:") || line.contains("AGY_ERROR")
                }
            if let errorLine { return String(errorLine.prefix(limit)) }
            let stderr = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            if !stderr.isEmpty { return String(stderr.suffix(limit)) }
            return String(result.stdout.prefix(limit))
        }
    }

    public func locate(_ tool: CLITool, configuredPath: String?) async throws -> String {
        try await locator.locate(tool, configuredPath: configuredPath, runner: runner)
    }

    public func complete(
        systemMessage: String,
        userMessage: String,
        configuration: AIProviderConfiguration,
        token: String?
    ) async throws -> String {
        let preset = configuration.preset
        guard let tool = preset.kind.cliTool else { throw CLIProviderError.notACLIPreset(preset.name) }
        let executable = try await locate(tool, configuredPath: configuration.cliPath(for: tool))
        let projectExists = tool == .antigravity && antigravityProjectExists()
        return try await run(tool, executable: executable, timeout: Self.timeout) { directory in
            guard tool == .antigravity else { return directory }
            // The agent sees an empty folder; the schema and log sit beside it.
            try Data(CLIArguments.notesReplySchema.utf8).write(
                to: directory.appendingPathComponent(CLIArguments.antigravitySchemaFileName)
            )
            let workspace = directory.appendingPathComponent(CLIArguments.antigravityProjectName, isDirectory: true)
            try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
            return workspace
        } argv: { directory in
            switch tool {
            case .copilot:
                CLIArguments.copilot(
                    executable: executable, prompt: userMessage,
                    model: configuration.model, reasoningEffort: configuration.reasoningEffort
                )
            case .claudeCode:
                CLIArguments.claudeCode(
                    executable: executable, prompt: userMessage, systemMessage: systemMessage,
                    model: configuration.model, reasoningEffort: configuration.reasoningEffort
                )
            case .codex:
                CLIArguments.codex(
                    executable: executable, prompt: userMessage,
                    model: configuration.model, reasoningEffort: configuration.reasoningEffort,
                    workingDirectory: directory.path,
                    outputFile: directory.appendingPathComponent(CLIArguments.codexReplyFileName).path
                )
            case .antigravity:
                CLIArguments.antigravity(
                    executable: executable,
                    prompt: CLIArguments.antigravityPrompt(systemMessage: systemMessage, userMessage: userMessage),
                    model: configuration.model, reasoningEffort: configuration.reasoningEffort,
                    schemaFile: directory.appendingPathComponent(CLIArguments.antigravitySchemaFileName).path,
                    logFile: directory.appendingPathComponent(CLIArguments.antigravityLogFileName).path,
                    projectExists: projectExists
                )
            }
        } finish: { result, directory in
            try Self.reply(tool: tool, result: result, directory: directory)
        }
    }

    /// The reply of a finished notes run, or the error it maps to.
    static func reply(tool: CLITool, result: CLIRunResult, directory: URL) throws -> String {
        if result.timedOut { throw CLIProviderError.timedOut(tool) }
        guard result.exitCode == 0 else {
            let excerpt = Self.excerpt(tool: tool, result: result)
            if CLIProviderError.indicatesLoggedOut(result.stderr + "\n" + result.stdout, tool: tool) {
                throw CLIProviderError.notLoggedIn(tool, excerpt: excerpt)
            }
            throw CLIProviderError.failed(tool, exitCode: result.exitCode, excerpt: excerpt)
        }
        let reply: String
        switch tool {
        case .copilot, .claudeCode:
            reply = result.stdout
        case .codex:
            let file = directory.appendingPathComponent(CLIArguments.codexReplyFileName)
            reply = (try? String(contentsOf: file, encoding: .utf8)) ?? ""
        case .antigravity:
            reply = try antigravityReply(result)
        }
        guard !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CLIProviderError.emptyOutput(tool)
        }
        return reply
    }

    /// The reply inside `agy --output-format json`: `structured_output` (the
    /// object the schema enforced, re-encoded) when present, else
    /// `response`. agy adds its own keys (`toolAction`, `toolSummary`) to
    /// `response` but not to `structured_output`. Output that is not such an
    /// envelope is returned as is; a `status` other than `SUCCESS` fails.
    static func antigravityReply(_ result: CLIRunResult) throws -> String {
        guard let object = try? JSONSerialization.jsonObject(with: Data(result.stdout.utf8)) as? [String: Any],
              ["status", "response", "structured_output"].contains(where: { object[$0] != nil }) else {
            return result.stdout
        }
        if let status = object["status"] as? String, status != "SUCCESS" {
            let detail = (object["error"] as? String).map { String($0.prefix(ChatCompletionsError.excerptLength)) }
            throw CLIProviderError.failed(
                .antigravity, exitCode: result.exitCode,
                excerpt: detail ?? excerpt(tool: .antigravity, result: result)
            )
        }
        if let structured = object["structured_output"], JSONSerialization.isValidJSONObject(structured),
           let data = try? JSONSerialization.data(withJSONObject: structured, options: [.sortedKeys]) {
            return String(decoding: data, as: UTF8.self)
        }
        return object["response"] as? String ?? ""
    }

    /// The binary's path, `--version` output, and login state, for
    /// Settings > AI.
    public func checkInstallation(_ tool: CLITool, configuredPath: String?) async throws -> CLIInstallation {
        let executable = try await locate(tool, configuredPath: configuredPath)
        let version = try await run(tool, executable: executable, timeout: Self.versionTimeout) { _ in
            [executable, "--version"]
        } finish: { result, _ in
            if result.timedOut { throw CLIProviderError.timedOut(tool) }
            guard result.exitCode == 0 else {
                throw CLIProviderError.failed(
                    tool, exitCode: result.exitCode, excerpt: Self.excerpt(tool: tool, result: result)
                )
            }
            return result.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        var installation = CLIInstallation(tool: tool, path: executable, version: version)
        if let statusArguments = tool.loginStatusArguments {
            let status = try await run(tool, executable: executable, timeout: tool.loginStatusTimeout) { _ in
                [executable] + statusArguments
            } finish: { result, _ in
                Self.loginStatus(tool: tool, result: result)
            }
            installation.loginStatus = status.text
            installation.loggedIn = status.loggedIn
        }
        return installation
    }

    /// One line from `claude auth status` (JSON), `codex login status`
    /// (plain text on stderr), or `agy models` (only whether it worked and
    /// how many models it listed). The account e-mail is not shown.
    static func loginStatus(tool: CLITool, result: CLIRunResult) -> (text: String, loggedIn: Bool) {
        let advice = "Run `\(tool.loginCommand)` once in Terminal to log in."
        if result.timedOut {
            return ("The login status check did not answer.", false)
        }
        switch tool {
        case .claudeCode:
            if let object = try? JSONSerialization.jsonObject(with: Data(result.stdout.utf8)) as? [String: Any],
               let loggedIn = object["loggedIn"] as? Bool {
                guard loggedIn else { return ("Not logged in. \(advice)", false) }
                var text = "Logged in"
                if let method = object["authMethod"] as? String, !method.isEmpty { text += " via \(method)" }
                if let plan = object["subscriptionType"] as? String, !plan.isEmpty { text += " (\(plan) plan)" }
                return (text, true)
            }
        case .antigravity:
            if result.exitCode == 0 {
                // One "<id>\t<label>" line per model after "Fetching available models...".
                let count = result.stdout.split(whereSeparator: \.isNewline).filter { $0.contains("\t") }.count
                return ("Logged in; \(count) model\(count == 1 ? "" : "s") available", true)
            }
            if CLIProviderError.indicatesLoggedOut(result.stdout + "\n" + result.stderr, tool: tool) {
                return ("Not logged in. \(advice)", false)
            }
            return ("`agy models` failed (exit code \(result.exitCode)). \(advice)", false)
        case .codex, .copilot:
            break
        }
        let output = [result.stdout, result.stderr]
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        let line = String(output.prefix(ChatCompletionsError.excerptLength))
        if result.exitCode == 0, !output.lowercased().contains("not logged in") {
            return (line.isEmpty ? "Logged in" : line, true)
        }
        return (line.isEmpty ? "Not logged in. \(advice)" : "\(line). \(advice)", false)
    }

    /// Runs in a fresh temporary folder, removed afterwards: the CLIs are
    /// agents that could use tools, so they are kept away from the user's
    /// files. `prepare` may add files and returns the working folder
    /// (default: the folder itself). `finish` reads anything it needs from
    /// the folder before it is removed.
    private func run<Value: Sendable>(
        _ tool: CLITool,
        executable: String,
        timeout: TimeInterval,
        prepare: (URL) throws -> URL = { $0 },
        argv makeArgv: (URL) -> [String],
        finish: (CLIRunResult, URL) throws -> Value
    ) async throws -> Value {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(tool.temporaryFolderPrefix)\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let workingDirectory: URL
        do {
            workingDirectory = try prepare(directory)
        } catch {
            throw CLIProviderError.launchFailed(tool, error.localizedDescription)
        }
        let result: CLIRunResult
        do {
            result = try await runner(
                makeArgv(directory), workingDirectory, Self.environment(for: tool, executable: executable), timeout
            )
        } catch let error as CLIProviderError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw CLIProviderError.launchFailed(tool, error.localizedDescription)
        }
        return try finish(result, directory)
    }
}
