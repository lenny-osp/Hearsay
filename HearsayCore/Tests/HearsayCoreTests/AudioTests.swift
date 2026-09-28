import AVFoundation
import Foundation
import Testing
@testable import HearsayCore

private func makeTempDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("HearsayAudioTests-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private let fixtures = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()  // HearsayCoreTests
    .deletingLastPathComponent()  // Tests
    .deletingLastPathComponent()  // HearsayCore
    .deletingLastPathComponent()  // repo root
    .appendingPathComponent("Fixtures", isDirectory: true)

/// A 440 Hz sine at half scale, `count` samples at 16 kHz.
private func sine(count: Int) -> [Float] {
    (0..<count).map { Float(0.5 * sin(2 * Double.pi * 440 * Double($0) / 16_000)) }
}

private func readInt16(_ url: URL) throws -> (AVAudioFormat, [Int16]) {
    let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatInt16, interleaved: true)
    let frames = AVAudioFrameCount(file.length)
    let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: max(frames, 1)))
    try file.read(into: buffer)
    let channel = try #require(buffer.int16ChannelData?[0])
    return (file.fileFormat, Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength))))
}

struct WavWriterTests {
    @Test func headerIsCanonical44BytesForPythonFormat() {
        let header = WavWriter.header(dataBytes: 320)
        #expect(header.count == 44)
        #expect(Array(header[0..<4]) == Array("RIFF".utf8))
        #expect(Array(header[8..<16]) == Array("WAVEfmt ".utf8))
        #expect(Array(header[36..<40]) == Array("data".utf8))
        let bytes = [UInt8](header)
        func u32(_ o: Int) -> UInt32 { UInt32(bytes[o]) | UInt32(bytes[o + 1]) << 8 | UInt32(bytes[o + 2]) << 16 | UInt32(bytes[o + 3]) << 24 }
        func u16(_ o: Int) -> UInt16 { UInt16(bytes[o]) | UInt16(bytes[o + 1]) << 8 }
        #expect(u32(4) == 36 + 320)
        #expect(u16(20) == 1)        // PCM
        #expect(u16(22) == 1)        // -ac 1
        #expect(u32(24) == 16_000)   // -ar 16000
        #expect(u32(28) == 32_000)   // byte rate
        #expect(u16(32) == 2)        // block align
        #expect(u16(34) == 16)       // pcm_s16le
        #expect(u32(40) == 320)
    }

    @Test func sampleConversionClampsAndScales() {
        #expect(WavWriter.int16(0) == 0)
        #expect(WavWriter.int16(1) == 32767)
        #expect(WavWriter.int16(-1) == -32767)
        #expect(WavWriter.int16(2.5) == 32767)
        #expect(WavWriter.int16(-7) == -32767)
        #expect(WavWriter.int16(.nan) == 0)
        #expect(WavWriter.int16(0.5) == 16384)
    }

    @Test func writtenFileReadsBackWithFormatCountAndValues() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("out.wav")
        let samples = sine(count: 16_000) + [1.5, -1.5]
        let writer = try WavWriter(url: url)
        try writer.append(Array(samples[0..<7_000]))
        try writer.append(Array(samples[7_000...]))
        #expect(writer.sampleCount == samples.count)
        try writer.close()
        try writer.close()  // idempotent

        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        #expect((attributes[.size] as? Int) == 44 + samples.count * 2)

