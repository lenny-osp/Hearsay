import Foundation
import Testing
@testable import HearsayCore

let agyPath = "/Users/test/.local/bin/agy"

/// What `agy --print --output-format json --json-schema …` prints on
/// success (shape observed with agy 1.2.12): `response` carries agy's own
/// extra keys, `structured_output` the schema-checked object.
func antigravityEnvelope(response: String, structured: String? = nil, status: String = "SUCCESS") -> String {
    var object: [String: Any] = [
        "conversation_id": "6cfd8496-ae02-41ac-afc4-1460ec1eaf0c",
        "status": status,
        "response": response,
        "duration_seconds": 24.2,
        "num_turns": 1,
    ]
    if let structured, let value = try? JSONSerialization.jsonObject(with: Data(structured.utf8)) {
        object["structured_output"] = value
    }
    let data = (try? JSONSerialization.data(withJSONObject: object)) ?? Data()
    return String(decoding: data, as: UTF8.self)
}

private func agyConfiguration(model: String = "gemini-3.8-flash-high", effort: String? = "high") -> AIProviderConfiguration {
    var configuration = AIProviderConfiguration(preset: .antigravityCLI)
    configuration.model = model
    configuration.reasoningEffort = effort
    configuration.antigravityPath = agyPath
    return configuration
}

/// Housekeeping on an in-memory `/Users/test`, so no test touches `~/.gemini`.
func fakeHousekeeping(
    _ fileSystem: FakeAntigravityFileSystem = FakeAntigravityFileSystem(),
    index: FakeConversationIndex = FakeConversationIndex()
) -> AntigravityHousekeeping {
    AntigravityHousekeeping(homeDirectory: "/Users/test", fileSystem: fileSystem, index: index)
}

private func agyClient(
    _ fake: FakeCLIRunner, fileSystem: FakeAntigravityFileSystem = FakeAntigravityFileSystem()
) -> CLIClient {
    CLIClient(runner: fake.runner, locator: locator(found: [agyPath]), antigravity: fakeHousekeeping(fileSystem))
}

private func value(after flag: String, in argv: [String]) -> String? {
    guard let index = argv.firstIndex(of: flag), index + 1 < argv.count else { return nil }
    return argv[index + 1]
}

struct AntigravityCLITests {
    // MARK: - Preset

    @Test func presetShape() {
        let preset = ProviderPreset.antigravityCLI
        #expect(preset.id == "antigravityCLI")
        #expect(preset.name == "Antigravity CLI (agy)")
        #expect(preset.kind == .antigravityCLI)
        #expect(preset.kind.cliTool == .antigravity)
        #expect(preset.defaultModel == "gemini-3.8-flash-high")
        #expect(preset.defaultEffort == "high")
        #expect(preset.auth == AuthHeaderStyle.none)
        #expect(preset.supportsReasoningEffort)
        #expect(!preset.supportsTemperature)
        #expect(ProviderPreset.all[3] == preset)
        #expect(ProviderPreset.preset(for: .antigravity) == preset)
        #expect(AIProviderConfiguration(preset: preset).temperature == nil)
        #expect(AIProviderConfiguration(preset: preset).effectiveTemperature == nil)
        #expect(CLITool.antigravity.binaryName == "agy")
        #expect(CLITool.allCases == [.copilot, .claudeCode, .codex, .antigravity])
    }

    // MARK: - argv

