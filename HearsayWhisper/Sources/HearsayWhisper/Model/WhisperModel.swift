// Vendored from https://github.com/Blaizzy/mlx-audio-swift
// commit 01dec7c9bdce3088a6b6b7ab9f2e403458195efb, Sources/MLXAudioSTT/Models/Whisper/WhisperModel.swift
// MIT License, Copyright (c) 2025 Prince Canuma. See LICENSES/mlx-audio-swift.txt.
// Local changes are marked "Hearsay:".

import Foundation
import MLX
import MLXNN

// Hearsay: the STTGenerationModel conformance, generate/generateStream, the fixed
// 30 s chunking, the untimestamped decode loop, fromPretrained and every Hugging
// Face Hub download were removed. Decoding lives in Decoder/ (ported from
// mlx_whisper 0.4.3). The tokenizer directory is passed in explicitly.
public final class WhisperModel: Module {
    public let config: WhisperConfig
    public let generationConfig: WhisperGenerationConfig?

    @ModuleInfo(key: "model") var model: WhisperSubmodels

    // Hearsay: set by fromDirectory; readable inside the module for the decoder.
    private(set) var tokenizer: WhisperTokenizer?

    public init(config: WhisperConfig, generationConfig: WhisperGenerationConfig? = nil) {
        self.config = config
        self.generationConfig = generationConfig
        self._model.wrappedValue = WhisperSubmodels(config: config)
    }

    // MARK: - Loading

    /// Source layout for a Whisper safetensors checkpoint.
    enum WeightFormat {
        /// HuggingFace `transformers` layout (`openai/whisper-*`).
        case huggingFace
        /// OpenAI / mlx-whisper layout (`mlx-community/whisper-*`).
        case mlxWhisper
    }

    static func detectFormat(_ weights: [String: MLXArray]) -> WeightFormat {
        for key in weights.keys where key.contains(".blocks.") {
            return .mlxWhisper
        }
        return .huggingFace
    }

    static func sanitize(weights: [String: MLXArray], config: WhisperConfig) -> [String: MLXArray] {
        switch detectFormat(weights) {
        case .huggingFace: return sanitizeHuggingFace(weights)
        case .mlxWhisper: return sanitizeMlxWhisper(weights)
        }
    }

    private static func sanitizeHuggingFace(_ weights: [String: MLXArray]) -> [String: MLXArray] {
        var sanitized: [String: MLXArray] = [:]
        sanitized.reserveCapacity(weights.count)

        for (rawKey, value) in weights {
            // proj_out is tied to embed_tokens; projectToVocab uses the embedding directly.
            if rawKey == "proj_out.weight" || rawKey == "model.proj_out.weight" {
                continue
            }

            var key = rawKey
            // Re-exports that drop the top-level `model.` still need it for module lookup.
            if !key.hasPrefix("model.") {
                if key.hasPrefix("encoder.") || key.hasPrefix("decoder.") {
                    key = "model." + key
                }
            }

            var newValue = value
            if (key == "model.encoder.conv1.weight" || key == "model.encoder.conv2.weight"), newValue.ndim == 3 {
                // PyTorch Conv1d: [out, in, kernel] -> MLX Conv1d: [out, kernel, in]
                newValue = newValue.transposed(0, 2, 1)
            }
            sanitized[key] = newValue
        }

        return sanitized
    }

    private static func sanitizeMlxWhisper(_ weights: [String: MLXArray]) -> [String: MLXArray] {
        var sanitized: [String: MLXArray] = [:]
        sanitized.reserveCapacity(weights.count)

        for (rawKey, value) in weights {
            if rawKey == "alignment_heads" { continue }
            guard let mapped = remapMlxWhisperKey(rawKey) else { continue }
            sanitized[mapped] = value
        }

        // mlx-whisper omits the encoder positional embedding because it's a
        // fixed sinusoid; synthesise it so `update(parameters:verify:.all)` passes.
        // Hearsay: computed like mlx_whisper's `sinusoids()` (float32 MLX ops)
        // and cast to the weights' dtype, as `AudioEncoder` does. Upstream kept
        // it float32, which silently promoted the whole encoder to float32 and
        // changed near-tie decisions against the Python tool.
        let encPosKey = "model.encoder.embed_positions.weight"
        if sanitized[encPosKey] == nil, let conv2 = sanitized["model.encoder.conv2.weight"] {
            sanitized[encPosKey] = whisperSinusoids(length: 1500, channels: conv2.shape[0])
                .asType(conv2.dtype)
        }

        return sanitized
    }

