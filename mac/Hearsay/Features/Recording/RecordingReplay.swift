import AppKit
import SwiftUI
import HearsayCore
import os

/// Debug only. When the app is launched with `HEARSAY_REPLAY_FILE=<wav>`
/// and `HEARSAY_MODEL_DIR=<dir>` (one folder holding the model and the
/// tokenizer), feeds the file through a `RecordingController` exactly as a
/// recording: a fake microphone yields it in 0.1 s chunks paced in real
/// time from a private dispatch queue (like a capture callback), through
/// `AudioMixer`, the WAV spool, `LiveChunker`, and the live queue. When the
/// file is used up it presses Stop; the session becomes a job of a real
/// `TranscriptionQueue`, which runs the final pass.
///
/// `HEARSAY_REPLAY_FILE=<a.wav>,<b.wav>[,...]` replays the files as
/// consecutive sessions joined by Stop & Start Next (PLAN.md 4.9): when a
/// file is used up the next one starts at once, and Stop follows the last.
/// Every live job (queued, started, finished, with its session), every
/// queue event (queued, running, suspended, resumed, language, done with
/// the SRT path, failed), and the gap between two sessions are printed
/// with timestamps to stderr. The run ends when the queue has nothing left,
/// prints the tables and totals, deletes everything it wrote, and quits
/// with status 0 (1 when a job failed).
///
/// `HEARSAY_LANGUAGE` (auto, en, zh-TW, zh-CN, de, es; zh is an alias for
/// zh-TW; default en) is optional and applies to every session. Every
/// language detection and each job's final language decision are printed
/// to stdout. `HEARSAY_REPLAY_TIMING=immediate|whenIdle` picks the
/// final-pass timing (default immediate). `HEARSAY_REPLAY_SYSTEM=silence`
/// adds a second, silent source in place of system audio. Settings live in
/// a throwaway defaults suite and every file goes to a temporary folder, so
/// the user's settings, spool, and output folder are never touched
/// (cfprefsd may still leave an empty
/// `~/Library/Preferences/tw.og1o.hearsay.replay-*.plist` behind).
///
///     HEARSAY_REPLAY_FILE=/tmp/a.wav,/tmp/b.wav HEARSAY_LANGUAGE=auto \
///     HEARSAY_MODEL_DIR=Spike/models/mlx-community_whisper-large-v3-turbo \
///     .build/derived/Build/Products/Release/Hearsay.app/Contents/MacOS/Hearsay
///
/// Returns false (and does nothing) when the variables are not set.
@MainActor
enum RecordingReplay {
    static func runIfRequested(engine: WhisperEngine) -> Bool {
        let environment = ProcessInfo.processInfo.environment
        guard let file = environment["HEARSAY_REPLAY_FILE"], !file.isEmpty,
              let directory = environment["HEARSAY_MODEL_DIR"], !directory.isEmpty
        else { return false }
        let language = environment["HEARSAY_LANGUAGE"].flatMap(LanguageChoice.init(debugValue:))
            ?? .fixed(.english)
        let timing = environment["HEARSAY_REPLAY_TIMING"].flatMap(FinalPassTiming.init(rawValue:)) ?? .immediate
        let silentSystem = environment["HEARSAY_REPLAY_SYSTEM"] == "silence"
        let modelURL = URL(fileURLWithPath: directory, isDirectory: true)
        let files = file.split(separator: ",").map { URL(fileURLWithPath: String($0)) }
        Task { @MainActor in
            let status = await run(
                files: files,
                location: WhisperModelLocation(modelDirectory: modelURL, tokenizerDirectory: modelURL),
                language: language,
                timing: timing,
                silentSystem: silentSystem,
                engine: engine
            )
            DebugDefaults.removeSuite()
            exit(status)
        }
        return true
    }

    private struct JobTiming {
        var start: TimeInterval
        var seconds: TimeInterval
        var queued: TimeInterval
        var started: TimeInterval?
        var finished: TimeInterval?
        var cues = 0
        var error: String?
    }

    /// Numbers sessions and queue jobs 1, 2, 3... in the order they appear.
    @MainActor
    private final class ReplayNumbers {
        private var sessions: [Int: Int] = [:]
        private var jobs: [String: Int] = [:]

        func session(_ id: Int) -> Int {
            if let known = sessions[id] { return known }
            sessions[id] = sessions.count + 1
            return sessions.count
        }

        func job(_ id: String) -> Int {
            if let known = jobs[id] { return known }
            jobs[id] = jobs.count + 1
            return jobs.count
        }
    }

