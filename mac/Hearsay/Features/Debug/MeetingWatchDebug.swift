import Foundation
import HearsayCore

/// Debug only. `HEARSAY_WATCH_MEETINGS=<seconds>` lists the audio processes
/// CoreAudio knows every second for that many seconds and prints what
/// `MeetingAutoRecord` would act on: every process once at the start (pid,
/// bundle id, executable path, whether it is running input), then one line
/// per change in that list, and `meeting started` / `meeting ended` from a
/// `MeetingDetector`. It only prints: nothing is recorded, and the coordinator
/// does not exist in a debug run. Exit status 0, or 1 for a bad value or on
/// macOS before 14.2 (the process-object API is new there). Port of
/// `MeetingWatchDebug.cs`; unlike the app it polls, which is fine here.
///
///     HEARSAY_WATCH_MEETINGS=90 .build/derived/Build/Products/Release/Hearsay.app/Contents/MacOS/Hearsay
@MainActor
enum MeetingWatchDebug {
    static let variable = "HEARSAY_WATCH_MEETINGS"

    /// Returns false (and does nothing) when the variable is not set.
    static func runIfRequested() -> Bool {
        guard let text = ProcessInfo.processInfo.environment[variable], !text.isEmpty else { return false }
        guard let seconds = Double(text), seconds > 0 else {
            say("\(variable) needs a positive number of seconds")
            finish(1)
        }
        guard #available(macOS 14.2, *) else {
            say("the audio process list needs macOS 14.2 or later; nothing to watch")
            finish(1)
        }
        Task { @MainActor in
            let status = await watch(seconds: seconds)
            finish(status)
        }
        return true
    }

    private static func finish(_ status: Int32) -> Never {
        DebugDefaults.removeSuite()
        exit(status)
    }

    @available(macOS 14.2, *)
    private static func watch(seconds: Double) async -> Int32 {
        var detector = MeetingDetector()
        let start = Date()
        var previous: [Int32: CaptureProcess]?
        var lastFailure: String?
        say(String(format: "watching for %.0f s (start delay %.0f s, end grace %.0f s)",
                   seconds, MeetingDetector.startDelay, MeetingDetector.endGrace))
        while true {
            var processes: [CaptureProcess] = []
            do {
                processes = try MeetingAudioProbe.captureProcesses()
                lastFailure = nil
            } catch {
                let message = String(describing: error)
                if message != lastFailure { say("cannot list the audio processes: \(message)") }
                lastFailure = message
            }
            let now = Date()
            let current = Dictionary(processes.map { ($0.pid, $0) }, uniquingKeysWith: { _, last in last })
            if let before = previous {
                for process in processes {
                    if let old = before[process.pid] {
                        if old != process { say("changed " + describe(process)) }
                    } else {
                        say("added " + describe(process))
                    }
                }
                for process in before.values.sorted(by: { $0.pid < $1.pid }) where current[process.pid] == nil {
                    say("removed " + describe(process))
                }
            } else {
                say("\(processes.count) audio process(es)")
                for process in processes { say("  " + describe(process)) }
            }
            previous = current
            switch detector.observe(processes, now: now) {
            case .started: say("meeting started")
            case .ended: say("meeting ended")
            case nil: break
            }
            if now.timeIntervalSince(start) >= seconds { break }
            try? await Task.sleep(for: .seconds(1))
        }
        say(detector.isInMeeting ? "done (in a meeting)" : "done")
        return 0
    }

    private static func describe(_ process: CaptureProcess) -> String {
        let bundle = process.bundleID ?? "(no bundle id)"
        let path = process.executablePath ?? "(exited)"
        let input = process.isRunningInput ? "running input" : "not running input"
        let teams = MeetingDetector.isTeams(process) ? ", Teams" : ""
        return "pid \(process.pid) \(bundle) \(path): \(input)\(teams)"
    }

    private static func say(_ line: String) {
        FileHandle.standardOutput.write(Data(("hearsay meeting watch: " + line + "\n").utf8))
    }
}
