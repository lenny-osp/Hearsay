import CoreAudio
import Darwin
import Foundation

/// A CoreAudio call failed while listing or observing audio processes.
public struct MeetingAudioProbeError: Error, Equatable, Sendable, CustomStringConvertible {
    /// What was being done, for the log.
    public let operation: String
    public let status: OSStatus

    public init(operation: String, status: OSStatus) {
        self.operation = operation
        self.status = status
    }

    public var description: String { "\(operation) failed (OSStatus \(status))" }
}

/// Lists the processes CoreAudio knows as audio clients, with whether each is
/// capturing input right now (PLAN.md 4.10). macOS 14.2 and later: the
/// process-object API is new there. Reads no audio and needs no permission.
@available(macOS 14.2, *)
public enum MeetingAudioProbe {
    /// Every process object with its pid, bundle id, executable path and
    /// `isRunningInput`, one entry per pid. A process object that goes away
    /// while it is being read is skipped; a failed list call throws.
    public static func captureProcesses() throws -> [CaptureProcess] {
        let objects = try processObjectIDs()
        var byPID: [Int32: CaptureProcess] = [:]
        var order: [Int32] = []
        for object in objects {
            guard let process = captureProcess(for: object) else { continue }
            if var existing = byPID[process.pid] {
                // Two objects for one pid: it is capturing if either is.
                existing.isRunningInput = existing.isRunningInput || process.isRunningInput
                existing.bundleID = existing.bundleID ?? process.bundleID
                existing.executablePath = existing.executablePath ?? process.executablePath
                byPID[process.pid] = existing
            } else {
                byPID[process.pid] = process
                order.append(process.pid)
            }
        }
        return order.compactMap { byPID[$0] }
    }

    /// `kAudioHardwarePropertyProcessObjectList` on the system object.
    static func processObjectIDs() throws -> [AudioObjectID] {
        var address = processListAddress
        var size: UInt32 = 0
        var status = AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size)
        guard status == noErr else {
            throw MeetingAudioProbeError(operation: "listing audio processes (size)", status: status)
        }
        // The list can change between the size and the data call: retry once
        // with a fresh size when the data does not fit.
        for _ in 0..<2 {
            if size == 0 { return [] }
            let capacity = Int(size) / MemoryLayout<AudioObjectID>.size
            var ids = [AudioObjectID](repeating: 0, count: capacity)
            var dataSize = size
            status = ids.withUnsafeMutableBytes { buffer in
                guard let base = buffer.baseAddress else { return kAudioHardwareUnspecifiedError }
                return AudioObjectGetPropertyData(
                    AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize, base)
            }
            if status == noErr {
                return Array(ids.prefix(Int(dataSize) / MemoryLayout<AudioObjectID>.size))
            }
            guard status == kAudioHardwareBadPropertySizeError else { break }
            let sizeStatus = AudioObjectGetPropertyDataSize(
                AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size)
            guard sizeStatus == noErr else { status = sizeStatus; break }
        }
        throw MeetingAudioProbeError(operation: "listing audio processes", status: status)
    }

    /// One process object, or nil when it vanished or has no pid.
    static func captureProcess(for object: AudioObjectID) -> CaptureProcess? {
        guard let pid: Int32 = readValue(object, kAudioProcessPropertyPID),
              let running: UInt32 = readValue(object, kAudioProcessPropertyIsRunningInput)
        else { return nil }
        let bundleID = readString(object, kAudioProcessPropertyBundleID)
        return CaptureProcess(
            pid: pid,
            bundleID: (bundleID?.isEmpty ?? true) ? nil : bundleID,
            executablePath: executablePath(pid: pid),
            isRunningInput: running != 0)
    }

    /// The executable's path from libproc, or nil when the process is gone or
    /// not readable.
    static func executablePath(pid: Int32) -> String? {
        guard pid > 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: 4096)
        let length = proc_pidpath(pid, &buffer, UInt32(buffer.count))
        guard length > 0 else { return nil }
        let bytes = buffer.prefix(Int(length)).map { UInt8(bitPattern: $0) }
        return String(decoding: bytes, as: UTF8.self)
    }

    static var processListAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyProcessObjectList,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
    }

    static func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
    }

    private static func readValue<T: FixedWidthInteger>(
        _ object: AudioObjectID, _ selector: AudioObjectPropertySelector
    ) -> T? {
        var address = address(selector)
        var value: T = 0
        var size = UInt32(MemoryLayout<T>.size)
        let status = AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value)
        return status == noErr ? value : nil
    }

    private static func readString(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) { pointer in
            AudioObjectGetPropertyData(object, &address, 0, nil, &size, pointer)
        }
        guard status == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }
}

