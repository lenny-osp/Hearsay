import AppKit
import AVFoundation
import Foundation
import HearsayCore
import Observation
import os
import Security

/// Microphone and Screen & System Audio Recording status for the Record tab
/// and the re-approval sheet (PLAN.md section 9).
///
/// Refreshes at launch, whenever Hearsay becomes active (users come back from
/// System Settings), and every 2 s while the guidance sheet is shown. A
/// system audio grant that went stale after an update of the ad-hoc signed
/// app (`StaleGrantDetector`) has its old TCC entry removed with
/// `tccutil reset ScreenCapture <bundle id>`, at most once per build, and the
/// guidance sheet is shown.
@MainActor
@Observable
final class PermissionMonitor {
    /// Everything that touches the system, injectable for the UI snapshots.
    struct Sources {
        var microphone: @MainActor () -> MicrophonePermission
        var screenPreflight: @MainActor () -> Bool
        var requestMicrophone: @MainActor () async -> Bool
        /// Shows the system prompt when TCC has no entry for Hearsay (which
        /// also lists Hearsay in System Settings); otherwise does nothing.
        var requestScreenAudio: @MainActor () -> Bool
        /// Removes the Screen & System Audio Recording entry for a bundle id.
        var resetScreenCapture: @Sendable (String) async -> Bool
        var open: @MainActor (URL) -> Void

        static let live = Sources(
            microphone: { MicrophoneRecorder.permission },
            screenPreflight: { SystemAudioRecorder.permission == .authorized },
            requestMicrophone: { await MicrophoneRecorder.requestPermission() },
            requestScreenAudio: { SystemAudioRecorder.requestPermission() },
            resetScreenCapture: { await PermissionMonitor.resetScreenCaptureEntry(bundleID: $0) },
            open: { NSWorkspace.shared.open($0) }
        )

        /// Fixed answers and no side effects (UI snapshots).
        static func fixed(microphone: MicrophonePermission, screenGranted: Bool) -> Sources {
            Sources(
                microphone: { microphone },
                screenPreflight: { screenGranted },
                requestMicrophone: { microphone == .authorized },
                requestScreenAudio: { screenGranted },
                resetScreenCapture: { _ in true },
                open: { _ in }
            )
        }
    }

    static let screenAudioSettingsURL = URL(
        string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
    )
    static let microphoneSettingsURL = URL(
        string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
    )

