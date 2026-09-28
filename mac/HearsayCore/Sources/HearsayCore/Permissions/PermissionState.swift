import Foundation

/// The two privacy permissions a recording needs (PLAN.md section 9).
public enum PermissionKind: String, Sendable, Equatable, CaseIterable {
    /// Microphone (AVFoundation).
    case microphone
    /// Screen & System Audio Recording (ScreenCaptureKit).
    case screenAudio
}

/// What the Record tab shows for one permission.
public enum PermissionStatus: Sendable, Equatable {
    case granted
    /// Denied, or (for system audio, where CoreGraphics cannot tell the two
    /// apart) never asked.
    case denied
    /// Never asked (microphone only).
    case notDetermined
    /// macOS no longer honors an earlier grant because this build's code
    /// signature differs from the one that was granted (an ad-hoc signed
    /// update). System Settings may still show Hearsay as allowed.
    case staleGrant
}

/// Decides whether a permission grant went stale because the app's code
/// identity changed (PLAN.md section 9).
///
/// TCC keys a grant on the app's code-signing requirement. For an ad-hoc
/// signature that requirement is the cdhash, which changes with every build,
/// so after an update `CGPreflightScreenCaptureAccess()` returns false while
/// System Settings still lists Hearsay as enabled, and macOS shows no new
/// prompt. Hearsay remembers the identity it last saw granted; a denial under
/// a different identity is a stale grant. For system audio the stale entry
/// is removed with `tccutil reset` at most once per identity.
///
/// Pure: every input is injected, every output is returned; the caller
/// stores `grantedHashToStore` and `resetHashToStore`.
public struct StaleGrantDetector: Sendable, Equatable {
    /// This build's code identity, or nil when it could not be read.
    public var currentCodeHash: String?
    /// The identity stored when the permission was last seen granted.
    public var lastGrantedCodeHash: String?
    /// The identity for which the stale entry was last reset.
    public var lastResetCodeHash: String?

    public init(currentCodeHash: String?, lastGrantedCodeHash: String?, lastResetCodeHash: String? = nil) {
        self.currentCodeHash = currentCodeHash
        self.lastGrantedCodeHash = lastGrantedCodeHash
        self.lastResetCodeHash = lastResetCodeHash
    }

    public struct Decision: Sendable, Equatable {
        public var status: PermissionStatus
        /// Run `tccutil reset` now (system audio only).
        public var shouldReset: Bool
        /// Store as the last-granted identity (nil: leave the stored value).
        public var grantedHashToStore: String?
        /// Store as the last-reset identity (nil: leave the stored value).
        public var resetHashToStore: String?

        public init(status: PermissionStatus, shouldReset: Bool = false,
                    grantedHashToStore: String? = nil, resetHashToStore: String? = nil) {
            self.status = status
            self.shouldReset = shouldReset
            self.grantedHashToStore = grantedHashToStore
            self.resetHashToStore = resetHashToStore
        }
    }

    /// A grant was stored under an identity other than the current one.
    private var grantedUnderOtherIdentity: Bool {
        guard let currentCodeHash, let lastGrantedCodeHash else { return false }
        return lastGrantedCodeHash != currentCodeHash
    }

    /// Stores the current identity after a grant, unless it is already
    /// stored or unknown.
    private var grantedDecision: Decision {
        let store = currentCodeHash.flatMap { $0 == lastGrantedCodeHash ? nil : $0 }
        return Decision(status: .granted, grantedHashToStore: store)
    }

    /// Screen & System Audio Recording, from `CGPreflightScreenCaptureAccess()`.
    public func screenAudio(preflightGranted: Bool) -> Decision {
        if preflightGranted { return grantedDecision }
        guard grantedUnderOtherIdentity, let currentCodeHash else { return Decision(status: .denied) }
        guard lastResetCodeHash != currentCodeHash else { return Decision(status: .staleGrant) }
        return Decision(status: .staleGrant, shouldReset: true, resetHashToStore: currentCodeHash)
    }

    /// Microphone, from `AVCaptureDevice.authorizationStatus(for: .audio)`.
    /// Never resets: macOS asks again by itself when the status is
    /// `.notDetermined`.
    public func microphone(_ permission: MicrophonePermission) -> Decision {
        switch permission {
        case .authorized: grantedDecision
        case .notDetermined: Decision(status: .notDetermined)
        case .restricted: Decision(status: .denied)
        case .denied: Decision(status: grantedUnderOtherIdentity ? .staleGrant : .denied)
        }
    }
}
