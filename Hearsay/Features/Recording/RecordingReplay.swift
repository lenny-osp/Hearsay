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
/// file is used up it presses Stop, waits for the final pass, prints one
/// line per live job (queued, started, finished, audio seconds, cues) and
/// the totals to stderr, deletes everything it wrote, and quits with
/// status 0 (1 when the final pass failed).
///
/// `HEARSAY_LANGUAGE` (auto, en, zh, de, es; default en) is optional. Every
/// language detection and the session's final language decision are
/// printed to stdout. `HEARSAY_REPLAY_SYSTEM=silence`
/// adds a second, silent source in place of system audio. Settings live in
/// a throwaway defaults suite and every file goes to a temporary folder, so
/// the user's settings, spool, and output folder are never touched
/// (cfprefsd may still leave an empty
/// `~/Library/Preferences/tw.og1o.hearsay.replay-*.plist` behind).
///
///     HEARSAY_REPLAY_FILE=/tmp/meeting.wav HEARSAY_LANGUAGE=zh \
///     HEARSAY_MODEL_DIR=Spike/models/mlx-community_whisper-large-v3-turbo \
///     .build/derived/Build/Products/Debug/Hearsay.app/Contents/MacOS/Hearsay
///
/// Returns false (and does nothing) when the variables are not set.
@MainActor
enum RecordingReplay {
    static func runIfRequested(engine: WhisperEngine) -> Bool {
        let environment = ProcessInfo.processInfo.environment
        guard let file = environment["HEARSAY_REPLAY_FILE"], !file.isEmpty,
              let directory = environment["HEARSAY_MODEL_DIR"], !directory.isEmpty
        else { return false }
        let language = environment["HEARSAY_LANGUAGE"].flatMap(LanguageChoice.init(storageValue:))
            ?? .fixed(.english)
        let silentSystem = environment["HEARSAY_REPLAY_SYSTEM"] == "silence"
        let modelURL = URL(fileURLWithPath: directory, isDirectory: true)
        Task { @MainActor in
            let status = await run(
                file: URL(fileURLWithPath: file),
                location: WhisperModelLocation(modelDirectory: modelURL, tokenizerDirectory: modelURL),
                language: language,
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

    private static func run(
        file: URL, location: WhisperModelLocation, language: LanguageChoice, silentSystem: Bool,
        engine: WhisperEngine
    ) async -> Int32 {
        let samples: [Float]
        do {
            samples = try AudioFileLoader.loadMono16k(url: file)
        } catch {
            say("cannot read \(file.path): \(error)")
            return 1
        }
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("hearsay-replay-\(UUID().uuidString)")
        let output = root.appendingPathComponent("out", isDirectory: true)
        let suite = "tw.og1o.hearsay.replay-\(UUID().uuidString)"
        defer {
            try? fileManager.removeItem(at: root)
            UserDefaults.standard.removePersistentDomain(forName: suite)
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
            .flatMap(TranscriptLanguage.init(rawValue:)) {
            // Read only: the user's preferred language, copied into the suite.
            settings.preferredLanguage = preferred
        }
        settings.captureSystemAudio = silentSystem
        settings.keepRecording = false

        let clock = ReplayClock()
        let exhausted = AsyncStream<Void>.makeStream()
        let sources = CaptureSources(
            requestMicrophonePermission: { true },
            makeMicrophone: {
                ReplayMicrophone(feed: ReplayFeed(samples: samples, clock: clock) {
                    exhausted.continuation.yield()
                })
            },
            makeSystemAudio: { ReplaySystemAudio(feed: ReplayFeed(samples: nil, clock: clock) {}) },
            modelLocation: { _ in location }
        )
        let controller = RecordingController(
            settings: settings,
            modelStore: ModelStore(settings: settings, rootURL: root.appendingPathComponent("models")),
            engine: engine,
            spool: RecordingSpool(root: root.appendingPathComponent("spool")),
            sources: sources
        )

        var jobs: [Int: JobTiming] = [:]
        controller.liveJobObserver = { event in
            let now = clock.now
            switch event {
            case let .queued(index, start, seconds):
                jobs[index] = JobTiming(start: start, seconds: seconds, queued: now)
                say(String(format: "%7.2f  job %d queued (%.1f-%.1f s)", now, index, start, start + seconds))
            case let .started(index):
                jobs[index]?.started = now
                say(String(format: "%7.2f  job %d started", now, index))
            case let .finished(index, cues, error):
                jobs[index]?.finished = now
                jobs[index]?.cues = cues
                jobs[index]?.error = error
                say(String(format: "%7.2f  job %d finished, %d cues%@", now, index, cues, error.map { ", error: \($0)" } ?? ""))
            case let .detection(seconds, result, decision):
                let line = String(format: "%7.2f  detection over %.1f s: ", now, seconds)
                    + (result?.debugSummary ?? "failed")
                    + (decision.map { "; settled: " + $0.debugSummary } ?? "; not settled")
                say(line)
                print(line)
            }
        }

        // HEARSAY_REPLAY_UI=1: also show the Record tab for this session.
        var window: NSWindow?
        if ProcessInfo.processInfo.environment["HEARSAY_REPLAY_UI"] == "1" {
            let shown = NSWindow(
                contentRect: NSRect(x: 100, y: 100, width: 560, height: 760),
                styleMask: [.titled, .resizable], backing: .buffered, defer: false
            )
            shown.title = "Hearsay replay"
            shown.contentView = NSHostingView(rootView: RecordView()
                .environment(controller)
                .environment(AIProviderStore())
                .environment(settings))
            shown.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            window = shown
            say("window number \(shown.windowNumber)")
        }
        defer { window?.close() }
        say(String(format: "replaying %@ (%.2f s), language %@, preferred %@, system audio %@",
                   file.lastPathComponent, Double(samples.count) / 16_000, language.storageValue,
                   settings.preferredLanguage.code, silentSystem ? "silence" : "off"))
        clock.reset()
        controller.start()
        let snapshots = ProcessInfo.processInfo.environment["HEARSAY_REPLAY_SNAPSHOTS"]
        let status = Task { @MainActor in
            var tick = 0
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                tick += 1
                say(String(format: "%7.2f  status: recorded %.1f s, waiting %d, live cues %d",
                           clock.now, controller.elapsed, controller.liveChunksWaiting, controller.liveSegments.count))
                if let window {
                    describeScrollViews(in: window)
                    if let snapshots, tick % 6 == 0 {
                        snapshot(window, to: URL(fileURLWithPath: snapshots)
                            .appendingPathComponent(String(format: "replay-%03d.png", tick * 5)))
                    }
                }
            }
        }
        for await _ in exhausted.stream { break }
        let stopAt = clock.now
        let waitingAtStop = controller.liveChunksWaiting
        say(String(format: "%7.2f  file used up; Stop with %d live chunks waiting", stopAt, waitingAtStop))
        await controller.stop()
        while controller.isTranscribing || controller.phase == .stopping {
            try? await Task.sleep(for: .milliseconds(100))
        }
        status.cancel()
        let doneAt = clock.now

        say("job  audio (s)        len   queued  started finished  latency  cues")
        for index in jobs.keys.sorted() {
            guard let job = jobs[index] else { continue }
            func time(_ value: TimeInterval?) -> String { value.map { String(format: "%8.2f", $0) } ?? "       -" }
            let latency = job.finished.map { String(format: "%8.2f", $0 - job.queued) } ?? "       -"
            say(String(format: "%3d  %6.1f-%6.1f  %5.1f %@ %@ %@ %@  %4d",
                       index, job.start, job.start + job.seconds, job.seconds,
                       time(job.queued), time(job.started), time(job.finished), latency, job.cues))
        }
        let latencies = jobs.values.compactMap { job in job.finished.map { $0 - job.queued } }
        let duringRecording = jobs.values.filter { ($0.finished ?? .infinity) <= stopAt }.count
        say(String(format: "totals: %d jobs, %d finished before Stop, %d waiting at Stop, max latency %.2f s, "
                   + "live cues %d, final pass done %.2f s after Stop",
                   jobs.count, duringRecording, waitingAtStop, latencies.max() ?? 0,
                   controller.liveSegments.count, doneAt - stopAt))

        print("decision: " + (controller.sessionDecision?.debugSummary ?? "none"))
        if let notice = controller.languageNotice {
            print("notice: " + notice.message)
        }
        var failed = false
        switch controller.phase {
        case .finished(let srt, _):
            let cues = srt.flatMap { try? String(contentsOf: $0, encoding: .utf8) }
                .map { $0.components(separatedBy: " --> ").count - 1 } ?? 0
            say("final pass: \(cues) cues")
        case .failed(let message):
            say("final pass failed: \(message)")
            failed = true
        default:
            say("ended in \(controller.phase)")
            failed = true
        }
        // Everything was written below `root`; remove anything that was not.
        let rootPath = root.resolvingSymlinksInPath().path
        for url in [controller.finishedTranscript, controller.finishedRecording].compactMap({ $0 })
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
