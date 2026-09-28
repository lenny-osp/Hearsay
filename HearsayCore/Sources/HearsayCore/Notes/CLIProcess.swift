import Foundation

/// A command-line program that writes meeting notes with the user's own
/// login: GitHub Copilot CLI, Claude Code, OpenAI Codex, or Google Antigravity.
public enum CLITool: String, Sendable, CaseIterable, Codable {
    case copilot
    case claudeCode
    case codex
    case antigravity

    /// The executable's file name.
    public var binaryName: String {
        switch self {
        case .copilot: "copilot"
        case .claudeCode: "claude"
        case .codex: "codex"
        case .antigravity: "agy"
        }
    }

    public var displayName: String {
        switch self {
        case .copilot: "GitHub Copilot CLI"
        case .claudeCode: "Claude Code CLI"
        case .codex: "Codex CLI"
        case .antigravity: "Antigravity CLI"
        }
    }

    /// For the "Check <name>" button.
    public var shortName: String {
        switch self {
        case .copilot: "Copilot"
        case .claudeCode: "Claude Code"
        case .codex: "Codex"
        case .antigravity: "Antigravity"
        }
    }

    public var installCommand: String {
        switch self {
        case .copilot: "npm install -g @github/copilot"
        case .claudeCode: "curl -fsSL https://claude.ai/install.sh | bash"
        case .codex: "npm install -g @openai/codex"
        // https://antigravity.google/docs/cli/install; `agy install` only
        // sets up PATH and shell aliases for an existing binary.
        case .antigravity: "curl -fsSL https://antigravity.google/cli/install.sh | bash"
        }
    }

    /// What to run once in Terminal to log in.
    public var loginCommand: String {
        switch self {
        case .copilot: "copilot"
        case .claudeCode: "claude"
        case .codex: "codex login"
        case .antigravity: "agy"
        }
    }

    /// Arguments after the binary that report the login state, or nil when
    /// the CLI has no such command. Antigravity has none; `agy models` only
    /// succeeds when logged in, so it stands in.
    public var loginStatusArguments: [String]? {
        switch self {
        case .copilot: nil
        case .claudeCode: ["auth", "status"]
        case .codex: ["login", "status"]
        case .antigravity: ["models"]
        }
    }

    /// Time limit of the login status command. `agy models` asks the
    /// server, so it gets longer than a local status check.
    public var loginStatusTimeout: TimeInterval {
        self == .antigravity ? 20 : CLIClient.versionTimeout
    }

    /// Prefix of the temporary working folder of one run.
    public var temporaryFolderPrefix: String { "Hearsay-\(rawValue)-" }
}

/// Why a CLI provider call produced no usable reply. The Copilot wording
/// follows the Copilot branch of Python `generate_meeting_notes`.
public enum CLIProviderError: Error, LocalizedError, Equatable {
    /// No binary at the configured path or any searched location.
    case notInstalled(CLITool, searched: [String])
    /// The binary could not be started.
    case launchFailed(CLITool, String)
    /// Nonzero exit status. `excerpt` is at most
    /// `ChatCompletionsError.excerptLength` characters (`CLIClient.excerpt`).
    case failed(CLITool, exitCode: Int32, excerpt: String)
    /// Nonzero exit status whose output says the CLI has no valid login
    /// (Claude Code, Codex, Antigravity; Copilot keeps the Python wording).
    case notLoggedIn(CLITool, excerpt: String)
    /// Still running after `CLIClient.timeout`; it was terminated.
    case timedOut(CLITool)
    /// Exit status 0 but no reply.
    case emptyOutput(CLITool)
    /// The configuration's preset does not run a CLI.
    case notACLIPreset(String)

