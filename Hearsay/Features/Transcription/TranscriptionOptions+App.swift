import HearsayCore
import HearsayWhisper

extension TranscriptionOptions {
    /// The options whisper-tools passes to `mlx_whisper` (PLAN.md section
    /// 6): the session language, no initial prompt, no conditioning on
    /// previous text, and the decoder defaults for everything else.
    static func app(language: TranscriptLanguage) -> TranscriptionOptions {
        TranscriptionOptions(
            language: language.code,
            initialPrompt: nil,
            conditionOnPreviousText: false
        )
    }
}

extension ChineseScript {
    /// The script cue text is converted to for a session in `language`:
    /// the "Chinese output" setting for zh, asIs (unchanged) for everything
    /// else. Whisper itself is called the same way either way.
    @MainActor
    static func app(language: TranscriptLanguage, settings: AppSettings) -> ChineseScript {
        forSession(languageCode: language.code, preference: settings.chineseScript)
    }
}

extension DetectionResult {
    /// The pair `LanguageDecision.decide` takes.
    var decisionInput: (code: String?, confidence: Float) { (code, confidence) }

    /// One line for the debug paths.
    var debugSummary: String {
        let windows = perWindow.map { window in
            TranscriptLanguage.allCases
                .map { String(format: "%@ %.4f", $0.code, window[$0.code] ?? 0) }
                .joined(separator: ", ")
        }
        return "detected \(code ?? "none") \(String(format: "%.4f", confidence)) over \(windowsUsed) speech windows"
            + (windows.isEmpty ? "" : " [" + windows.joined(separator: "] [") + "]")
    }
}