    /// Hearsay: port of `sinusoids(length, channels)` in mlx_whisper `whisper.py`.
    private static func whisperSinusoids(length: Int, channels: Int) -> MLXArray {
        precondition(channels % 2 == 0, "Whisper sinusoid channels must be even")
        let half = channels / 2
        let logTimescaleIncrement = log(10000.0) / Double(max(half - 1, 1))
        let invTimescales = MLX.exp(
            MLXArray(Float(-logTimescaleIncrement)) * MLXArray(Int32(0)..<Int32(half)).asType(.float32)
        )
        let scaledTime = MLXArray(Int32(0)..<Int32(length)).asType(.float32).expandedDimensions(axis: 1)
            * invTimescales.expandedDimensions(axis: 0)
        return MLX.concatenated([MLX.sin(scaledTime), MLX.cos(scaledTime)], axis: 1)
    }

    private static func remapMlxWhisperKey(_ rawKey: String) -> String? {
        if rawKey == "encoder.positional_embedding" {
            return "model.encoder.embed_positions.weight"
        }
        if rawKey == "decoder.positional_embedding" {
            return "model.decoder.embed_positions.weight"
        }
        if rawKey.hasPrefix("decoder.token_embedding.") {
            return "model.decoder.embed_tokens."
                + String(rawKey.dropFirst("decoder.token_embedding.".count))
        }
        if rawKey == "encoder.conv1.weight" || rawKey == "encoder.conv1.bias"
            || rawKey == "encoder.conv2.weight" || rawKey == "encoder.conv2.bias"
        {
            return "model." + rawKey
        }
        if rawKey.hasPrefix("encoder.ln_post.") {
            return "model.encoder.layer_norm." + String(rawKey.dropFirst("encoder.ln_post.".count))
        }
        if rawKey.hasPrefix("decoder.ln.") {
            return "model.decoder.layer_norm." + String(rawKey.dropFirst("decoder.ln.".count))
        }

        for stem in ["encoder", "decoder"] {
            let blocksPrefix = "\(stem).blocks."
            guard rawKey.hasPrefix(blocksPrefix) else { continue }
            let rest = rawKey.dropFirst(blocksPrefix.count)
            guard let dot = rest.firstIndex(of: ".") else { return nil }
            let layerIndex = String(rest[..<dot])
            let suffix = String(rest[rest.index(after: dot)...])
            guard let mapped = remapBlockSuffix(suffix, isDecoder: stem == "decoder") else { return nil }
            return "model.\(stem).layers.\(layerIndex).\(mapped)"
        }

        return nil
    }

    private static func remapBlockSuffix(_ suffix: String, isDecoder: Bool) -> String? {
        let attnNameMap: [String: String] = [
            "query": "q_proj", "key": "k_proj", "value": "v_proj", "out": "out_proj",
        ]

        if let rest = stripPrefix(suffix, "attn_ln.") {
            return "self_attn_layer_norm.\(rest)"
        }
        if isDecoder, let rest = stripPrefix(suffix, "cross_attn_ln.") {
            return "encoder_attn_layer_norm.\(rest)"
        }
        if let rest = stripPrefix(suffix, "mlp_ln.") {
            return "final_layer_norm.\(rest)"
        }
        if let rest = stripPrefix(suffix, "mlp1.") {
            return "fc1.\(rest)"
        }
        if let rest = stripPrefix(suffix, "mlp2.") {
            return "fc2.\(rest)"
        }
        if let rest = stripPrefix(suffix, "attn.") {
            return remapAttnSuffix(rest, container: "self_attn", attnNameMap: attnNameMap)
        }
        if isDecoder, let rest = stripPrefix(suffix, "cross_attn.") {
            return remapAttnSuffix(rest, container: "encoder_attn", attnNameMap: attnNameMap)
        }
        return nil
    }