        let (format, values) = try readInt16(url)
        #expect(format.sampleRate == 16_000)
        #expect(format.channelCount == 1)
        #expect(format.commonFormat == .pcmFormatInt16)
        #expect(values.count == samples.count)
        for (index, sample) in samples.enumerated() {
            #expect(values[index] == WavWriter.int16(sample))
        }
        #expect(values[values.count - 2] == 32767)
        #expect(values[values.count - 1] == -32767)
        #expect(abs(try WavWriter.duration(of: url) - Double(samples.count) / 16_000) < 1e-9)
        #expect(try WavWriter.hasUnfinishedHeader(at: url) == false)
        #expect(try WavWriter.patchHeader(at: url) == false)
    }

    @Test func appendAfterCloseThrows() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let writer = try WavWriter(url: directory.appendingPathComponent("a.wav"))
        try writer.close()
        #expect(throws: WavError.closed) { try writer.append([0.1]) }
    }

    @Test func patchHeaderRepairsZeroSizesLeftByACrash() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("crashed.wav")
        let samples = sine(count: 8_000)
        // What a crash leaves: the initial zero-size header plus the audio,
        // plus a torn half sample at the end.
        var contents = WavWriter.header(dataBytes: 0)
        contents.append(WavWriter.pcmData(samples))
        contents.append(0x7F)
        try contents.write(to: url)

        #expect(try WavWriter.hasUnfinishedHeader(at: url))
        #expect(abs(try WavWriter.duration(of: url) - 0.5) < 1e-9)
        #expect(try WavWriter.patchHeader(at: url))
        #expect(try WavWriter.hasUnfinishedHeader(at: url) == false)
        #expect(try WavWriter.patchHeader(at: url) == false)

        let info = try WavWriter.inspect(url: url)
        #expect(info.declaredDataBytes == UInt32(samples.count * 2))
        #expect(info.riffSize == UInt32(contents.count - 8))
        let (_, values) = try readInt16(url)
        #expect(values.count == samples.count)
        #expect(values.first == WavWriter.int16(samples[0]))
        #expect(values.last == WavWriter.int16(samples[samples.count - 1]))
    }

    @Test func emptyRecordingIsNotUnfinished() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("empty.wav")
        try WavWriter.header(dataBytes: 0).write(to: url)
        #expect(try WavWriter.hasUnfinishedHeader(at: url) == false)
        #expect(try WavWriter.duration(of: url) == 0)
    }

    @Test func nonWavFileIsRejected() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("not.wav")
        try Data("hello, this is not audio at all".utf8).write(to: url)
        #expect(throws: WavError.self) { try WavWriter.patchHeader(at: url) }
        #expect(throws: WavError.self) { try WavWriter.duration(of: url) }
    }

    @Test func durationOfFixturesWithExtraChunks() throws {
        #expect(abs(try WavWriter.duration(of: fixtures.appendingPathComponent("en-30s.wav")) - 18.94) < 0.01)
    }
}

struct RecordingSpoolTests {
    private func writeFinished(_ url: URL, samples: Int) throws {
        let writer = try WavWriter(url: url)
        try writer.append(sine(count: samples))
        try writer.close()
    }

    @Test func defaultRootIsApplicationSupportHearsayRecording() {
        let root = RecordingSpool.defaultRoot()
        #expect(root.lastPathComponent == "Recording")
        #expect(root.deletingLastPathComponent().lastPathComponent == "Hearsay")
        #expect(root.deletingLastPathComponent().deletingLastPathComponent().lastPathComponent == "Application Support")
    }

    @Test func newRecordingURLUsesTimestampAndAvoidsCollisions() throws {
        let root = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = RecordingSpool(root: root)
        let first = spool.newRecordingURL(timestamp: "2026-09-28_10-00-00")
        #expect(first == root.appendingPathComponent("2026-09-28_10-00-00.wav"))
        try writeFinished(first, samples: 10)
        #expect(spool.newRecordingURL(timestamp: "2026-09-28_10-00-00").lastPathComponent == "2026-09-28_10-00-00-2.wav")
        #expect(Timestamps.parse(fromFilename: spool.newRecordingURL(timestamp: Timestamps.now()).lastPathComponent) != nil)
    }

