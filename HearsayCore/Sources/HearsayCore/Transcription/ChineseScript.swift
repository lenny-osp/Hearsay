import Foundation

/// Which Chinese characters a Chinese transcript uses: traditional for
/// ZH-TW, simplified for ZH-CN (`TranscriptLanguage.chineseScript`). Whisper
/// often writes Simplified characters even for Taiwanese speech, so the cue
/// text is converted after transcription with the ICU transform (character
/// level only: 软件 becomes 軟件, not the Taiwan word 軟體). The Whisper call
/// itself is the same "zh" either way (PLAN.md section 6).
public enum ChineseScript: String, Codable, CaseIterable, Sendable {
    case traditional
    case simplified

    private static let hansToHant = StringTransform(rawValue: "Hans-Hant")

    /// `text` in `script`: Hans-Hant for traditional, the same transform
    /// reversed for simplified, unchanged for nil or when ICU fails.
    public static func convert(_ text: String, to script: ChineseScript?) -> String {
        switch script {
        case nil:
            return text
        case .traditional:
            return text.applyingTransform(hansToHant, reverse: false) ?? text
        case .simplified:
            return text.applyingTransform(hansToHant, reverse: true) ?? text
        }
    }

    /// `segments` with their text converted to `script` (unchanged for
    /// nil); timings unchanged.
    public static func convert(_ segments: [TranscriptSegment], to script: ChineseScript?) -> [TranscriptSegment] {
        guard let script else { return segments }
        return segments.map { segment in
            var converted = segment
            converted.text = convert(segment.text, to: script)
            return converted
        }
    }
}
