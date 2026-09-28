import Foundation

/// Mel frames per second (`FRAMES_PER_SECOND`), 10 ms hops.
let framesPerSecond = WhisperAudioConfig.sampleRate / WhisperAudioConfig.hopLength
/// Mel frames per encoder output position (`N_FRAMES // n_audio_ctx`).
let inputStride = 2
/// Seconds per timestamp token (`input_stride * HOP_LENGTH / SAMPLE_RATE`).
let timePrecision = Double(inputStride * WhisperAudioConfig.hopLength) / Double(WhisperAudioConfig.sampleRate)

/// A segment cut from one window's tokens, before text decoding.
struct SplitSegment: Equatable, Sendable {
    var start: Double
    var end: Double
    var tokens: [Int]
}

/// The result of splitting one window: its segments and how far `seek` moves.
struct WindowSplit: Equatable, Sendable {
    var segments: [SplitSegment]
    /// Frames to add to `seek`.
    var seekAdvance: Int
    /// The tokens end in a single timestamp (`[..., text, <|t|>]`).
    var singleTimestampEnding: Bool
}

/// Port of the segment splitting and `seek` update in mlx_whisper
/// `transcribe.py`: segments are cut at consecutive timestamp pairs; a single
/// trailing timestamp means the window is done; otherwise the unfinished tail
/// is dropped and `seek` moves to the last timestamp.
func splitWindow(
    tokens: [Int],
    timestampBegin: Int,
    timeOffset: Double,
    segmentSize: Int
) -> WindowSplit {
    let isTimestamp = tokens.map { $0 >= timestampBegin }
    let singleTimestampEnding = isTimestamp.count >= 2
        && !isTimestamp[isTimestamp.count - 2] && isTimestamp[isTimestamp.count - 1]

    var consecutive: [Int] = []
    if isTimestamp.count >= 2 {
        for i in 0..<(isTimestamp.count - 1) where isTimestamp[i] && isTimestamp[i + 1] {
            consecutive.append(i + 1)
        }
    }

    var segments: [SplitSegment] = []
    let seekAdvance: Int
    if !consecutive.isEmpty {
        // the output contains two consecutive timestamp tokens
        var slices = consecutive
        if singleTimestampEnding {
            slices.append(tokens.count)
        }
        var lastSlice = 0
        for currentSlice in slices {
            let sliced = Array(tokens[lastSlice..<currentSlice])
            let startPosition = (sliced.first ?? timestampBegin) - timestampBegin
            let endPosition = (sliced.last ?? timestampBegin) - timestampBegin
            segments.append(SplitSegment(
                start: timeOffset + Double(startPosition) * timePrecision,
                end: timeOffset + Double(endPosition) * timePrecision,
                tokens: sliced
            ))
            lastSlice = currentSlice
        }
        if singleTimestampEnding {
            // single timestamp at the end means no speech after the last timestamp
            seekAdvance = segmentSize
        } else {
            // ignore the unfinished segment and seek to the last timestamp
            let lastTimestampPosition = tokens[lastSlice - 1] - timestampBegin
            seekAdvance = lastTimestampPosition * inputStride
        }
    } else {
        var duration = Double(segmentSize * WhisperAudioConfig.hopLength) / Double(WhisperAudioConfig.sampleRate)
        let timestamps = tokens.filter { $0 >= timestampBegin }
        if let last = timestamps.last, last != timestampBegin {
            // no consecutive timestamps but it has a timestamp; use the last one
            duration = Double(last - timestampBegin) * timePrecision
        }
        segments.append(SplitSegment(start: timeOffset, end: timeOffset + duration, tokens: tokens))
        seekAdvance = segmentSize
    }
    return WindowSplit(segments: segments, seekAdvance: seekAdvance, singleTimestampEnding: singleTimestampEnding)
}
