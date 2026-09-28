import HearsayCore
import HearsayWhisper

extension TranscriptionOptions {
    /// The options whisper-tools passes to `mlx_whisper` (PLAN.md section
    /// 6): the chosen language, no initial prompt, no conditioning on
    /// previous text, and the decoder defaults for everything else.
    static func app(languageCode: String) -> TranscriptionOptions {
        TranscriptionOptions(
            language: languageCode == "zh" ? "zh" : "en",
            initialPrompt: nil,
            conditionOnPreviousText: false
        )
    }

    /// Built from the default language in Settings (the Record tab picker).
    @MainActor
    static func app(settings: AppSettings) -> TranscriptionOptions {
        app(languageCode: settings.defaultLanguageCode)
    }
}
