import Foundation
import MLX
import MLXNN

/// Port of `DecodingResult` in mlx_whisper `decoding.py`.
struct DecodingResult: Sendable, Equatable {
    var tokens: [Int]
    var text: String
    var avgLogprob: Double
    var noSpeechProb: Double
    var temperature: Float
    var compressionRatio: Double
}

/// Port of `DecodingTask._get_initial_tokens` for a `prompt` given as tokens
/// and no `prefix`: `[sot_prev] + prompt[-(n_ctx // 2 - 1):] + sot_sequence`,
/// or just the sot sequence when the prompt is empty.
func initialTokens(sotSequence: [Int], prompt: [Int], sotPrev: Int, nCtx: Int) -> [Int] {
    guard !prompt.isEmpty else { return sotSequence }
    let keep = max(nCtx / 2 - 1, 0)
    return [sotPrev] + Array(prompt.suffix(keep)) + sotSequence
}

/// Port of `MaximumLikelihoodRanker.rank` with `length_penalty=None`:
/// the index of the highest `sum_logprob / length` (first on ties).
func rankByLikelihood(tokens: [[Int]], sumLogprobs: [Double]) -> Int {
    var best = 0
    var bestScore = -Double.infinity
    for (index, sequence) in tokens.enumerated() {
        let score = sequence.isEmpty ? -Double.infinity : sumLogprobs[index] / Double(sequence.count)
        if index == 0 || score > bestScore {
            best = index
            bestScore = score
        }
    }
    return best
}

/// The decode loop of one 30 s window: `DecodingTask.run` with the greedy
/// decoder (argmax at temperature 0, `best_of` categorical samples above it),
/// the `SuppressBlank`, `SuppressTokens` and `ApplyTimestampRules` filters,
/// `sum_logprobs` for `avg_logprob`, and `no_speech_prob` from the softmax at
/// the `<|startoftranscript|>` position of the first forward pass. Beam search
/// and word timestamps are not ported.
final class WindowDecoder {
    let model: WhisperModel
    let specials: WhisperSpecialTokens
    let suppressTokens: [Int]
    let blankTokens: [Int]
    let decodeText: ([Int]) -> String
    /// `n_text_ctx`.
    let nCtx: Int
    /// `sample_len = n_text_ctx // 2`.
    let sampleLen: Int
    /// `round(max_initial_timestamp / (CHUNK_LENGTH / n_audio_ctx))`.
    let maxInitialTimestampIndex: Int
    private var generator = SystemRandomNumberGenerator()

    init(
        model: WhisperModel,
        specials: WhisperSpecialTokens,
        suppressTokens: [Int],
        blankTokens: [Int],
        decodeText: @escaping ([Int]) -> String
    ) {
        self.model = model
        self.specials = specials
        self.suppressTokens = suppressTokens
        self.blankTokens = blankTokens
        self.decodeText = decodeText
        self.nCtx = model.config.maxTargetPositions
        self.sampleLen = model.config.maxTargetPositions / 2
        let precision = Double(WhisperAudioConfig.chunkLengthSeconds) / Double(model.config.maxSourcePositions)
        self.maxInitialTimestampIndex = Int((1.0 / precision).rounded(.toNearestOrEven))
    }

    /// Encoder forward pass for one `[N_FRAMES, n_mels]` window; returns
    /// `[1, n_audio_ctx, n_audio_state]`. The mel is cast to the weights'
    /// dtype, as Python casts it to float16.
    func encode(melWindow: MLXArray) -> MLXArray {
        let dtype = model.model.encoder.conv1.weight.dtype
        let features = melWindow.expandedDimensions(axis: 0).asType(dtype)
        let hidden = model.model.encoder(features)
        eval(hidden)
        return hidden
    }

    /// Port of `detect_language`: one decoder step on `[sot]`, every
    /// non-language token masked, argmax.
    func detectLanguage(audioFeatures: MLXArray) -> String? {
        let codes = specials.languageCodes
        guard !codes.isEmpty else { return nil }
        var caches = (0..<model.config.decoderLayers).map { _ in WhisperLayerCache() }
        let input = MLXArray([Int32(specials.sot)]).reshaped([1, 1])
        let hidden = model.model.decoder(
            tokens: input, startPosition: 0, encoderHidden: audioFeatures, caches: &caches
        )
        let logits = model.model.decoder.projectToVocab(hidden[0, 0]).asType(.float32)
        eval(logits)
        let values = logits.asArray(Float.self)
        var bestCode: String? = nil
        var bestValue = -Float.infinity
        for (index, code) in codes.enumerated() {
            let id = specials.sot + 1 + index
            guard id < values.count else { continue }
            if bestCode == nil || values[id] > bestValue {
                bestCode = code
                bestValue = values[id]
            }
        }
        return bestCode
    }