    private static func remapAttnSuffix(
        _ suffix: String,
        container: String,
        attnNameMap: [String: String]
    ) -> String? {
        guard let dot = suffix.firstIndex(of: ".") else { return nil }
        let projName = String(suffix[..<dot])
        let tail = String(suffix[suffix.index(after: dot)...])
        guard let mappedProj = attnNameMap[projName] else { return nil }
        return "\(container).\(mappedProj).\(tail)"
    }

    private static func stripPrefix(_ string: String, _ prefix: String) -> String? {
        guard string.hasPrefix(prefix) else { return nil }
        return String(string.dropFirst(prefix.count))
    }

    /// Load a model from `modelDirectory` (`config.json` plus `.safetensors`)
    /// and the tokenizer from `tokenizerDirectory` (`tokenizer.json`,
    /// `tokenizer_config.json`, `generation_config.json`, ...). The two may be
    /// the same folder.
    /// Hearsay: replaces the upstream `fromDirectory(_:cache:)`, which fell back
    /// to downloading the tokenizer from the Hub.
    public static func fromDirectory(
        modelDirectory: URL,
        tokenizerDirectory: URL
    ) async throws -> WhisperModel {
        let configURL = modelDirectory.appendingPathComponent("config.json")
        let configData = try Data(contentsOf: configURL)
        let config = try JSONDecoder().decode(WhisperConfig.self, from: configData)

        // Hearsay: generation_config.json is read from the model folder first,
        // then from the shared tokenizer folder.
        var generationConfig: WhisperGenerationConfig? = nil
        for directory in [modelDirectory, tokenizerDirectory] {
            let url = directory.appendingPathComponent("generation_config.json")
            if FileManager.default.fileExists(atPath: url.path),
               let data = try? Data(contentsOf: url),
               let decoded = try? JSONDecoder().decode(WhisperGenerationConfig.self, from: data)
            {
                generationConfig = decoded
                break
            }
        }

        let model = WhisperModel(config: config, generationConfig: generationConfig)
        if let quantization = try? JSONDecoder().decode(
            WhisperQuantizedModelConfig.self,
            from: configData
        ).quantization {
            quantize(
                model: model,
                groupSize: quantization.groupSize,
                bits: quantization.bits,
                filter: { path, module in
                    module is Linear || path.hasSuffix("decoder.embed_tokens")
                }
            )
        }

        let files = try FileManager.default.contentsOfDirectory(
            at: modelDirectory,
            includingPropertiesForKeys: nil
        )
        let safetensors = files
            .filter { $0.pathExtension == "safetensors" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        guard !safetensors.isEmpty else {
            throw NSError(
                domain: "WhisperModel",
                code: 2,
                userInfo: [NSLocalizedDescriptionKey: "No .safetensors files found in \(modelDirectory.path)."]
            )
        }

        var weights: [String: MLXArray] = [:]
        for url in safetensors {
            let shard = try MLX.loadArrays(url: url)
            weights.merge(shard) { _, new in new }
        }
        let sanitized = sanitize(weights: weights, config: config)
        try model.update(parameters: ModuleParameters.unflattened(sanitized), verify: .all)

        let localTokenizer = tokenizerDirectory.appendingPathComponent("tokenizer.json")
        guard FileManager.default.fileExists(atPath: localTokenizer.path) else {
            throw NSError(
                domain: "WhisperModel",
                code: 4,
                userInfo: [NSLocalizedDescriptionKey: "No tokenizer.json found in \(tokenizerDirectory.path)."]
            )
        }

        model.tokenizer = try await WhisperTokenizer(
            modelDirectory: tokenizerDirectory,
            baseConfig: config,
            generationConfig: generationConfig,
            tokenizerDirectory: tokenizerDirectory
        )

        eval(model)
        return model
    }
}

private struct WhisperQuantizedModelConfig: Decodable {
    let quantization: WhisperQuantizationConfig?
}

private struct WhisperQuantizationConfig: Decodable {
    let groupSize: Int
    let bits: Int

    private enum CodingKeys: String, CodingKey {
        case groupSize = "group_size"
        case bits
    }
}