    @Test func unfinishedRecordingsListsZeroHeadersAndMissingSRT() throws {
        let root = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = RecordingSpool(root: root)

        // Finished with its SRT: not listed.
        let done = root.appendingPathComponent("2026-09-28_09-00-00.wav")
        try writeFinished(done, samples: 100)
        try Data("1\n".utf8).write(to: root.appendingPathComponent("2026-09-28_09-00-00.srt"))
        // Finished header but no SRT: listed.
        let noSRT = root.appendingPathComponent("2026-09-28_10-00-00.wav")
        try writeFinished(noSRT, samples: 100)
        // Crashed (zero header) even though an SRT exists: listed.
        let crashed = root.appendingPathComponent("2026-09-28_11-00-00.wav")
        var contents = WavWriter.header(dataBytes: 0)
        contents.append(WavWriter.pcmData(sine(count: 100)))
        try contents.write(to: crashed)
        try Data("1\n".utf8).write(to: root.appendingPathComponent("2026-09-28_11-00-00.srt"))
        // Not a WAV: ignored.
        try Data("x".utf8).write(to: root.appendingPathComponent("notes.txt"))

        #expect(spool.unfinishedRecordings().map(\.lastPathComponent) == [
            "2026-09-28_10-00-00.wav", "2026-09-28_11-00-00.wav",
        ])
    }

    @Test func unfinishedRecordingsIsEmptyWhenFolderIsMissing() {
        let spool = RecordingSpool(root: FileManager.default.temporaryDirectory
            .appendingPathComponent("missing-\(UUID().uuidString)"))
        #expect(spool.unfinishedRecordings().isEmpty)
    }

    @Test func finalizeKeepMovesWithNumericSuffixOnCollision() throws {
        let root = try makeTempDirectory()
        let output = try makeTempDirectory().appendingPathComponent("out", isDirectory: true)
        defer {
            try? FileManager.default.removeItem(at: root)
            try? FileManager.default.removeItem(at: output.deletingLastPathComponent())
        }
        let spool = RecordingSpool(root: root)

        let first = spool.newRecordingURL(timestamp: "2026-09-28_10-00-00")
        try writeFinished(first, samples: 50)
        let moved = try spool.finalize(first, keep: true, outputFolder: output)
        #expect(moved == output.appendingPathComponent("2026-09-28_10-00-00.wav"))
        #expect(!FileManager.default.fileExists(atPath: first.path))
        #expect(FileManager.default.fileExists(atPath: output.appendingPathComponent("2026-09-28_10-00-00.wav").path))

        let second = spool.newRecordingURL(timestamp: "2026-09-28_10-00-00")
        try writeFinished(second, samples: 60)
        let movedAgain = try spool.finalize(second, keep: true, outputFolder: output)
        #expect(movedAgain?.lastPathComponent == "2026-09-28_10-00-00-2.wav")
        #expect(try WavWriter.duration(of: output.appendingPathComponent("2026-09-28_10-00-00.wav")) == 50.0 / 16_000)
    }

    @Test func finalizeWithoutKeepDeletes() throws {
        let root = try makeTempDirectory()
        let output = try makeTempDirectory()
        defer {
            try? FileManager.default.removeItem(at: root)
            try? FileManager.default.removeItem(at: output)
        }
        let spool = RecordingSpool(root: root)
        let url = spool.newRecordingURL(timestamp: "2026-09-28_10-00-00")
        try writeFinished(url, samples: 10)
        #expect(try spool.finalize(url, keep: false, outputFolder: output) == nil)
        #expect(!FileManager.default.fileExists(atPath: url.path))
        #expect(try FileManager.default.contentsOfDirectory(atPath: output.path).isEmpty)
    }
}

struct AudioFileLoaderTests {
    @Test func englishFixtureDecodesToAbout1894Seconds() throws {
        let samples = try AudioFileLoader.loadMono16k(url: fixtures.appendingPathComponent("en-30s.wav"))
        #expect(abs(Double(samples.count) / 16_000 - 18.94) < 0.05)
        #expect(samples.contains { $0 != 0 })
        #expect(samples.allSatisfy { $0 >= -1 && $0 <= 1 })
    }

    @Test func chineseFixtureDecodesToAbout276Seconds() throws {
        let samples = try AudioFileLoader.loadMono16k(url: fixtures.appendingPathComponent("zh-30s.wav"))
        #expect(abs(Double(samples.count) / 16_000 - 27.6) < 0.05)
    }

