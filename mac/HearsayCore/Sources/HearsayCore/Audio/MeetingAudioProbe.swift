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

/// Reports changes that may mean a process started or stopped capturing,
/// through one callback. The callback says only that something changed: the
/// owner re-reads `MeetingAudioProbe.captureProcesses()` and feeds the
/// detector.
///
/// Listeners: the system object's process list and device list, and every
/// audio device's `kAudioDevicePropertyDeviceIsRunningSomewhere`; the device
/// ones are re-attached whenever the device list changes, and all are
/// removed by `stop()` and `deinit`. A listener on a process object's
/// `kAudioProcessPropertyIsRunningInput` is not used: measured on macOS 27
/// (2026-10-02), CoreAudio never calls it, although the property itself
/// changes. The device listener does not fire either when another process
/// keeps the device running (Hearsay's own recording from the microphone
/// Teams uses), so these listeners only make a change noticed sooner: the
/// owner must still poll (PLAN.md 4.10).
///
/// The callback runs on `queue` (CoreAudio delivers there), may run several
/// times in a row, and may run once after `stop()` returns if it was already
/// queued; the owner should ignore it then.
@available(macOS 14.2, *)
public final class MeetingAudioObserver: @unchecked Sendable {
    private let queue: DispatchQueue
    private let handler: @Sendable () -> Void
    private let lock = NSLock()
    /// Serializes `syncDeviceListeners` (called from `start` and from `queue`).
    private let syncLock = NSLock()
    // All guarded by `lock`.
    private var running = false
    private var systemListeners: [(AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    private var deviceListeners: [AudioObjectID: AudioObjectPropertyListenerBlock] = [:]

    /// - Parameters:
    ///   - queue: where `handler` runs.
    ///   - handler: called when the process list, the device list or a
    ///     device's running state changed.
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
    /// (and leaves nothing attached) when the system object cannot be
    /// observed or the device list cannot be read.
    public func start() throws {
        let alreadyRunning: Bool = lock.withLock {
            if running { return true }
            running = true
            return false
        }
        if alreadyRunning { return }

        let processList: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.changed() }
        let deviceList: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.deviceListChanged() }
        for (selector, block) in [
            (kAudioHardwarePropertyProcessObjectList, processList),
            (kAudioHardwarePropertyDevices, deviceList),
        ] {
            var address = MeetingAudioProbe.address(selector)
            let status = AudioObjectAddPropertyListenerBlock(
                AudioObjectID(kAudioObjectSystemObject), &address, queue, block)
            guard status == noErr else {
                removeAll()
                throw MeetingAudioProbeError(operation: "observing the audio system", status: status)
            }
            lock.withLock { systemListeners.append((address, block)) }
        }
        do {
            try syncDeviceListeners()
        } catch {
            removeAll()
            throw error
        }
    }

    /// Removes every listener. Safe to call twice.
    public func stop() {
        removeAll()
    }

    private func changed() {
        guard isObserving else { return }
        handler()
    }

    private func deviceListChanged() {
        guard isObserving else { return }
        try? syncDeviceListeners()
        handler()
    }

    /// `kAudioHardwarePropertyDevices` on the system object.
    private static func deviceIDs() throws -> [AudioObjectID] {
        var address = MeetingAudioProbe.address(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        var status = AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size)
        guard status == noErr else {
            throw MeetingAudioProbeError(operation: "listing audio devices (size)", status: status)
        }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard !ids.isEmpty else { return [] }
        status = ids.withUnsafeMutableBytes { buffer in
            guard let base = buffer.baseAddress else { return kAudioHardwareUnspecifiedError }
            return AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, base)
        }
        guard status == noErr else {
            throw MeetingAudioProbeError(operation: "listing audio devices", status: status)
        }
        return Array(ids.prefix(Int(size) / MemoryLayout<AudioObjectID>.size))
    }

    /// Makes the per-device listeners match the current device list.
    private func syncDeviceListeners() throws {
        try syncLock.withLock {
            let current = Set(try Self.deviceIDs())
            var address = MeetingAudioProbe.address(kAudioDevicePropertyDeviceIsRunningSomewhere)

            let stale: [(AudioObjectID, AudioObjectPropertyListenerBlock)] = lock.withLock {
                let gone = deviceListeners.filter { !current.contains($0.key) }
                for key in gone.keys { deviceListeners[key] = nil }
                return gone.map { ($0.key, $0.value) }
            }
            for (device, block) in stale {
                // The device is gone: a failure here is expected and harmless.
                AudioObjectRemovePropertyListenerBlock(device, &address, queue, block)
            }

            for device in current {
                let known = lock.withLock { !running || deviceListeners[device] != nil }
                if known { continue }
                let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.changed() }
                let status = AudioObjectAddPropertyListenerBlock(device, &address, queue, block)
                // A device that vanished since the list was read: skip it.
                guard status == noErr else { continue }
                lock.withLock { deviceListeners[device] = block }
            }
        }
    }

    private func removeAll() {
        // The snapshot waits for a sync in flight, so a listener it is adding
        // cannot be left attached; a later sync sees `running` false and adds
        // nothing. The listeners are removed outside the lock, in case
        // CoreAudio waits for a callback that is itself waiting for the lock.
        let (system, devices) = syncLock.withLock {
            lock.withLock {
                running = false
                let result = (systemListeners, deviceListeners)
                systemListeners = []
                deviceListeners = [:]
                return result
            }
        }
        for (address, block) in system {
            var address = address
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, queue, block)
        }
        var address = MeetingAudioProbe.address(kAudioDevicePropertyDeviceIsRunningSomewhere)
        for (device, block) in devices {
            AudioObjectRemovePropertyListenerBlock(device, &address, queue, block)
        }
    }
}
