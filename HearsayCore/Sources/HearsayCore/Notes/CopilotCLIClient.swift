import Foundation

/// Why a GitHub Copilot CLI call produced no usable reply. Wording follows the
/// Copilot branch of Python `generate_meeting_notes`.
public enum CopilotCLIError: Error, LocalizedError, Equatable {
    /// No `copilot` binary at the configured path or any searched location.
    case notInstalled(searched: [String])
    /// The binary could not be started.
    case launchFailed(String)
    /// Nonzero exit status. `stderrExcerpt` is at most
    /// `ChatCompletionsError.excerptLength` characters.
    case failed(exitCode: Int32, stderrExcerpt: String)
    /// Still running after `CopilotCLIClient.timeout`; it was terminated.
    case timedOut
    /// Exit status 0 but nothing on stdout.
    case emptyOutput

    public var errorDescription: String? {
        switch self {
        case .notInstalled:
            return "GitHub Copilot CLI not found. Install it with `npm install -g @github/copilot` or set the path in Settings > AI."
        case .launchFailed(let detail):
            return "GitHub Copilot CLI call failed. Check the Copilot login status and model configuration: \(detail)"
        case let .failed(code, excerpt):
            var message = "GitHub Copilot CLI call failed (exit code \(code)). Check the Copilot login status and model configuration."
            let trimmed = excerpt.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                message += "\n\(trimmed)"
            }
            if Self.mentionsLogin(trimmed) {
                message += "\nRun `copilot` once in Terminal to log in."
            }
            return message
        case .timedOut:
            return "GitHub Copilot CLI did not answer within \(Int(CopilotCLIClient.timeout / 60)) minutes and was stopped."
        case .emptyOutput:
            return "GitHub Copilot CLI returned empty output."
        }
    }

    static func mentionsLogin(_ text: String) -> Bool {
        let lowered = text.lowercased()
        return ["auth", "login", "log in", "logged in", "sign in", "token"].contains { lowered.contains($0) }
    }
}

/// What one run of a program produced.
public struct CopilotCLIRunResult: Sendable, Equatable {
    public var exitCode: Int32
    public var stdout: String
    public var stderr: String
    /// True when the run was terminated because it exceeded its timeout.
    public var timedOut: Bool

    public init(exitCode: Int32, stdout: String, stderr: String, timedOut: Bool = false) {
        self.exitCode = exitCode
        self.stdout = stdout
        self.stderr = stderr
        self.timedOut = timedOut
    }
}

/// Runs `argv` (argv[0] is an absolute executable path) in `directory` with
/// `environment`, terminating it after `timeout` seconds. Injected in tests so
/// no process is spawned.
public typealias CopilotCLIRunner = @Sendable (
    _ argv: [String],
    _ directory: URL,
    _ environment: [String: String],
    _ timeout: TimeInterval
) async throws -> CopilotCLIRunResult

/// Where the `copilot` binary was found and what `copilot --version` says.
public struct CopilotInstallation: Sendable, Equatable {
    public var path: String
    public var version: String
}

/// Finds the `copilot` binary (PLAN.md section 7, Copilot CLI preset).
public struct CopilotCLILocator: Sendable {
    public var homeDirectory: String
    public var isExecutable: @Sendable (String) -> Bool
    public var directoryContents: @Sendable (String) -> [String]

