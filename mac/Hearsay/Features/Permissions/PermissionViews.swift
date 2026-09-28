import HearsayCore
import SwiftUI

/// The Permissions row at the top of the Record tab: one line per
/// permission with the button that fixes it, or a single green line when
/// both are granted (PLAN.md section 9).
struct PermissionsSection: View {
    @Environment(PermissionMonitor.self) private var monitor

    var body: some View {
        Section {
            if monitor.allGranted {
                Label {
                    Text("Microphone and system audio allowed",
                         comment: "Record tab, Permissions row when both permissions are granted")
                } icon: {
                    Image(systemName: "checkmark.circle.fill")
                }
                .foregroundStyle(.green)
            } else {
                microphoneLine
                screenAudioLine
            }
        } header: {
            if !monitor.allGranted {
                Text("Permissions", comment: "Record tab: header of the Permissions row")
            }
        }
    }

    private var microphoneLine: some View {
        HStack {
            statusLabel(PermissionTexts.line(for: .microphone, status: monitor.microphone),
                        status: monitor.microphone)
            Spacer()
            switch monitor.microphone {
            case .granted:
                EmptyView()
            case .notDetermined:
                Button {
                    monitor.requestMicrophone()
                } label: {
                    Text("Allow", comment: "Record tab, Permissions row: ask for microphone access now")
                }
            case .staleGrant:
                fixButton(.microphone)
                openButton(.microphone)
            case .denied:
                openButton(.microphone)
            }
        }
    }

    private var screenAudioLine: some View {
        HStack {
            statusLabel(PermissionTexts.line(for: .screenAudio, status: monitor.screenAudio),
                        status: monitor.screenAudio)
            Spacer()
            switch monitor.screenAudio {
            case .granted:
                EmptyView()
            case .staleGrant:
                fixButton(.screenAudio)
                openButton(.screenAudio)
            case .denied, .notDetermined:
                openButton(.screenAudio)
            }
        }
    }

    private func statusLabel(_ text: String, status: PermissionStatus) -> some View {
        Label {
            Text(verbatim: text)
        } icon: {
            switch status {
            case .granted:
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            case .notDetermined:
                Image(systemName: "questionmark.circle").foregroundStyle(.secondary)
            case .denied:
                Image(systemName: "xmark.circle.fill").foregroundStyle(.red)
            case .staleGrant:
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            }
        }
    }

    private func fixButton(_ kind: PermissionKind) -> some View {
        Button {
            monitor.showGuidance(for: kind)
        } label: {
            Text("Fix…", comment: "Record tab, Permissions row: open the re-approval instructions")
        }
    }

    private func openButton(_ kind: PermissionKind) -> some View {
        Button("Open System Settings") { monitor.openSystemSettings(kind: kind) }
    }
}

/// Localized status lines, shared by the Record tab and the sheet.
enum PermissionTexts {
    static func line(for kind: PermissionKind, status: PermissionStatus) -> String {
        switch (kind, status) {
        case (.microphone, .granted):
            String(localized: "Microphone: Granted", comment: "Record tab, Permissions row")
        case (.microphone, .denied):
            String(localized: "Microphone: Not granted", comment: "Record tab, Permissions row")
        case (.microphone, .notDetermined):
            String(localized: "Microphone: Not asked yet",
                   comment: "Record tab, Permissions row: macOS has not asked for microphone access yet")
        case (.microphone, .staleGrant):
            String(localized: "Microphone: Needs re-approval after update",
                   comment: "Record tab, Permissions row: an update made macOS forget the earlier permission")
        case (.screenAudio, .granted):
            String(localized: "System audio: Granted", comment: "Record tab, Permissions row")
        case (.screenAudio, .denied), (.screenAudio, .notDetermined):
            String(localized: "System audio: Not granted", comment: "Record tab, Permissions row")
        case (.screenAudio, .staleGrant):
            String(localized: "System audio: Needs re-approval after update",
                   comment: "Record tab, Permissions row: an update made macOS forget the earlier permission")
        }
    }
}