    /// Port of `detect_language` returning the distribution instead of the
    /// argmax: one decoder step on `[sot]`, every non-language token masked,
    /// softmax over the language tokens (`language_probs`). Also returns the
    /// `<|nospeech|>` probability from the full-vocabulary softmax at that
    /// same `<|startoftranscript|>` position, which is where `DecodingTask`
    /// reads `no_speech_prob` (causal attention: position 0 sees only `sot`,
    /// so the logits there do not depend on the tokens after it).
    ///
    /// `detectLanguage(audioFeatures:)` above stays the one `transcribe()`
    /// uses, so its output is unchanged.
    func languageProbabilities(audioFeatures: MLXArray) -> (probabilities: [String: Float], noSpeechProb: Float) {
        var caches = (0..<model.config.decoderLayers).map { _ in WhisperLayerCache() }
        let input = MLXArray([Int32(specials.sot)]).reshaped([1, 1])
        let hidden = model.model.decoder(
            tokens: input, startPosition: 0, encoderHidden: audioFeatures, caches: &caches
        )
        let logits = model.model.decoder.projectToVocab(hidden[0, 0]).asType(.float32)
        eval(logits)
        let values = logits.asArray(Float.self)

        var languageLogits: [(code: String, logit: Float)] = []
        for (index, code) in specials.languageCodes.enumerated() {
            let id = specials.sot + 1 + index
            guard id < values.count else { continue }
            languageLogits.append((code, values[id]))
        }
        let languageLSE = logSumExp(languageLogits.map(\.logit)[...])
        var probabilities: [String: Float] = [:]
        for (code, logit) in languageLogits {
            probabilities[code] = Float(Foundation.exp(Double(logit) - languageLSE))
        }

        var noSpeechProb = Float.nan
        if let noSpeech = specials.noSpeech, noSpeech < values.count {
            noSpeechProb = Float(Foundation.exp(Double(values[noSpeech]) - logSumExp(values[...])))
        }
        return (probabilities, noSpeechProb)
    }

    /// Decode one window at one temperature (`DecodingTask(model, options).run`).
    func decode(
        audioFeatures: MLXArray,
        language: String,
        prompt: [Int],
        temperature: Float,
        bestOf: Int?
    ) -> DecodingResult {
        let groupSize = temperature > 0 ? max(bestOf ?? 1, 1) : 1
        let initial = initialTokens(
            sotSequence: specials.sotSequence(language: language),
            prompt: prompt,
            sotPrev: specials.sotPrev,
            nCtx: nCtx
        )
        let sampleBegin = initial.count
        let sotIndex = initial.firstIndex(of: specials.sot) ?? 0
        let filters = LogitFilters(
            specials: specials,
            sampleBegin: sampleBegin,
            blankTokens: blankTokens,
            suppressTokens: suppressTokens,
            maxInitialTimestampIndex: maxInitialTimestampIndex
        )

        var rows = Array(repeating: initial, count: groupSize)
        // float32, like `mx.zeros(n_batch)` in `_main_loop`.
        var sumLogprobs = Array(repeating: Float(0), count: groupSize)
        let features = groupSize > 1
            ? MLX.concatenated(Array(repeating: audioFeatures, count: groupSize), axis: 0)
            : audioFeatures
        var caches = (0..<model.config.decoderLayers).map { _ in WhisperLayerCache() }
        let vocab = model.config.vocabSize

        func step(_ logits: MLXArray) -> Bool {
            let flat = logits.asArray(Float.self)
            let width = flat.count / groupSize
            for row in 0..<groupSize {
                var rowLogits = Array(flat[(row * width)..<((row + 1) * width)])
                filters.apply(logits: &rowLogits, tokens: rows[row])
                var (token, logprob) = greedySelect(
                    logits: rowLogits, temperature: temperature, generator: &generator
                )
                if rows[row].last == specials.eot {
                    token = specials.eot
                } else {
                    sumLogprobs[row] += Float(logprob)
                }
                rows[row].append(token)
            }
            return rows.allSatisfy { $0.last == specials.eot }
        }

        // First forward pass over the initial tokens.
        let prefill = MLXArray(rows.flatMap { $0.map(Int32.init) }).reshaped([groupSize, sampleBegin])
        let hidden = model.model.decoder(
            tokens: prefill, startPosition: 0, encoderHidden: features, caches: &caches
        )
        let lastLogits = model.model.decoder.projectToVocab(hidden[0..., sampleBegin - 1]).asType(.float32)
        var noSpeechProb = Double.nan
        if let noSpeech = specials.noSpeech, noSpeech < vocab {
            let sotLogits = model.model.decoder.projectToVocab(hidden[0, sotIndex]).asType(.float32)
            eval(lastLogits, sotLogits)
            let values = sotLogits.asArray(Float.self)
            noSpeechProb = Foundation.exp(Double(values[noSpeech]) - logSumExp(values[...]))
        } else {
            eval(lastLogits)
        }
        var completed = step(lastLogits)

        if sampleLen > 1 {
            for _ in 1..<sampleLen {
                if rows[0].count > nCtx || completed { break }
                let last = MLXArray(rows.map { Int32($0[$0.count - 1]) }).reshaped([groupSize, 1])
                let stepHidden = model.model.decoder(
                    tokens: last,
                    startPosition: rows[0].count - 1,
                    encoderHidden: features,
                    caches: &caches
                )
                let logits = model.model.decoder.projectToVocab(stepHidden[0..., 0]).asType(.float32)
                eval(logits)
                completed = step(logits)
            }
        }

        // finalize: append EOT, slice from sample_begin, cut at the first EOT.
        let candidates: [[Int]] = rows.map { row in
            let sampled = Array(row[sampleBegin...]) + [specials.eot]
            let end = sampled.firstIndex(of: specials.eot) ?? sampled.count
            return Array(sampled[..<end])
        }
        let sums = sumLogprobs.map(Double.init)
        let selected = rankByLikelihood(tokens: candidates, sumLogprobs: sums)
        let tokens = candidates[selected]
        let text = decodeText(tokens).trimmingCharacters(in: .whitespacesAndNewlines)
        return DecodingResult(
            tokens: tokens,
            text: text,
            avgLogprob: sums[selected] / Double(tokens.count + 1),
            noSpeechProb: noSpeechProb,
            temperature: temperature,
            compressionRatio: compressionRatio(text)
        )
    }
}
