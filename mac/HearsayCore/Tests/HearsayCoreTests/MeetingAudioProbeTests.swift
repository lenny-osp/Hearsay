import Foundation
import Testing
@testable import HearsayCore

/// Smoke tests of the CoreAudio process-object probe against the real
/// system. The machine may have no audio process at all, so these check only
/// that the calls succeed and are well formed.
struct MeetingAudioProbeTests {
    @Test func listingReturnsWithoutThrowingAndHasNoDuplicatePIDs() throws {
        guard #available(macOS 14.2, *) else { return }
        let processes = try MeetingAudioProbe.captureProcesses()
        let pids = processes.map(\.pid)
        #expect(Set(pids).count == pids.count)
        for process in processes {
            #expect(process.pid > 0)
            #expect(process.bundleID?.isEmpty != true)
        }
        // Twice in a row: nothing is left registered or leaked by a listing.
        _ = try MeetingAudioProbe.captureProcesses()
    }

    @Test func executablePathOfThisProcessIsReadable() {
        guard #available(macOS 14.2, *) else { return }
        let path = MeetingAudioProbe.executablePath(pid: ProcessInfo.processInfo.processIdentifier)
        #expect(path?.isEmpty == false)
        #expect(MeetingAudioProbe.executablePath(pid: -1) == nil)
    }

    @Test func observationCanBeStartedAndStopped() throws {
        guard #available(macOS 14.2, *) else { return }
        let queue = DispatchQueue(label: "tw.og1o.hearsay.tests.meeting-probe")
        let observer = MeetingAudioObserver(queue: queue) {}
        #expect(!observer.isObserving)
        try observer.start()
        #expect(observer.isObserving)
        try observer.start()  // Idempotent.
        observer.stop()
        #expect(!observer.isObserving)
        observer.stop()  // Safe twice.
        // Restartable, and deinit removes whatever is attached.
        try observer.start()
        #expect(observer.isObserving)
    }
}
