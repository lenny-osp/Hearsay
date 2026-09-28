// hearsay-transcribe <model-dir> <tokenizer-dir> <audio.wav>
//     [--language xx] [--initial-prompt "..."] [--srt out.srt]
//     [--temperatures 0,0.2,...]
//
// `--temperatures 0` reproduces the `mlx_whisper` CLI, which decodes at the
// single temperature 0 (no fallback). The default is the `transcribe()` API
// schedule 0, 0.2, ..., 1.0.
//
// Transcribes an audio file with the HearsayWhisper decoder and prints one
// line per segment, `[MM:SS.mmm --> MM:SS.mmm] text`, like `mlx_whisper
// --verbose True`. Timings go to stderr.
import AVFoundation
import Foundation
import HearsayWhisper

struct Arguments {
    var modelDirectory: URL
    var tokenizerDirectory: URL
    var audio: URL
    var language: String?
    var initialPrompt: String?
    var srt: URL?
    var temperatures: [Float]?
}

enum CLIError: Error, CustomStringConvertible {
    case usage(String)
    case audio(String)

    var description: String {
        switch self {
        case .usage(let message): return message
        case .audio(let message): return "Could not read audio: \(message)"
        }
    }
}

let usage = """
    usage: hearsay-transcribe <model-dir> <tokenizer-dir> <audio.wav> \
    [--language xx] [--initial-prompt "..."] [--srt out.srt] [--temperatures 0,0.2,...]
    """

func parseArguments(_ raw: [String]) throws -> Arguments {
    var positional: [String] = []
    var language: String?
    var initialPrompt: String?
    var srt: URL?
    var temperatures: [Float]?
    var index = 0
    func value(after flag: String) throws -> String {
        index += 1
        guard index < raw.count else { throw CLIError.usage("\(flag) needs a value\n\(usage)") }
        return raw[index]
    }
    while index < raw.count {
        let argument = raw[index]
        switch argument {
        case "--language": language = try value(after: argument)
        case "--initial-prompt": initialPrompt = try value(after: argument)
        case "--srt": srt = URL(fileURLWithPath: try value(after: argument))
        case "--temperatures":
            let list = try value(after: argument).split(separator: ",").compactMap { Float($0) }
            guard !list.isEmpty else { throw CLIError.usage("--temperatures needs numbers\n\(usage)") }
            temperatures = list
        case "-h", "--help": throw CLIError.usage(usage)
        default:
            if argument.hasPrefix("--") { throw CLIError.usage("unknown option \(argument)\n\(usage)") }
            positional.append(argument)
        }
        index += 1
    }
    guard positional.count == 3 else { throw CLIError.usage(usage) }
    return Arguments(
        modelDirectory: URL(fileURLWithPath: positional[0]),
        tokenizerDirectory: URL(fileURLWithPath: positional[1]),
        audio: URL(fileURLWithPath: positional[2]),
        language: language,
        initialPrompt: initialPrompt,
        srt: srt,
        temperatures: temperatures
    )
}

/// Read any AVFoundation-readable file as 16 kHz mono Float32.
func loadSamples(_ url: URL) throws -> [Float] {
    let file = try AVAudioFile(forReading: url)
    guard let target = AVAudioFormat(
        commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false
    ) else { throw CLIError.audio("cannot create the 16 kHz format") }
    let source = file.processingFormat
    guard let input = AVAudioPCMBuffer(
        pcmFormat: source, frameCapacity: AVAudioFrameCount(file.length)
    ) else { throw CLIError.audio("cannot allocate a buffer") }
    try file.read(into: input)

    if source.sampleRate == 16_000, source.channelCount == 1, let data = input.floatChannelData {
        return Array(UnsafeBufferPointer(start: data[0], count: Int(input.frameLength)))
    }
    guard let converter = AVAudioConverter(from: source, to: target) else {
        throw CLIError.audio("no converter from \(source)")
    }
    let ratio = 16_000 / source.sampleRate
    let capacity = AVAudioFrameCount(Double(input.frameLength) * ratio) + 1_024
    guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else {
        throw CLIError.audio("cannot allocate the output buffer")
    }
    nonisolated(unsafe) var consumed = false
    var error: NSError?
    let status = converter.convert(to: output, error: &error) { _, outStatus in
        if consumed {
            outStatus.pointee = .endOfStream
            return nil
        }
        consumed = true
        outStatus.pointee = .haveData
        return input
    }
    if status == .error { throw CLIError.audio(error?.localizedDescription ?? "conversion failed") }
    guard let data = output.floatChannelData else { throw CLIError.audio("empty conversion") }
    return Array(UnsafeBufferPointer(start: data[0], count: Int(output.frameLength)))
}

func log(_ message: String) {
    FileHandle.standardError.write(Data((message + "\n").utf8))
}

do {
    let arguments = try parseArguments(Array(CommandLine.arguments.dropFirst()))

    let loadStart = Date()
    let transcriber = try await Transcriber.load(
        modelDirectory: arguments.modelDirectory,
        tokenizerDirectory: arguments.tokenizerDirectory
    )
    log(String(format: "model load: %.2f s", Date().timeIntervalSince(loadStart)))

    let samples = try loadSamples(arguments.audio)
    let audioSeconds = Double(samples.count) / 16_000
    log(String(format: "audio: %.2f s", audioSeconds))

    var options = TranscriptionOptions(
        language: arguments.language,
        initialPrompt: arguments.initialPrompt
    )
    if let temperatures = arguments.temperatures {
        options.temperatures = temperatures
    }
    let start = Date()
    let result = try transcriber.transcribe(samples: samples, options: options)
    let elapsed = Date().timeIntervalSince(start)
    log(String(format: "transcribe: %.2f s, RTF %.3f", elapsed, elapsed / max(audioSeconds, 0.001)))
    log("language: \(result.language)")

    for segment in result.segments {
        print(consoleLine(segment))
    }
    if let srt = arguments.srt {
        try renderSRT(result.segments).write(to: srt, atomically: true, encoding: .utf8)
        log("wrote \(srt.path)")
    }
} catch {
    log("\(error)")
    exit(1)
}