/// "System audio permission needs re-approval": shown once when a stale
/// grant was detected (and its entry removed), and from the Record tab's
/// Fix… button. Refreshes every 2 s and turns to "Granted" on its own.
struct PermissionGuidanceSheet: View {
    @Environment(PermissionMonitor.self) private var monitor
    @Environment(AppRelauncher.self) private var relauncher: AppRelauncher?
    let kind: PermissionKind
    let onClose: () -> Void

    private var isGranted: Bool { monitor.status(of: kind) == .granted }

    private var title: String {
        switch kind {
        case .screenAudio:
            String(localized: "System audio permission needs re-approval", comment: "Permission sheet title")
        case .microphone:
            String(localized: "Microphone permission needs re-approval", comment: "Permission sheet title")
        }
    }

    private var steps: [String] {
        let second = switch kind {
        case .screenAudio:
            String(localized: "Turn Hearsay on under Screen & System Audio Recording.",
                   comment: "Permission sheet, step 2. Use the macOS name of the System Settings section.")
        case .microphone:
            String(localized: "Turn Hearsay on under Microphone.",
                   comment: "Permission sheet, step 2. Use the macOS name of the System Settings section.")
        }
        return [
            String(localized: "Open System Settings.", comment: "Permission sheet, step 1"),
            second,
            String(localized: "If it stays off, relaunch Hearsay.", comment: "Permission sheet, step 3"),
        ]
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label {
                Text(verbatim: title)
            } icon: {
                Image(systemName: "exclamationmark.triangle")
            }
            .font(.headline)
            Text("This Hearsay build is not signed with a Developer ID, so macOS treats each update as a new app and forgets the permission you gave before.",
                 comment: "Permission sheet explanation")
                .fixedSize(horizontal: false, vertical: true)
            if kind == .screenAudio {
                Text(monitor.screenAudioResetSucceeded == false
                     ? String(localized: "Hearsay could not remove the old entry. If Hearsay is still listed as on, turn it off and on again.",
                              comment: "Permission sheet: tccutil reset failed")
                     : String(localized: "Hearsay has removed the old entry.",
                              comment: "Permission sheet: the stale Screen & System Audio Recording entry was reset"))
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(alignment: .leading, spacing: 4) {
                ForEach(Array(steps.enumerated()), id: \.offset) { index, step in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(verbatim: "\(index + 1).")
                            .monospacedDigit()
                        Text(verbatim: step)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            Group {
                if isGranted {
                    Label {
                        Text("Granted", comment: "Permission sheet: the permission is now allowed")
                    } icon: {
                        Image(systemName: "checkmark.circle.fill")
                    }
                    .foregroundStyle(.green)
                } else {
                    Label {
                        Text("Not granted yet", comment: "Permission sheet: still waiting for the permission")
                    } icon: {
                        Image(systemName: "hourglass")
                    }
                    .foregroundStyle(.orange)
                }
            }
            .font(.callout)
            HStack {
                Button("Open System Settings") { monitor.openSystemSettings(kind: kind) }
                    .disabled(isGranted)
                Button {
                    onClose()
                    relauncher?.restart()
                } label: {
                    Text("Relaunch Hearsay", comment: "Permission sheet button: quit and reopen Hearsay")
                }
                Spacer()
                if isGranted {
                    Button {
                        onClose()
                    } label: {
                        Text("Done", comment: "Permission sheet button once the permission is granted")
                    }
                    .keyboardShortcut(.defaultAction)
                } else {
                    Button(role: .cancel) {
                        onClose()
                    } label: {
                        Text("Later", comment: "Permission sheet button: close the sheet without fixing it now")
                    }
                    .keyboardShortcut(.cancelAction)
                }
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear {
            monitor.refresh()
            monitor.startPolling()
        }
        .onDisappear { monitor.stopPolling() }
    }
}
