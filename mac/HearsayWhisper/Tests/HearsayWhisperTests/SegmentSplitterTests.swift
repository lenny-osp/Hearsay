import Foundation
import Testing
@testable import HearsayWhisper

/// Mirrors the segment splitting and seek update in mlx_whisper
/// `transcribe.py`. Timestamp token t means `tb + t` (t * 0.02 s).
struct SegmentSplitterTests {
    static let tb = WhisperSpecialTokens.largeV3.timestampBegin

    static func ts(_ position: Int) -> Int { tb + position }

    @Test func pairsWithSingleTimestampEndingKeepEverySegmentAndSkipTheWindow() {
        // <0.00> a b <2.00><2.00> c <3.00>   -> two segments, seek += segment_size
        let tokens = [Self.ts(0), 100, 101, Self.ts(100), Self.ts(100), 102, Self.ts(150)]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 30, segmentSize: 3000)
        #expect(split.singleTimestampEnding)
        #expect(split.seekAdvance == 3000)
        #expect(split.segments == [
            SplitSegment(start: 30.0, end: 32.0, tokens: [Self.ts(0), 100, 101, Self.ts(100)]),
            SplitSegment(start: 32.0, end: 33.0, tokens: [Self.ts(100), 102, Self.ts(150)]),
        ])
    }

    @Test func pairsEndingInAPairDropTheTailAndSeekToTheLastTimestamp() {
        // <0.00> a <1.00><1.00> b <2.50><2.50>  -> two segments; seek += 125 * 2 frames
        let tokens = [Self.ts(0), 100, Self.ts(50), Self.ts(50), 101, Self.ts(125), Self.ts(125)]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 0, segmentSize: 3000)
        #expect(!split.singleTimestampEnding)
        #expect(split.seekAdvance == 250)
        #expect(split.segments.map(\.start) == [0.0, 1.0])
        #expect(split.segments.map(\.end) == [1.0, 2.5])
    }

    @Test func unfinishedTextAfterTheLastPairIsDropped() {
        // <0.00> a <1.00><1.00> b c (no closing timestamp) -> one segment, seek to 1.00
        let tokens = [Self.ts(0), 100, Self.ts(50), Self.ts(50), 101, 102]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 0, segmentSize: 3000)
        #expect(split.segments.count == 1)
        #expect(split.segments[0].tokens == [Self.ts(0), 100, Self.ts(50)])
        #expect(split.seekAdvance == 100)
    }

    @Test func singleSegmentUsesTheLastTimestampAsDuration() {
        // <0.00> a b <4.48>  -> no consecutive pair; end = offset + 4.48
        let tokens = [Self.ts(0), 100, 101, Self.ts(224)]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 0, segmentSize: 1893)
        #expect(split.segments == [SplitSegment(start: 0, end: 4.48, tokens: tokens)])
        #expect(split.seekAdvance == 1893)
    }

    @Test func noTimestampsSpansTheWholeSegment() {
        let tokens = [100, 101]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 10, segmentSize: 1893)
        #expect(split.segments == [SplitSegment(start: 10, end: 10 + 18.93, tokens: tokens)])
        #expect(split.seekAdvance == 1893)
    }

    @Test func onlyTimestampBeginKeepsTheSegmentDuration() {
        // `timestamps[-1] != timestamp_begin` guard.
        let tokens = [Self.ts(0), 100]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 0, segmentSize: 3000)
        #expect(split.segments[0].end == 30.0)
    }

    @Test func emptyTokensGiveOneEmptySegment() {
        let split = splitWindow(tokens: [], timestampBegin: Self.tb, timeOffset: 0, segmentSize: 500)
        #expect(split.segments == [SplitSegment(start: 0, end: 5.0, tokens: [])])
        #expect(split.seekAdvance == 500)
    }

    @Test func timesUseThePythonFloatArithmetic() {
        // time_offset + pos * 0.02 in float64: 0 + 535 * 0.02 == 10.700000000000001
        let tokens = [Self.ts(239), 100, Self.ts(535), Self.ts(535)]
        let split = splitWindow(tokens: tokens, timestampBegin: Self.tb, timeOffset: 0, segmentSize: 3000)
        #expect(split.segments[0].end == 10.700000000000001)
        #expect(split.segments[0].start == 4.78)
    }
}
