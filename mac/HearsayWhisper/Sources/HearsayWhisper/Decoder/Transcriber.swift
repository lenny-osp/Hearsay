// Ported from mlx_whisper 0.4.3 (ml-explore/mlx-examples, MIT, Copyright © 2023 Apple Inc.); see LICENSES/mlx-whisper.txt.

import Foundation
import MLX

/// Transcribes 16 kHz mono audio with a loaded `WhisperModel`. A port of
/// `transcribe()` in mlx_whisper 0.4.3 `transcribe.py` (greedy decoding,
/// `clip_timestamps="0"`, no word timestamps).
///
/// Not thread-safe: one transcription at a time per instance.
public final class Transcriber {
    public let model: WhisperModel
    public let specials: WhisperSpecialTokens
    let tokenizer: WhisperTokenizer
    let decoder: WindowDecoder

    public init(model: WhisperModel) throws {
        guard let tokenizer = model.tokenizer else { throw TranscriptionError.modelNotLoaded }
        let specials = try Self.specialTokens(model: model, tokenizer: tokenizer)
        self.model = model
        self.tokenizer = tokenizer
        self.specials = specials

        let blank = tokenizer.encode(text: " ") + [specials.eot]
        let nonSpeech = model.generationConfig?.suppressTokens
            ?? nonSpeechTokens(encode: { tokenizer.encode(text: $0) })
        let suppress = suppressTokenSet(nonSpeech: nonSpeech, specials: specials)
        let timestampBegin = specials.timestampBegin
        self.decoder = WindowDecoder(
            model: model,
            specials: specials,
            suppressTokens: suppress,
            blankTokens: blank,
            decodeText: { tokens in
                Self.decode(tokens: tokens, tokenizer: tokenizer, below: timestampBegin)
            }
        )
    }

    /// Load a model and its (possibly shared) tokenizer folder.
    public static func load(modelDirectory: URL, tokenizerDirectory: URL) async throws -> Transcriber {
        let model = try await WhisperModel.fromDirectory(
            modelDirectory: modelDirectory,
            tokenizerDirectory: tokenizerDirectory
        )
        return try Transcriber(model: model)
    }

    /// Returns MLX's buffer cache to the system. Freed arrays go into that
    /// cache, not back to the system, so call this after the last reference
    /// to a `Transcriber` is dropped; otherwise its weights stay resident.
    public static func releaseCachedMemory() {
        Memory.clearCache()
    }

    /// Transcribe 16 kHz mono float samples in [-1, 1].
    /// `progress` receives the fraction of content frames done, 0...1.
    /// `shouldCancel` (and `Task.isCancelled`, when called from a task) is
    /// checked once per 30 s window before it is decoded; when either fires
    /// the call throws `TranscriptionError.cancelled(partial:)` with the
    /// segments decoded so far. Without it the call never cancels.
    public func transcribe(
        samples: [Float],
        options: TranscriptionOptions,
        progress: (@Sendable (Double) -> Void)? = nil,
        shouldCancel: (@Sendable () -> Bool)? = nil
    ) throws -> Transcription {
        var checkpoint: TranscriptionCheckpoint? = nil
        while true {
            // Without shouldYield a step never suspends; the loop only
            // guards the type.
            switch try transcribeStep(
                samples: samples, options: options, resumingFrom: checkpoint,
                progress: progress, shouldCancel: shouldCancel, shouldYield: nil
            ) {
            case .finished(let transcription):
                return transcription
            case .suspended(let next):
                checkpoint = next
            }
        }
    }

