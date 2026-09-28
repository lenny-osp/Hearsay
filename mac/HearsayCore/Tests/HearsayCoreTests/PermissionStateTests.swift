import Testing
@testable import HearsayCore

/// `StaleGrantDetector` (PLAN.md section 9). No Python counterpart: the CLI
/// runs in Terminal, which holds the permissions.
struct PermissionStateTests {
    private let current = "cdhash H\"new\""
    private let previous = "cdhash H\"old\""

    // MARK: - System audio

    @Test func screenGrantedStoresTheCurrentHash() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: nil)
        #expect(detector.screenAudio(preflightGranted: true)
                == .init(status: .granted, grantedHashToStore: current))
    }

    @Test func screenGrantedAfterUpdateReplacesTheStoredHash() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous,
                                          lastResetCodeHash: current)
        #expect(detector.screenAudio(preflightGranted: true)
                == .init(status: .granted, grantedHashToStore: current))
    }

    @Test func screenGrantedWithTheSameHashStoresNothing() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: current)
        #expect(detector.screenAudio(preflightGranted: true) == .init(status: .granted))
    }

    @Test func screenGrantedWithUnknownHashStoresNothing() {
        let detector = StaleGrantDetector(currentCodeHash: nil, lastGrantedCodeHash: previous)
        #expect(detector.screenAudio(preflightGranted: true) == .init(status: .granted))
    }

    @Test func screenNeverGrantedIsDenied() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: nil)
        #expect(detector.screenAudio(preflightGranted: false) == .init(status: .denied))
    }

    @Test func screenRevokedUnderTheSameHashIsDenied() {
        // The user turned it off in System Settings: not stale, no reset.
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: current)
        #expect(detector.screenAudio(preflightGranted: false) == .init(status: .denied))
    }

    @Test func screenDeniedWithUnknownCurrentHashIsDenied() {
        let detector = StaleGrantDetector(currentCodeHash: nil, lastGrantedCodeHash: previous)
        #expect(detector.screenAudio(preflightGranted: false) == .init(status: .denied))
    }

    @Test func screenDeniedAfterUpdateIsStaleAndResetsOnce() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous)
        #expect(detector.screenAudio(preflightGranted: false)
                == .init(status: .staleGrant, shouldReset: true, resetHashToStore: current))
    }

    @Test func screenStaleAfterResetForThisHashDoesNotResetAgain() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous,
                                          lastResetCodeHash: current)
        #expect(detector.screenAudio(preflightGranted: false) == .init(status: .staleGrant))
    }

    @Test func screenStaleAfterAnotherUpdateResetsAgain() {
        // Reset for an earlier build, then another update arrived.
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous,
                                          lastResetCodeHash: previous)
        #expect(detector.screenAudio(preflightGranted: false)
                == .init(status: .staleGrant, shouldReset: true, resetHashToStore: current))
    }

    @Test func fullUpdateCycle() {
        var detector = StaleGrantDetector(currentCodeHash: previous, lastGrantedCodeHash: nil)
        // First grant under the old build.
        var decision = detector.screenAudio(preflightGranted: true)
        detector.lastGrantedCodeHash = decision.grantedHashToStore ?? detector.lastGrantedCodeHash
        #expect(detector.lastGrantedCodeHash == previous)
        // Update: new identity, preflight false.
        detector.currentCodeHash = current
        decision = detector.screenAudio(preflightGranted: false)
        #expect(decision.shouldReset)
        detector.lastResetCodeHash = decision.resetHashToStore
        // Later polls: still stale, no second reset.
        #expect(detector.screenAudio(preflightGranted: false) == .init(status: .staleGrant))
        // The user turns Hearsay on again.
        decision = detector.screenAudio(preflightGranted: true)
        #expect(decision == .init(status: .granted, grantedHashToStore: current))
        detector.lastGrantedCodeHash = decision.grantedHashToStore
        #expect(detector.screenAudio(preflightGranted: false) == .init(status: .denied))
    }

    // MARK: - Microphone

    @Test func micAuthorizedStoresTheCurrentHash() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous)
        #expect(detector.microphone(.authorized) == .init(status: .granted, grantedHashToStore: current))
    }

    @Test func micAuthorizedWithTheSameHashStoresNothing() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: current)
        #expect(detector.microphone(.authorized) == .init(status: .granted))
    }

    @Test func micNotDeterminedNeverResets() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous)
        #expect(detector.microphone(.notDetermined) == .init(status: .notDetermined))
    }

    @Test func micDeniedWithoutStoredGrantIsDenied() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: nil)
        #expect(detector.microphone(.denied) == .init(status: .denied))
    }

    @Test func micDeniedUnderTheSameHashIsDenied() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: current)
        #expect(detector.microphone(.denied) == .init(status: .denied))
    }

    @Test func micDeniedAfterUpdateIsStaleWithoutReset() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous)
        #expect(detector.microphone(.denied) == .init(status: .staleGrant))
    }

    @Test func micRestrictedIsDenied() {
        let detector = StaleGrantDetector(currentCodeHash: current, lastGrantedCodeHash: previous)
        #expect(detector.microphone(.restricted) == .init(status: .denied))
    }
}
