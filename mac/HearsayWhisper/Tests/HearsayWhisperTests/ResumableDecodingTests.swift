import Foundation
import Testing
@testable import HearsayWhisper

/// `Transcriber.transcribeStep` (PLAN.md 4.9, "Resumable decoding"):
/// suspending after every window and resuming gives the same result as one
/// uninterrupted `transcribe`. Model-gated like `IntegrationTests`.
struct ResumableDecodingTests {
    static let enabled = IntegrationTests.modelDirectory != nil

    /// en + de + es + zh fixtures back to back (several 30 s windows).
    static func multiWindowSamples() throws -> [Float] {
        var samples: [Float] = []
        for name in ["en-30s", "de-30s", "es-30s", "zh-30s"] {
            samples += try IntegrationTests.readWav(IntegrationTests.fixtures.appendingPathComponent("\(name).wav"))
        }
        return samples
    }

    static let options = TranscriptionOptions(language: "en")

    @Test(.enabled(if: enabled))
    func yieldingAfterEveryWindowMatchesUninterrupted() async throws {
        let transcriber = try await IntegrationTests.loadTranscriber()
        let samples = try Self.multiWindowSamples()
        let whole = try transcriber.transcribe(samples: samples, options: Self.options)

        let progress = ProgressLog()
        var checkpoint: TranscriptionCheckpoint? = nil
        var suspensions = 0
        var stepped: Transcription? = nil
        while stepped == nil {
            switch try transcriber.transcribeStep(
                samples: samples, options: Self.options, resumingFrom: checkpoint,
                progress: { progress.append($0) }, shouldYield: { true }
            ) {
            case .finished(let transcription):
                stepped = transcription
            case .suspended(let next):
                if let checkpoint { #expect(next.seek > checkpoint.seek) }  // every call advances
                checkpoint = next
                suspensions += 1
            }
        }
        let result = try #require(stepped)
        print("resumable: \(suspensions) suspensions, \(result.segments.count) segments")
        #expect(suspensions >= 2)
        #expect(result.segments.contains { $0.seek > 0 })
        #expect(result.segments == whole.segments)
        #expect(result.text == whole.text)
        #expect(result.language == whole.language)
        #expect(result == whole)
        let values = progress.values
        #expect(zip(values, values.dropFirst()).allSatisfy { $0 <= $1 }, "progress went back: \(values)")
        #expect(values.last == 1)
    }

    @Test(.enabled(if: enabled))
    func neverYieldingFinishesLikeTranscribe() async throws {
        let transcriber = try await IntegrationTests.loadTranscriber()
        let samples = try Self.multiWindowSamples()
        let whole = try transcriber.transcribe(samples: samples, options: Self.options)
        let noClosure = try transcriber.transcribeStep(samples: samples, options: Self.options)
        #expect(noClosure == .finished(whole))
        let asked = CallCounter()
        let neverTrue = try transcriber.transcribeStep(
            samples: samples, options: Self.options, shouldYield: { _ = asked.next(); return false }
        )
        #expect(neverTrue == .finished(whole))
        #expect(asked.count >= 2)  // asked before every window but the first
    }

    @Test(.enabled(if: enabled))
    func cancelDuringResumedCallKeepsAllSegments() async throws {
        let transcriber = try await IntegrationTests.loadTranscriber()
        let samples = try Self.multiWindowSamples()
        let first = try transcriber.transcribeStep(
            samples: samples, options: Self.options, shouldYield: { true }
        )
        guard case .suspended(let checkpoint) = first else {
            Issue.record("expected a suspension after the first window")
            return
        }
        #expect(!checkpoint.segments.isEmpty)
        let calls = CallCounter()
        do {
            // Cancel is checked before yield: true on the second check, with
            // yield also true there, must cancel, not suspend.
            _ = try transcriber.transcribeStep(
                samples: samples, options: Self.options, resumingFrom: checkpoint,
                shouldCancel: { calls.next() >= 2 }, shouldYield: { true }
            )
            Issue.record("expected cancellation")
        } catch let TranscriptionError.cancelled(partial) {
            #expect(calls.count == 2)
            #expect(partial.count > checkpoint.segments.count)
            #expect(Array(partial.prefix(checkpoint.segments.count)) == checkpoint.segments)
            #expect(partial.dropFirst(checkpoint.segments.count).allSatisfy { $0.seek == checkpoint.seek })
        }
    }

    @Test(.enabled(if: enabled))
    func checkpointRejectsOtherInput() async throws {
        let transcriber = try await IntegrationTests.loadTranscriber()
        let samples = try Self.multiWindowSamples()
        let first = try transcriber.transcribeStep(
            samples: samples, options: Self.options, shouldYield: { true }
        )
        guard case .suspended(let checkpoint) = first else {
            Issue.record("expected a suspension after the first window")
            return
        }
        #expect(throws: TranscriptionCheckpointError.samplesMismatch) {
            _ = try transcriber.transcribeStep(
                samples: Array(samples.dropLast()), options: Self.options, resumingFrom: checkpoint
            )
        }
        var changed = samples
        changed[0] += 0.5  // same count, other content
        #expect(throws: TranscriptionCheckpointError.samplesMismatch) {
            _ = try transcriber.transcribeStep(samples: changed, options: Self.options, resumingFrom: checkpoint)
        }
        #expect(throws: TranscriptionCheckpointError.optionsMismatch) {
            _ = try transcriber.transcribeStep(
                samples: samples, options: TranscriptionOptions(language: "de"), resumingFrom: checkpoint
            )
        }
    }
}

/// How long the log-mel spectrogram of 60 minutes of audio takes (the cost
/// every resumed call pays). Runs only with `TEST_RUNNER_HEARSAY_MEL_TIMING=1`.
struct MelTimingTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["HEARSAY_MEL_TIMING"] == "1"))
    func sixtyMinutesOfNoise() {
        var generator = SystemRandomNumberGenerator()
        let count = 60 * 60 * WhisperAudioConfig.sampleRate
        let samples = (0..<count).map { _ in Float.random(in: -0.5...0.5, using: &generator) }
        let clock = ContinuousClock()
        for run in 1...3 {
            let elapsed = clock.measure {
                let mel = logMelSpectrogram(
                    samples: samples, nMels: 128, padding: WhisperAudioConfig.chunkLengthSamples
                )
                #expect(abs(mel.dim(0) - (count + WhisperAudioConfig.chunkLengthSamples) / WhisperAudioConfig.hopLength) <= 1)
            }
            let fingerprint = clock.measure { _ = sampleFingerprint(samples) }
            print("mel 60 min run \(run): \(elapsed), fingerprint \(fingerprint)")
        }
    }
}

final class ProgressLog: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [Double] = []

    func append(_ value: Double) {
        lock.lock()
        defer { lock.unlock() }
        stored.append(value)
    }

    var values: [Double] {
        lock.lock()
        defer { lock.unlock() }
        return stored
    }
}
