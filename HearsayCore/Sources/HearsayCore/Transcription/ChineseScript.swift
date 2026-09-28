import Foundation

/// Which Chinese characters the transcript of a zh session uses. Whisper
/// often writes Simplified characters even for Taiwanese speech, so the cue
/// text is converted after transcription with the ICU transform (character
/// level only: 软件 becomes 軟件, not the Taiwan word 軟體). The Whisper call
/// itself is unchanged (PLAN.md section 6).
public enum ChineseScript: String, Codable, CaseIterable, Sendable {
    case traditional
    case simplified
    /// No conversion. Internal: used for non-zh sessions, never offered in
    /// a picker (see `pickerCases`).
    case asIs

    /// The choices the pickers offer, in order.
    public static let pickerCases: [ChineseScript] = [.traditional, .simplified]

    /// The value a picker shows and a zh session uses for this preference:
    /// asIs is treated as traditional.
    public var pickerValue: ChineseScript { self == .asIs ? .traditional : self }

    /// Short label for the pickers.
    public var displayName: String {
        switch self {
        case .traditional: "繁體中文"
        case .simplified: "简体中文"
        case .asIs: "繁體中文"
        }
    }

    private static let hansToHant = StringTransform(rawValue: "Hans-Hant")

    /// `text` in `script`: Hans-Hant for traditional, the same transform
    /// reversed for simplified, unchanged for asIs or when ICU fails.
    public static func convert(_ text: String, to script: ChineseScript) -> String {
        switch script {
        case .asIs:
            return text
        case .traditional:
            return text.applyingTransform(hansToHant, reverse: false) ?? text
        case .simplified:
            return text.applyingTransform(hansToHant, reverse: true) ?? text
        }
    }

    /// The script to apply for a session in `languageCode`: `preference`
    /// for "zh" (asIs counts as traditional), asIs for every other language.
    public static func forSession(languageCode: String, preference: ChineseScript) -> ChineseScript {
        languageCode == "zh" ? preference.pickerValue : .asIs
    }

    /// `segments` with their text converted to `script`; timings unchanged.
    public static func convert(_ segments: [TranscriptSegment], to script: ChineseScript) -> [TranscriptSegment] {
        guard script != .asIs else { return segments }
        return segments.map { segment in
            var converted = segment
            converted.text = convert(segment.text, to: script)
            return converted
        }
    }
}
