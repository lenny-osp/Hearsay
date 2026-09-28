import Foundation
import Testing
@testable import HearsayWhisper

/// Runs only when `HEARSAY_MODEL_DIR` points at a model folder that also
/// holds the tokenizer files (for example
/// `Spike/models/mlx-community_whisper-large-v3-turbo`). xcodebuild only
/// forwards variables prefixed with `TEST_RUNNER_` to the test process, so run
/// `TEST_RUNNER_HEARSAY_MODEL_DIR=/abs/path xcodebuild ... test`.
struct IntegrationTests {
    static let modelDirectory: URL? = {
        guard let path = ProcessInfo.processInfo.environment["HEARSAY_MODEL_DIR"], !path.isEmpty else {
            return nil
        }
        return URL(fileURLWithPath: path)
    }()

    static let fixtures = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()  // HearsayWhisperTests
        .deletingLastPathComponent()  // Tests
        .deletingLastPathComponent()  // HearsayWhisper
        .deletingLastPathComponent()  // repo root
        .appendingPathComponent("Fixtures")

    static func loadTranscriber() async throws -> Transcriber {
        let directory = try #require(modelDirectory)
        return try await Transcriber.load(modelDirectory: directory, tokenizerDirectory: directory)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func englishFixtureMatchesPython() async throws {
        try await compare(fixture: "en-30s", options: TranscriptionOptions(language: "en"), lowercase: true)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func chineseFixtureMatchesPython() async throws {
        // Generated without an initial prompt (Hearsay's zh default).
        try await compare(fixture: "zh-30s", options: TranscriptionOptions(language: "zh"), lowercase: false)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func germanFixtureMatchesPython() async throws {
        try await compare(fixture: "de-30s", options: TranscriptionOptions(language: "de"), lowercase: false)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func spanishFixtureMatchesPython() async throws {
        try await compare(fixture: "es-30s", options: TranscriptionOptions(language: "es"), lowercase: false)
    }

    // MARK: - Language detection

    static let detectionCandidates = ["en", "zh", "de", "es"]

    /// Python reference: top language and its probability from
    /// `model.detect_language(pad_or_trim(log_mel_spectrogram(audio,
    /// padding=N_SAMPLES), N_FRAMES).astype(float16))`, the path
    /// `transcribe(language=None)` takes, with mlx_whisper 0.4.3 and
    /// `Spike/models/mlx-community_whisper-large-v3-turbo` (fp16), computed
    /// 2026-09-28. Probabilities are over all 100 languages. The four-language
    /// values were en/zh/de/es:
    /// en-30s 0.999725/1.2e-05/3.8e-05/5.5e-05; zh-30s 0.001023/0.997967/5.4e-05/6.4e-05;
    /// de-30s 0.000341/7e-06/0.999425/3.2e-05; es-30s 0.000232/3e-06/1.7e-05/0.99943.
    static let pythonDetection: [(fixture: String, code: String, probability: Float)] = [
        ("en-30s", "en", 0.999725),
        ("zh-30s", "zh", 0.997967),
        ("de-30s", "de", 0.999425),
        ("es-30s", "es", 0.999430),
    ]

    @Test(.enabled(if: modelDirectory != nil))
    func singleWindowDetectionMatchesPython() async throws {
        let transcriber = try await Self.loadTranscriber()
        for reference in Self.pythonDetection {
            let samples = try Self.readWav(Self.fixtures.appendingPathComponent("\(reference.fixture).wav"))
            let (probabilities, noSpeech) = try transcriber.detectLanguage(samples: samples)
            let all = probabilities.probabilities
            #expect(all.count == 100)
            #expect(abs(all.values.reduce(0, +) - 1) < 1e-3)
            let top = try #require(all.max { $0.value < $1.value })
            print("detect \(reference.fixture): swift \(top.key) \(top.value) no-speech \(noSpeech); python \(reference.code) \(reference.probability)")
            #expect(top.key == reference.code, "\(reference.fixture)")
            #expect(abs(top.value - reference.probability) <= 0.01, "\(reference.fixture): \(top.value)")
            #expect(noSpeech < 0.6)
        }
    }

    @Test(.enabled(if: modelDirectory != nil))
    func candidateDetectionFindsEachFixtureLanguage() async throws {
        let transcriber = try await Self.loadTranscriber()
        for reference in Self.pythonDetection {
            let samples = try Self.readWav(Self.fixtures.appendingPathComponent("\(reference.fixture).wav"))
            let result = try transcriber.detectLanguage(samples: samples, candidates: Self.detectionCandidates)
            print("detect \(reference.fixture) among 4: \(result.code ?? "nil") \(result.confidence) windows \(result.windowsUsed)")
            #expect(result.code == reference.code, "\(reference.fixture)")
            #expect(result.confidence > 0.9, "\(reference.fixture): \(result.confidence)")
            #expect(result.windowsUsed == 1)
            #expect(result.perWindow.count == 1)
        }
    }

    @Test(.enabled(if: modelDirectory != nil))
    func candidateDetectionSkipsSilenceAndCapsWindows() async throws {
        let transcriber = try await Self.loadTranscriber()
        let clip = try Self.readWav(Self.fixtures.appendingPathComponent("de-30s.wav"))
        let silence = [Float](repeating: 0, count: WhisperAudioConfig.chunkLengthSamples)
        // silence, then five 30 s windows each starting with the German clip
        let pad = [Float](repeating: 0, count: WhisperAudioConfig.chunkLengthSamples - clip.count)
        let samples = silence + Array((0..<5).map { _ in clip + pad }.joined())
        let result = try transcriber.detectLanguage(
            samples: samples, candidates: Self.detectionCandidates, maxSpeechWindows: 3
        )
        #expect(result.code == "de")
        #expect(result.windowsUsed == 3)
        #expect(result.confidence > 0.9)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func silentBufferDetectsNothing() async throws {
        let transcriber = try await Self.loadTranscriber()
        let silence = [Float](repeating: 0, count: WhisperAudioConfig.chunkLengthSamples)
        let result = try transcriber.detectLanguage(samples: silence, candidates: Self.detectionCandidates)
        #expect(result.code == nil)
        #expect(result.confidence == 0)
        #expect(result.windowsUsed == 0)
        #expect(result.perWindow.isEmpty)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func detectionRejectsUnknownCandidate() async throws {
        let transcriber = try await Self.loadTranscriber()
        #expect(throws: TranscriptionError.unsupportedLanguage("xx")) {
            _ = try transcriber.detectLanguage(samples: [0], candidates: ["en", "xx"])
        }
    }

    @Test(.enabled(if: modelDirectory != nil))
    func tokenizerMatchesTiktoken() async throws {
        let transcriber = try await Self.loadTranscriber()
        #expect(transcriber.specials == .largeV3)
        // tiktoken: encode(" The following is a sentence in Traditional Chinese.")
        #expect(transcriber.tokenizer.encode(text: " The following is a sentence in Traditional Chinese.")
                == [440, 3480, 307, 257, 8174, 294, 46738, 4649, 13])
        // SuppressBlank: encode(" ")
        #expect(transcriber.tokenizer.encode(text: " ") == [220])
        // non_speech_tokens computed like Python equals generation_config
        // suppress_tokens minus the specials _get_suppress_tokens adds.
        let computed = nonSpeechTokens(encode: { transcriber.tokenizer.encode(text: $0) })
        let configured = try #require(transcriber.model.generationConfig?.suppressTokens)
        #expect(suppressTokenSet(nonSpeech: computed, specials: transcriber.specials)
                == suppressTokenSet(nonSpeech: configured, specials: transcriber.specials))
        #expect(computed.count == 82)
    }

    @Test(.enabled(if: modelDirectory != nil))
    func cancelBeforeSecondWindowKeepsFirstWindowSegments() async throws {
        let transcriber = try await Self.loadTranscriber()
        let clip = try Self.readWav(Self.fixtures.appendingPathComponent("en-30s.wav"))
        let samples = clip + clip + clip  // 57 s: at least two windows
        let calls = CallCounter()
        do {
            _ = try transcriber.transcribe(
                samples: samples,
                options: TranscriptionOptions(language: "en", temperatures: [0]),
                shouldCancel: { calls.next() >= 2 }  // false for window 1, true for window 2
            )
            Issue.record("expected cancellation")
        } catch let TranscriptionError.cancelled(partial) {
            #expect(calls.count == 2)
            #expect(!partial.isEmpty)
            #expect(partial.allSatisfy { $0.seek == 0 })  // all from the first window
            #expect(partial.first?.start == 0)
        }
    }

    @Test(.enabled(if: modelDirectory != nil))
    func noCancelClosureNeverCancels() async throws {
        let transcriber = try await Self.loadTranscriber()
        let clip = try Self.readWav(Self.fixtures.appendingPathComponent("en-30s.wav"))
        let result = try transcriber.transcribe(
            samples: clip + clip, options: TranscriptionOptions(language: "en", temperatures: [0]),
            shouldCancel: { false }
        )
        #expect(result.segments.contains { $0.seek > 0 })
    }

    func compare(fixture: String, options: TranscriptionOptions, lowercase: Bool) async throws {
        let transcriber = try await Self.loadTranscriber()
        let samples = try Self.readWav(Self.fixtures.appendingPathComponent("\(fixture).wav"))
        let result = try transcriber.transcribe(samples: samples, options: options)
        let rendered = renderSRT(result.segments)
        let actual = Self.parseSRT(rendered)
        let expectedText = try String(
            contentsOf: Self.fixtures.appendingPathComponent("\(fixture).expected.srt"), encoding: .utf8
        )
        #expect(rendered == expectedText, "\(fixture): SRT is not byte-identical to the Python reference")
        let expected = Self.parseSRT(expectedText)

        #expect(abs(actual.count - expected.count) <= 1, "cue count \(actual.count) vs \(expected.count)")
        for (a, e) in zip(actual, expected) {
            #expect(abs(a.start - e.start) <= 0.5, "start \(a.start) vs \(e.start)")
            #expect(abs(a.end - e.end) <= 0.5, "end \(a.end) vs \(e.end)")
        }
        let normalize: (String) -> String = { text in
            let base = lowercase ? text.lowercased() : text
            return String(String.UnicodeScalarView(base.unicodeScalars.filter {
                !CharacterSet.punctuationCharacters.contains($0) && !CharacterSet.whitespacesAndNewlines.contains($0)
            }))
        }
        #expect(normalize(actual.map(\.text).joined()) == normalize(expected.map(\.text).joined()))
    }

    struct Cue {
        var start: Double
        var end: Double
        var text: String
    }

    static func parseSRT(_ text: String) -> [Cue] {
        var cues: [Cue] = []
        for block in text.components(separatedBy: "\n\n") {
            let lines = block.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
            guard lines.count >= 2, lines[1].contains(" --> ") else { continue }
            let times = lines[1].components(separatedBy: " --> ")
            guard times.count == 2, let start = seconds(times[0]), let end = seconds(times[1]) else { continue }
            cues.append(Cue(start: start, end: end, text: lines.dropFirst(2).joined(separator: "\n")))
        }
        return cues
    }

    static func seconds(_ stamp: String) -> Double? {
        let parts = stamp.replacingOccurrences(of: ",", with: ".").split(separator: ":")
        guard parts.count == 3, let h = Double(parts[0]), let m = Double(parts[1]), let s = Double(parts[2]) else {
            return nil
        }
        return h * 3600 + m * 60 + s
    }

    /// 16-bit PCM mono WAV at 16 kHz -> Float in [-1, 1) (sample / 32768, as
    /// mlx_whisper's ffmpeg loader).
    static func readWav(_ url: URL) throws -> [Float] {
        let data = try Data(contentsOf: url)
        let bytes = [UInt8](data)
        func u32(_ o: Int) -> Int { Int(bytes[o]) | Int(bytes[o + 1]) << 8 | Int(bytes[o + 2]) << 16 | Int(bytes[o + 3]) << 24 }
        func u16(_ o: Int) -> Int { Int(bytes[o]) | Int(bytes[o + 1]) << 8 }
        var offset = 12
        var samples: [Float] = []
        while offset + 8 <= bytes.count {
            let id = String(decoding: bytes[offset..<(offset + 4)], as: UTF8.self)
            let size = u32(offset + 4)
            if id == "fmt " {
                #expect(u16(offset + 8 + 2) == 1)          // mono
                #expect(u32(offset + 8 + 4) == 16_000)     // 16 kHz
                #expect(u16(offset + 8 + 14) == 16)        // s16
            } else if id == "data" {
                let end = min(offset + 8 + size, bytes.count)
                var i = offset + 8
                while i + 1 < end {
                    samples.append(Float(Int16(bitPattern: UInt16(u16(i)))) / 32768)
                    i += 2
                }
            }
            offset += 8 + size + (size & 1)
        }
        return samples
    }
}

/// Thread-safe call counter for the `@Sendable` cancel closure.
final class CallCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    /// Increment and return the new count.
    func next() -> Int {
        lock.lock()
        defer { lock.unlock() }
        value += 1
        return value
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return value
    }
}
