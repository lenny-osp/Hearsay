import Foundation

/// Decides where the live preview cuts the recording into chunks
/// (PLAN.md 4.1 step 4): a chunk closes at 30 s, or earlier once it holds at
/// least 10 s and the last 2 s were below the silence threshold.
///
/// The caller feeds the running number of 16 kHz samples recorded so far
/// together with the RMS level of the samples added since the previous call
/// (normally one 0.1 s window, see `windowSamples`). Closed chunks come back
/// as sample offsets into the recording; together with `flush()` they cover
/// the recording contiguously from sample 0, with no gaps or overlaps.
///
/// A value type with no clock: time is the sample count, so tests are exact.
public struct LiveChunker: Sendable, Equatable {
    public static let sampleRate = 16_000
    /// Hard cut: a chunk never holds more than this.
    public static let maxChunkSeconds: TimeInterval = 30
    /// A silence cut needs at least this much audio in the chunk.
    public static let minChunkSeconds: TimeInterval = 10
    /// Trailing silence that allows an early cut.
    public static let silenceCutSeconds: TimeInterval = 2
    /// Same threshold as the level meter's silence warning (-55 dBFS).
    public static let silenceThresholdDB: Double = LevelMeter.silenceThresholdDB
    /// Samples per level window the caller should measure (0.1 s).
    public static let windowSamples = sampleRate / 10

    public let maxChunkSamples: Int
    public let minChunkSamples: Int
    public let silenceCutSamples: Int
    public let silenceThresholdDB: Double

    /// Start of the chunk that is still open.
    public private(set) var chunkStart = 0
    /// Samples observed so far.
    public private(set) var sampleCount = 0
    /// Where the current run of silent windows began, nil after sound.
    private var silentSince: Int?

    public init(
        sampleRate: Int = LiveChunker.sampleRate,
        maxChunkSeconds: TimeInterval = LiveChunker.maxChunkSeconds,
        minChunkSeconds: TimeInterval = LiveChunker.minChunkSeconds,
        silenceCutSeconds: TimeInterval = LiveChunker.silenceCutSeconds,
        silenceThresholdDB: Double = LiveChunker.silenceThresholdDB
    ) {
        self.maxChunkSamples = Int((maxChunkSeconds * Double(sampleRate)).rounded())
        self.minChunkSamples = Int((minChunkSeconds * Double(sampleRate)).rounded())
        self.silenceCutSamples = Int((silenceCutSeconds * Double(sampleRate)).rounded())
        self.silenceThresholdDB = silenceThresholdDB
    }

    /// Records that the recording now holds `totalSamples` samples and that
    /// the samples added since the last call measured `rmsDB` (nil or
    /// -infinity count as silence, as in `LevelMeter`). Returns the chunks
    /// that closed, oldest first; usually none. A count that does not grow
    /// is ignored.
    public mutating func observe(totalSamples: Int, rmsDB: Double?) -> [Range<Int>] {
        guard totalSamples > sampleCount else { return [] }
        let windowStart = sampleCount
        sampleCount = totalSamples

        let isSound = rmsDB.map { $0 > silenceThresholdDB } ?? false
        if isSound {
            silentSince = nil
        } else if silentSince == nil {
            silentSince = windowStart
        }

        var closed: [Range<Int>] = []
        // Hard cuts at exactly 30 s, several if the caller jumped far ahead.
        while sampleCount - chunkStart >= maxChunkSamples {
            closed.append(cut(at: chunkStart + maxChunkSamples))
        }
        // Early cut at the end of a long enough silence.
        if let silentSince,
           sampleCount - chunkStart >= minChunkSamples,
           sampleCount - max(silentSince, chunkStart) >= silenceCutSamples {
            closed.append(cut(at: sampleCount))
        }
        return closed
    }

    /// Closes the open chunk on Stop. Nil when it is empty.
    public mutating func flush() -> Range<Int>? {
        guard sampleCount > chunkStart else { return nil }
        return cut(at: sampleCount)
    }

    private mutating func cut(at end: Int) -> Range<Int> {
        let range = chunkStart..<end
        chunkStart = end
        if let since = silentSince {
            // Silence before the new chunk does not count toward its cut;
            // right after a silence cut the run starts over.
            silentSince = end >= sampleCount ? nil : max(since, end)
        }
        return range
    }
}
