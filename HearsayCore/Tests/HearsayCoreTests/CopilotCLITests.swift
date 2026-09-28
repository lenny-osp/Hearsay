import Foundation
import Testing
@testable import HearsayCore

/// Records every runner call and answers from a canned result.
private final class FakeRunner: @unchecked Sendable {
    struct Call {
        var argv: [String]
        var directory: URL
        var environment: [String: String]
        var timeout: TimeInterval
    }

    private let lock = NSLock()
    private var recorded: [Call] = []
    private let respond: @Sendable ([String]) throws -> CopilotCLIRunResult

    init(_ respond: @escaping @Sendable ([String]) throws -> CopilotCLIRunResult) {
        self.respond = respond
    }

    var calls: [Call] { lock.withLock { recorded } }

    var runner: CopilotCLIRunner {
        { [self] argv, directory, environment, timeout in
            lock.withLock {
                recorded.append(Call(argv: argv, directory: directory, environment: environment, timeout: timeout))
            }
            return try respond(argv)
        }
    }
}

private let copilotPath = "/usr/bin/copilot"

private func freshDefaults() -> UserDefaults {
    let suite = "hearsay.copilot.tests.\(UUID().uuidString)"
    return UserDefaults(suiteName: suite) ?? .standard
}

/// A locator that finds `copilotPath` only.
private func locator(found: Set<String> = [copilotPath]) -> CopilotCLILocator {
    CopilotCLILocator(
        homeDirectory: "/Users/test",
        isExecutable: { found.contains($0) },
        directoryContents: { _ in [] }
    )
}

private let copilotSRT = "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n"

private let launchNotes = ##"{"filename": "Product Launch Plan", "markdown": "# Notes\n", "transcript_markdown": "# Transcript\n\n## 00:00 — Launch\n"}"##

private func copilotConfiguration(model: String) -> AIProviderConfiguration {
    var configuration = AIProviderConfiguration(preset: .copilotCLI)
    configuration.model = model
    configuration.copilotPath = copilotPath
    return configuration
}

struct CopilotCLITests {
    // MARK: - Preset

    @Test func presetShape() {
        let preset = ProviderPreset.copilotCLI
        #expect(preset.id == "copilotCLI")
        #expect(preset.name == "GitHub Copilot CLI")
        #expect(preset.baseURL.isEmpty)
        #expect(preset.defaultModel == "gpt-5.6-luna")
        #expect(preset.auth == AuthHeaderStyle.none)
        #expect(preset.supportsReasoningEffort)
        #expect(preset.kind == .copilotCLI)
        #expect(ProviderPreset.all.first == preset)
        #expect(ProviderPreset.all.dropFirst().allSatisfy { $0.kind == .http })
    }

    @MainActor @Test func firstLaunchPicksCopilotOnlyWhenInstalled() {
        let withCopilot = AIProviderStore(
            defaults: freshDefaults(), secrets: InMemorySecretStore(),
            copilotInstalled: { true }
        )
        #expect(withCopilot.configuration == AIProviderConfiguration(preset: .copilotCLI))
        let without = AIProviderStore(
            defaults: freshDefaults(), secrets: InMemorySecretStore(),
            copilotInstalled: { false }
        )
        #expect(without.configuration == .default)
    }