    /// `transcribe`, stoppable between windows (PLAN.md 4.9). Before each 30 s
    /// window, after `shouldCancel` (cancel wins), `shouldYield` is asked, but
    /// only once this call has processed at least one window, so every call
    /// advances; when it returns true the call returns
    /// `.suspended(checkpoint)`. Pass the checkpoint back as `resumingFrom`
    /// with the same samples and options to continue: the result equals one
    /// uninterrupted call. On resume `progress` first reports the
    /// checkpoint's fraction, then continues from it, and a cancel throws
    /// `cancelled(partial:)` with the checkpoint's segments plus the new ones.
    /// The log-mel spectrogram is recomputed from `samples` on each call.
    /// Throws `TranscriptionCheckpointError` when the checkpoint was made
    /// from other samples or options.
    public func transcribeStep(
        samples: [Float],
        options: TranscriptionOptions,
        resumingFrom checkpoint: TranscriptionCheckpoint? = nil,
        progress: (@Sendable (Double) -> Void)? = nil,
        shouldCancel: (@Sendable () -> Bool)? = nil,
        shouldYield: (@Sendable () -> Bool)? = nil
    ) throws -> TranscriptionStep {
        guard !options.temperatures.isEmpty else { throw TranscriptionError.noTemperatures }
        let fingerprint = sampleFingerprint(samples)
        if let checkpoint {
            guard checkpoint.sampleCount == samples.count, checkpoint.sampleFingerprint == fingerprint else {
                throw TranscriptionCheckpointError.samplesMismatch
            }
            guard checkpoint.options == options else { throw TranscriptionCheckpointError.optionsMismatch }
        }
        let nFrames = WhisperAudioConfig.nFrames
        let hop = WhisperAudioConfig.hopLength
        let sampleRate = WhisperAudioConfig.sampleRate

        // Pad 30 seconds of silence to the input audio, for slicing
        let mel = logMelSpectrogram(
            samples: samples,
            nMels: model.config.numMelBins,
            padding: WhisperAudioConfig.chunkLengthSamples
        )
        let contentFrames = max(mel.dim(0) - nFrames, 0)

        let language: String
        var seek: Int
        var allTokens: [Int]
        var allSegments: [TranscriptSegment]
        var promptResetSince: Int
        let initialPromptCount: Int

        if let checkpoint {
            language = checkpoint.language
            seek = checkpoint.seek
            allTokens = checkpoint.allTokens
            allSegments = checkpoint.segments
            promptResetSince = checkpoint.promptResetSince
            initialPromptCount = checkpoint.initialPromptTokenCount
            progress?(checkpoint.fractionDone)
        } else {
            language = try resolveLanguage(options.language, mel: mel)
            seek = 0
            allTokens = []
            allSegments = []
            promptResetSince = 0
            var initialPromptTokens: [Int] = []
            if let initialPrompt = options.initialPrompt {
                initialPromptTokens = tokenizer.encode(
                    text: " " + initialPrompt.trimmingCharacters(in: .whitespacesAndNewlines)
                )
                allTokens.append(contentsOf: initialPromptTokens)
            }
            initialPromptCount = initialPromptTokens.count
        }

        let compressionThreshold = options.compressionRatioThreshold.map(decimal)
        let logprobThreshold = options.logprobThreshold.map(decimal)
        let noSpeechThreshold = options.noSpeechThreshold.map(decimal)
        // options.hallucinationSilenceThreshold: every use in transcribe.py is
        // inside `if word_timestamps:`; word timestamps are not ported, so it
        // has no effect, as in the Python CLI whisper-tools runs.

        var windowsThisCall = 0
        while seek < contentFrames {
            if shouldCancel?() == true || Task.isCancelled {
                throw TranscriptionError.cancelled(partial: allSegments)
            }
            if windowsThisCall > 0, shouldYield?() == true {
                return .suspended(TranscriptionCheckpoint(
                    seek: seek,
                    contentFrames: contentFrames,
                    allTokens: allTokens,
                    segments: allSegments,
                    promptResetSince: promptResetSince,
                    language: language,
                    initialPromptTokenCount: initialPromptCount,
                    sampleCount: samples.count,
                    sampleFingerprint: fingerprint,
                    options: options
                ))
            }
            windowsThisCall += 1
            let timeOffset = Double(seek * hop) / Double(sampleRate)
            let segmentSize = min(nFrames, contentFrames - seek)
            let melWindow = padOrTrimFrames(mel[seek..<(seek + segmentSize)], length: nFrames)
            let audioFeatures = decoder.encode(melWindow: melWindow)

            let prompt = Array(allTokens[promptResetSince...])
            let result = decodeWithFallback(
                audioFeatures: audioFeatures,
                language: language,
                prompt: prompt,
                options: options,
                compressionThreshold: compressionThreshold,
                logprobThreshold: logprobThreshold,
                noSpeechThreshold: noSpeechThreshold
            )
            Memory.clearCache()

            if let noSpeechThreshold {
                // no voice activity check
                var shouldSkip = result.noSpeechProb > noSpeechThreshold
                if let logprobThreshold, result.avgLogprob > logprobThreshold {
                    // don't skip if the logprob is high enough, despite the no_speech_prob
                    shouldSkip = false
                }
                if shouldSkip {
                    seek += segmentSize  // fast-forward to the next segment boundary
                    progress?(Double(min(contentFrames, seek)) / Double(max(contentFrames, 1)))
                    continue
                }
            }

            let previousSeek = seek
            let split = splitWindow(
                tokens: result.tokens,
                timestampBegin: specials.timestampBegin,
                timeOffset: timeOffset,
                segmentSize: segmentSize
            )
            seek += split.seekAdvance

            var currentSegments: [TranscriptSegment] = []
            for (offset, piece) in split.segments.enumerated() {
                let textTokens = piece.tokens.filter { $0 < specials.eot }
                var segment = TranscriptSegment(
                    id: allSegments.count + offset,
                    seek: previousSeek,
                    start: piece.start,
                    end: piece.end,
                    text: decoder.decodeText(textTokens),
                    tokens: piece.tokens,
                    temperature: result.temperature,
                    avgLogprob: Float(result.avgLogprob),
                    compressionRatio: Float(result.compressionRatio),
                    noSpeechProb: Float(result.noSpeechProb)
                )
                // if a segment is instantaneous or does not contain text, clear it
                if segment.start == segment.end
                    || segment.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                {
                    segment.text = ""
                    segment.tokens = []
                }
                currentSegments.append(segment)
            }

            allSegments.append(contentsOf: currentSegments)
            allTokens.append(contentsOf: currentSegments.flatMap(\.tokens))

            if !options.conditionOnPreviousText || result.temperature > 0.5 {
                // do not feed the prompt tokens if a high temperature was used
                promptResetSince = allTokens.count
            }

            progress?(Double(min(contentFrames, seek)) / Double(max(contentFrames, 1)))
        }

        let text = decoder.decodeText(Array(allTokens[initialPromptCount...]))
        return .finished(Transcription(segments: allSegments, language: language, text: text))
    }

