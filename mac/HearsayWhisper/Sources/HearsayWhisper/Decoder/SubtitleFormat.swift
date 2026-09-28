// Ported from mlx_whisper 0.4.3 (ml-explore/mlx-examples, MIT, Copyright © 2023 Apple Inc.); see LICENSES/mlx-whisper.txt.

import Foundation

/// Port of `format_timestamp` in mlx_whisper `writers.py` (also
/// `_format_timestamp` in `transcribe.py`). Python's `round` rounds half to
/// even, so `.toNearestOrEven` is used.
public func formatTimestamp(
    _ seconds: TimeInterval,
    alwaysIncludeHours: Bool = false,
    decimalMarker: String = "."
) -> String {
    var milliseconds = Int((max(seconds, 0) * 1000.0).rounded(.toNearestOrEven))
    let hours = milliseconds / 3_600_000
    milliseconds -= hours * 3_600_000
    let minutes = milliseconds / 60_000
    milliseconds -= minutes * 60_000
    let wholeSeconds = milliseconds / 1_000
    milliseconds -= wholeSeconds * 1_000
    let hoursMarker = (alwaysIncludeHours || hours > 0) ? String(format: "%02d:", hours) : ""
    return hoursMarker + String(format: "%02d:%02d", minutes, wholeSeconds)
        + decimalMarker + String(format: "%03d", milliseconds)
}

/// Port of `WriteSRT` (segment mode, no word timestamps): one cue per
/// segment, including segments whose text was cleared, text stripped and
/// `-->` replaced with `->`.
public func renderSRT(_ segments: [TranscriptSegment]) -> String {
    var output = ""
    for (index, segment) in segments.enumerated() {
        let start = formatTimestamp(segment.start, alwaysIncludeHours: true, decimalMarker: ",")
        let end = formatTimestamp(segment.end, alwaysIncludeHours: true, decimalMarker: ",")
        let text = segment.text
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "-->", with: "->")
        output += "\(index + 1)\n\(start) --> \(end)\n\(text)\n\n"
    }
    return output
}

/// The verbose console line printed by `transcribe` for each segment.
public func consoleLine(_ segment: TranscriptSegment) -> String {
    "[\(formatTimestamp(segment.start)) --> \(formatTimestamp(segment.end))] \(segment.text)"
}