    @Test func resamplesStereo44kToMono16k() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("stereo.caf")
        let format = try #require(AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 2))
        let frames: AVAudioFrameCount = 44_100 * 2
        let buffer = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames))
        buffer.frameLength = frames
        for channel in 0..<2 {
            let data = try #require(buffer.floatChannelData?[channel])
            for index in 0..<Int(frames) {
                data[index] = Float(0.25 * sin(2 * Double.pi * 440 * Double(index) / 44_100))
            }
        }
        do {
            let file = try AVAudioFile(forWriting: url, settings: format.settings)
            try file.write(from: buffer)
        }
        let samples = try AudioFileLoader.loadMono16k(url: url)
        #expect(abs(samples.count - 32_000) <= 16)
        let peak = samples.map(abs).max() ?? 0
        #expect(peak > 0.2 && peak < 0.3)
    }

    @Test func roundTripsAWavWriterFile() throws {
        let directory = try makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("rt.wav")
        let source = sine(count: 4_000)
        let writer = try WavWriter(url: url)
        try writer.append(source)
        try writer.close()
        let loaded = try AudioFileLoader.loadMono16k(url: url)
        #expect(loaded.count == source.count)
        for index in stride(from: 0, to: source.count, by: 97) {
            #expect(abs(loaded[index] - source[index]) < 1.0 / 16_000)
        }
    }

    @Test func missingFileThrows() {
        #expect(throws: (any Error).self) {
            try AudioFileLoader.loadMono16k(url: URL(fileURLWithPath: "/nonexistent/\(UUID().uuidString).wav"))
        }
    }
}

struct AudioDeviceListTests {
    @Test func enumerationDoesNotCrash() {
        let devices = AudioDeviceList.inputDevices()
        for device in devices {
            #expect(!device.uid.isEmpty)
            #expect(AudioDeviceList.inputChannelCount(device.id) > 0)
        }
        if let preferred = AudioDeviceList.defaultInputDevice() {
            #expect(devices.contains(preferred))
            #expect(AudioDeviceList.inputDevice(uid: preferred.uid) == preferred)
        }
    }

    @Test func observationRegistersAndUnregisters() {
        var observation: AudioDeviceListObservation? = AudioDeviceList.observeChanges {}
        #expect(observation != nil)
        observation = nil
        #expect(observation == nil)
    }
}

@MainActor
struct DefaultLanguageSettingTests {
    @Test func defaultLanguageCodeDefaultsToEnglishAndPersists() {
        let suite = "tw.og1o.hearsay.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite) ?? .standard
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        #expect(settings.defaultLanguageCode == "en")
        settings.defaultLanguageCode = "zh"
        #expect(defaults.string(forKey: AppSettings.Key.languageChoice) == "zh-TW")
        #expect(AppSettings(defaults: defaults).defaultLanguageCode == "zh-TW")
        #expect(AppSettings(defaults: defaults).languageChoice == .fixed(.chineseTaiwan))
        settings.defaultLanguageCode = "zh-CN"
        #expect(AppSettings(defaults: defaults).languageChoice == .fixed(.chineseMainland))
    }
}

