import Foundation
import os

/// A chunk of 16 kHz mono Float32 samples stamped with the host time of its
/// first sample (PLAN.md 4.1, step 3).
///
/// `hostTime` is in seconds on the host clock (`mach_absolute_time`, the
/// clock both `AVAudioTime` and ScreenCaptureKit sample buffers use) with the
/// recorder's paused time removed, so a pause leaves no gap between sources.
public struct TimedChunk: Sendable, Equatable {
    public var samples: [Float]
    public var hostTime: TimeInterval

    public init(samples: [Float], hostTime: TimeInterval) {
        self.samples = samples
        self.hostTime = hostTime
    }
}

/// One fixed-size block of mixed output plus the level of each source inside
/// it, for the main meter and the two small per-source meters.
public struct MixedChunk: Sendable, Equatable {
    /// 16 kHz mono samples within [-1, 1].
    public var mixed: [Float]
    /// dBFS RMS of the microphone's contribution; nil when the microphone
    /// supplied no samples to this block (not started, ended, or lagging).
    public var micRMSDB: Double?
    /// dBFS RMS of the system audio's contribution; nil as for `micRMSDB`.
    public var systemRMSDB: Double?

    public init(mixed: [Float], micRMSDB: Double?, systemRMSDB: Double?) {
        self.mixed = mixed
        self.micRMSDB = micRMSDB
        self.systemRMSDB = systemRMSDB
    }
}

/// Aligns the microphone and system audio on host time and sums them into
/// one 16 kHz mono stream (PLAN.md 4.1, step 3).
///
/// The struct is the pure mixing state machine: feed it chunks with
/// `append(_:from:)`, tell it a source ended with `finish(_:)`, and collect
/// the `MixedChunk`s each call returns. `mix(mic:system:onSourceEnd:)` runs
/// the same machine over two `AsyncStream`s.
///
/// Rules:
/// - Positions are sample offsets from the first chunk's host time. Each
///   source keeps a buffer of samples not yet emitted, anchored at an
///   absolute position.
/// - A chunk that lands within `resyncToleranceSeconds` of where the source's
///   previous chunk ended is placed right after it, so timestamp jitter never
///   opens gaps or overlaps. Beyond the tolerance the source resyncs to the
///   chunk's host time: a gap is filled with silence, an overlap is trimmed.
///   This also absorbs slow clock drift between the two devices.
/// - A chunk stamped more than `clockSanitySeconds` away from the other live
///   source is clamped to that distance, so a foreign clock cannot produce
///   hours of padding.
/// - Output waits for the slower source, but never by more than
///   `maxLagSeconds`: a source that is further behind is padded with silence
///   and whatever arrives later for already emitted time is dropped.
/// - Where both sources supply samples they are summed and soft-limited
///   (`softLimit`), so the result stays within [-1, 1]. Where only one does,
///   its samples pass through unchanged (clamped to [-1, 1]), so a mic-only
///   recording is bit-identical to the unmixed microphone.
/// - Output comes in blocks of `chunkSize` samples; only the final block,
///   emitted once every source has ended, may be shorter.
public struct AudioMixer: Sendable {
    public enum Source: Sendable, Hashable, CaseIterable {
        case mic
        case system
    }

    public static let sampleRate: Double = 16_000
    /// 0.1 s at 16 kHz.
    public static let chunkSize = 1600
    public static let maxLagSeconds: TimeInterval = 0.5
    public static let resyncToleranceSeconds: TimeInterval = 0.05
    /// A chunk stamped further than this from the other live source is
    /// clamped to this distance (guards against mismatched clocks).
    public static let clockSanitySeconds: TimeInterval = 5
    /// Below this magnitude the limiter is the identity.
    public static let limiterKnee: Float = 0.5

    static let maxLagSamples = Int(maxLagSeconds * sampleRate)
    static let resyncToleranceSamples = Int(resyncToleranceSeconds * sampleRate)
    static let clockSanitySamples = Int(clockSanitySeconds * sampleRate)

    private struct Lane: Sendable {
        var started = false
        var ended = false
        /// Absolute position just after the last placed chunk.
        var nextPosition = 0
        /// Samples not yet emitted; `buffer[bufferHead]` sits at `bufferStart`.
        var buffer: [Float] = []
        var bufferHead = 0
        var bufferStart = 0