    /// Hands out the files in order, one per session.
    @MainActor
    private final class ReplaySessions {
        let samples: [[Float]]
        var next = 0

        init(samples: [[Float]]) {
            self.samples = samples
        }
    }

    private static func run(
        files: [URL], location: WhisperModelLocation, language: LanguageChoice, timing: FinalPassTiming,
        silentSystem: Bool, engine: WhisperEngine
    ) async -> Int32 {
        var loaded: [[Float]] = []
        for file in files {
            do {
                loaded.append(try AudioFileLoader.loadMono16k(url: file))
            } catch {
                say("cannot read \(file.path): \(error)")
                return 1
            }
        }
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("hearsay-replay-\(UUID().uuidString)")
        let output = root.appendingPathComponent("out", isDirectory: true)
        let suite = "tw.og1o.hearsay.replay-\(UUID().uuidString)"
        defer {
            try? fileManager.removeItem(at: root)
            DebugDefaults.removeDomain(named: suite)
        }
        guard let defaults = UserDefaults(suiteName: suite) else {
            say("cannot create the defaults suite")
            return 1
        }
        let settings = AppSettings(defaults: defaults)
        do {
            try fileManager.createDirectory(at: output, withIntermediateDirectories: true)
            settings.outputFolderBookmark = try OutputLocation.makeBookmark(for: output)
        } catch {
            say("cannot prepare \(output.path): \(error)")
            return 1
        }
        settings.languageChoice = language
        if let preferred = UserDefaults.standard.string(forKey: AppSettings.Key.preferredLanguage)
            .flatMap({ TranscriptLanguage(
                storedValue: $0,
                legacyChineseScript: UserDefaults.standard.string(forKey: AppSettings.Key.chineseScript)) }) {
            // Read only: the user's preferred language, copied into the suite.
            settings.preferredLanguage = preferred
        }
        settings.captureSystemAudio = silentSystem
        settings.keepRecording = false
        settings.finalPassTiming = timing

        let clock = ReplayClock()
        let exhausted = AsyncStream<Int>.makeStream()
        let sessions = ReplaySessions(samples: loaded)
        let sources = CaptureSources(
            requestMicrophonePermission: { true },
            makeMicrophone: {
                let index = sessions.next
                sessions.next += 1
                let samples = index < sessions.samples.count ? sessions.samples[index] : []
                return ReplayMicrophone(feed: ReplayFeed(samples: samples, clock: clock) {
                    exhausted.continuation.yield(index)
                })
            },
            makeSystemAudio: { ReplaySystemAudio(feed: ReplayFeed(samples: nil, clock: clock) {}) },
            modelLocation: { _ in location }
        )
        let spool = RecordingSpool(root: root.appendingPathComponent("spool"))
        let modelStore = ModelStore(settings: settings, rootURL: root.appendingPathComponent("models"))
        let queue = TranscriptionQueue(
            settings: settings, modelStore: modelStore, engine: engine, spool: spool,
            modelLocation: { _ in location }
        )
        // No notes flow in a replay.
        queue.notesOnScreen = { true }
        let controller = RecordingController(
            settings: settings, modelStore: modelStore, engine: engine, queue: queue,
            spool: spool, sources: sources
        )

        // Sessions and jobs numbered 1, 2, 3...
        let numbers = ReplayNumbers()
        @MainActor func number(_ session: Int) -> Int { numbers.session(session) }
        struct LiveKey: Hashable { var session: Int; var index: Int }
        var jobs: [LiveKey: JobTiming] = [:]
        controller.liveJobObserver = { event in
            let now = clock.now
            switch event {
            case let .queued(session, index, start, seconds):
                let s = number(session)
                jobs[LiveKey(session: s, index: index)] = JobTiming(start: start, seconds: seconds, queued: now)
                say(String(format: "%7.2f  s%d live %d queued (%.1f-%.1f s)", now, s, index, start, start + seconds))
            case let .started(session, index):
                let s = number(session)
                jobs[LiveKey(session: s, index: index)]?.started = now
                say(String(format: "%7.2f  s%d live %d started", now, s, index))
            case let .finished(session, index, cues, error):
                let s = number(session)
                jobs[LiveKey(session: s, index: index)]?.finished = now
                jobs[LiveKey(session: s, index: index)]?.cues = cues
                jobs[LiveKey(session: s, index: index)]?.error = error
                say(String(format: "%7.2f  s%d live %d finished, %d cues%@", now, s, index, cues,
                           error.map { ", error: \($0)" } ?? ""))
            case let .detection(session, seconds, result, decision):
                let line = String(format: "%7.2f  s%d detection over %.1f s: ", now, number(session), seconds)
                    + (result?.debugSummary ?? "failed")
                    + (decision.map { "; settled: " + $0.debugSummary } ?? "; not settled")
                say(line)
                print(line)
            }
        }

        var failed = false
        queue.eventObserver = { event in
            let now = clock.now
            @MainActor func label(_ id: String) -> String { "queue job \(numbers.job(id))" }
            switch event {
            case let .queued(id, recording):
                say(String(format: "%7.2f  %@ queued (%@)", now, label(id), recording.lastPathComponent))
            case let .running(id):
                say(String(format: "%7.2f  %@ running", now, label(id)))
            case let .suspended(id, progress):
                say(String(format: "%7.2f  %@ suspended at %.0f%%", now, label(id), progress * 100))
            case let .resumed(id):
                say(String(format: "%7.2f  %@ resumed", now, label(id)))
            case let .language(id, decision):
                let line = String(format: "%7.2f  %@ language: %@", now, label(id), decision.debugSummary)
                say(line)
                print(line)
            case let .done(id, srt):
                say(String(format: "%7.2f  %@ done: %@", now, label(id), srt.path))
            case let .failed(id, message):
                failed = true
                say(String(format: "%7.2f  %@ failed: %@", now, label(id), message))
            }
        }

        var stopPressedAt: TimeInterval?
        var gaps: [TimeInterval] = []
        controller.sessionStartObserver = { wav in
            let now = clock.now
            if let pressed = stopPressedAt {
                gaps.append(now - pressed)
                say(String(format: "%7.2f  next session recording (%@), %.3f s after Stop & Start Next",
                           now, wav.lastPathComponent, now - pressed))
                stopPressedAt = nil
            } else {
                say(String(format: "%7.2f  session recording (%@)", now, wav.lastPathComponent))
            }
        }

        // HEARSAY_REPLAY_UI=1: also show the Record tab for this session.
        let window: NSWindow? = {
            guard ProcessInfo.processInfo.environment["HEARSAY_REPLAY_UI"] == "1" else { return nil }
            let shown = NSWindow(
                contentRect: NSRect(x: 100, y: 100, width: 560, height: 760),
                styleMask: [.titled, .resizable], backing: .buffered, defer: false
            )
            shown.title = "Hearsay replay"
            shown.contentView = NSHostingView(rootView: RecordView()
                .environment(controller)
                .environment(queue)
                .environment(AIProviderStore())
                .environment(settings))
            shown.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            say("window number \(shown.windowNumber)")
            return shown
        }()
        defer { window?.close() }
        let names = zip(files, loaded).map { String(format: "%@ (%.2f s)", $0.lastPathComponent, Double($1.count) / 16_000) }
        say(String(format: "replaying %@, language %@, preferred %@, timing %@, system audio %@",
                   names.joined(separator: ", "), language.storageValue,
                   settings.preferredLanguage.rawValue, timing.rawValue, silentSystem ? "silence" : "off"))
        clock.reset()
        controller.start()
        let snapshots = ProcessInfo.processInfo.environment["HEARSAY_REPLAY_SNAPSHOTS"]
        let status = Task { @MainActor in
            var tick = 0
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                tick += 1
                let states = queue.jobs.map { job in
                    "\(job.state.rawValue) \(Int((job.progress * 100).rounded()))%"
                }.joined(separator: ", ")
                say(String(format: "%7.2f  status: recorded %.1f s, live waiting %d, live cues %d, queue [%@]",
                           clock.now, controller.elapsed, controller.liveChunksWaiting,
                           controller.liveSegments.count, states))
                if let window {
                    describeScrollViews(in: window)
                    if let snapshots, tick % 6 == 0 {
                        snapshot(window, to: URL(fileURLWithPath: snapshots)
                            .appendingPathComponent(String(format: "replay-%03d.png", tick * 5)))
                    }
                }
            }
        }
        var stopAt: TimeInterval = 0
        for await index in exhausted.stream {
            if index + 1 < loaded.count {
                stopPressedAt = clock.now
                say(String(format: "%7.2f  file %d used up; Stop & Start Next with %d live chunks waiting",
                           clock.now, index + 1, controller.liveChunksWaiting))
                controller.stopAndStartNext()
            } else {
                stopAt = clock.now
                say(String(format: "%7.2f  file %d used up; Stop with %d live chunks waiting",
                           stopAt, index + 1, controller.liveChunksWaiting))
                await controller.stop()
                break
            }
        }
        while controller.isSessionActive || queue.pendingCount > 0 || queue.hasWorkInFlight
            || queue.jobs.contains(where: \.hasLiveTail) {
            try? await Task.sleep(for: .milliseconds(100))
        }
        status.cancel()
        let doneAt = clock.now