    @Test func argvForNamedModelWithoutAnEffortSuffix() {
        let argv = CLIArguments.antigravity(
            executable: agyPath, prompt: "PROMPT", model: "gemini-3.1-pro", reasoningEffort: "medium",
            schemaFile: "/t/reply-schema.json", logFile: "/t/agy.log"
        )
        #expect(argv == [
            agyPath, "--output-format", "json", "--json-schema", "/t/reply-schema.json",
            "--disable-slash-commands", "--sandbox", "--print-timeout", "570s",
            "--log-file", "/t/agy.log", "--project", "hearsay-notes",
            "--model", "gemini-3.1-pro", "--effort", "medium",
            "--print=PROMPT",
        ])
    }

    /// The preset default names its effort in the id, so `--effort` is left
    /// out (agy rejects a different one and needs none for the same one).
    @Test func argvForPresetDefaults() {
        let configuration = AIProviderConfiguration(preset: .antigravityCLI)
        let argv = CLIArguments.antigravity(
            executable: agyPath, prompt: "P", model: configuration.model,
            reasoningEffort: configuration.reasoningEffort,
            schemaFile: "/t/s.json", logFile: "/t/l.log"
        )
        #expect(argv == [
            agyPath, "--output-format", "json", "--json-schema", "/t/s.json",
            "--disable-slash-commands", "--sandbox", "--print-timeout", "570s",
            "--log-file", "/t/l.log", "--project", "hearsay-notes",
            "--model", "gemini-3.8-flash-high",
            "--print=P",
        ])
        #expect(!argv.contains { $0.contains("temperature") })
        #expect(!argv.contains("--dangerously-skip-permissions"))
        #expect(!argv.contains("--"))
    }

    /// agy takes the argument after `--print` as its value and rejects a
    /// prompt after `--`, so the prompt is attached with `=`, dash or not.
    @Test func promptIsAttachedToPrint() {
        let argv = CLIArguments.antigravity(
            executable: agyPath, prompt: "-starts with a dash\n--output-format text", model: " ", reasoningEffort: " ",
            schemaFile: "/s", logFile: "/l"
        )
        #expect(argv.last == "--print=-starts with a dash\n--output-format text")
        #expect(!argv.contains("--model"))
        #expect(!argv.contains("--effort"))
    }

    /// agy has no system prompt option and acts as an agent, so the system
    /// message and a no-tools instruction go before the user message.
    @Test func promptCarriesSystemMessageAndNoToolsInstruction() {
        #expect(CLIArguments.antigravityPrompt(systemMessage: "SYSTEM", userMessage: "USER")
            == "SYSTEM\n\n" + CLIArguments.antigravityInstructions + "\n\nUSER")
        #expect(CLIArguments.antigravityPrompt(systemMessage: " \n", userMessage: "USER")
            == CLIArguments.antigravityInstructions + "\n\nUSER")
        #expect(CLIArguments.antigravityInstructions.contains("Do not run commands"))
    }

    @Test func effortMapping() {
        let expected: [String: String?] = [
            "none": "low", "minimal": "low", "low": "low", "medium": "medium",
            "high": "high", "xhigh": "max", "max": "max", " MAX ": "max",
            "": nil, "turbo": nil,
        ]
        for (input, output) in expected {
            #expect(CLIArguments.antigravityEffort(input, model: "gemini-3.8-flash") == output, "\(input)")
            #expect(CLIArguments.antigravityEffort(input, model: "") == output, "\(input)")
        }
        #expect(CLIArguments.antigravityEffort(nil, model: "gemini-3.8-flash") == nil)
        for model in ["gemini-3.8-flash-high", "gemini-3.8-flash-medium", "gemini-3.1-pro-low", "gpt-oss-120b-medium", "X-HIGH"] {
            #expect(CLIArguments.antigravityEffort("low", model: model) == nil, "\(model)")
        }
        #expect(CLIArguments.antigravityEffort("high", model: "claude-opus-4-6-thinking") == "high")
    }

    @Test func schemaMatchesTheReplyShape() throws {
        let schema = try #require(
            try JSONSerialization.jsonObject(with: Data(CLIArguments.notesReplySchema.utf8)) as? [String: Any]
        )
        #expect(schema["type"] as? String == "object")
        #expect(schema["required"] as? [String] == ["filename", "markdown", "transcript_markdown"])
        #expect(schema["additionalProperties"] as? Bool == false)
        let properties = try #require(schema["properties"] as? [String: [String: String]])
        #expect(properties == [
            "filename": ["type": "string"], "markdown": ["type": "string"], "transcript_markdown": ["type": "string"],
        ])
        #expect(CLIArguments.notesReplySchema == #"{"type":"object","required":["filename","markdown","transcript_markdown"],"properties":{"filename":{"type":"string"},"markdown":{"type":"string"},"transcript_markdown":{"type":"string"}},"additionalProperties":false}"#)
    }

    @Test func environmentDropsGoogleKeys() {
        let environment = CLIClient.environment(
            for: .antigravity, executable: agyPath,
            base: [
                "PATH": "/usr/bin", "HOME": "/Users/test", "GEMINI_API_KEY": "g", "GOOGLE_API_KEY": "k",
                "GOOGLE_APPLICATION_CREDENTIALS": "/c.json", "GOOGLE_CLOUD_PROJECT": "p",
            ]
        )
        #expect(environment == [
            "PATH": "/Users/test/.local/bin:/usr/bin", "HOME": "/Users/test", "GOOGLE_CLOUD_PROJECT": "p",
        ])
    }

    // MARK: - Runs

    @Test func successParsesStructuredOutputAndRunsInAnEmptyWorkingFolder() async throws {
        let agyResponse = #"{"filename":"x","markdown":"m","toolAction":"Writing","toolSummary":"s","transcript_markdown":"t"}"#
        let fake = FakeCLIRunner(withDirectory: { argv, directory in
            let root = directory.deletingLastPathComponent()
            #expect(directory.lastPathComponent == "hearsay-notes")
            #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty)
            let schemaFile = try #require(value(after: "--json-schema", in: argv))
            #expect(schemaFile == root.appendingPathComponent("reply-schema.json").path)
            #expect(try String(contentsOfFile: schemaFile, encoding: .utf8) == CLIArguments.notesReplySchema)
            #expect(value(after: "--log-file", in: argv) == root.appendingPathComponent("agy.log").path)
            return CLIRunResult(
                exitCode: 0, stdout: antigravityEnvelope(response: agyResponse, structured: launchNotes), stderr: ""
            )
        })
        let pipeline = NotesPipeline(cliClient: agyClient(fake))
        let response = try await pipeline.generate(
            srtText: "\n" + copilotSRT + "\n", languageCode: "en", template: .generalMeeting,
            configuration: agyConfiguration(), token: "ignored"
        )
        #expect(response == NotesResponse(
            filename: "Product Launch Plan", markdown: "# Notes\n", transcriptMarkdown: "# Transcript\n\n## 00:00 — Launch\n"
        ))
        let call = try #require(fake.calls.first)
        #expect(fake.calls.count == 1)
        let root = call.directory.deletingLastPathComponent()
        let prompt = try MeetingPrompt.build(
            transcript: copilotSRT.trimmingCharacters(in: .whitespacesAndNewlines), languageCode: "en"
        )
        #expect(call.argv == CLIArguments.antigravity(
            executable: agyPath,
            prompt: MeetingPrompt.systemMessage + "\n\n" + CLIArguments.antigravityInstructions + "\n\n" + prompt, model: "gemini-3.8-flash-high", reasoningEffort: "high",
            schemaFile: root.appendingPathComponent("reply-schema.json").path,
            logFile: root.appendingPathComponent("agy.log").path
        ))
        #expect(!call.argv.contains("ignored"))
        #expect(call.timeout == 600)
        #expect(root.lastPathComponent.hasPrefix("Hearsay-antigravity-"))
        #expect(!FileManager.default.fileExists(atPath: root.path))
    }

    /// Before agy starts, the project with the deny rules exists; after it
    /// ends, that run's conversation is deleted.
    @Test func runEnsuresTheProjectAndDeletesItsConversation() async throws {
        let id = "c0f98fe5-eaac-452a-9332-470822a1d3ed"
        let files = FakeAntigravityFileSystem()
        let base = "/Users/test/.gemini/antigravity-cli"
        files.add("\(base)/conversations/\(id).db")
        files.add("\(base)/brain/\(id)/.system_generated/logs/transcript.jsonl")
        files.add("\(base)/conversations/0cb76a4c-28ef-4515-99ad-6f6ee04c3723.db")
        let fake = FakeCLIRunner { _ in
            // The project file is in place by the time agy runs.
            #expect(files.paths.contains { $0.hasPrefix("/Users/test/.gemini/config/projects/") })
            var envelope = antigravityEnvelope(response: launchNotes)
            envelope = envelope.replacingOccurrences(of: "6cfd8496-ae02-41ac-afc4-1460ec1eaf0c", with: id)
            return CLIRunResult(exitCode: 0, stdout: envelope, stderr: "")
        }
        let reply = try await agyClient(fake, fileSystem: files).complete(
            systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil
        )
        #expect(try NotesResponse.parse(reply).filename == "Product Launch Plan")
        #expect(files.paths.filter { $0.contains(id) }.isEmpty)
        #expect(files.paths.contains("\(base)/conversations/0cb76a4c-28ef-4515-99ad-6f6ee04c3723.db"))
    }

    /// A failed run with no conversation id deletes nothing; a failed run
    /// with one still deletes it.
    @Test func failedRunsDeleteOnlyWhenThereIsAnID() async {
        let id = "a4955120-504f-425d-beda-a27a3123f471"
        let files = FakeAntigravityFileSystem()
        files.add("/Users/test/.gemini/antigravity-cli/conversations/\(id).db")
        let noID = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: "error: invalid model selection") }
        await #expect(throws: CLIProviderError.self) {
            _ = try await agyClient(noID, fileSystem: files).complete(
                systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil
            )
        }
        #expect(files.removed.isEmpty)
        let withID = FakeCLIRunner { _ in
            CLIRunResult(exitCode: 3, stdout: #"{"conversation_id":"\#(id)","status":"ERROR","response":"partial"}"#, stderr: "AGY_ERROR x")
        }
        await #expect(throws: CLIProviderError.self) {
            _ = try await agyClient(withID, fileSystem: files).complete(
                systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil
            )
        }
        #expect(files.removed == ["/Users/test/.gemini/antigravity-cli/conversations/\(id).db"])
    }

    /// Without the deny rules agy must not start.
    @Test func projectWriteFailureStopsTheRun() async {
        let files = FakeAntigravityFileSystem()
        files.failWrites = true
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 0, stdout: launchNotes, stderr: "") }
        do {
            _ = try await agyClient(fake, fileSystem: files).complete(
                systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil
            )
            Issue.record("expected launchFailed")
        } catch let error as CLIProviderError {
            guard case .launchFailed(.antigravity, let detail) = error else {
                Issue.record("unexpected \(error)")
                return
            }
            #expect(detail.contains("hearsay-notes project in /Users/test/.gemini/config/projects"))
        } catch {
            Issue.record("unexpected \(error)")
        }
        #expect(fake.calls.isEmpty)
    }

    /// Without `structured_output`, `response` is parsed; stdout that is not
    /// the JSON envelope is used as is.
    @Test func replyFallsBackToResponseThenStdout() throws {
        let fromResponse = try CLIClient.antigravityReply(
            CLIRunResult(exitCode: 0, stdout: antigravityEnvelope(response: "```json\n" + launchNotes + "\n```"), stderr: "")
        )
        #expect(try NotesResponse.parse(fromResponse).filename == "Product Launch Plan")
        let raw = try CLIClient.antigravityReply(CLIRunResult(exitCode: 0, stdout: launchNotes, stderr: ""))
        #expect(raw == launchNotes)
        let structured = try CLIClient.antigravityReply(
            CLIRunResult(exitCode: 0, stdout: antigravityEnvelope(response: "junk", structured: launchNotes), stderr: "")
        )
        #expect(try NotesResponse.parse(structured).markdown == "# Notes\n")
    }

    // MARK: - Errors

    /// A tool call that needs a permission is auto-denied in print mode: exit
    /// 0, an empty `response`, and the reason on stderr.
    @Test func deniedToolAndEmptyReplyIsEmptyOutput() async {
        let fake = FakeCLIRunner { _ in
            CLIRunResult(
                exitCode: 0, stdout: antigravityEnvelope(response: ""),
                stderr: "jetski: no output produced — a tool required the \"command\" permission that headless mode cannot prompt for, so it was auto-denied.\n"
            )
        }
        await #expect(throws: CLIProviderError.emptyOutput(.antigravity)) {
            _ = try await agyClient(fake).complete(systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil)
        }
        #expect(CLIProviderError.emptyOutput(.antigravity).errorDescription == "Antigravity CLI returned empty output.")
    }

    @Test func notLoggedInIsDetectedWithoutShowingTheLoginURL() async throws {
        let stderr = """
        Authentication required. Please visit the URL to log in:
          https://accounts.google.com/o/oauth2/auth?access_type=offline&client_id=x

        Waiting for authentication (timeout 60s)...
        Or, paste the authorization code here and press Enter:
        Error: authentication timed out.
        error: authentication failed or timed out
        """
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: stderr) }
        do {
            _ = try await agyClient(fake).complete(systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil)
            Issue.record("expected notLoggedIn")
        } catch let error as CLIProviderError {
            #expect(error == .notLoggedIn(.antigravity, excerpt: "error: authentication failed or timed out"))
            let message = error.errorDescription ?? ""
            #expect(message.contains("Antigravity CLI is not logged in. Run `agy` once in Terminal"))
            #expect(message.contains("Google account"))
            #expect(!message.contains("accounts.google.com"))
        }
    }

    @Test func nonzeroExitKeepsTheLastErrorLine() async {
        let stderr = "error: invalid model selection (--model \"gemini-3.8-flash-high\" --effort \"low\"): --model gemini-3.8-flash-high conflicts with --effort=low\n"
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: stderr) }
        await #expect(throws: CLIProviderError.failed(
            .antigravity, exitCode: 1,
            excerpt: "error: invalid model selection (--model \"gemini-3.8-flash-high\" --effort \"low\"): --model gemini-3.8-flash-high conflicts with --effort=low"
        )) {
            _ = try await agyClient(fake).complete(systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil)
        }
        let message = CLIProviderError.failed(.antigravity, exitCode: 3, excerpt: "AGY_ERROR quota").errorDescription ?? ""
        #expect(message.contains("Antigravity CLI call failed (exit code 3)"))
        #expect(message.contains("AGY_ERROR quota"))
        let noErrorLine = CLIClient.excerpt(
            tool: .antigravity, result: CLIRunResult(exitCode: 2, stdout: "", stderr: String(repeating: "z", count: 600) + "END\n")
        )
        #expect(noErrorLine.count == 500)
        #expect(noErrorLine.hasSuffix("END"))
        #expect(CLIClient.excerpt(tool: .antigravity, result: CLIRunResult(exitCode: 2, stdout: "out", stderr: " \n")) == "out")
    }

    @Test func failedStatusInTheEnvelopeFails() async {
        var envelope = antigravityEnvelope(response: "partial", status: "ERROR")
        envelope = envelope.replacingOccurrences(of: #""status""#, with: #""error":"model overloaded","status""#)
        let result = CLIRunResult(exitCode: 0, stdout: envelope, stderr: "")
        #expect(throws: CLIProviderError.failed(.antigravity, exitCode: 0, excerpt: "model overloaded")) {
            _ = try CLIClient.antigravityReply(result)
        }
    }

    @Test func timeoutAndNotInstalled() async {
        let fake = FakeCLIRunner { _ in CLIRunResult(exitCode: 15, stdout: "", stderr: "", timedOut: true) }
        await #expect(throws: CLIProviderError.timedOut(.antigravity)) {
            _ = try await agyClient(fake).complete(systemMessage: "s", userMessage: "u", configuration: agyConfiguration(), token: nil)
        }
        #expect(CLIProviderError.timedOut(.antigravity).errorDescription == "Antigravity CLI did not answer within 10 minutes and was stopped.")

        let missing = FakeCLIRunner { _ in CLIRunResult(exitCode: 1, stdout: "", stderr: "") }
        let client = CLIClient(runner: missing.runner, locator: locator(found: []), antigravity: fakeHousekeeping())
        var configuration = agyConfiguration()
        configuration.antigravityPath = nil
        do {
            _ = try await client.complete(systemMessage: "s", userMessage: "u", configuration: configuration, token: nil)
            Issue.record("expected notInstalled")
        } catch let error as CLIProviderError {
            guard case .notInstalled(.antigravity, let searched) = error else {
                Issue.record("unexpected \(error)")
                return
            }
            #expect(searched.first == "/Users/test/.local/bin/agy")
            #expect(searched.last == "zsh -lc 'command -v agy'")
            #expect(error.errorDescription == "Antigravity CLI not found. Install it with `curl -fsSL https://antigravity.google/cli/install.sh | bash` or set the path in Settings > AI.")
        } catch {
            Issue.record("unexpected \(error)")
        }
    }

    // MARK: - Check

    @Test func checkRunsVersionAndModels() async throws {
        let fake = FakeCLIRunner { argv in
            switch (argv.count, argv.last) {
            case (2, "--version"):
                return CLIRunResult(exitCode: 0, stdout: "1.2.12\n", stderr: "")
            case (4, "models"):
                return CLIRunResult(
                    exitCode: 0,
                    stdout: "Fetching available models...\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\ngemini-3.1-pro-low\tGemini 3.1 Pro (Low)\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\n",
                    stderr: ""
                )
            default:
                Issue.record("unexpected \(argv)")
                return CLIRunResult(exitCode: 1, stdout: "", stderr: "")
            }
        }
        let pipeline = NotesPipeline(cliClient: agyClient(fake))
        let installation = try await pipeline.checkInstallation(configuration: agyConfiguration())
        #expect(installation == CLIInstallation(
            tool: .antigravity, path: agyPath, version: "1.2.12",
            loginStatus: "Logged in; 3 models available", loggedIn: true
        ))
        #expect(fake.calls.count == 2)
        #expect(fake.calls.first?.argv == [agyPath, "--version"])
        let models = try #require(fake.calls.last)
        #expect(models.argv == [agyPath, "--log-file", models.directory.appendingPathComponent("agy.log").path, "models"])
        #expect(fake.calls.map(\.timeout) == [15, 20])
    }

    @Test func modelsCheckWhenLoggedOutOrFailing() {
        let loggedOut = CLIClient.loginStatus(
            tool: .antigravity,
            result: CLIRunResult(
                exitCode: 1,
                stdout: "Fetching available models...\nError: Please sign in to view available models. Launch the CLI without arguments to sign in.\n",
                stderr: ""
            )
        )
        #expect(loggedOut.loggedIn == false)
        #expect(loggedOut.text == "Not logged in. Run `agy` once in Terminal to log in.")
        let failing = CLIClient.loginStatus(
            tool: .antigravity, result: CLIRunResult(exitCode: 4, stdout: "", stderr: "network down")
        )
        #expect(failing.loggedIn == false)
        #expect(failing.text == "`agy models` failed (exit code 4). Run `agy` once in Terminal to log in.")
        let timedOut = CLIClient.loginStatus(
            tool: .antigravity, result: CLIRunResult(exitCode: 15, stdout: "", stderr: "", timedOut: true)
        )
        #expect(timedOut == ("The login status check did not answer.", false))
        let one = CLIClient.loginStatus(
            tool: .antigravity, result: CLIRunResult(exitCode: 0, stdout: "gemini-3.8-flash-high\tG\n", stderr: "")
        )
        #expect(one.text == "Logged in; 1 model available")
    }
}
