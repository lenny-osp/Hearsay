import XCTest
@testable import HearsayCore

/// PLAN.md 4.1: cut at 30 s, or earlier at >= 2 s of silence (< -55 dB)
/// once the chunk holds >= 10 s. Chunks are contiguous sample ranges.
final class LiveChunkerTests: XCTestCase {
    private let rate = LiveChunker.sampleRate
    private let window = LiveChunker.windowSamples
    private let speech = -20.0
    private let silence = -70.0

    /// Feeds `seconds` of 0.1 s windows at `db` and collects closed chunks.
    private func feed(_ chunker: inout LiveChunker, seconds: Double, db: Double?) -> [Range<Int>] {
        var closed: [Range<Int>] = []
        let windows = Int((seconds * 10).rounded())
        for _ in 0..<windows {
            closed += chunker.observe(totalSamples: chunker.sampleCount + window, rmsDB: db)
        }
        return closed
    }

    func testCutsAtThirtySecondsOfSpeech() {
        var chunker = LiveChunker()
        XCTAssertEqual(feed(&chunker, seconds: 29.9, db: speech), [])
        XCTAssertEqual(feed(&chunker, seconds: 0.1, db: speech), [0..<(30 * rate)])
        XCTAssertEqual(feed(&chunker, seconds: 30, db: speech), [(30 * rate)..<(60 * rate)])
    }

    func testCutsEarlyAtTwoSecondsOfSilenceAfterTenSeconds() {
        var chunker = LiveChunker()
        XCTAssertEqual(feed(&chunker, seconds: 12, db: speech), [])
        XCTAssertEqual(feed(&chunker, seconds: 1.9, db: silence), [])
        XCTAssertEqual(feed(&chunker, seconds: 0.1, db: silence), [0..<(14 * rate)])
    }

    func testDigitalSilenceAndMissingLevelCountAsSilence() {
        var chunker = LiveChunker()
        _ = feed(&chunker, seconds: 10, db: speech)
        XCTAssertEqual(feed(&chunker, seconds: 1, db: -.infinity), [])
        XCTAssertEqual(feed(&chunker, seconds: 1, db: nil), [0..<(12 * rate)])
    }

    func testThresholdIsExclusiveLikeTheLevelMeter() {
        // -55 dB exactly is not above the threshold, so it is silence.
        var chunker = LiveChunker()
        _ = feed(&chunker, seconds: 10, db: speech)
        XCTAssertEqual(feed(&chunker, seconds: 2, db: -55), [0..<(12 * rate)])
    }

    func testSoundInterruptsTheSilenceRun() {
        var chunker = LiveChunker()
        _ = feed(&chunker, seconds: 10, db: speech)
        XCTAssertEqual(feed(&chunker, seconds: 1.5, db: silence), [])
        XCTAssertEqual(feed(&chunker, seconds: 0.1, db: speech), [])
        XCTAssertEqual(feed(&chunker, seconds: 1.9, db: silence), [])
        XCTAssertEqual(feed(&chunker, seconds: 0.1, db: silence), [0..<(136 * rate / 10)])
    }

    func testNoCutBeforeTenSecondsEvenInSilence() {
        var chunker = LiveChunker()
        XCTAssertEqual(feed(&chunker, seconds: 9.9, db: silence), [])
        // Silence since the start: the cut happens the moment the chunk
        // reaches 10 s.
        XCTAssertEqual(feed(&chunker, seconds: 0.1, db: silence), [0..<(10 * rate)])

        var speechThenSilence = LiveChunker()
        XCTAssertEqual(feed(&speechThenSilence, seconds: 3, db: speech), [])
        XCTAssertEqual(feed(&speechThenSilence, seconds: 6.9, db: silence), [])
        XCTAssertEqual(feed(&speechThenSilence, seconds: 0.1, db: silence), [0..<(10 * rate)])
    }

    func testSilenceBeforeACutDoesNotCountForTheNextChunk() {
        var chunker = LiveChunker()
        _ = feed(&chunker, seconds: 10, db: speech)
        XCTAssertEqual(feed(&chunker, seconds: 2, db: silence), [0..<(12 * rate)])
        // Continuing silence needs a fresh 10 s chunk before the next cut.
        XCTAssertEqual(feed(&chunker, seconds: 9.9, db: silence), [])
        XCTAssertEqual(feed(&chunker, seconds: 0.1, db: silence), [(12 * rate)..<(22 * rate)])
    }

    func testFlushReturnsTheRemainderOnce() {
        var chunker = LiveChunker()
        _ = feed(&chunker, seconds: 35, db: speech)
        XCTAssertEqual(chunker.flush(), (30 * rate)..<(35 * rate))
        XCTAssertNil(chunker.flush())

        var empty = LiveChunker()
        XCTAssertNil(empty.flush())
    }

    func testOffsetsAreContiguous() {
        var chunker = LiveChunker()
        var closed: [Range<Int>] = []
        // Irregular input: speech, pauses, and uneven block sizes.
        let pattern: [(Int, Double)] = [
            (8_000, speech), (1_600, silence), (32_000, silence), (240_000, speech),
            (3_200, silence), (40_000, silence), (500_000, speech), (1_600, silence), (777, speech),
        ]
        for _ in 0..<5 {
            for (count, db) in pattern {
                closed += chunker.observe(totalSamples: chunker.sampleCount + count, rmsDB: db)
            }
        }
        if let tail = chunker.flush() { closed.append(tail) }
        XCTAssertFalse(closed.isEmpty)
        XCTAssertEqual(closed.first?.lowerBound, 0)
        XCTAssertEqual(closed.last?.upperBound, chunker.sampleCount)
        for (previous, next) in zip(closed, closed.dropFirst()) {
            XCTAssertEqual(previous.upperBound, next.lowerBound)
        }
        XCTAssertTrue(closed.allSatisfy { !$0.isEmpty && $0.count <= 30 * rate })
    }

    func testLargeJumpProducesSeveralHardCuts() {
        var chunker = LiveChunker()
        XCTAssertEqual(
            chunker.observe(totalSamples: 65 * rate, rmsDB: speech),
            [0..<(30 * rate), (30 * rate)..<(60 * rate)]
        )
        XCTAssertEqual(chunker.flush(), (60 * rate)..<(65 * rate))
    }

    func testNonGrowingCountIsIgnored() {
        var chunker = LiveChunker()
        _ = chunker.observe(totalSamples: 100, rmsDB: speech)
        XCTAssertEqual(chunker.observe(totalSamples: 100, rmsDB: silence), [])
        XCTAssertEqual(chunker.sampleCount, 100)
    }
}