    public init(
        homeDirectory: String = NSHomeDirectory(),
        isExecutable: @escaping @Sendable (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) },
        directoryContents: @escaping @Sendable (String) -> [String] = {
            (try? FileManager.default.contentsOfDirectory(atPath: $0)) ?? []
        }
    ) {
        self.homeDirectory = homeDirectory
        self.isExecutable = isExecutable
        self.directoryContents = directoryContents
    }

    /// The login-shell lookup run when no well-known location has the binary.
    public static let shellLookupArguments = ["/bin/zsh", "-lc", "command -v copilot"]
    public static let shellLookupTimeout: TimeInterval = 5

    /// `/opt/homebrew/bin/copilot`, `/usr/local/bin/copilot`, then
    /// `~/.nvm/versions/node/<v>/bin/copilot` from the newest Node version.
    public func knownCandidates() -> [String] {
        let nvmRoot = (homeDirectory as NSString).appendingPathComponent(".nvm/versions/node")
        let versions = directoryContents(nvmRoot).sorted { Self.isNewer($0, than: $1) }
        return ["/opt/homebrew/bin/copilot", "/usr/local/bin/copilot"]
            + versions.map { "\(nvmRoot)/\($0)/bin/copilot" }
    }

    /// The first executable well-known candidate. No shell is started.
    public func knownInstallation() -> String? {
        knownCandidates().first(where: isExecutable)
    }

    /// The configured path when set, else a well-known location, else what
    /// `zsh -lc 'command -v copilot'` prints (5 s timeout).
    public func locate(configuredPath: String?, runner: CopilotCLIRunner) async throws -> String {
        let configured = configuredPath?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !configured.isEmpty {
            let expanded = (configured as NSString).expandingTildeInPath
            guard isExecutable(expanded) else { throw CopilotCLIError.notInstalled(searched: [expanded]) }
            return expanded
        }
        let candidates = knownCandidates()
        if let found = candidates.first(where: isExecutable) { return found }
        let searched = candidates + ["zsh -lc 'command -v copilot'"]
        let result = try? await runner(
            Self.shellLookupArguments,
            URL(fileURLWithPath: homeDirectory, isDirectory: true),
            ProcessInfo.processInfo.environment,
            Self.shellLookupTimeout
        )
        guard let result, result.exitCode == 0, !result.timedOut else {
            throw CopilotCLIError.notInstalled(searched: searched)
        }
        // A login shell may print profile noise first; the path is the last
        // absolute line.
        let path = result.stdout
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .last { $0.hasPrefix("/") }
        guard let path, isExecutable(path) else {
            throw CopilotCLIError.notInstalled(searched: searched)
        }
        return path
    }

    /// Compares nvm folder names such as `v24.16.0` numerically.
    static func isNewer(_ lhs: String, than rhs: String) -> Bool {
        func parts(_ name: String) -> [Int] {
            name.drop { $0 == "v" }.split(separator: ".").map { Int($0) ?? 0 }
        }
        let left = parts(lhs), right = parts(rhs)
        for index in 0..<max(left.count, right.count) {
            let a = index < left.count ? left[index] : 0
            let b = index < right.count ? right[index] : 0
            if a != b { return a > b }
        }
        return lhs > rhs
    }
}

/// Meeting notes through the installed GitHub Copilot CLI (port of the
/// Copilot branch of Python `generate_meeting_notes`). The prompt goes in
/// `--prompt`; as in Python there is no system message and no token, since
/// the CLI uses its own login.
public actor CopilotCLIClient: ChatCompleting {
    /// Generous: a long meeting at reasoning effort "max" takes minutes.
    public static let timeout: TimeInterval = 600
    public static let versionTimeout: TimeInterval = 15

    private let runner: CopilotCLIRunner
    private let locator: CopilotCLILocator

    public init(
        runner: @escaping CopilotCLIRunner = CopilotProcessRunner.run,
        locator: CopilotCLILocator = CopilotCLILocator()
    ) {
        self.runner = runner
        self.locator = locator
    }

    /// Python `copilot_argv`, with the resolved binary as argv[0]. An empty
    /// model or effort falls back to the defaults, as Python's `or` does.
    public static func arguments(
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

    /// The inherited environment with the binary's folder first on PATH, so
    /// `#!/usr/bin/env node` finds the Node that installed it.
    public static func environment(
        executable: String,
        base: [String: String] = ProcessInfo.processInfo.environment
    ) -> [String: String] {
        var environment = base
        let directory = (executable as NSString).deletingLastPathComponent
        let path = base["PATH"].flatMap { $0.isEmpty ? nil : $0 } ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        environment["PATH"] = directory + ":" + path
        return environment
    }

    public func locate(configuredPath: String?) async throws -> String {
        try await locator.locate(configuredPath: configuredPath, runner: runner)
    }

    public func complete(
        systemMessage: String,
        userMessage: String,
        configuration: AIProviderConfiguration,
        token: String?
    ) async throws -> String {
        let executable = try await locate(configuredPath: configuration.copilotPath)
        let argv = Self.arguments(
            executable: executable,
            prompt: userMessage,
            model: configuration.model,
            reasoningEffort: configuration.reasoningEffort
        )
        let result = try await run(argv, executable: executable, timeout: Self.timeout)
        if result.timedOut { throw CopilotCLIError.timedOut }
        guard result.exitCode == 0 else {
            throw CopilotCLIError.failed(
                exitCode: result.exitCode,
                stderrExcerpt: String(result.stderr.prefix(ChatCompletionsError.excerptLength))
            )
        }
        guard !result.stdout.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CopilotCLIError.emptyOutput
        }
        return result.stdout
    }

    /// The binary's path and `copilot --version` output, for Settings > AI.
    public func checkInstallation(configuredPath: String?) async throws -> CopilotInstallation {
        let executable = try await locate(configuredPath: configuredPath)
        let result = try await run([executable, "--version"], executable: executable, timeout: Self.versionTimeout)
        if result.timedOut { throw CopilotCLIError.timedOut }
        guard result.exitCode == 0 else {
            throw CopilotCLIError.failed(
                exitCode: result.exitCode,
                stderrExcerpt: String(result.stderr.prefix(ChatCompletionsError.excerptLength))
            )
        }
        let version = result.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        return CopilotInstallation(path: executable, version: version)
    }

    /// Runs in a fresh temporary folder, removed afterwards: Copilot can use
    /// tools, so it is kept away from the user's files.
    private func run(_ argv: [String], executable: String, timeout: TimeInterval) async throws -> CopilotCLIRunResult {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("Hearsay-copilot-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        do {
            return try await runner(argv, directory, Self.environment(executable: executable), timeout)
        } catch let error as CopilotCLIError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw CopilotCLIError.launchFailed(error.localizedDescription)
        }
    }
}