    public var errorDescription: String? {
        switch self {
        case let .notInstalled(tool, _):
            return "\(tool.displayName) not found. Install it with `\(tool.installCommand)` or set the path in Settings > AI."
        case let .launchFailed(tool, detail):
            if tool == .copilot {
                return "GitHub Copilot CLI call failed. Check the Copilot login status and model configuration: \(detail)"
            }
            return "\(tool.displayName) could not be started: \(detail)"
        case let .failed(tool, code, excerpt):
            let trimmed = excerpt.trimmingCharacters(in: .whitespacesAndNewlines)
            var message: String
            if tool == .copilot {
                message = "GitHub Copilot CLI call failed (exit code \(code)). Check the Copilot login status and model configuration."
            } else {
                message = "\(tool.displayName) call failed (exit code \(code)). Check the model and reasoning effort in Settings > AI."
            }
            if !trimmed.isEmpty {
                message += "\n\(trimmed)"
            }
            if tool == .copilot, Self.mentionsLogin(trimmed) {
                message += "\nRun `copilot` once in Terminal to log in."
            }
            return message
        case let .notLoggedIn(tool, excerpt):
            var message = "\(tool.displayName) is not logged in. Run `\(tool.loginCommand)` once in Terminal to log in"
            switch tool {
            case .claudeCode: message += " with your Claude subscription."
            case .codex: message += " with your ChatGPT account."
            case .antigravity: message += " with your Google account."
            case .copilot: message += "."
            }
            let trimmed = excerpt.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                message += "\n\(trimmed)"
            }
            return message
        case .timedOut(let tool):
            return "\(tool.displayName) did not answer within \(Int(CLIClient.timeout / 60)) minutes and was stopped."
        case .emptyOutput(let tool):
            return "\(tool.displayName) returned empty output."
        case .notACLIPreset(let name):
            return "The \(name) preset does not use a command-line tool."
        }
    }

    /// Copilot: any hint of a login problem (Python parity).
    static func mentionsLogin(_ text: String) -> Bool {
        let lowered = text.lowercased()
        return ["auth", "login", "log in", "logged in", "sign in", "token"].contains { lowered.contains($0) }
    }

    /// Claude Code, Codex, Antigravity: output that means the login is missing or no
    /// longer valid. Stricter than `mentionsLogin`, because Codex always
    /// prints "tokens used".
    static func indicatesLoggedOut(_ text: String, tool: CLITool) -> Bool {
        let lowered = text.lowercased()
        let markers: [String]
        switch tool {
        case .copilot:
            return false
        case .claudeCode:
            // "Not logged in · Please run /login", "Invalid API key · Please
            // run /login", "OAuth token has expired", "API Error: 401".
            markers = ["not logged in", "please run /login", "invalid api key", "oauth token", "api error: 401"]
        case .codex:
            // "Not logged in"; with no login every request ends in
            // "unexpected status 401 Unauthorized".
            markers = ["not logged in", "401 unauthorized", "codex login"]
        case .antigravity:
            // Print mode without a login shows "Authentication required.
            // Please visit the URL to log in:", waits 60 s, then "Error:
            // authentication timed out." / "error: authentication failed or
            // timed out". `agy models`: "Please sign in to view available
            // models."
            markers = [
                "authentication required", "authentication failed", "authentication timed out",
                "not logged into antigravity", "please sign in",
            ]
        }
        return markers.contains { lowered.contains($0) }
    }
}

/// What one run of a program produced.
public struct CLIRunResult: Sendable, Equatable {
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
public typealias CLIRunner = @Sendable (
    _ argv: [String],
    _ directory: URL,
    _ environment: [String: String],
    _ timeout: TimeInterval
) async throws -> CLIRunResult

/// Where a CLI was found, what `--version` says, and its login state.
public struct CLIInstallation: Sendable, Equatable {
    public var tool: CLITool
    public var path: String
    public var version: String
    /// One line from the CLI's login status command; nil when it has none.
    public var loginStatus: String?
    /// Nil when unknown (no status command).
    public var loggedIn: Bool?

    public init(tool: CLITool, path: String, version: String, loginStatus: String? = nil, loggedIn: Bool? = nil) {
        self.tool = tool
        self.path = path
        self.version = version
        self.loginStatus = loginStatus
        self.loggedIn = loggedIn
    }
}

/// Finds a CLI binary (PLAN.md section 7).
public struct CLILocator: Sendable {
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
    public static func shellLookupArguments(for tool: CLITool) -> [String] {
        ["/bin/zsh", "-lc", "command -v \(tool.binaryName)"]
    }

