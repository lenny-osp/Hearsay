import AppKit
import HearsayCore

/// Debug only. When the app is launched with `HEARSAY_RECORD_SECONDS=<n>`
/// and `HEARSAY_RECORD_DEVICE=<part of a device name>`, records `n` seconds
/// from the first input device whose name contains that text (ignoring
/// case), writes them to a temporary WAV that is deleted afterwards, prints
/// the recorder diagnostics and the RMS level of what was captured to
/// stdout, and quits with status 0, or 1 when nothing was captured or the
/// device was not found. Nothing permanent is written.
///
/// Launch it through LaunchServices so macOS applies the app's own
/// microphone permission (a binary started from a shell inherits the
/// shell's):
///
///     open -W -n --stdout /tmp/out.txt --stderr /tmp/out.txt \
///         --env HEARSAY_RECORD_SECONDS=5 --env HEARSAY_RECORD_DEVICE=AirPods \
///         .build/derived/Build/Products/Debug/Hearsay.app
///
/// Returns false (and does nothing) when the variables are not set.
@MainActor
enum RecordingDebug {
    static func runIfRequested() -> Bool {
        let environment = ProcessInfo.processInfo.environment
        guard let secondsText = environment["HEARSAY_RECORD_SECONDS"],
              let seconds = Double(secondsText), seconds > 0,
              let query = environment["HEARSAY_RECORD_DEVICE"], !query.isEmpty
        else { return false }
        Task { @MainActor in
            let status = await record(seconds: seconds, deviceQuery: query)
            DebugDefaults.removeSuite()
            exit(status)
        }
        return true
    }

    private static func record(seconds: Double, deviceQuery: String) async -> Int32 {
        let devices = AudioDeviceList.inputDevices()
        guard let device = devices.first(where: { $0.name.localizedCaseInsensitiveContains(deviceQuery) }) else {
            say("no input device matches \"\(deviceQuery)\"; devices: "
                + devices.map(\.name).joined(separator: ", "))
            return 1
        }
        say("permission before request: \(MicrophoneRecorder.permission)")
        guard await MicrophoneRecorder.requestPermission() else {
            say("microphone permission is \(MicrophoneRecorder.permission); cannot record")
            return 1
        }

        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("hearsay-debug-\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: url) }
        let writer: WavWriter
        do {
            writer = try WavWriter(url: url)
        } catch {
            say("cannot create the temporary WAV: \(error)")
            return 1
        }

        let recorder = MicrophoneRecorder()
        do {
            try recorder.start(device: device)
        } catch {
            say("start failed: \(error)")
            try? writer.close()
            return 1
        }
        say("recording \(seconds) s from \(device.name) (\(device.uid))")
        let stream = recorder.timedSamples
        let consumer = Task { @MainActor in
            var captured: [Float] = []
            var firstChunkAfter: TimeInterval?
            let started = Date()
            for await chunk in stream {
                if firstChunkAfter == nil { firstChunkAfter = Date().timeIntervalSince(started) }
                captured.append(contentsOf: chunk.samples)
                try? writer.append(chunk.samples)
            }
            return (captured, firstChunkAfter)
        }
        try? await Task.sleep(for: .seconds(seconds))
        recorder.stop()
        let (captured, firstChunkAfter) = await consumer.value
        try? writer.close()
        // HEARSAY_RECORD_KEEP=<path>: keep a copy of the captured WAV there.
        if let keep = ProcessInfo.processInfo.environment["HEARSAY_RECORD_KEEP"], !keep.isEmpty {
            let target = URL(fileURLWithPath: keep)
            try? FileManager.default.removeItem(at: target)
            do { try FileManager.default.copyItem(at: url, to: target); say("kept a copy at \(keep)") }
            catch { say("could not keep a copy: \(error)") }
        }
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? -1

        say(recorder.diagnostics.description)
        let rms = LevelMeter.rmsDB(floatSamples: captured).map { String(format: "%.1f dBFS", $0) } ?? "no samples"
        let peak = captured.reduce(Float(0)) { max($0, abs($1)) }
        say(String(
            format: "captured %d samples (%.2f s at 16 kHz), RMS %@, peak %.4f, first chunk after %@, WAV %d bytes",
            captured.count, Double(captured.count) / 16_000, rms, peak,
            firstChunkAfter.map { String(format: "%.2f s", $0) } ?? "never", size
        ))
        if let failure = recorder.failure {
            say("recorder failure: \(failure)")
        }
        return captured.isEmpty ? 1 : 0
    }

    private static func say(_ line: String) {
        FileHandle.standardOutput.write(Data(("hearsay record debug: " + line + "\n").utf8))
    }
}
