import Foundation

/// What `MeetingDetector.observe(_:now:)` reports when the meeting state changes.
public enum MeetingEvent: Equatable, Sendable {
    case started
    case ended
}

/// One process that CoreAudio lists as an audio process object, reduced to
/// what the detector needs (see `MeetingAudioProbe`).
public struct CaptureProcess: Equatable, Sendable {
    public var pid: Int32
    /// The process's bundle identifier as CoreAudio reports it, if any.
    public var bundleID: String?
    /// The executable's path (`proc_pidpath`), if it could be read.
    public var executablePath: String?
    /// `kAudioProcessPropertyIsRunningInput`: the process is capturing audio now.
    public var isRunningInput: Bool

    public init(pid: Int32, bundleID: String? = nil, executablePath: String? = nil, isRunningInput: Bool) {
        self.pid = pid
        self.bundleID = bundleID
        self.executablePath = executablePath
        self.isRunningInput = isRunningInput
    }
}

/// Decides from successive probe results when a Microsoft Teams meeting
/// starts and ends (PLAN.md 4.10). Pure: no timers, no CoreAudio; the caller
/// passes the time. Port of `MeetingDetector.cs` (Windows).
///
/// The signal: when Teams joins a meeting or call it opens a capture stream
/// and keeps it running until the call ends, even while muted. So a meeting
/// is a Teams process with `isRunningInput` true.
///
/// Teams matching (Mac): bundle id `com.microsoft.teams` (classic) or
/// `com.microsoft.teams2` (new), or either followed by "." and more (helpers
/// such as a ModuleHost, which new Teams uses for calls), case-insensitive.
/// When the bundle id does not match or is nil, an executable path inside an
/// app bundle whose name starts with "Microsoft Teams" (a path component
/// "Microsoft Teams....app") also counts.
///
/// `started` is reported once a Teams input stream has been present at every
/// observation for `startDelay` (the first observation that sees one starts
/// the clock; one without resets it). `ended` is reported once none has been
/// seen for `endGrace`, measured from the last observation that saw one
/// (Teams closes and reopens its stream on a device switch). After `ended` a
/// new `started` needs the full delay again.
///
/// The Mac probe is listener-driven, not polled: use `nextDeadline` to
/// schedule the re-observation that lets a pending start or end fire.
/// Not thread-safe: use from one actor or queue.
public struct MeetingDetector: Sendable {
    /// How long a Teams input stream must be seen before the meeting counts as started.
    public static let startDelay: TimeInterval = 2
    /// How long no Teams input stream may be seen before the meeting counts as ended.
    public static let endGrace: TimeInterval = 15

    private static let teamsBundleIDs = ["com.microsoft.teams", "com.microsoft.teams2"]
    private static let teamsAppPrefix = "microsoft teams"

    private var presentSince: Date?
    private var lastSeen: Date?
    private var lastObservationPresent = false

    /// True between a reported `.started` and the next `.ended`.
    public private(set) var isInMeeting = false

    public init() {}

    /// The time at which a pending event fires if the next observation sees
    /// the same state as the last one, or nil when nothing is pending.
    ///
    /// - Not in a meeting and a Teams stream has been seen since some time:
    ///   that time plus `startDelay`.
    /// - In a meeting and the last observation saw no Teams stream: the last
    ///   observation that saw one plus `endGrace`.
    /// - Otherwise nil (idle, or in a meeting whose stream is still there:
    ///   the stream's disappearance is itself a change the probe reports).
    ///
    /// The caller re-observes (with the current probe result and time) at
    /// this moment, then asks again: an observation that sees a change
    /// moves or clears the deadline.
    public var nextDeadline: Date? {
        if isInMeeting {
            guard !lastObservationPresent, let lastSeen else { return nil }
            return lastSeen.addingTimeInterval(Self.endGrace)
        }
        return presentSince?.addingTimeInterval(Self.startDelay)
    }

    /// True when `process` is Microsoft Teams (or one of its helpers). The
    /// process need not be running input; `observe` checks that.
    public static func isTeams(_ process: CaptureProcess) -> Bool {
        if let bundleID = process.bundleID?.lowercased(), !bundleID.isEmpty {
            for teams in teamsBundleIDs where bundleID == teams || bundleID.hasPrefix(teams + ".") {
                return true
            }
        }
        guard let path = process.executablePath, !path.isEmpty else { return false }
        return path.split(separator: "/").contains { component in
            let name = component.lowercased()
            return name.hasSuffix(".app") && name.hasPrefix(teamsAppPrefix)
        }
    }

    /// Takes one probe result at `now` and returns the meeting event it
    /// completes, or nil when the state did not change.
    public mutating func observe(_ processes: [CaptureProcess], now: Date) -> MeetingEvent? {
        let present = processes.contains { $0.isRunningInput && Self.isTeams($0) }
        lastObservationPresent = present

        if !isInMeeting {
            guard present else {
                presentSince = nil
                return nil
            }
            let since = presentSince ?? now
            presentSince = since
            guard now.timeIntervalSince(since) >= Self.startDelay else { return nil }
            presentSince = nil
            lastSeen = now
            isInMeeting = true
            return .started
        }

        if present {
            lastSeen = now
            return nil
        }
        guard let lastSeen, now.timeIntervalSince(lastSeen) >= Self.endGrace else { return nil }
        isInMeeting = false
        return .ended
    }
}
