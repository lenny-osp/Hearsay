import Foundation
import Testing
@testable import HearsayCore

private let claudePath = "/Users/test/.local/bin/claude"
private let codexPath = "/Users/test/.local/bin/codex"

private func cliLocator() -> CLILocator {
    locator(found: [claudePath, codexPath])
}

private func claudeConfiguration(model: String = "claude-sonnet-5", effort: String? = "high") -> AIProviderConfiguration {
    var configuration = AIProviderConfiguration(preset: .claudeCodeCLI)
    configuration.model = model
    configuration.reasoningEffort = effort
    configuration.claudeCodePath = claudePath
    return configuration
}

private func codexConfiguration(model: String = "gpt-6-luna", effort: String? = "max") -> AIProviderConfiguration {
    var configuration = AIProviderConfiguration(preset: .codexCLI)
    configuration.model = model
    configuration.reasoningEffort = effort
    configuration.codexPath = codexPath
    return configuration
}

/// The value after `flag` in `argv`, or nil.
private func value(after flag: String, in argv: [String]) -> String? {
    guard let index = argv.firstIndex(of: flag), index + 1 < argv.count else { return nil }
    return argv[index + 1]
}

struct ClaudeCodeCLITests {
    // MARK: - Preset

    @Test func presetShape() {
        let preset = ProviderPreset.claudeCodeCLI
        #expect(preset.id == "claudeCodeCLI")
        #expect(preset.name == "Claude Code CLI (Claude subscription)")
        #expect(preset.kind == .claudeCodeCLI)
        #expect(preset.kind.cliTool == .claudeCode)
        #expect(preset.defaultModel == "claude-sonnet-5")
        #expect(preset.defaultEffort == "high")
        #expect(preset.auth == AuthHeaderStyle.none)
        #expect(preset.supportsReasoningEffort)
        #expect(!preset.supportsTemperature)
        #expect(ProviderPreset.all[1] == preset)
    }

    // MARK: - argv

