import HearsayCore
import SwiftUI

/// The segmented Auto / EN / ZH / DE / ES picker of the Record and File tabs,
/// bound to `AppSettings.languageChoice` (PLAN.md section 1, "Languages").
/// Followed by the 繁體中文 / 简体中文 row when ZH or Auto is selected; for
/// Auto it applies only when the session turns out to be zh.
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
        .help("Auto detects English, Chinese, German, or Spanish from the first speech")

        if settings.languageChoice == .auto || settings.languageChoice == .fixed(.chinese) {
            ChineseScriptPicker()
                .pickerStyle(.segmented)
                .disabled(isDisabled)
                .help(settings.languageChoice == .auto
                      ? "Used when Auto detects Chinese"
                      : "Characters of the Chinese transcript")
        }
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
                        .help("Transcribe this recording again in \(language.displayName). "
                              + "Your language choice stays as it is.")
                    if let onDismiss {
                        Button("Dismiss", action: onDismiss)
                    }
                case .fallback:
                    ForEach(notice.rerunLanguages, id: \.self) { language in
                        Button(language.displayName) { onRerun(language) }
                            .help("Transcribe this recording again in \(language.displayName)")
                    }
                }
                Spacer(minLength: 0)
            }
            .controlSize(.small)
            .disabled(!isEnabled)
        }
    }
}