    private nonisolated static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "permissions")

    private(set) var microphone: PermissionStatus = .notDetermined
    private(set) var screenAudio: PermissionStatus = .denied
    /// The permission whose re-approval sheet is shown, or nil.
    var guidance: PermissionKind?
    /// Whether this launch's `tccutil reset` succeeded; nil when none ran in
    /// this launch (an earlier launch may have removed the entry).
    private(set) var screenAudioResetSucceeded: Bool?

    /// Called when a stale system audio grant was just detected and its
    /// entry removed; the app delegate brings the main window forward.
    @ObservationIgnored var onStaleGrantDetected: () -> Void = {}

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private let sources: Sources
    /// This build's code-signing requirement (what TCC keys a grant on).
    @ObservationIgnored let codeIdentity: String?
    @ObservationIgnored private let bundleIdentifier: String?
    /// False in debug runs and snapshots: `tccutil` is never run.
    @ObservationIgnored private let allowsReset: Bool
    @ObservationIgnored private var activeObserver: NSObjectProtocol?
    @ObservationIgnored private var pollTask: Task<Void, Never>?
    @ObservationIgnored private var resetInFlight = false

    init(
        settings: AppSettings,
        sources: Sources = .live,
        codeIdentity: String? = PermissionMonitor.currentCodeIdentity(),
        bundleIdentifier: String? = Bundle.main.bundleIdentifier,
        allowsReset: Bool = !DebugDefaults.isDebugRun
    ) {
        self.settings = settings
        self.sources = sources
        self.codeIdentity = codeIdentity
        self.bundleIdentifier = bundleIdentifier
        self.allowsReset = allowsReset
    }

    var allGranted: Bool { microphone == .granted && screenAudio == .granted }

    func status(of kind: PermissionKind) -> PermissionStatus {
        switch kind {
        case .microphone: microphone
        case .screenAudio: screenAudio
        }
    }

    /// First refresh, and a refresh whenever Hearsay becomes active.
    func start() {
        refresh()
        guard activeObserver == nil else { return }
        activeObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    /// Reads both permissions, stores a granted identity, and handles a
    /// stale system audio grant.
    func refresh() {
        let micDecision = StaleGrantDetector(
            currentCodeHash: codeIdentity, lastGrantedCodeHash: settings.microphoneGrantedCodeHash
        ).microphone(sources.microphone())
        if let hash = micDecision.grantedHashToStore { settings.microphoneGrantedCodeHash = hash }
        if microphone != micDecision.status { microphone = micDecision.status }

        let screenDecision = StaleGrantDetector(
            currentCodeHash: codeIdentity,
            lastGrantedCodeHash: settings.screenAudioGrantedCodeHash,
            lastResetCodeHash: settings.screenAudioResetCodeHash
        ).screenAudio(preflightGranted: sources.screenPreflight())
        if let hash = screenDecision.grantedHashToStore { settings.screenAudioGrantedCodeHash = hash }
        if screenAudio != screenDecision.status { screenAudio = screenDecision.status }

        if screenDecision.shouldReset, allowsReset, !resetInFlight, let bundleIdentifier {
            resetStaleScreenAudioEntry(bundleID: bundleIdentifier, hash: screenDecision.resetHashToStore)
        }
    }

    private func resetStaleScreenAudioEntry(bundleID: String, hash: String?) {
        resetInFlight = true
        // Stored first: at most one reset per build, even if this one fails.
        settings.screenAudioResetCodeHash = hash
        Self.logger.notice("system audio grant is stale for this build; resetting the TCC entry")
        let reset = sources.resetScreenCapture
        Task { @MainActor in
            let succeeded = await reset(bundleID)
            resetInFlight = false
            screenAudioResetSucceeded = succeeded
            guidance = .screenAudio
            onStaleGrantDetected()
        }
    }

    // MARK: - Sheet polling

    /// Refreshes every 2 s until `stopPolling()` (the guidance sheet).
    func startPolling() {
        pollTask?.cancel()
        pollTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                guard !Task.isCancelled else { return }
                self?.refresh()
            }
        }
    }

    func stopPolling() {
        pollTask?.cancel()
        pollTask = nil
    }

    // MARK: - Actions

    func showGuidance(for kind: PermissionKind) {
        guidance = kind
    }

    /// "Allow" for a microphone that was never asked.
    func requestMicrophone() {
        Task { @MainActor in
            _ = await sources.requestMicrophone()
            refresh()
        }
    }

    /// Opens the Privacy & Security pane for `kind`. For system audio that
    /// is not granted, asks first: when TCC has no entry (never asked, or
    /// removed by the reset) that shows the system prompt and lists Hearsay
    /// in the pane so it can be turned on; otherwise it does nothing.
    func openSystemSettings(kind: PermissionKind) {
        let url: URL?
        switch kind {
        case .microphone:
            url = Self.microphoneSettingsURL
        case .screenAudio:
            if screenAudio != .granted { _ = sources.requestScreenAudio() }
            url = Self.screenAudioSettingsURL
        }
        guard let url else { return }
        sources.open(url)
    }

    // MARK: - System

    /// The running code's designated requirement, the value TCC stores with
    /// a grant. For an ad-hoc signature it is `cdhash H"…"`, which changes
    /// with every build; for a Developer ID it stays the same across
    /// updates. Falls back to the cdhash (`kSecCodeInfoUnique`).
    nonisolated static func currentCodeIdentity() -> String? {
        var code: SecCode?
        guard SecCodeCopySelf(SecCSFlags(), &code) == errSecSuccess, let code else { return nil }
        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(code, SecCSFlags(), &staticCode) == errSecSuccess, let staticCode
        else { return nil }
        var requirement: SecRequirement?
        if SecCodeCopyDesignatedRequirement(staticCode, SecCSFlags(), &requirement) == errSecSuccess,
           let requirement {
            var text: CFString?
            if SecRequirementCopyString(requirement, SecCSFlags(), &text) == errSecSuccess, let text {
                return text as String
            }
        }
        var information: CFDictionary?
        guard SecCodeCopySigningInformation(
            staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &information
        ) == errSecSuccess,
            let dictionary = information as? [String: Any],
            let unique = dictionary[kSecCodeInfoUnique as String] as? Data
        else { return nil }
        return "cdhash H\"" + unique.map { String(format: "%02x", $0) }.joined() + "\""
    }

    /// `/usr/bin/tccutil reset ScreenCapture <bundleID>` with a 10 s limit.
    /// Removes the entry for the current user; no administrator rights are
    /// needed. Never runs in a debug run (its bundle id is the real app's).
    nonisolated static func resetScreenCaptureEntry(bundleID: String) async -> Bool {
        let isDebugRun = await MainActor.run { DebugDefaults.isDebugRun }
        guard !isDebugRun else {
            logger.notice("tccutil skipped in a debug run")
            return false
        }
        return await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
                process.arguments = ["reset", "ScreenCapture", bundleID]
                let pipe = Pipe()
                process.standardOutput = pipe
                process.standardError = pipe
                do {
                    try process.run()
                } catch {
                    logger.error("tccutil could not start: \(error.localizedDescription, privacy: .public)")
                    continuation.resume(returning: false)
                    return
                }
                let timeout = DispatchWorkItem { process.terminate() }
                DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 10, execute: timeout)
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                timeout.cancel()
                let output = String(decoding: data, as: UTF8.self)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                let succeeded = process.terminationReason == .exit && process.terminationStatus == 0
                logger.notice(
                    "tccutil reset ScreenCapture \(bundleID, privacy: .public): status \(process.terminationStatus), \(output, privacy: .public)"
                )
                continuation.resume(returning: succeeded)
            }
        }
    }
}