    @Test func argvForNamedModel() {
        let argv = CLIArguments.claudeCode(
            executable: claudePath, prompt: "PROMPT", systemMessage: "SYSTEM", model: "opus", reasoningEffort: "high"
        )
        #expect(argv == [
            claudePath, "--print", "--output-format", "text",
            "--tools", "", "--setting-sources", "", "--strict-mcp-config",
            "--no-session-persistence", "--permission-mode", "dontAsk",
            "--disable-slash-commands", "--no-chrome",
            "--system-prompt", "SYSTEM",
            "--model", "opus", "--effort", "high",
            "--", "PROMPT",
        ])
    }

    @Test func argvForPresetDefaults() {
        let configuration = AIProviderConfiguration(preset: .claudeCodeCLI)
        let argv = CLIArguments.claudeCode(
            executable: claudePath, prompt: "P", systemMessage: MeetingPrompt.systemMessage,
            model: configuration.model, reasoningEffort: configuration.reasoningEffort
        )
        #expect(value(after: "--model", in: argv) == "claude-sonnet-5")
        #expect(value(after: "--effort", in: argv) == "high")
        #expect(value(after: "--system-prompt", in: argv) == MeetingPrompt.systemMessage)
        #expect(!argv.contains("--temperature"))
        #expect(argv.suffix(2) == ["--", "P"])
    }

    /// An empty model or effort leaves the choice to the CLI.
    @Test func emptyModelAndEffortAreOmitted() {
        let argv = CLIArguments.claudeCode(
            executable: claudePath, prompt: "-starts with a dash", systemMessage: "", model: "  ", reasoningEffort: " "
        )
        #expect(!argv.contains("--model"))
        #expect(!argv.contains("--effort"))
        #expect(!argv.contains("--system-prompt"))
        #expect(argv.suffix(2) == ["--", "-starts with a dash"])
    }

    @Test func effortMapping() {
        let expected: [String: String?] = [
            "none": "low", "minimal": "low", "low": "low", "medium": "medium",
            "high": "high", "xhigh": "xhigh", "max": "max", " MAX ": "max",
            "": nil, "turbo": nil,
        ]
        for (input, output) in expected {
            #expect(CLIArguments.claudeCodeEffort(input) == output, "\(input)")
        }
        #expect(CLIArguments.claudeCodeEffort(nil) == nil)
    }

    @Test func environmentDropsAPIKeysAndNestedSessionVariables() {
        let environment = CLIClient.environment(
            for: .claudeCode, executable: claudePath,
            base: [
                "PATH": "/usr/bin:/bin", "HOME": "/Users/test", "ANTHROPIC_API_KEY": "sk", "ANTHROPIC_AUTH_TOKEN": "t",
                "CLAUDECODE": "1", "CLAUDE_CODE_ENTRYPOINT": "cli", "CLAUDE_CONFIG_DIR": "/Users/test/.claude",
            ]
        )
        #expect(environment == [
            "PATH": "/Users/test/.local/bin:/usr/bin:/bin", "HOME": "/Users/test",
            "CLAUDE_CONFIG_DIR": "/Users/test/.claude",
        ])
    }

    // MARK: - Runs

    @Test func successReturnsStdoutAndRunsInATemporaryFolder() async throws {
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 0, stdout: launchNotes, stderr: "") }
        let pipeline = NotesPipeline(cliClient: CLIClient(runner: fake.runner, locator: cliLocator()))
        let response = try await pipeline.generate(
            srtText: "\n" + copilotSRT + "\n", languageCode: "en", template: .generalMeeting,
            configuration: claudeConfiguration(), token: "ignored"
        )
        #expect(response.filename == "Product Launch Plan")
        let call = try #require(fake.calls.first)
        #expect(fake.calls.count == 1)
        let prompt = try MeetingPrompt.build(
            transcript: copilotSRT.trimmingCharacters(in: .whitespacesAndNewlines), languageCode: "en"
        )
        #expect(call.argv == CLIArguments.claudeCode(
            executable: claudePath, prompt: prompt, systemMessage: MeetingPrompt.systemMessage,
            model: "claude-sonnet-5", reasoningEffort: "high"
        ))
        #expect(!call.argv.contains("ignored"))
        #expect(call.timeout == 600)
        #expect(call.directory.lastPathComponent.hasPrefix("Hearsay-claudeCode-"))
        #expect(call.environment["PATH"]?.hasPrefix("/Users/test/.local/bin:") == true)
        #expect(!FileManager.default.fileExists(atPath: call.directory.path))
    }

    /// Claude Code prints "Not logged in · Please run /login" on stdout and
    /// exits 1.
    @Test func notLoggedInIsDetectedFromStdout() async throws {
        let fake = FakeCLIRunner { _ in
            CLIRunResult(exitCode: 1, stdout: "Not logged in · Please run /login\n", stderr: "")
        }
        let client = CLIClient(runner: fake.runner, locator: cliLocator())
        do {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: claudeConfiguration(), token: nil)
            Issue.record("expected notLoggedIn")
        } catch let error as CLIProviderError {
            #expect(error == .notLoggedIn(.claudeCode, excerpt: "Not logged in · Please run /login\n"))
            let message = error.errorDescription ?? ""
            #expect(message.contains("Run `claude` once in Terminal"))
            #expect(message.contains("Claude subscription"))
        }
    }

    @Test func nonzeroExitKeepsAStderrExcerpt() async throws {
        let stderr = "API Error: model not found " + String(repeating: "y", count: 700)
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 2, stdout: "", stderr: stderr) }
        let client = CLIClient(runner: fake.runner, locator: cliLocator())
        await #expect(throws: CLIProviderError.failed(.claudeCode, exitCode: 2, excerpt: String(stderr.prefix(500)))) {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: claudeConfiguration(), token: nil)
        }
        let message = CLIProviderError.failed(.claudeCode, exitCode: 2, excerpt: "bad model").errorDescription ?? ""
        #expect(message.contains("Claude Code CLI call failed (exit code 2)"))
        #expect(message.contains("bad model"))
        #expect(!message.contains("log in"))
    }

    @Test func timeoutEmptyOutputAndLaunchFailure() async {
        struct Boom: Error, LocalizedError { var errorDescription: String? { "boom" } }
        let cases: [(CLIRunResult?, CLIProviderError)] = [
            (CLIRunResult(exitCode: 15, stdout: "partial", stderr: "", timedOut: true), .timedOut(.claudeCode)),
            (CLIRunResult(exitCode: 0, stdout: "\n ", stderr: ""), .emptyOutput(.claudeCode)),
            (nil, .launchFailed(.claudeCode, "boom")),
        ]
        for (result, expected) in cases {
            let fake = FakeCLIRunner { _ in
                guard let result else { throw Boom() }
                return result
            }
            let client = CLIClient(runner: fake.runner, locator: cliLocator())
            await #expect(throws: expected) {
                _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: claudeConfiguration(), token: nil)
            }
        }
        #expect(CLIProviderError.timedOut(.claudeCode).errorDescription == "Claude Code CLI did not answer within 10 minutes and was stopped.")
    }

    @Test func notInstalledNamesTheInstallCommand() async {
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: "") }
        let client = CLIClient(runner: fake.runner, locator: locator(found: []))
        var configuration = claudeConfiguration()
        configuration.claudeCodePath = nil
        do {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: configuration, token: nil)
            Issue.record("expected notInstalled")
        } catch let error as CLIProviderError {
            guard case .notInstalled(.claudeCode, let searched) = error else {
                Issue.record("unexpected \(error)")
                return
            }
            #expect(Array(searched.prefix(3)) == [
                "/Users/test/.local/bin/claude", "/opt/homebrew/bin/claude", "/usr/local/bin/claude",
            ])
            #expect(searched.last == "zsh -lc 'command -v claude'")
            #expect(error.errorDescription?.contains("curl -fsSL https://claude.ai/install.sh | bash") == true)
        } catch {
            Issue.record("unexpected \(error)")
        }
        #expect(fake.calls.map(\.argv) == [["/bin/zsh", "-lc", "command -v claude"]])
    }

    // MARK: - Check

    @Test func checkRunsVersionAndAuthStatus() async throws {
        let fake = FakeCLIRunner { argv in
            switch Array(argv.dropFirst()) {
            case ["--version"]:
                return CLIRunResult(exitCode: 0, stdout: "2.1.278 (Claude Code)\n", stderr: "")
            case ["auth", "status"]:
                return CLIRunResult(
                    exitCode: 0,
                    stdout: #"{"loggedIn": true, "authMethod": "claude.ai", "email": "a@b.test", "subscriptionType": "max"}"#,
                    stderr: ""
                )
            default:
                Issue.record("unexpected \(argv)")
                return CLIRunResult(exitCode: 1, stdout: "", stderr: "")
            }
        }
        let pipeline = NotesPipeline(cliClient: CLIClient(runner: fake.runner, locator: cliLocator()))
        let installation = try await pipeline.checkInstallation(configuration: claudeConfiguration())
        #expect(installation == CLIInstallation(
            tool: .claudeCode, path: claudePath, version: "2.1.278 (Claude Code)",
            loginStatus: "Logged in via claude.ai (max plan)", loggedIn: true
        ))
        #expect(fake.calls.map(\.argv) == [[claudePath, "--version"], [claudePath, "auth", "status"]])
    }

    @Test func authStatusLoggedOut() {
        let status = CLIClient.loginStatus(
            tool: .claudeCode,
            result: CLIRunResult(exitCode: 1, stdout: #"{"loggedIn": false, "authMethod": "none"}"#, stderr: "")
        )
        #expect(status.loggedIn == false)
        #expect(status.text == "Not logged in. Run `claude` once in Terminal to log in.")
    }
}