struct ConfigurationRecoveryTests {
    @Test func allowsFiveRecoveriesPerTenSecondsThenRefuses() {
        var recovery = ConfigurationRecovery()
        for second in 0..<5 {
            let allowed = recovery.allowRecovery(at: Double(second))
            #expect(allowed)
        }
        do { let allowed = recovery.allowRecovery(at: 5); #expect(!allowed) }
        do { let allowed = recovery.allowRecovery(at: 9.9); #expect(!allowed) }
        // The first recovery (t = 0) has left the window.
        do { let allowed = recovery.allowRecovery(at: 10); #expect(allowed) }
        do { let allowed = recovery.allowRecovery(at: 10.5); #expect(!allowed) }
    }

    @Test func spacedOutChangesAreAlwaysAllowed() {
        var recovery = ConfigurationRecovery()
        for index in 0..<50 {
            let allowed = recovery.allowRecovery(at: Double(index) * 2.5)
            #expect(allowed)
        }
    }
}

@MainActor
struct MicrophoneRecorderConfigurationTests {
    private final class Counter: @unchecked Sendable {
        var restarts = 0
        var reinstalls = 0
        var probes = 0
    }

    private func recordingRecorder(
        center: NotificationCenter = NotificationCenter(),
        counter: Counter = Counter(),
        valid: Bool = true
    ) -> MicrophoneRecorder {
        let recorder = MicrophoneRecorder(notificationCenter: center)
        recorder.beginForTesting(probe: .init(
            deviceConnected: { counter.probes += 1; return valid },
            restart: { counter.restarts += 1 }
        ))
        return recorder
    }

    private func collect(_ recorder: MicrophoneRecorder) async -> [[Float]] {
        var chunks: [[Float]] = []
        for await chunk in recorder.samples {
            chunks.append(chunk)
        }
        return chunks
    }

    @Test func changeRightAfterStartRestartsAndKeepsRecording() async {
        let counter = Counter()
        let recorder = recordingRecorder(counter: counter)
        let outcome = recorder.handleConfigurationChange(
            inputFormatValid: true, now: 0,
            reinstall: { counter.reinstalls += 1 },
            restart: { counter.restarts += 1 }
        )
        #expect(outcome == .recovered)
        #expect(recorder.state == .recording)
        #expect(recorder.failure == nil)
        #expect(recorder.configurationRecoveries == 1)
        #expect(counter.reinstalls == 1)
        #expect(counter.restarts == 1)

        // The stream is still open: a later chunk arrives before stop.
        recorder.output.yield([0.25], hostTime: 1)
        recorder.stop()
        #expect(await collect(recorder) == [[0.25]])
        #expect(recorder.failure == nil)
    }

    @Test func runtimeErrorNotificationIsRecoveredNotFinished() async throws {
        let center = NotificationCenter()
        let counter = Counter()
        let recorder = recordingRecorder(center: center, counter: counter)
        let error = NSError(domain: AVFoundationErrorDomain, code: -11819)
        center.post(
            name: AVCaptureSession.runtimeErrorNotification,
            object: recorder.configurationNotificationObject,
            userInfo: [AVCaptureSessionErrorKey: error]
        )
        for _ in 0..<100 where counter.probes == 0 {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(counter.probes == 1)
        #expect(counter.restarts == 1)
        #expect(recorder.state == .recording)
        #expect(recorder.configurationRecoveries == 1)
        #expect(recorder.diagnostics.runtimeErrors == 1)
        #expect(recorder.diagnostics.lastRuntimeError?.contains("-11819") == true)

        // A notification for another session is not ours.
        center.post(name: AVCaptureSession.runtimeErrorNotification, object: NSObject())
        try await Task.sleep(for: .milliseconds(50))
        #expect(counter.probes == 1)

        recorder.output.yield([0.5, -0.5], hostTime: 2)
        recorder.stop()
        #expect(await collect(recorder) == [[0.5, -0.5]])
    }

    @Test func runtimeErrorWithDeviceGoneFinishes() async throws {
        let center = NotificationCenter()
        let recorder = recordingRecorder(center: center, valid: false)
        center.post(name: AVCaptureSession.runtimeErrorNotification, object: recorder.configurationNotificationObject)
        for _ in 0..<100 where recorder.state != .stopped {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(recorder.failure == .configurationChanged)
        #expect(await collect(recorder).isEmpty)
    }

    @Test func disconnectOfChosenDeviceFinishesOthersAreIgnored() async throws {
        let center = NotificationCenter()
        let recorder = recordingRecorder(center: center)
        center.post(name: AVCaptureDevice.wasDisconnectedNotification, object: NSObject())
        try await Task.sleep(for: .milliseconds(50))
        #expect(recorder.state == .recording)

        recorder.output.yield([0.125], hostTime: 1)
        center.post(name: AVCaptureDevice.wasDisconnectedNotification, object: recorder.deviceNotificationObject)
        for _ in 0..<100 where recorder.state != .stopped {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(recorder.state == .stopped)
        #expect(recorder.failure == .configurationChanged)
        // What arrived before the disconnect is still delivered.
        #expect(await collect(recorder) == [[0.125]])
    }

    @Test func interruptionIsCountedAndRestartsWhenItEnds() async throws {
        let center = NotificationCenter()
        let counter = Counter()
        let recorder = recordingRecorder(center: center, counter: counter)
        let session = recorder.configurationNotificationObject
        center.post(name: AVCaptureSession.wasInterruptedNotification, object: session)
        center.post(name: AVCaptureSession.interruptionEndedNotification, object: session)
        for _ in 0..<100 where counter.restarts == 0 {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(recorder.diagnostics.interruptions == 1)
        #expect(counter.restarts == 1)
        #expect(recorder.state == .recording)
        #expect(recorder.failure == nil)
        recorder.stop()
    }

    @Test func pausedRecorderReinstallsWithoutStarting() {
        let counter = Counter()
        let recorder = recordingRecorder(counter: counter)
        recorder.pause()
        let outcome = recorder.handleConfigurationChange(
            inputFormatValid: true, now: 0,
            reinstall: { counter.reinstalls += 1 },
            restart: { counter.restarts += 1 }
        )
        #expect(outcome == .recovered)
        #expect(recorder.state == .paused)
        #expect(counter.reinstalls == 1)
        #expect(counter.restarts == 0)
        #expect(throws: Never.self) { try recorder.resume() }
        #expect(recorder.state == .recording)
    }

    @Test func deviceGoneFinishesWithConfigurationChanged() async {
        let recorder = recordingRecorder()
        let outcome = recorder.handleConfigurationChange(inputFormatValid: false, now: 0, restart: {})
        #expect(outcome == .finished)
        #expect(recorder.state == .stopped)
        #expect(recorder.failure == .configurationChanged)
        #expect(await collect(recorder).isEmpty)
    }

    @Test func failedRestartFinishesWithConfigurationChanged() {
        struct Boom: Error {}
        let recorder = recordingRecorder()
        let outcome = recorder.handleConfigurationChange(inputFormatValid: true, now: 0, restart: { throw Boom() })
        #expect(outcome == .finished)
        #expect(recorder.failure == .configurationChanged)
        #expect(recorder.configurationRecoveries == 0)
    }

    @Test func flappingDeviceFinishesOnSixthChangeWithinTenSeconds() {
        let counter = Counter()
        let recorder = recordingRecorder(counter: counter)
        for index in 0..<5 {
            #expect(recorder.handleConfigurationChange(
                inputFormatValid: true, now: Double(index), restart: { counter.restarts += 1 }
            ) == .recovered)
        }
        #expect(recorder.handleConfigurationChange(inputFormatValid: true, now: 6, restart: {}) == .finished)
        #expect(recorder.failure == .configurationChanged)
        #expect(recorder.configurationRecoveries == 5)
        #expect(counter.restarts == 5)
    }

    @Test func changesWhenNotRecordingAreIgnored() {
        let idle = MicrophoneRecorder(notificationCenter: NotificationCenter())
        #expect(idle.handleConfigurationChange(inputFormatValid: false, restart: {}) == .ignored)
        #expect(idle.state == .idle)

        let stopped = recordingRecorder()
        stopped.stop()
        #expect(stopped.handleConfigurationChange(inputFormatValid: false, restart: {}) == .ignored)
        #expect(stopped.failure == nil)
    }
}

struct NoAudioWatchdogTests {
    @Test func firesAfterThreeSecondsOfRecordingWithoutSamples() {
        var watchdog = NoAudioWatchdog()
        do { let fired = watchdog.check(now: 100, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 101.5, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 102.9, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 103, isRecording: true, samplesDelivered: 0); #expect(fired) }
    }

    @Test func neverFiresOnceAudioArrived() {
        var watchdog = NoAudioWatchdog()
        do { let fired = watchdog.check(now: 0, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 1, isRecording: true, samplesDelivered: 1600); #expect(!fired) }
        // A later silent device is the silence warning's job, not this one.
        do { let fired = watchdog.check(now: 10, isRecording: true, samplesDelivered: 1600); #expect(!fired) }
    }

    @Test func pausedTimeDoesNotCount() {
        var watchdog = NoAudioWatchdog()
        do { let fired = watchdog.check(now: 0, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 2, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 3, isRecording: false, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 60, isRecording: false, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 60.5, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 61, isRecording: true, samplesDelivered: 0); #expect(!fired) }
        do { let fired = watchdog.check(now: 61.5, isRecording: true, samplesDelivered: 0); #expect(fired) }
    }

    @Test func noAudioMessageNamesTheDevice() {
        let message = MicrophoneRecorderError.noAudio(deviceName: "AirPods Pro").description
        #expect(message.hasPrefix("No audio from AirPods Pro."))
        #expect(message.contains("3 seconds"))
    }
}

struct MicrophoneDiagnosticsTests {
    @Test func describesFormatsAndCounters() {
        var asbd = AudioStreamBasicDescription()
        asbd.mFormatID = kAudioFormatLinearPCM
        asbd.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsNonInterleaved
        asbd.mSampleRate = 16_000
        asbd.mChannelsPerFrame = 1
        asbd.mBitsPerChannel = 32
        #expect(MicrophoneDiagnostics.describe(asbd) == "1 ch, 16000 Hz, Float32 non-interleaved")

        var diagnostics = MicrophoneDiagnostics()
        diagnostics.deviceName = "AirPods Pro"
        diagnostics.deviceNominalSampleRate = 24_000
        diagnostics.conversionFailures = 2
        diagnostics.lastConversionError = "boom"
        let text = diagnostics.description
        #expect(text.contains("device nominal rate: 24000 Hz"))
        #expect(text.contains("conversion failures: 2 (last: boom)"))
    }

    @Test func sampleBufferConverterPassesTargetFormatThrough() throws {
        let format = try MonoResampler.outputFormat()
        let converter = try PCMSampleBufferConverter(format: format)
        let samples: [Float] = [0, 0.25, -0.5, 1]
        let buffer = try makeSampleBuffer(samples, format: format)
        #expect(try converter.convert(buffer) == samples)
    }

    @Test func sampleBufferConverterResamples48kStereo() throws {
        let format = try #require(AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2, interleaved: false
        ))
        let converter = try PCMSampleBufferConverter(format: format)
        var total = 0
        for _ in 0..<10 {
            let buffer = try makeSampleBuffer([Float](repeating: 0.5, count: 4800), format: format)
            total += try converter.convert(buffer).count
        }
        total += try converter.flush().count
        #expect(abs(total - 16_000) <= 32)
    }

    /// A CMSampleBuffer holding `samples` in every channel of `format`.
    private func makeSampleBuffer(_ samples: [Float], format: AVAudioFormat) throws -> CMSampleBuffer {
        let frames = AVAudioFrameCount(samples.count)
        let pcm = try #require(AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames))
        pcm.frameLength = frames
        let channels = try #require(pcm.floatChannelData)
        for channel in 0..<Int(format.channelCount) {
            for (index, value) in samples.enumerated() {
                channels[channel][index] = value
            }
        }
        var timing = CMSampleTimingInfo(
            duration: CMTime(value: 1, timescale: CMTimeScale(format.sampleRate)),
            presentationTimeStamp: .zero,
            decodeTimeStamp: .invalid
        )
        var buffer: CMSampleBuffer?
        var status = CMSampleBufferCreate(
            allocator: kCFAllocatorDefault, dataBuffer: nil, dataReady: false,
            makeDataReadyCallback: nil, refcon: nil, formatDescription: format.formatDescription,
            sampleCount: CMItemCount(frames), sampleTimingEntryCount: 1, sampleTimingArray: &timing,
            sampleSizeEntryCount: 0, sampleSizeArray: nil, sampleBufferOut: &buffer
        )
        #expect(status == noErr)
        let sampleBuffer = try #require(buffer)
        status = CMSampleBufferSetDataBufferFromAudioBufferList(
            sampleBuffer, blockBufferAllocator: kCFAllocatorDefault, blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: 0, bufferList: pcm.audioBufferList
        )
        #expect(status == noErr)
        return sampleBuffer
    }
}
