import HearsayCore
import HearsayWhisper

extension TranscriptionOptions {
    /// The options whisper-tools passes to `mlx_whisper` (PLAN.md section
    /// 6): the session language, no initial prompt, no conditioning on
    /// previous text, and the decoder defaults for everything else.
    static func app(language: TranscriptLanguage) -> TranscriptionOptions {
        TranscriptionOptions(
            language: language.whisperCode,
            initialPrompt: nil,
            conditionOnPreviousText: false
        )
    }
}

extension DetectionResult {
    /// The pair `LanguageDecision.decide` takes.
    var decisionInput: (code: String?, confidence: Float) { (code, confidence) }

    /// One line for the debug paths.
    var debugSummary: String {
        let windows = perWindow.map { window in
            TranscriptLanguage.whisperCodes
                .map { String(format: "%@ %.4f", $0, window[$0] ?? 0) }
                .joined(separator: ", ")
        }
        return "detected \(code ?? "none") \(String(format: "%.4f", confidence)) over \(windowsUsed) speech windows"
            + (windows.isEmpty ? "" : " [" + windows.joined(separator: "] [") + "]")
    }
}