    @MainActor @Test func firstLaunchChoiceIsSavedAndNotReconsidered() {
        let defaults = freshDefaults()
        _ = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore(), copilotInstalled: { false })
        let later = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore(), copilotInstalled: { true })
        #expect(later.configuration.presetID == "openai")
    }

    @MainActor @Test func copilotPathSurvivesReloadAndPresetSwitch() {
        let defaults = freshDefaults()
        let store = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore(), copilotInstalled: { true })
        store.configuration.copilotPath = "/custom/copilot"
        store.selectPreset(.openAI)
        store.selectPreset(.copilotCLI)
        let reloaded = AIProviderStore(defaults: defaults, secrets: InMemorySecretStore(), copilotInstalled: { false })
        #expect(reloaded.configuration.copilotPath == "/custom/copilot")
        #expect(reloaded.configuration.presetID == "copilotCLI")
    }

    // MARK: - argv (Python test_copilot_success_failure_and_auto_model)

    @Test func argvForNamedModel() {
        let argv = CopilotCLIClient.arguments(
            executable: copilotPath, prompt: "PROMPT", model: "gpt-5.6-luna", reasoningEffort: "max"
        )
        #expect(argv == [
            copilotPath, "--prompt", "PROMPT", "--silent", "--no-color", "--no-ask-user",
            "--no-auto-update", "--output-format", "text", "--reasoning-effort", "max",
            "--model", "gpt-5.6-luna",
        ])
    }

    @Test func argvForAutoModelOmitsModel() {
        let argv = CopilotCLIClient.arguments(
            executable: copilotPath, prompt: "PROMPT", model: "auto", reasoningEffort: "max"
        )
        #expect(argv == [
            copilotPath, "--prompt", "PROMPT", "--silent", "--no-color", "--no-ask-user",
            "--no-auto-update", "--output-format", "text", "--reasoning-effort", "max",
        ])
        #expect(!argv.contains("--model"))
    }

    /// Python `env.get("AI_MODEL") or DEFAULT_AI_MODEL` and the same for effort.
    @Test func emptyModelAndEffortFallBackToDefaults() {
        let argv = CopilotCLIClient.arguments(executable: copilotPath, prompt: "P", model: " ", reasoningEffort: nil)
        #expect(argv[argv.firstIndex(of: "--reasoning-effort").map { $0 + 1 } ?? 0] == "max")
        #expect(argv.suffix(2) == ["--model", "gpt-5.6-luna"])
    }

    @Test func environmentPrefixesBinaryFolderOnPath() {
        let environment = CopilotCLIClient.environment(
            executable: "/Users/x/.nvm/versions/node/v24.16.0/bin/copilot",
            base: ["PATH": "/usr/bin:/bin", "HOME": "/Users/x"]
        )
        #expect(environment["PATH"] == "/Users/x/.nvm/versions/node/v24.16.0/bin:/usr/bin:/bin")
        #expect(environment["HOME"] == "/Users/x")
    }

    // MARK: - Pipeline through the CLI

    @Test func successParsesFencedJSONReplyAndSendsOnlyTheUserPrompt() async throws {
        let fake = FakeRunner { _ in
            CopilotCLIRunResult(exitCode: 0, stdout: "```json\n\(launchNotes)\n```\n", stderr: "")
        }
        let pipeline = NotesPipeline(copilotClient: CopilotCLIClient(runner: fake.runner, locator: locator()))
        let response = try await pipeline.generate(
            srtText: "\n" + copilotSRT + "\n\n", languageCode: "en", template: .generalMeeting,
            configuration: copilotConfiguration(model: "auto"), token: "ignored"
        )
        #expect(response.filename == "Product Launch Plan")
        #expect(response.markdown == "# Notes\n")
        #expect(response.transcriptMarkdown == "# Transcript\n\n## 00:00 — Launch\n")

        let call = try #require(fake.calls.first)
        #expect(fake.calls.count == 1)
        #expect(!call.argv.contains("--model"))
        #expect(call.argv[call.argv.firstIndex(of: "--reasoning-effort").map { $0 + 1 } ?? 0] == "max")
        // Python: build_meeting_prompt(srt_text.strip(), language_code); no system message.
        let expectedPrompt = try MeetingPrompt.build(
            transcript: copilotSRT.trimmingCharacters(in: .whitespacesAndNewlines), languageCode: "en"
        )
        #expect(call.argv[2] == expectedPrompt)
        #expect(!call.argv.contains(MeetingPrompt.systemMessage))
        #expect(call.argv[0] == copilotPath)
        #expect(call.timeout == 600)
        #expect(call.directory.lastPathComponent.hasPrefix("Hearsay-copilot-"))
        #expect(call.environment["PATH"]?.hasPrefix("/usr/bin:") == true)
        // The temporary working folder is removed afterwards.
        #expect(!FileManager.default.fileExists(atPath: call.directory.path))
    }

    @Test func nonzeroExitMapsToFailedWithStderrExcerpt() async throws {
        let longStderr = "Error: not logged in. " + String(repeating: "x", count: 600)
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 1, stdout: "", stderr: longStderr) }
        let client = CopilotCLIClient(runner: fake.runner, locator: locator())
        do {
            _ = try await client.complete(
                systemMessage: "s", userMessage: "u", configuration: copilotConfiguration(model: "auto"), token: nil
            )
            Issue.record("expected failure")
        } catch let error as CopilotCLIError {
            #expect(error == .failed(exitCode: 1, stderrExcerpt: String(longStderr.prefix(500))))
            let message = error.errorDescription ?? ""
            #expect(message.contains("exit code 1"))
            #expect(message.contains("not logged in"))
            #expect(message.contains("Run `copilot` once in Terminal to log in."))
        }
    }

    @Test func failureWithoutLoginHintHasNoLoginAdvice() {
        let message = CopilotCLIError.failed(exitCode: 2, stderrExcerpt: "Unknown model").errorDescription ?? ""
        #expect(message.contains("Unknown model"))
        #expect(!message.contains("log in"))
    }

    @Test func emptyStdoutMapsToEmptyOutput() async {
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 0, stdout: " \n", stderr: "") }
        let client = CopilotCLIClient(runner: fake.runner, locator: locator())
        await #expect(throws: CopilotCLIError.emptyOutput) {
            _ = try await client.complete(
                systemMessage: "s", userMessage: "u", configuration: copilotConfiguration(model: "auto"), token: nil
            )
        }
    }

    @Test func timeoutMapsToTimedOut() async {
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 15, stdout: "partial", stderr: "", timedOut: true) }
        let client = CopilotCLIClient(runner: fake.runner, locator: locator())
        await #expect(throws: CopilotCLIError.timedOut) {
            _ = try await client.complete(
                systemMessage: "s", userMessage: "u", configuration: copilotConfiguration(model: "auto"), token: nil
            )
        }
    }

    @Test func launchErrorMapsToLaunchFailed() async {
        struct Boom: Error, LocalizedError { var errorDescription: String? { "boom" } }
        let fake = FakeRunner { _ in throw Boom() }
        let client = CopilotCLIClient(runner: fake.runner, locator: locator())
        await #expect(throws: CopilotCLIError.launchFailed("boom")) {
            _ = try await client.complete(
                systemMessage: "s", userMessage: "u", configuration: copilotConfiguration(model: "auto"), token: nil
            )
        }
    }

    // MARK: - Locating the binary

    @Test func missingBinaryMapsToNotInstalled() async {
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 1, stdout: "", stderr: "") }
        let client = CopilotCLIClient(runner: fake.runner, locator: locator(found: []))
        var configuration = copilotConfiguration(model: "auto")
        configuration.copilotPath = nil
        do {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: configuration, token: nil)
            Issue.record("expected notInstalled")
        } catch let error as CopilotCLIError {
            guard case .notInstalled(let searched) = error else {
                Issue.record("unexpected \(error)")
                return
            }
            #expect(searched.contains("/opt/homebrew/bin/copilot"))
            #expect(searched.contains("/usr/local/bin/copilot"))
            #expect(error.errorDescription?.contains("npm install -g @github/copilot") == true)
        } catch {
            Issue.record("unexpected \(error)")
        }
        // Only the login-shell lookup ran; copilot itself never did.
        #expect(fake.calls.map(\.argv) == [CopilotCLILocator.shellLookupArguments])
        #expect(fake.calls.first?.timeout == 5)
    }

    @Test func configuredPathThatDoesNotExistIsNotInstalled() async {
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 0, stdout: "/usr/bin/copilot\n", stderr: "") }
        let client = CopilotCLIClient(runner: fake.runner, locator: locator())
        await #expect(throws: CopilotCLIError.notInstalled(searched: ["/nowhere/copilot"])) {
            _ = try await client.locate(configuredPath: "/nowhere/copilot")
        }
        #expect(fake.calls.isEmpty)
    }

    @Test func searchOrderPrefersHomebrewThenUsrLocalThenNewestNvm() async throws {
        let nvm = "/Users/test/.nvm/versions/node"
        let versions = ["v9.11.2", "v24.16.0", "v24.2.0"]
        func make(_ found: Set<String>) -> CopilotCLILocator {
            CopilotCLILocator(
                homeDirectory: "/Users/test",
                isExecutable: { found.contains($0) },
                directoryContents: { $0 == nvm ? versions : [] }
            )
        }
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 1, stdout: "", stderr: "") }
        let allNvm = Set(versions.map { "\(nvm)/\($0)/bin/copilot" })
        #expect(try await make(allNvm).locate(configuredPath: nil, runner: fake.runner)
                == "\(nvm)/v24.16.0/bin/copilot")
        #expect(try await make(allNvm.union(["/usr/local/bin/copilot"])).locate(configuredPath: nil, runner: fake.runner)
                == "/usr/local/bin/copilot")
        #expect(try await make(allNvm.union(["/usr/local/bin/copilot", "/opt/homebrew/bin/copilot"]))
                    .locate(configuredPath: nil, runner: fake.runner) == "/opt/homebrew/bin/copilot")
        #expect(fake.calls.isEmpty)
        #expect(make(allNvm).knownInstallation() == "\(nvm)/v24.16.0/bin/copilot")
    }

    @Test func loginShellLookupIsTheLastResort() async throws {
        let fake = FakeRunner { _ in
            CopilotCLIRunResult(exitCode: 0, stdout: "welcome banner\n/opt/tools/copilot\n", stderr: "")
        }
        let found = try await locator(found: ["/opt/tools/copilot"]).locate(configuredPath: nil, runner: fake.runner)
        #expect(found == "/opt/tools/copilot")
    }

    @Test func checkInstallationReportsPathAndVersion() async throws {
        let fake = FakeRunner { argv in
            #expect(argv == [copilotPath, "--version"])
            return CopilotCLIRunResult(exitCode: 0, stdout: "1.0.78\n", stderr: "")
        }
        let pipeline = NotesPipeline(copilotClient: CopilotCLIClient(runner: fake.runner, locator: locator()))
        let installation = try await pipeline.checkInstallation(configuration: copilotConfiguration(model: "auto"))
        #expect(installation == CopilotInstallation(path: copilotPath, version: "1.0.78"))
    }

    // MARK: - Routing

    @Test func pipelineRoutesCopilotPresetToTheCLIAndOthersToHTTP() async throws {
        let fake = FakeRunner { _ in CopilotCLIRunResult(exitCode: 0, stdout: launchNotes, stderr: "") }
        let http = RecordingChatClient()
        let pipeline = NotesPipeline(client: http, copilotClient: CopilotCLIClient(runner: fake.runner, locator: locator()))

        _ = try await pipeline.generate(
            srtText: copilotSRT, languageCode: "en", template: .generalMeeting,
            configuration: copilotConfiguration(model: "gpt-5.6-luna"), token: nil
        )
        #expect(fake.calls.count == 1)
        #expect(await http.count == 0)

        var openAI = AIProviderConfiguration(preset: .openAI)
        openAI.copilotPath = copilotPath
        _ = try await pipeline.generate(
            srtText: copilotSRT, languageCode: "en", template: .generalMeeting,
            configuration: openAI, token: "t"
        )
        #expect(fake.calls.count == 1)
        #expect(await http.count == 1)
        #expect(pipeline.client(for: AIProviderConfiguration(preset: .copilotCLI)) is CopilotCLIClient)
        #expect(pipeline.client(for: openAI) is RecordingChatClient)
    }
}

private actor RecordingChatClient: ChatCompleting {
    private(set) var count = 0

    func complete(
        systemMessage: String, userMessage: String, configuration: AIProviderConfiguration, token: String?
    ) async throws -> String {
        count += 1
        return launchNotes
    }
}