    // MARK: - Steps

    /// `decode_with_fallback`: retry at the next temperature while the result
    /// is too repetitive or too improbable, unless it looks like silence.
    func decodeWithFallback(
        audioFeatures: MLXArray,
        language: String,
        prompt: [Int],
        options: TranscriptionOptions,
        compressionThreshold: Double?,
        logprobThreshold: Double?,
        noSpeechThreshold: Double?
    ) -> DecodingResult {
        var result: DecodingResult? = nil
        for temperature in options.temperatures {
            let current = decoder.decode(
                audioFeatures: audioFeatures,
                language: language,
                prompt: prompt,
                temperature: temperature,
                bestOf: options.bestOf
            )
            result = current
            if !needsFallback(
                current,
                compressionThreshold: compressionThreshold,
                logprobThreshold: logprobThreshold,
                noSpeechThreshold: noSpeechThreshold
            ) {
                break
            }
        }
        // temperatures is non-empty (checked by transcribe), so result is set.
        return result ?? DecodingResult(
            tokens: [], text: "", avgLogprob: .nan, noSpeechProb: .nan,
            temperature: 0, compressionRatio: .nan
        )
    }

    /// Language for the whole recording: the given one normalized as
    /// `get_tokenizer` does, "en" for English-only models, else detected on
    /// the first 30 s.
    func resolveLanguage(_ requested: String?, mel: MLXArray) throws -> String {
        if let requested {
            guard let code = WhisperLanguages.resolve(requested) else {
                throw TranscriptionError.unsupportedLanguage(requested)
            }
            if specials.isMultilingual, specials.languageToken(code) == nil {
                throw TranscriptionError.unsupportedLanguage(requested)
            }
            return code
        }
        guard specials.isMultilingual else { return "en" }
        let window = padOrTrimFrames(mel, length: WhisperAudioConfig.nFrames)
        let features = decoder.encode(melWindow: window)
        return decoder.detectLanguage(audioFeatures: features) ?? "en"
    }

    // MARK: - Tokens

    static func specialTokens(model: WhisperModel, tokenizer: WhisperTokenizer) throws -> WhisperSpecialTokens {
        func require(_ id: Int?, _ name: String) throws -> Int {
            guard let id else { throw TranscriptionError.missingSpecialToken(name) }
            return id
        }
        let nVocab = model.config.vocabSize
        let isMultilingual = nVocab >= 51865
        return WhisperSpecialTokens(
            eot: tokenizer.endOfTextId,
            sot: tokenizer.startOfTranscriptId,
            translate: try require(tokenizer.translateId, "<|translate|>"),
            transcribe: try require(tokenizer.transcribeId, "<|transcribe|>"),
            sotLm: try require(tokenizer.startOfLmId, "<|startoflm|>"),
            sotPrev: try require(tokenizer.prevSotId, "<|startofprev|>"),
            noSpeech: tokenizer.noSpeechId,
            noTimestamps: tokenizer.noTimestampsId,
            timestampBegin: tokenizer.timestampBeginId,
            isMultilingual: isMultilingual,
            numLanguages: nVocab - 51765 - (isMultilingual ? 1 : 0)
        )
    }

    /// `Tokenizer.decode`: drop timestamp tokens, then byte-level decode like tiktoken.
    static func decode(tokens: [Int], tokenizer: WhisperTokenizer, below timestampBegin: Int) -> String {
        let strings = tokens
            .filter { $0 >= 0 && $0 < timestampBegin }
            .compactMap { tokenizer.inner.convertIdToToken($0) }
        return ByteLevelText.decode(tokenStrings: strings)
    }
}

/// Needs a retry at the next temperature (`decode_with_fallback`).
func needsFallback(
    _ result: DecodingResult,
    compressionThreshold: Double?,
    logprobThreshold: Double?,
    noSpeechThreshold: Double?
) -> Bool {
    var needs = false
    if let compressionThreshold, result.compressionRatio > compressionThreshold {
        needs = true  // too repetitive
    }
    if let logprobThreshold, result.avgLogprob < logprobThreshold {
        needs = true  // average log probability is too low
    }
    if let noSpeechThreshold, result.noSpeechProb > noSpeechThreshold {
        needs = false  // silence
    }
    return needs
}

/// The Double a Float threshold was written as (2.4 -> 2.4, not
/// 2.4000000953674316), so comparisons match Python's float64 thresholds.
func decimal(_ value: Float) -> Double {
    Double(String(value)) ?? Double(value)
}