        var bufferedCount: Int { buffer.count - bufferHead }
        var bufferEnd: Int { bufferStart + bufferedCount }

        mutating func compactIfNeeded() {
            if bufferHead > 0, bufferHead >= buffer.count / 2 {
                buffer.removeFirst(bufferHead)
                bufferHead = 0
            }
        }
    }

    private var anchor: TimeInterval?
    /// Absolute position of the next output sample.
    private var emitted = 0
    private var mic = Lane()
    private var system = Lane()

    /// `sources` lists the sources that will deliver chunks; any other source
    /// counts as already ended.
    public init(sources: Set<Source> = Set(Source.allCases)) {
        for source in Source.allCases where !sources.contains(source) {
            self[source].ended = true
        }
    }

    private subscript(source: Source) -> Lane {
        get { source == .mic ? mic : system }
        set {
            if source == .mic { mic = newValue } else { system = newValue }
        }
    }

    /// True once every source has ended and all output was returned.
    public var isFinished: Bool {
        mic.ended && system.ended && mic.bufferedCount == 0 && system.bufferedCount == 0
    }

    // MARK: - Input

    /// Adds one chunk from `source` and returns the blocks that became ready.
    public mutating func append(_ chunk: TimedChunk, from source: Source) -> [MixedChunk] {
        guard !self[source].ended, !chunk.samples.isEmpty else { return [] }
        if anchor == nil {
            anchor = chunk.hostTime
        }
        let base = anchor ?? chunk.hostTime
        var position = Int(((chunk.hostTime - base) * Self.sampleRate).rounded())
        let other = self[source == .mic ? .system : .mic]
        if other.started, !other.ended {
            // Both sources run in real time, so a stamp far from the other
            // source means a clock mismatch, not a real offset.
            position = min(max(position, other.nextPosition - Self.clockSanitySamples),
                           other.nextPosition + Self.clockSanitySamples)
        }
        if position < 0, emitted == 0 {
            // Earlier than anything seen and nothing emitted yet: move the
            // anchor back instead of losing the start of this source.
            rebase(by: -position)
            position = 0
        }

        var lane = self[source]
        if lane.started, abs(position - lane.nextPosition) <= Self.resyncToleranceSamples {
            position = lane.nextPosition
        }
        lane.started = true
        lane.nextPosition = position + chunk.samples.count

        var samples = chunk.samples[...]
        if position < emitted {
            samples = samples.dropFirst(emitted - position)
            position = emitted
        }
        if lane.bufferedCount == 0 {
            lane.buffer.removeAll(keepingCapacity: true)
            lane.bufferHead = 0
            lane.bufferStart = position
        } else if position > lane.bufferEnd {
            lane.buffer.append(contentsOf: repeatElement(0, count: position - lane.bufferEnd))
        } else if position < lane.bufferEnd {
            samples = samples.dropFirst(lane.bufferEnd - position)
        }
        lane.buffer.append(contentsOf: samples)
        self[source] = lane
        return drain()
    }

    /// Marks `source` as ended and returns the blocks that became ready; once
    /// both sources ended this includes the final, possibly short, block.
    public mutating func finish(_ source: Source) -> [MixedChunk] {
        self[source].ended = true
        return drain()
    }

    private mutating func rebase(by shift: Int) {
        anchor = anchor.map { $0 - Double(shift) / Self.sampleRate }
        for source in Source.allCases where self[source].started {
            self[source].nextPosition += shift
            self[source].bufferStart += shift
        }
    }

    // MARK: - Output

    private func frontier(_ lane: Lane) -> Int {
        lane.bufferedCount == 0 ? emitted : lane.bufferEnd
    }

    private mutating func drain() -> [MixedChunk] {
        let lanes = [mic, system]
        let highest = lanes.map(frontier).max() ?? emitted
        let active = lanes.filter { !$0.ended }
        var output: [MixedChunk] = []
        if active.isEmpty {
            while emitted < highest {
                output.append(takeBlock(count: min(Self.chunkSize, highest - emitted)))
            }
            return output
        }
        let slowest = active.map(frontier).min() ?? emitted
        let ready = max(slowest, highest - Self.maxLagSamples)
        while emitted + Self.chunkSize <= ready {
            output.append(takeBlock(count: Self.chunkSize))
        }
        return output
    }