        say("sess live  audio (s)        len   queued  started finished  latency  cues")
        for key in jobs.keys.sorted(by: { ($0.session, $0.index) < ($1.session, $1.index) }) {
            guard let job = jobs[key] else { continue }
            func time(_ value: TimeInterval?) -> String { value.map { String(format: "%8.2f", $0) } ?? "       -" }
            let latency = job.finished.map { String(format: "%8.2f", $0 - job.queued) } ?? "       -"
            say(String(format: "s%-3d %4d  %6.1f-%6.1f  %5.1f %@ %@ %@ %@  %4d",
                       key.session, key.index, job.start, job.start + job.seconds, job.seconds,
                       time(job.queued), time(job.started), time(job.finished), latency, job.cues))
        }
        let latencies = jobs.values.compactMap { job in job.finished.map { $0 - job.queued } }
        say(String(format: "totals: %d live jobs, max latency %.2f s, queue empty %.2f s after the last Stop%@",
                   jobs.count, latencies.max() ?? 0, doneAt - stopAt,
                   gaps.isEmpty ? "" : ", session gaps " + gaps.map { String(format: "%.3f s", $0) }.joined(separator: ", ")))

        for (offset, job) in queue.jobs.enumerated() {
            let number = offset + 1
            print("queue job \(number) decision: " + (job.tracker.decision?.debugSummary ?? "none"))
            if let notice = job.languageNotice {
                print("queue job \(number) notice: " + notice.message)
            }
            switch job.state {
            case .done:
                let text = job.srt.flatMap { try? String(contentsOf: $0, encoding: .utf8) } ?? ""
                let cues = text.components(separatedBy: " --> ").count - 1
                let first = SRT.parse(text).first(where: { !$0.text.isEmpty })?.text ?? ""
                say("queue job \(number) final pass: \(cues) cues, first: \(first)")
            case .failed:
                say("queue job \(number) failed: \(job.errorMessage ?? "")")
                failed = true
            default:
                say("queue job \(number) ended in \(job.state.rawValue)")
                failed = true
            }
        }
        if case .failed(let message) = controller.phase {
            say("session failed: \(message)")
            failed = true
        }
        // Everything was written below `root`; remove anything that was not.
        let rootPath = root.resolvingSymlinksInPath().path
        for url in queue.jobs.flatMap({ [$0.srt, $0.wav] }).compactMap({ $0 })
            where !url.resolvingSymlinksInPath().path.hasPrefix(rootPath) {
            say("removing \(url.path)")
            try? fileManager.removeItem(at: url)
        }
        return failed ? 1 : 0
    }

    /// Logs where every scroll view inside the window is scrolled to, so a
    /// preview that stops following new lines shows up in the log.
    private static func describeScrollViews(in window: NSWindow) {
        guard let root = window.contentView else { return }
        var found: [NSScrollView] = []
        func walk(_ view: NSView) {
            if let scroll = view as? NSScrollView { found.append(scroll) }
            view.subviews.forEach(walk)
        }
        walk(root)
        for (index, scroll) in found.enumerated() {
            let visible = scroll.documentVisibleRect
            let height = scroll.documentView?.frame.height ?? 0
            say(String(format: "         scroll view %d: showing %.0f-%.0f of %.0f pt",
                       index, visible.minY, visible.maxY, height))
        }
    }

    /// Writes the window's own content (not the screen) as a PNG.
    private static func snapshot(_ window: NSWindow, to url: URL) {
        guard let view = window.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    private static func say(_ line: String) {
        FileHandle.standardError.write(Data(("hearsay replay: " + line + "\n").utf8))
    }
}