struct CodexCLITests {
    // MARK: - Preset

    @Test func presetShape() {
        let preset = ProviderPreset.codexCLI
        #expect(preset.id == "codexCLI")
        #expect(preset.name == "Codex CLI (ChatGPT subscription)")
        #expect(preset.kind == .codexCLI)
        #expect(preset.kind.cliTool == .codex)
        #expect(preset.defaultModel == "gpt-6-luna")
        #expect(preset.defaultEffort == "max")
        #expect(preset.auth == AuthHeaderStyle.none)
        #expect(preset.supportsReasoningEffort)
        #expect(!preset.supportsTemperature)
        #expect(ProviderPreset.all[2] == preset)
    }

    // MARK: - argv

    @Test func argvForNamedModel() {
        let argv = CLIArguments.codex(
            executable: codexPath, prompt: "PROMPT", model: "gpt-6-astra", reasoningEffort: "high",
            workingDirectory: "/tmp/w", outputFile: "/tmp/w/last-message.txt"
        )
        #expect(argv == [
            codexPath, "exec", "--sandbox", "read-only", "--cd", "/tmp/w",
            "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules",
            "--color", "never", "--output-last-message", "/tmp/w/last-message.txt",
            "--model", "gpt-6-astra", "-c", "model_reasoning_effort=\"high\"",
            "--", "PROMPT",
        ])
    }

    @Test func argvForPresetDefaults() {
        let configuration = AIProviderConfiguration(preset: .codexCLI)
        let argv = CLIArguments.codex(
            executable: codexPath, prompt: "P", model: configuration.model,
            reasoningEffort: configuration.reasoningEffort, workingDirectory: "/w", outputFile: "/w/o"
        )
        #expect(value(after: "--model", in: argv) == "gpt-6-luna")
        #expect(value(after: "-c", in: argv) == "model_reasoning_effort=\"max\"")
        #expect(!argv.contains { $0.contains("temperature") })
        #expect(argv.suffix(2) == ["--", "P"])
    }

    /// An empty model omits `--model`, so the CLI's own default applies.
    @Test func argvForEmptyModel() {
        let argv = CLIArguments.codex(
            executable: codexPath, prompt: "P", model: " ", reasoningEffort: nil, workingDirectory: "/w", outputFile: "/w/o"
        )
        #expect(!argv.contains("--model"))
        #expect(!argv.contains("-c"))
    }

    @Test func effortMapping() {
        for effort in ["none", "minimal", "low", "medium", "high", "xhigh", "max"] {
            #expect(CLIArguments.codexEffort(effort) == effort)
        }
        #expect(CLIArguments.codexEffort(" High ") == "high")
        #expect(CLIArguments.codexEffort("") == nil)
        #expect(CLIArguments.codexEffort(nil) == nil)
        #expect(CLIArguments.codexEffort("x\" = 1") == nil)
        let argv = CLIArguments.codex(
            executable: codexPath, prompt: "P", model: "", reasoningEffort: "turbo", workingDirectory: "/w", outputFile: "/w/o"
        )
        #expect(!argv.contains("-c"))
    }

    @Test func environmentDropsAPIKeys() {
        let environment = CLIClient.environment(
            for: .codex, executable: codexPath,
            base: ["PATH": "/usr/bin", "OPENAI_API_KEY": "sk", "CODEX_API_KEY": "k", "CODEX_HOME": "/c"]
        )
        #expect(environment == ["PATH": "/Users/test/.local/bin:/usr/bin", "CODEX_HOME": "/c"])
    }

    // MARK: - Reply file

    @Test func replyIsReadFromTheOutputFileNotStdout() async throws {
        let fake = FakeCLIRunner(withDirectory: { argv, directory in
            let file = try #require(value(after: "--output-last-message", in: argv))
            #expect(file == directory.appendingPathComponent("last-message.txt").path)
            #expect(value(after: "--cd", in: argv) == directory.path)
            try launchNotes.write(toFile: file, atomically: true, encoding: .utf8)
            return CLIRunResult(exitCode: 0, stdout: "codex\nsomething else\ntokens used\n1,234\n", stderr: "banner")
        })
        let pipeline = NotesPipeline(cliClient: CLIClient(runner: fake.runner, locator: cliLocator()))
        let response = try await pipeline.generate(
            srtText: copilotSRT, languageCode: "en", template: .generalMeeting,
            configuration: codexConfiguration(), token: "ignored"
        )
        #expect(response.filename == "Product Launch Plan")
        let call = try #require(fake.calls.first)
        #expect(call.argv.last == (try MeetingPrompt.build(
            transcript: copilotSRT.trimmingCharacters(in: .whitespacesAndNewlines), languageCode: "en"
        )))
        #expect(call.directory.lastPathComponent.hasPrefix("Hearsay-codex-"))
        #expect(!FileManager.default.fileExists(atPath: call.directory.path))
    }

    @Test func missingOrEmptyReplyFileIsEmptyOutputEvenWithStdout() async {
        for contents in [nil, " \n"] as [String?] {
            let fake = FakeCLIRunner(withDirectory: { argv, _ in
                if let contents, let file = value(after: "--output-last-message", in: argv) {
                    try contents.write(toFile: file, atomically: true, encoding: .utf8)
                }
                return CLIRunResult(exitCode: 0, stdout: launchNotes, stderr: "")
            })
            let client = CLIClient(runner: fake.runner, locator: cliLocator())
            await #expect(throws: CLIProviderError.emptyOutput(.codex)) {
                _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: codexConfiguration(), token: nil)
            }
        }
    }

    // MARK: - Errors

    /// Without a login every request ends in "401 Unauthorized"; stderr also
    /// carries the banner and "tokens used", which must not count as a hint.
    @Test func unauthorizedMapsToNotLoggedInWithTheLastErrorLine() async throws {
        let stderr = """
        OpenAI Codex v0.156.0
        --------
        model: gpt-6-astra
        --------
        user
        prompt
        ERROR: Reconnecting... 5/5
        ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header
        """
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: stderr) }
        let client = CLIClient(runner: fake.runner, locator: cliLocator())
        do {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: codexConfiguration(), token: nil)
            Issue.record("expected notLoggedIn")
        } catch let error as CLIProviderError {
            #expect(error == .notLoggedIn(
                .codex, excerpt: "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header"
            ))
            #expect(error.errorDescription?.contains("Run `codex login` once in Terminal") == true)
        }
    }

    @Test func otherFailureKeepsTheLastErrorLine() async {
        let stderr = "OpenAI Codex v0.156.0\ntokens used\nERROR: model 'nope' does not exist\n"
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: stderr) }
        let client = CLIClient(runner: fake.runner, locator: cliLocator())
        await #expect(throws: CLIProviderError.failed(.codex, exitCode: 1, excerpt: "ERROR: model 'nope' does not exist")) {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: codexConfiguration(), token: nil)
        }
        let noErrorLine = CLIClient.excerpt(
            tool: .codex, result: CLIRunResult(exitCode: 1, stdout: "", stderr: String(repeating: "z", count: 600) + "END")
        )
        #expect(noErrorLine.count == 500)
        #expect(noErrorLine.hasSuffix("END"))
    }

    @Test func timeoutAndNotInstalled() async {
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 15, stdout: "", stderr: "", timedOut: true) }
        let client = CLIClient(runner: fake.runner, locator: cliLocator())
        await #expect(throws: CLIProviderError.timedOut(.codex)) {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: codexConfiguration(), token: nil)
        }
        await #expect(throws: CLIProviderError.notInstalled(.codex, searched: ["/nowhere/codex"])) {
            _ = try await client.locate(.codex, configuredPath: "/nowhere/codex")
        }
        #expect(CLIProviderError.notInstalled(.codex, searched: []).errorDescription?
            .contains("npm install -g @openai/codex") == true)
    }

    // MARK: - Check

    @Test func checkRunsVersionAndLoginStatus() async throws {
        let fake = FakeCLIRunner { argv in
            switch Array(argv.dropFirst()) {
            case ["--version"]: CLIRunResult(exitCode: 0, stdout: "codex-cli 0.156.0\n", stderr: "")
            case ["login", "status"]: CLIRunResult(exitCode: 0, stdout: "", stderr: "Logged in using ChatGPT\n")
            default: CLIRunResult(exitCode: 1, stdout: "", stderr: "")
            }
        }
        let pipeline = NotesPipeline(cliClient: CLIClient(runner: fake.runner, locator: cliLocator()))
        let installation = try await pipeline.checkInstallation(configuration: codexConfiguration())
        #expect(installation == CLIInstallation(
            tool: .codex, path: codexPath, version: "codex-cli 0.156.0",
            loginStatus: "Logged in using ChatGPT", loggedIn: true
        ))
        #expect(fake.calls.map(\.argv) == [[codexPath, "--version"], [codexPath, "login", "status"]])

        let loggedOut = CLIClient.loginStatus(
            tool: .codex, result: CLIRunResult(exitCode: 1, stdout: "", stderr: "Not logged in\n")
        )
        #expect(loggedOut.loggedIn == false)
        #expect(loggedOut.text == "Not logged in. Run `codex login` once in Terminal to log in.")
    }
}