    private mutating func takeBlock(count: Int) -> MixedChunk {
        let micPart = take(count: count, from: .mic)
        let systemPart = take(count: count, from: .system)
        emitted += count
        var mixed = [Float](repeating: 0, count: count)
        switch (micPart, systemPart) {
        case let (micSamples?, systemSamples?):
            for index in 0..<count {
                mixed[index] = Self.softLimit(micSamples[index] + systemSamples[index])
            }
        case let (only?, nil), let (nil, only?):
            for index in 0..<count {
                mixed[index] = min(1, max(-1, only[index]))
            }
        case (nil, nil):
            break
        }
        return MixedChunk(
            mixed: mixed,
            micRMSDB: micPart.flatMap { LevelMeter.rmsDB(floatSamples: $0) },
            systemRMSDB: systemPart.flatMap { LevelMeter.rmsDB(floatSamples: $0) }
        )
    }

    /// Removes the samples covering [emitted, emitted + count) from `source`.
    /// Returns nil when the source has none there; uncovered samples are 0.
    private mutating func take(count: Int, from source: Source) -> [Float]? {
        var lane = self[source]
        let end = emitted + count
        guard lane.bufferedCount > 0, lane.bufferStart < end else { return nil }
        if lane.bufferStart < emitted {
            // Not expected (input is trimmed to `emitted`), but never index
            // before the window.
            let stale = min(lane.bufferedCount, emitted - lane.bufferStart)
            lane.bufferHead += stale
            lane.bufferStart += stale
            guard lane.bufferedCount > 0 else {
                lane.compactIfNeeded()
                self[source] = lane
                return nil
            }
        }
        var window = [Float](repeating: 0, count: count)
        let offset = lane.bufferStart - emitted
        let available = min(lane.bufferedCount, end - lane.bufferStart)
        for index in 0..<available {
            window[offset + index] = lane.buffer[lane.bufferHead + index]
        }
        lane.bufferHead += available
        lane.bufferStart += available
        lane.compactIfNeeded()
        self[source] = lane
        return window
    }

    /// Identity below `limiterKnee`, then a tanh curve that approaches 1:
    /// continuous, with slope 1 at the knee, and always within (-1, 1).
    public static func softLimit(_ value: Float) -> Float {
        let magnitude = abs(value)
        guard magnitude > limiterKnee else { return value }
        let headroom = 1 - limiterKnee
        let limited = limiterKnee + headroom * tanh((magnitude - limiterKnee) / headroom)
        return value < 0 ? -limited : limited
    }

    // MARK: - Streams

    private enum Event: Sendable {
        case chunk(Source, TimedChunk)
        case ended(Source)
    }

    /// Mixes two recorder streams. A nil source is treated as absent, so
    /// `mix(mic: stream, system: nil)` re-blocks the microphone unchanged.
    /// The result finishes after both sources have finished.
    /// `onSourceEnd` is called (on an arbitrary thread) when each source's
    /// stream finishes, before the mixer sees the end, so the owner can react
    /// to a device that went away.
    public static func mix(
        mic: AsyncStream<TimedChunk>?,
        system: AsyncStream<TimedChunk>?,
        onSourceEnd: (@Sendable (Source) -> Void)? = nil
    ) -> AsyncStream<MixedChunk> {
        let (output, outputContinuation) = AsyncStream<MixedChunk>.makeStream(bufferingPolicy: .unbounded)
        let (events, eventContinuation) = AsyncStream<Event>.makeStream(bufferingPolicy: .unbounded)

        var sources: Set<Source> = []
        var feeders: [Task<Void, Never>] = []
        for (source, stream) in [(Source.mic, mic), (Source.system, system)] {
            guard let stream else { continue }
            sources.insert(source)
            feeders.append(Task {
                for await chunk in stream {
                    eventContinuation.yield(.chunk(source, chunk))
                }
                onSourceEnd?(source)
                eventContinuation.yield(.ended(source))
            })
        }
        if sources.isEmpty {
            eventContinuation.finish()
        }

        let initialSources = sources
        let feederTasks = feeders
        let consumer = Task {
            var mixer = AudioMixer(sources: initialSources)
            for await event in events {
                let blocks: [MixedChunk]
                switch event {
                case let .chunk(source, chunk): blocks = mixer.append(chunk, from: source)
                case let .ended(source): blocks = mixer.finish(source)
                }
                for block in blocks {
                    outputContinuation.yield(block)
                }
                if mixer.isFinished {
                    break
                }
            }
            eventContinuation.finish()
            outputContinuation.finish()
        }
        outputContinuation.onTermination = { _ in
            for feeder in feederTasks {
                feeder.cancel()
            }
            consumer.cancel()
        }
        return output
    }
}

