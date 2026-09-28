import HearsayCore
import SwiftUI

/// First-run prompt when no model is installed: one click downloads the
/// recommended model.
struct OnboardingModelSheet: View {
    @Environment(ModelStore.self) private var store
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Download a speech model")
                .font(.title2.weight(.semibold))
            Text("""
                Hearsay transcribes on this Mac with a Whisper model that you download once. \
                Nothing is sent anywhere while transcribing. The recommended model gives the \
                best balance of accuracy and speed; smaller ones download faster but make \
                more mistakes.
                """)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Choose another…") { dismiss() }
                Spacer()
                if let recommended = store.catalog.recommended {
                    Button("Download recommended model (\(ByteSize.short(recommended.sizeBytes)))") {
                        store.download(recommended)
                        dismiss()
                    }
                    .keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(24)
        .frame(width: 460)
    }
}
