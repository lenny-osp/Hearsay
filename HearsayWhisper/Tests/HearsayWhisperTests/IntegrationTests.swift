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

    func compare(fixture: String, options: TranscriptionOptions, lowercase: Bool) async throws {
        let transcriber = try await Self.loadTranscriber()
        let samples = try Self.readWav(Self.fixtures.appendingPathComponent("\(fixture).wav"))
        let result = try transcriber.transcribe(samples: samples, options: options)
        let actual = Self.parseSRT(renderSRT(result.segments))
        let expectedText = try String(
            contentsOf: Self.fixtures.appendingPathComponent("\(fixture).expected.srt"), encoding: .utf8
        )
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
