import Foundation

/// SubRip rendering, lenient parsing, and the transcript cleanup used for the
/// AI prompt (port of `clean_srt_text` in `run_whisper.py`).
public enum SRT {
    // MARK: - Render

    /// Standard SRT: 1-based index, `HH:MM:SS,mmm --> HH:MM:SS,mmm`, text, and
    /// a blank line after every block (so the output ends with a newline).
    public static func render(_ segments: [TranscriptSegment]) -> String {
        var output = ""
        for (offset, segment) in segments.enumerated() {
            output += "\(offset + 1)\n"
            output += "\(timestamp(segment.start)) --> \(timestamp(segment.end))\n"
            output += "\(segment.text)\n\n"
        }
        return output
    }

    /// `HH:MM:SS,mmm`, rounded to the nearest millisecond, never negative.
    public static func timestamp(_ seconds: TimeInterval) -> String {
        let totalMilliseconds: Int
        if seconds.isFinite, seconds > 0 {
            totalMilliseconds = Int((seconds * 1000).rounded())
        } else {
            totalMilliseconds = 0
        }
        let milliseconds = totalMilliseconds % 1000
        let totalSeconds = totalMilliseconds / 1000
        return String(
            format: "%02d:%02d:%02d,%03d",
            totalSeconds / 3600, totalSeconds / 60 % 60, totalSeconds % 60, milliseconds
        )
    }

    // MARK: - Parse

    /// Lenient parse: tolerates CRLF or CR line endings, a byte-order mark, a
    /// missing final blank line, a missing index line, and either `,` or `.`
    /// as the millisecond separator. Blocks without a timing line are skipped.
    public static func parse(_ text: String) -> [TranscriptSegment] {
        var normalized = text
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        if normalized.hasPrefix("\u{FEFF}") {
            normalized.removeFirst()
        }

        var segments: [TranscriptSegment] = []
        var block: [String] = []

        func flush() {
            defer { block.removeAll() }
            guard let timingIndex = block.firstIndex(where: { parseTiming($0) != nil }),
                  let timing = parseTiming(block[timingIndex]) else { return }
            let text = block[(timingIndex + 1)...].joined(separator: "\n")
            segments.append(TranscriptSegment(start: timing.start, end: timing.end, text: text))
        }

        for line in normalized.split(separator: "\n", omittingEmptySubsequences: false) {
            if line.trimmingCharacters(in: .whitespaces).isEmpty {
                flush()
            } else {
                block.append(String(line))
            }
        }
        flush()
        return segments
    }

    private static let timingPattern: NSRegularExpression? = try? NSRegularExpression(
        pattern: #"^\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})"#
    )

    private static func parseTiming(_ line: String) -> (start: TimeInterval, end: TimeInterval)? {
        guard let pattern = timingPattern else { return nil }
        let range = NSRange(line.startIndex..., in: line)
        guard let match = pattern.firstMatch(in: line, range: range), match.numberOfRanges == 9 else {
            return nil
        }
        var numbers: [Int] = []
        for group in 1...8 {
            guard let groupRange = Range(match.range(at: group), in: line) else { return nil }
            let digits = line[groupRange]
            if group == 4 || group == 8 {
                // "5" means 500 ms and "05" means 50 ms, as in decimal fractions.
                let padded = digits.padding(toLength: 3, withPad: "0", startingAt: 0)
                guard let value = Int(padded) else { return nil }
                numbers.append(value)
            } else {
                guard let value = Int(digits) else { return nil }
                numbers.append(value)
            }
        }
        func seconds(_ h: Int, _ m: Int, _ s: Int, _ ms: Int) -> TimeInterval {
            Double(((h * 60 + m) * 60 + s) * 1000 + ms) / 1000.0
        }
        return (
            seconds(numbers[0], numbers[1], numbers[2], numbers[3]),
            seconds(numbers[4], numbers[5], numbers[6], numbers[7])
        )
    }

    // MARK: - Clean

    /// Port of `clean_srt_text`: drops index lines, timing lines, and blank
    /// lines, keeping every other line with its original line ending.
    ///
    /// Line splitting follows Python's `str.splitlines(keepends=True)`, and
    /// only `\r` and `\n` are stripped before the checks, as in Python.
    public static func cleanText(_ srtText: String) -> String {
        var kept = String.UnicodeScalarView()
        for rawLine in pythonSplitLinesKeepingEnds(srtText.unicodeScalars) {
            var end = rawLine.endIndex
            while end > rawLine.startIndex {
                let previous = rawLine.index(before: end)
                let scalar = rawLine[previous]
                guard scalar == "\r" || scalar == "\n" else { break }
                end = previous
            }
            let line = rawLine[rawLine.startIndex..<end]
            if line.isEmpty { continue }
            if line.allSatisfy(isASCIIDigit) { continue }
            if startsWithClockPrefix(line) { continue }
            kept.append(contentsOf: rawLine)
        }
        return String(kept)
    }

    private static func isASCIIDigit(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value >= 0x30 && scalar.value <= 0x39
    }

    /// Mirrors `re.match(r"^[0-9]{2}:[0-9]{2}:[0-9]{2}", line)`.
    private static func startsWithClockPrefix(_ line: Substring.UnicodeScalarView) -> Bool {
        let scalars = Array(line.prefix(8))
        guard scalars.count == 8 else { return false }
        for (offset, scalar) in scalars.enumerated() {
            if offset == 2 || offset == 5 {
                if scalar != ":" { return false }
            } else if !isASCIIDigit(scalar) {
                return false
            }
        }
        return true
    }

    /// The line boundaries recognized by Python's `str.splitlines`.
    private static func isPythonLineBoundary(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x0A, 0x0B, 0x0C, 0x0D, 0x1C, 0x1D, 0x1E, 0x85, 0x2028, 0x2029:
            return true
        default:
            return false
        }
    }

    private static func pythonSplitLinesKeepingEnds(
        _ scalars: String.UnicodeScalarView
    ) -> [Substring.UnicodeScalarView] {
        var lines: [Substring.UnicodeScalarView] = []
        var lineStart = scalars.startIndex
        var index = scalars.startIndex
        while index < scalars.endIndex {
            let scalar = scalars[index]
            guard isPythonLineBoundary(scalar) else {
                index = scalars.index(after: index)
                continue
            }
            var next = scalars.index(after: index)
            if scalar == "\r", next < scalars.endIndex, scalars[next] == "\n" {
                next = scalars.index(after: next)
            }
            lines.append(scalars[lineStart..<next])
            lineStart = next
            index = next
        }
        if lineStart < scalars.endIndex {
            lines.append(scalars[lineStart..<scalars.endIndex])
        }
        return lines
    }
}