    public static let shellLookupTimeout: TimeInterval = 5

    /// `~/.local/bin`, `/opt/homebrew/bin`, `/usr/local/bin`, then
    /// `~/.nvm/versions/node/<v>/bin` from the newest Node version.
    public func knownCandidates(for tool: CLITool) -> [String] {
        let name = tool.binaryName
        let localBin = (homeDirectory as NSString).appendingPathComponent(".local/bin/\(name)")
        let nvmRoot = (homeDirectory as NSString).appendingPathComponent(".nvm/versions/node")
        let versions = directoryContents(nvmRoot).sorted { Self.isNewer($0, than: $1) }
        return [localBin, "/opt/homebrew/bin/\(name)", "/usr/local/bin/\(name)"]
            + versions.map { "\(nvmRoot)/\($0)/bin/\(name)" }
    }

    /// The first executable well-known candidate. No shell is started.
    public func knownInstallation(for tool: CLITool) -> String? {
        knownCandidates(for: tool).first(where: isExecutable)
    }

    /// The first tool, in preset order, with a binary in a well-known place.
    public func firstKnownTool() -> CLITool? {
        CLITool.allCases.first { knownInstallation(for: $0) != nil }
    }

    /// The configured path when set, else a well-known location, else what
    /// `zsh -lc 'command -v <tool>'` prints (5 s timeout).
    public func locate(_ tool: CLITool, configuredPath: String?, runner: CLIRunner) async throws -> String {
        let configured = configuredPath?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !configured.isEmpty {
            let expanded = (configured as NSString).expandingTildeInPath
            guard isExecutable(expanded) else { throw CLIProviderError.notInstalled(tool, searched: [expanded]) }
            return expanded
        }
        let candidates = knownCandidates(for: tool)
        if let found = candidates.first(where: isExecutable) { return found }
        let searched = candidates + ["zsh -lc 'command -v \(tool.binaryName)'"]
        let result = try? await runner(
            Self.shellLookupArguments(for: tool),
            URL(fileURLWithPath: homeDirectory, isDirectory: true),
            ProcessInfo.processInfo.environment,
            Self.shellLookupTimeout
        )
        guard let result, result.exitCode == 0, !result.timedOut else {
            throw CLIProviderError.notInstalled(tool, searched: searched)
        }
        // A login shell may print profile noise first; the path is the last
        // absolute line.
        let path = result.stdout
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .last { $0.hasPrefix("/") }
        guard let path, isExecutable(path) else {
            throw CLIProviderError.notInstalled(tool, searched: searched)
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

/// The real `CLIRunner`: `Process` with piped stdout and stderr, stdin from
/// /dev/null, terminated on timeout or task cancellation.
public enum CLIProcessRunner {
    public static let run: CLIRunner = { argv, directory, environment, timeout in
        try Task.checkCancellation()
        guard let executable = argv.first else {
            throw LaunchError(detail: "No executable.")
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
        let result: CLIRunResult = try await withTaskCancellationHandler {
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

    /// Thrown by the runner when `Process.run()` fails; `CLIClient` maps it
    /// to `CLIProviderError.launchFailed` for the right tool.
    struct LaunchError: Error, LocalizedError {
        var detail: String
        var errorDescription: String? { detail }
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
        var continuation: CheckedContinuation<CLIRunResult, Error>? {
            get { lock.withLock { storedContinuation } }
            set { lock.withLock { storedContinuation = newValue } }
        }
        private var storedContinuation: CheckedContinuation<CLIRunResult, Error>?

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
            continuation?.resume(throwing: LaunchError(detail: error.localizedDescription))
        }

        private func finish() {
            lock.lock()
            guard !resumed, let status else {
                lock.unlock()
                return
            }
            resumed = true
            let result = CLIRunResult(
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
