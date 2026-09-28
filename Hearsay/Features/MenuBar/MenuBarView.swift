import AppKit
import SwiftUI

/// Content of the menu bar extra window. Static for now: no recording yet.
struct MenuBarView: View {
    @Environment(MainWindowOpener.self) private var windowOpener
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                Circle()
                    .fill(.secondary)
                    .frame(width: 8, height: 8)
                Text("Idle")
                    .font(.headline)
            }

            Button {
            } label: {
                Label("Start", systemImage: "record.circle")
                    .frame(maxWidth: .infinity)
            }
            .controlSize(.large)
            .disabled(true)

            Divider()

            menuRow("Open Hearsay") {
                windowOpener.show()
            }
            menuRow("Settings…") {
                NSApp.activate()
                openSettings()
            }
            .keyboardShortcut(",", modifiers: .command)

            Divider()

            menuRow("Quit Hearsay") {
                NSApp.terminate(nil)
            }
            .keyboardShortcut("q", modifiers: .command)
        }
        .padding(12)
        .frame(width: 240)
    }

    private func menuRow(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .foregroundStyle(.primary)
    }
}