// MARK: - Recorder output

/// Shared output of `MicrophoneRecorder` and `SystemAudioRecorder`: an
/// untimed and a timed stream of the same chunks, plus pause bookkeeping.
///
/// Both streams buffer from the moment the recorder exists. A recording is
/// read through one of them: the first one accessed claims the chunks and
/// the other is finished and released, so an unread stream never grows for
/// the length of a meeting. Thread-safe; the realtime callbacks yield here.
final class ChunkFanout: Sendable {
    private struct State: Sendable {
        var plainStream: AsyncStream<[Float]>?
        var plainContinuation: AsyncStream<[Float]>.Continuation?
        var timedStream: AsyncStream<TimedChunk>?
        var timedContinuation: AsyncStream<TimedChunk>.Continuation?
        var claimed = false
        var paused = false
        var pausedAt: TimeInterval = 0
        var pausedTotal: TimeInterval = 0
    }

    private let state: OSAllocatedUnfairLock<State>

    init() {
        let (plain, plainContinuation) = AsyncStream<[Float]>.makeStream(bufferingPolicy: .unbounded)
        let (timed, timedContinuation) = AsyncStream<TimedChunk>.makeStream(bufferingPolicy: .unbounded)
        state = OSAllocatedUnfairLock(initialState: State(
            plainStream: plain,
            plainContinuation: plainContinuation,
            timedStream: timed,
            timedContinuation: timedContinuation
        ))
    }

    var samples: AsyncStream<[Float]> {
        state.withLock { state in
            if !state.claimed {
                state.claimed = true
                state.timedContinuation?.finish()
                state.timedContinuation = nil
                state.timedStream = nil
            }
            return state.plainStream ?? Self.finishedStream()
        }
    }

    var timedSamples: AsyncStream<TimedChunk> {
        state.withLock { state in
            if !state.claimed {
                state.claimed = true
                state.plainContinuation?.finish()
                state.plainContinuation = nil
                state.plainStream = nil
            }
            return state.timedStream ?? Self.finishedStream()
        }
    }

    /// Yields a chunk whose first sample was captured at `hostTime`. Chunks
    /// that arrive while paused are dropped.
    func yield(_ samples: [Float], hostTime: TimeInterval) {
        guard !samples.isEmpty else { return }
        state.withLock { state in
            guard !state.paused else { return }
            state.plainContinuation?.yield(samples)
            state.timedContinuation?.yield(TimedChunk(samples: samples, hostTime: hostTime - state.pausedTotal))
        }
    }

    func pause(at hostTime: TimeInterval) {
        state.withLock { state in
            guard !state.paused else { return }
            state.paused = true
            state.pausedAt = hostTime
        }
    }

    func resume(at hostTime: TimeInterval) {
        state.withLock { state in
            guard state.paused else { return }
            state.paused = false
            state.pausedTotal += max(0, hostTime - state.pausedAt)
        }
    }

    func finish() {
        state.withLock { state in
            state.plainContinuation?.finish()
            state.timedContinuation?.finish()
        }
    }

    private static func finishedStream<Element>() -> AsyncStream<Element> {
        let (stream, continuation) = AsyncStream<Element>.makeStream()
        continuation.finish()
        return stream
    }
}

/// Current host time in seconds, on the clock `TimedChunk.hostTime` uses.
func currentHostTimeSeconds() -> TimeInterval {
    var info = mach_timebase_info_data_t()
    mach_timebase_info(&info)
    let ticks = Double(mach_absolute_time())
    return ticks * Double(info.numer) / Double(info.denom) / 1_000_000_000
}