// MARK: - Routing

struct CLIRoutingTests {
    @Test func everyCLIPresetGoesToTheCLIClientAndHTTPPresetsDoNot() async throws {
        let fake = FakeCLIRunner(withDirectory: { argv, directory in
            if argv.contains("--json-schema") {
                return CLIRunResult(exitCode: 0, stdout: antigravityEnvelope(response: launchNotes), stderr: "")
            }
            if argv.contains("exec") {
                try launchNotes.write(
                    to: directory.appendingPathComponent(CLIArguments.codexReplyFileName), atomically: true, encoding: .utf8
                )
                return CLIRunResult(exitCode: 0, stdout: "", stderr: "")
            }
            return CLIRunResult(exitCode: 0, stdout: launchNotes, stderr: "")
        })
        let http = RecordingChatClient()
        let pipeline = NotesPipeline(
            client: http,
            cliClient: CLIClient(
                runner: fake.runner, locator: locator(found: ["/usr/bin/copilot", claudePath, codexPath, agyPath]),
                antigravity: fakeHousekeeping()
            )
        )
        var copilot = AIProviderConfiguration(preset: .copilotCLI)
        copilot.copilotPath = "/usr/bin/copilot"
        var antigravity = AIProviderConfiguration(preset: .antigravityCLI)
        antigravity.antigravityPath = agyPath
        for configuration in [copilot, claudeConfiguration(), codexConfiguration(), antigravity] {
            #expect(pipeline.client(for: configuration) is CLIClient)
            _ = try await pipeline.generate(
                srtText: copilotSRT, languageCode: "en", template: .generalMeeting,
                configuration: configuration, token: "t"
            )
        }
        #expect(fake.calls.map { $0.argv[0] } == ["/usr/bin/copilot", claudePath, codexPath, agyPath])
        #expect(await http.count == 0)

        for preset in [ProviderPreset.ollama, .custom] {
            #expect(pipeline.client(for: AIProviderConfiguration(preset: preset)) is RecordingChatClient)
        }
        await #expect(throws: CLIProviderError.notACLIPreset("Custom")) {
            _ = try await pipeline.checkInstallation(configuration: AIProviderConfiguration(preset: .custom))
        }
    }
}