/// Seconds since `reset()`, on the host uptime clock, readable anywhere.
private final class ReplayClock: Sendable {
    private let origin = OSAllocatedUnfairLock(initialState: ProcessInfo.processInfo.systemUptime)

    func reset() {
        origin.withLock { $0 = ProcessInfo.processInfo.systemUptime }
    }

    var now: TimeInterval {
        ProcessInfo.processInfo.systemUptime - origin.withLock { $0 }
    }

    /// Host time in seconds, the clock `TimedChunk.hostTime` is stamped on.
    var hostTime: TimeInterval { ProcessInfo.processInfo.systemUptime }
}

/// Yields `samples` (or endless silence when nil) as 0.1 s `TimedChunk`s
/// from a timer on a private serial queue, paced in real time, like a
/// capture callback. Calls `onExhausted` once when the samples run out; the
/// stream stays open until `finish()`, as a live device's would.
private final class ReplayFeed: Sendable {
    static let chunk = 1600

    private struct State: Sendable {
        var timer: DispatchSourceTimer?
        var startHostTime: TimeInterval = 0
        var chunksSent = 0
        var delivered = 0
        var exhausted = false
        var finished = false
    }

    let stream: AsyncStream<TimedChunk>
    private let continuation: AsyncStream<TimedChunk>.Continuation
    private let samples: [Float]?
    private let clock: ReplayClock
    private let onExhausted: @Sendable () -> Void
    private let state = OSAllocatedUnfairLock(initialState: State())
    private let queue = DispatchQueue(label: "tw.og1o.hearsay.replay", qos: .userInitiated)

