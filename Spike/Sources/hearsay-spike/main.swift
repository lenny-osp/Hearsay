// Phase 0 spike. Usage: hearsay-spike <hf-repo | local-model-dir> <wav> [language]
import Foundation
import MLX
import MLXAudioCore
import MLXAudioSTT

let args = CommandLine.arguments
guard args.count >= 3 else {
    print("usage: hearsay-spike <hf-repo | local-model-dir> <wav> [language]")
    exit(2)
}
let modelArg = args[1]
let wavPath = args[2]
let language: String? = args.count > 3 ? args[3] : nil

func seconds(since start: Date) -> String { String(format: "%.2f s", Date().timeIntervalSince(start)) }

let loadStart = Date()
let model: WhisperModel
if FileManager.default.fileExists(atPath: modelArg) {
    model = try await WhisperModel.fromDirectory(URL(fileURLWithPath: modelArg))
} else {
    model = try await WhisperModel.fromPretrained(modelArg)
}
print("model load: \(seconds(since: loadStart))")

let (sampleRate, audio) = try loadAudioArray(from: URL(fileURLWithPath: wavPath), sampleRate: 16000)
let audioSeconds = Double(audio.dim(0)) / 16000.0
print("audio: \(String(format: "%.1f", audioSeconds)) s at \(sampleRate) Hz")

let genStart = Date()
let output = model.generate(
    audio: audio,
    generationParameters: STTGenerateParameters(language: language)
)
let elapsed = Date().timeIntervalSince(genStart)
print("transcribe: \(String(format: "%.2f", elapsed)) s, RTF \(String(format: "%.3f", elapsed / audioSeconds))")
print("language: \(output.language ?? "-")")
print("text: \(output.text)")
for segment in output.segments ?? [] {
    print("segment: \(segment)")
}
