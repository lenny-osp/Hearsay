import HearsayCore
import SwiftUI

/// The segmented Auto / EN / ZH-TW / ZH-CN / DE / ES picker of the Record and
/// File tabs, bound to `AppSettings.languageChoice` (PLAN.md section 1,
/// "Languages"). ZH-TW transcripts are written in Traditional characters,
/// ZH-CN in Simplified.
struct LanguageChoicePicker: View {
    @Environment(AppSettings.self) private var settings
    var isDisabled = false

    var body: some View {
        @Bindable var settings = settings
        Picker("Language", selection: $settings.languageChoice) {
            ForEach(LanguageChoice.allCases, id: \.self) { choice in
                Text(choice.shortLabel).tag(choice)
            }
        }
        .pickerStyle(.segmented)
        .disabled(isDisabled)
        .help(String(
            localized: "Auto detects English, Chinese, German, or Spanish from the first speech. ZH-TW writes Traditional characters, ZH-CN Simplified.",
            comment: "Tooltip of the language picker. ZH-TW and ZH-CN are the picker's labels; keep them as they are."))
    }
}

/// The suggestion or fallback banner with its re-run buttons. `onRerun`
/// re-runs this session only; it never changes the language choice or the
/// preferred language.
struct LanguageNoticeView: View {
    let notice: LanguageNotice
    var isEnabled = true
    let onRerun: (TranscriptLanguage) -> Void
    var onDismiss: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(notice.message, systemImage: "globe")
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                switch notice {
                case .suggestion(let language):
                    Button("Transcribe again") { onRerun(language) }
                        .help(String(
                            localized: "Transcribe this recording again in \(language.displayName). Your language choice stays as it is.",
                            comment: "Tooltip. %@ is a language name in its own language (English, 繁體中文, 简体中文, Deutsch, Español)."))
                    if let onDismiss {
                        Button("Dismiss", action: onDismiss)
                    }
                case .fallback:
                    ForEach(notice.rerunLanguages, id: \.self) { language in
                        Button(language.displayName) { onRerun(language) }
                            .help(String(localized: "Transcribe this recording again in \(language.displayName)",
                                         comment: "Tooltip. %@ is a language name in its own language."))
                    }
                }
                Spacer(minLength: 0)
            }
            .controlSize(.small)
            .disabled(!isEnabled)
        }
    }
}