    init(samples: [Float]?, clock: ReplayClock, onExhausted: @escaping @Sendable () -> Void) {
        (stream, continuation) = AsyncStream<TimedChunk>.makeStream(bufferingPolicy: .unbounded)
        self.samples = samples
        self.clock = clock
        self.onExhausted = onExhausted
    }

    var delivered: Int { state.withLock { $0.delivered } }

    func start() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: .milliseconds(100), leeway: .milliseconds(5))
        timer.setEventHandler { [weak self] in self?.tick() }
        let hostTime = clock.hostTime
        state.withLock {
            $0.startHostTime = hostTime
            $0.timer = timer
        }
        timer.resume()
    }

    func finish() {
        let timer = state.withLock { state -> DispatchSourceTimer? in
            state.finished = true
            defer { state.timer = nil }
            return state.timer
        }
        timer?.cancel()
        continuation.finish()
    }

    /// Sends every chunk that is due by now.
    private func tick() {
        let now = clock.hostTime
        let samples = self.samples
        let (chunks, justExhausted) = state.withLock { state -> ([TimedChunk], Bool) in
            var chunks: [TimedChunk] = []
            var justExhausted = false
            guard !state.finished, !state.exhausted else { return (chunks, justExhausted) }
            let due = Int(((now - state.startHostTime) * 10).rounded(.down)) + 1
            while state.chunksSent < due {
                let offset = state.chunksSent * Self.chunk
                let piece: [Float]
                if let samples {
                    guard offset < samples.count else {
                        state.exhausted = true
                        justExhausted = true
                        break
                    }
                    piece = Array(samples[offset..<min(offset + Self.chunk, samples.count)])
                } else {
                    piece = [Float](repeating: 0, count: Self.chunk)
                }
                chunks.append(TimedChunk(
                    samples: piece, hostTime: state.startHostTime + Double(state.chunksSent) / 10
                ))
                state.chunksSent += 1
                state.delivered += piece.count
            }
            return (chunks, justExhausted)
        }
        for chunk in chunks {
            continuation.yield(chunk)
        }
        if justExhausted {
            onExhausted()
        }
    }
}

/// The fake microphone of the replay. Pause and resume are not simulated.
@MainActor
private final class ReplayMicrophone: MicrophoneCapture {
    private let feed: ReplayFeed

    init(feed: ReplayFeed) {
        self.feed = feed
    }

    var timedSamples: AsyncStream<TimedChunk> { feed.stream }
    var failure: MicrophoneRecorderError? { nil }
    var diagnostics: MicrophoneDiagnostics {
        var diagnostics = MicrophoneDiagnostics()
        diagnostics.deviceName = "replay"
        diagnostics.samplesDelivered = feed.delivered
        return diagnostics
    }

    func start(device: AudioInputDevice?) throws { feed.start() }
    func pause() {}
    func resume() throws {}
    func stop() { feed.finish() }
}

/// Silent system audio for the replay.
@MainActor
private final class ReplaySystemAudio: SystemAudioCapture {
    private let feed: ReplayFeed

    init(feed: ReplayFeed) {
        self.feed = feed
        feed.start()
    }

    var timedSamples: AsyncStream<TimedChunk> { feed.stream }
    var failure: SystemAudioRecorderError? { nil }
    func pause() {}
    func resume() {}
    func stop() { feed.finish() }
}