/// Reports changes of the audio process list and of each process's
/// `isRunningInput` through one callback, with no polling timer. The
/// callback says only that something changed: the owner re-reads
/// `MeetingAudioProbe.captureProcesses()` and feeds the detector.
///
/// Listeners: one on the system object's process list, one on every process
/// object's `kAudioProcessPropertyIsRunningInput`; the per-process ones are
/// re-attached whenever the list changes and all are removed by `stop()` and
/// `deinit`. The callback runs on `queue` (CoreAudio delivers there), may run
/// several times in a row, and may run once after `stop()` returns if it was
/// already queued; the owner should ignore it then.
@available(macOS 14.2, *)
public final class MeetingAudioObserver: @unchecked Sendable {
    private let queue: DispatchQueue
    private let handler: @Sendable () -> Void
    private let lock = NSLock()
    /// Serializes `syncProcessListeners` (called from `start` and from `queue`).
    private let syncLock = NSLock()
    // All guarded by `lock`.
    private var running = false
    private var listListener: AudioObjectPropertyListenerBlock?
    private var processListeners: [AudioObjectID: AudioObjectPropertyListenerBlock] = [:]

    /// - Parameters:
    ///   - queue: where `handler` runs.
    ///   - handler: called when the process list or any process's input state changed.
    public init(queue: DispatchQueue, handler: @escaping @Sendable () -> Void) {
        self.queue = queue
        self.handler = handler
    }

    deinit {
        removeAll()
    }

    /// True between a successful `start()` and `stop()`.
    public var isObserving: Bool { lock.withLock { running } }

    /// Attaches the listeners. Does nothing when already started; throws
    /// (and leaves nothing attached) when the process list cannot be
    /// observed or read.
    public func start() throws {
        let alreadyRunning: Bool = lock.withLock {
            if running { return true }
            running = true
            return false
        }
        if alreadyRunning { return }

        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            self?.processListChanged()
        }
        var address = MeetingAudioProbe.processListAddress
        let status = AudioObjectAddPropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &address, queue, block)
        guard status == noErr else {
            lock.withLock { running = false }
            throw MeetingAudioProbeError(operation: "observing the audio process list", status: status)
        }
        lock.withLock { listListener = block }
        do {
            try syncProcessListeners()
        } catch {
            removeAll()
            throw error
        }
    }

    /// Removes every listener. Safe to call twice.
    public func stop() {
        removeAll()
    }

    private func processListChanged() {
        guard isObserving else { return }
        try? syncProcessListeners()
        handler()
    }

    private func inputChanged() {
        guard isObserving else { return }
        handler()
    }

    /// Makes the per-process listeners match the current process list.
    private func syncProcessListeners() throws {
        syncLock.lock()
        defer { syncLock.unlock() }
        let current = Set(try MeetingAudioProbe.processObjectIDs())
        var address = MeetingAudioProbe.address(kAudioProcessPropertyIsRunningInput)

        let stale: [(AudioObjectID, AudioObjectPropertyListenerBlock)] = lock.withLock {
            let gone = processListeners.filter { !current.contains($0.key) }
            for key in gone.keys { processListeners[key] = nil }
            return gone.map { ($0.key, $0.value) }
        }
        for (object, block) in stale {
            // The object is gone: a failure here is expected and harmless.
            AudioObjectRemovePropertyListenerBlock(object, &address, queue, block)
        }

        for object in current {
            let known = lock.withLock { !running || processListeners[object] != nil }
            if known { continue }
            let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                self?.inputChanged()
            }
            let status = AudioObjectAddPropertyListenerBlock(object, &address, queue, block)
            // A process that vanished since the list was read: skip it.
            guard status == noErr else { continue }
            lock.withLock { processListeners[object] = block }
        }
    }

    private func removeAll() {
        // The snapshot waits for a sync in flight, so a listener it is adding
        // cannot be left attached; a later sync sees `running` false and adds
        // nothing. The listeners are removed outside the lock, in case
        // CoreAudio waits for a callback that is itself waiting for the lock.
        let (list, processes): (AudioObjectPropertyListenerBlock?, [AudioObjectID: AudioObjectPropertyListenerBlock]) =
            syncLock.withLock {
                lock.withLock {
                    running = false
                    let result = (listListener, processListeners)
                    listListener = nil
                    processListeners = [:]
                    return result
                }
            }
        if let list {
            var address = MeetingAudioProbe.processListAddress
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, queue, list)
        }
        var address = MeetingAudioProbe.address(kAudioProcessPropertyIsRunningInput)
        for (object, block) in processes {
            AudioObjectRemovePropertyListenerBlock(object, &address, queue, block)
        }
    }
}
