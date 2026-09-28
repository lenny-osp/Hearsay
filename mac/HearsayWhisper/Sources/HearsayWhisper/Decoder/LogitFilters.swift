// Ported from mlx_whisper 0.4.3 (ml-explore/mlx-examples, MIT, Copyright © 2023 Apple Inc.); see LICENSES/mlx-whisper.txt.

import Foundation

/// Logit filters from mlx_whisper `decoding.py`, applied to one batch row of
/// float32 logits on the CPU. `tokens` is the whole row so far, including the
/// initial (prompt + sot sequence) tokens, as in Python.
struct LogitFilters: Sendable {
    let specials: WhisperSpecialTokens
    /// `len(initial_tokens)`.
    let sampleBegin: Int
    /// `SuppressBlank` ids: `tokenizer.encode(" ") + [eot]`. nil disables the filter.
    let blankTokens: [Int]?
    /// `SuppressTokens` ids. Empty disables the filter.
    let suppressTokens: [Int]
    /// `round(max_initial_timestamp / precision)`; 50 for the default 1.0 s.
    let maxInitialTimestampIndex: Int?

    /// Apply `SuppressBlank`, `SuppressTokens`, `ApplyTimestampRules` in the
    /// order `DecodingTask` registers them.
    func apply(logits: inout [Float], tokens: [Int]) {
        if let blankTokens, tokens.count == sampleBegin {
            for id in blankTokens where id >= 0 && id < logits.count {
                logits[id] = -.infinity
            }
        }
        for id in suppressTokens where id >= 0 && id < logits.count {
            logits[id] = -.infinity
        }
        applyTimestampRules(logits: &logits, tokens: tokens)
    }

    /// Port of `ApplyTimestampRules.apply` for one row.
    func applyTimestampRules(logits: inout [Float], tokens: [Int]) {
        let vocab = logits.count
        let begin = specials.timestampBegin
        let eot = specials.eot
        var mask = [Bool](repeating: false, count: vocab)  // true = -inf
        func maskRange(_ lower: Int, _ upper: Int) {
            let lo = max(lower, 0)
            let hi = min(upper, vocab)
            guard lo < hi else { return }
            for i in lo..<hi { mask[i] = true }
        }

        // suppress <|notimestamps|>, which is handled by without_timestamps
        if specials.noTimestamps >= 0 && specials.noTimestamps < vocab {
            mask[specials.noTimestamps] = true
        }

        // timestamps have to appear in pairs, except directly before EOT
        let seq = tokens.count > sampleBegin ? Array(tokens[sampleBegin...]) : []
        let lastWasTimestamp = seq.count >= 1 && seq[seq.count - 1] >= begin
        let penultimateWasTimestamp = seq.count < 2 || seq[seq.count - 2] >= begin
        if lastWasTimestamp {
            if penultimateWasTimestamp {
                maskRange(begin, vocab)  // has to be non-timestamp
            } else {
                maskRange(0, eot)  // cannot be normal text tokens
            }
        }

        // mlx_whisper 0.4.3 collects the *positions* of timestamp tokens here
        // (`[i for i, v in enumerate(seq) if v > timestamp_begin]`), not their
        // values as openai-whisper does, so `mask[timestamp_begin:last_timestamp]`
        // is an empty slice and timestamps are not forced to be monotonic.
        // Ported as is so the output matches the Python tool.
        var lastTimestampPosition: Int? = nil
        for (index, value) in seq.enumerated() where value > begin {
            lastTimestampPosition = index
        }
        if var lastTimestamp = lastTimestampPosition {
            if lastTimestamp == 0 || penultimateWasTimestamp {
                lastTimestamp += 1
            }
            maskRange(begin, lastTimestamp)
        }

        if tokens.count == sampleBegin {
            // suppress generating non-timestamp tokens at the beginning
            maskRange(0, begin)
            // apply the `max_initial_timestamp` option
            if let maxIndex = maxInitialTimestampIndex {
                maskRange(begin + maxIndex + 1, vocab)
            }
        }

        // if sum of probability over timestamps is above any other token, sample
        // timestamp. Computed on the incoming logits, before this filter's mask.
        if begin < vocab {
            let total = logSumExp(logits[...])
            let timestampLogprob = logSumExp(logits[begin...]) - total
            var maxText = -Double.infinity
            for i in 0..<begin {
                maxText = max(maxText, Double(logits[i]) - total)
            }
            if timestampLogprob > maxText {
                maskRange(0, begin)
            }
        }

        for i in 0..<vocab where mask[i] {
            logits[i] = -.infinity
        }
    }
}

/// `logsumexp` with the max shift, accumulated in Double.
func logSumExp(_ values: ArraySlice<Float>) -> Double {
    var maxValue = -Double.infinity
    for v in values { maxValue = max(maxValue, Double(v)) }
    guard maxValue.isFinite else { return maxValue }
    var sum = 0.0
    for v in values { sum += Foundation.exp(Double(v) - maxValue) }
    return maxValue + Foundation.log(sum)
}

/// Index of the largest value; first one on ties, like `argmax`.
func argMax(_ values: [Float]) -> Int {
    guard values.count > 1 else { return 0 }
    var best = 0
    for i in 1..<values.count where values[i] > values[best] {
        best = i
    }
    return best
}

/// One step of `GreedyDecoder.update` for one row: choose the next token and
/// return it with its log probability. `logits` are already filtered.
func greedySelect<G: RandomNumberGenerator>(
    logits: [Float],
    temperature: Float,
    generator: inout G
) -> (token: Int, logprob: Double) {
    let total = logSumExp(logits[...])
    let token: Int
    if temperature == 0 {
        token = argMax(logits)
    } else {
        token = sampleCategorical(logits: logits, temperature: temperature, generator: &generator)
    }
    return (token, Double(logits[token]) - total)
}

/// `mx.random.categorical(logits / temperature)`.
func sampleCategorical<G: RandomNumberGenerator>(
    logits: [Float],
    temperature: Float,
    generator: inout G
) -> Int {
    let t = Double(temperature)
    var maxValue = -Double.infinity
    for v in logits { maxValue = max(maxValue, Double(v) / t) }
    var weights = [Double](repeating: 0, count: logits.count)
    var sum = 0.0
    for (i, v) in logits.enumerated() {
        let w = Foundation.exp(Double(v) / t - maxValue)
        weights[i] = w
        sum += w
    }
    guard sum > 0, sum.isFinite else { return argMax(logits) }
    var target = Double.random(in: 0..<sum, using: &generator)
    for (i, w) in weights.enumerated() where w > 0 {
        if target < w { return i }
        target -= w
    }
    return argMax(logits)
}