/// The real `CopilotCLIRunner`: `Process` with piped stdout and stderr, stdin
/// from /dev/null, terminated on timeout or task cancellation.
public enum CopilotProcessRunner {
    public static let run: CopilotCLIRunner = { argv, directory, environment, timeout in
        try Task.checkCancellation()
        guard let executable = argv.first else {
            throw CopilotCLIError.launchFailed("No executable.")
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = Array(argv.dropFirst())
        process.currentDirectoryURL = directory
        process.environment = environment
        process.standardInput = FileHandle.nullDevice
        let stdout = Pipe()
        let stderr = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr

        let state = RunState(process: process)
        let result: CopilotCLIRunResult = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                state.continuation = continuation
                stdout.fileHandleForReading.readabilityHandler = { handle in
                    let data = handle.availableData
                    if data.isEmpty { handle.readabilityHandler = nil }
                    state.append(data, isStdout: true)
                }
                stderr.fileHandleForReading.readabilityHandler = { handle in
                    let data = handle.availableData
                    if data.isEmpty { handle.readabilityHandler = nil }
                    state.append(data, isStdout: false)
                }
                process.terminationHandler = { finished in
                    state.terminated(status: finished.terminationStatus)
                }
                do {
                    try process.run()
                } catch {
                    stdout.fileHandleForReading.readabilityHandler = nil
                    stderr.fileHandleForReading.readabilityHandler = nil
                    state.failToLaunch(error)
                    return
                }
                // Cancelled between the handler's install and the launch.
                if state.wasCancelled { process.terminate() }
                DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                    state.stop(timedOut: true)
                }
            }
        } onCancel: {
            state.stop(timedOut: false)
        }
        if state.wasCancelled { throw CancellationError() }
        return result
    }

    /// Shared between the pipe handlers, the termination handler, the
    /// timeout, and cancellation; every access holds `lock`.
    private final class RunState: @unchecked Sendable {
        private let lock = NSLock()
        private let process: Process
        private var stdout = Data()
        private var stderr = Data()
        private var stdoutClosed = false
        private var stderrClosed = false
        private var status: Int32?
        private var timedOut = false
        private var cancelled = false
        private var resumed = false
        var continuation: CheckedContinuation<CopilotCLIRunResult, Error>? {
            get { lock.withLock { storedContinuation } }
            set { lock.withLock { storedContinuation = newValue } }
        }
        private var storedContinuation: CheckedContinuation<CopilotCLIRunResult, Error>?

        /// After exit, how long to wait for the pipes to reach end of file. A
        /// child process that inherited them could otherwise hold the result.
        private static let drainGrace: TimeInterval = 2

        init(process: Process) { self.process = process }

        var wasCancelled: Bool { lock.withLock { cancelled } }

        func append(_ data: Data, isStdout: Bool) {
            lock.lock()
            if isStdout {
                if data.isEmpty { stdoutClosed = true } else { stdout.append(data) }
            } else {
                if data.isEmpty { stderrClosed = true } else { stderr.append(data) }
            }
            let ready = status != nil && stdoutClosed && stderrClosed
            lock.unlock()
            if ready { finish() }
        }

        func terminated(status exitStatus: Int32) {
            lock.lock()
            status = exitStatus
            let ready = stdoutClosed && stderrClosed
            lock.unlock()
            if ready {
                finish()
            } else {
                DispatchQueue.global().asyncAfter(deadline: .now() + Self.drainGrace) { [self] in finish() }
            }
        }

        func stop(timedOut isTimeout: Bool) {
            lock.lock()
            guard status == nil, !resumed else {
                lock.unlock()
                return
            }
            if isTimeout { timedOut = true } else { cancelled = true }
            lock.unlock()
            if process.isRunning { process.terminate() }
        }

        func failToLaunch(_ error: Error) {
            lock.lock()
            guard !resumed else {
                lock.unlock()
                return
            }
            resumed = true
            let continuation = storedContinuation
            storedContinuation = nil
            lock.unlock()
            continuation?.resume(throwing: CopilotCLIError.launchFailed(error.localizedDescription))
        }

        private func finish() {
            lock.lock()
            guard !resumed, let status else {
                lock.unlock()
                return
            }
            resumed = true
            let result = CopilotCLIRunResult(
                exitCode: status,
                stdout: String(decoding: stdout, as: UTF8.self),
                stderr: String(decoding: stderr, as: UTF8.self),
                timedOut: timedOut
            )
            let continuation = storedContinuation
            storedContinuation = nil
            lock.unlock()
            continuation?.resume(returning: result)
        }
    }
}
