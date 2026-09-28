import Foundation
import Testing
@testable import HearsayWhisper

struct CompressionRatioTests {
    // Expected values from Python: `mlx_whisper.decoding.compression_ratio`.
    @Test(arguments: [
        ("hello world", 0.5789473684210527),
        ("hello hello hello hello hello hello hello hello", 2.764705882352941),
        ("", 0.0),
        ("關心這起自撞獨駕事件", 0.7317073170731707),
        (String(repeating: "The second action item is to record a 60-minute test call. ", count: 3), 2.6029411764705883),
    ])
    func matchesPythonZlib(text: String, expected: Double) {
        #expect(compressionRatio(text) == expected)
    }
}

struct TimestampFormatTests {
    // Expected strings from Python: `mlx_whisper.writers.format_timestamp`.
    @Test(arguments: [
        (0.0, "00:00:00,000", "00:00.000"),
        (0.0005, "00:00:00,000", "00:00.000"),
        (0.0015, "00:00:00,002", "00:00.002"),
        (4.48, "00:00:04,480", "00:04.480"),
        (10.700000000000001, "00:00:10,700", "00:10.700"),
        (59.9996, "00:01:00,000", "01:00.000"),
        (3599.9995, "01:00:00,000", "01:00:00.000"),
        (3725.123456, "01:02:05,123", "01:02:05.123"),
    ])
    func matchesPython(seconds: Double, srt: String, console: String) {
        #expect(formatTimestamp(seconds, alwaysIncludeHours: true, decimalMarker: ",") == srt)
        #expect(formatTimestamp(seconds) == console)
    }

    @Test func srtMatchesWriteSRT() {
        let segments = [
            TranscriptSegment(id: 0, seek: 0, start: 0, end: 4.48, text: " Hello --> there,",
                              tokens: [], temperature: 0, avgLogprob: 0, compressionRatio: 1, noSpeechProb: 0),
            TranscriptSegment(id: 1, seek: 0, start: 4.78, end: 10.700000000000001, text: "",
                              tokens: [], temperature: 0, avgLogprob: 0, compressionRatio: 1, noSpeechProb: 0),
        ]
        #expect(renderSRT(segments) ==
            "1\n00:00:00,000 --> 00:00:04,480\nHello -> there,\n\n"
            + "2\n00:00:04,780 --> 00:00:10,700\n\n\n")
        #expect(consoleLine(segments[0]) == "[00:00.000 --> 00:04.480]  Hello --> there,")
    }
}

struct ByteLevelTextTests {
    @Test func decodesSpacesAndAscii() {
        // "Ġ" is the byte-level form of a space.
        #expect(ByteLevelText.decode(tokenStrings: ["ĠWelcome", "Ġto", "Ġthe", "."]) == " Welcome to the.")
    }

    @Test func keepsSpacesBeforePunctuationLikeTiktoken() {
        // swift-transformers' cleanup would turn " ." into "."; tiktoken does not.
        #expect(ByteLevelText.decode(tokenStrings: ["Ġ", "."]) == " .")
    }

    @Test func decodesMultiByteUTF8() {
        // "關" is E9 97 9C; byte-level maps 0xE9 -> "é", 0x97 -> "Ĺ", 0x9C -> "ľ".
        #expect(ByteLevelText.decode(tokenStrings: ["é", "Ĺľ"]) == "關")
    }

    @Test func invalidBytesBecomeReplacementCharacters() {
        #expect(ByteLevelText.decode(tokenStrings: ["é"]) == "\u{FFFD}")
    }
}

struct PromptAndRankingTests {
    let specials = WhisperSpecialTokens.largeV3

    @Test func sotSequenceForMultilingual() {
        // `sot + 1 + LANGUAGES.index(lang)`, then <|transcribe|>.
        #expect(specials.sotSequence(language: "en") == [50258, 50259, 50360])
        #expect(specials.sotSequence(language: "zh") == [50258, 50260, 50360])
        #expect(specials.languageToken("yue") == 50358)
    }

    @Test func emptyPromptIsJustTheSotSequence() {
        let sot = specials.sotSequence(language: "zh")
        #expect(initialTokens(sotSequence: sot, prompt: [], sotPrev: specials.sotPrev, nCtx: 448) == sot)
    }

    @Test func initialPromptIsPrefixedWithSotPrev() {
        // _get_initial_tokens: [sot_prev] + prompt[-(n_ctx // 2 - 1):] + sot_sequence.
        // Tokens of " The following is a sentence in Traditional Chinese." from tiktoken.
        let prompt = [440, 3480, 307, 257, 8174, 294, 46738, 4649, 13]
        let sot = specials.sotSequence(language: "zh")
        #expect(initialTokens(sotSequence: sot, prompt: prompt, sotPrev: 50362, nCtx: 448)
                == [50362] + prompt + [50258, 50260, 50360])
    }

    @Test func longPromptKeepsTheLast223Tokens() {
        let prompt = Array(0..<300)
        let tokens = initialTokens(sotSequence: [50258], prompt: prompt, sotPrev: 50362, nCtx: 448)
        #expect(tokens.count == 1 + 223 + 1)
        #expect(tokens[1] == 77)
        #expect(tokens[223] == 299)
    }

    @Test func rankerPrefersHighestLengthNormalizedLogprob() {
        let tokens = [[1, 2], [1, 2, 3, 4], [1]]
        #expect(rankByLikelihood(tokens: tokens, sumLogprobs: [-2, -2, -1.5]) == 1)
        #expect(rankByLikelihood(tokens: tokens, sumLogprobs: [-1, -2, -0.5]) == 0)
    }

    @Test func fallbackRulesMatchDecodeWithFallback() {
        func result(_ ratio: Double, _ logprob: Double, _ noSpeech: Double) -> DecodingResult {
            DecodingResult(tokens: [], text: "", avgLogprob: logprob, noSpeechProb: noSpeech,
                           temperature: 0, compressionRatio: ratio)
        }
        let c = decimal(2.4), l = decimal(-1.0), n = decimal(0.6)
        #expect(!needsFallback(result(2.4, -0.5, 0.1), compressionThreshold: c, logprobThreshold: l, noSpeechThreshold: n))
        #expect(needsFallback(result(2.41, -0.5, 0.1), compressionThreshold: c, logprobThreshold: l, noSpeechThreshold: n))
        #expect(needsFallback(result(1.0, -1.2, 0.1), compressionThreshold: c, logprobThreshold: l, noSpeechThreshold: n))
        // silence wins over both
        #expect(!needsFallback(result(3.0, -1.2, 0.9), compressionThreshold: c, logprobThreshold: l, noSpeechThreshold: n))
        // disabled thresholds
        #expect(!needsFallback(result(3.0, -5, 0.1), compressionThreshold: nil, logprobThreshold: nil, noSpeechThreshold: nil))
    }

    @Test func thresholdsCompareAsTheirDecimalValues() {
        #expect(decimal(2.4) == 2.4)
        #expect(decimal(-1.0) == -1.0)
        #expect(decimal(0.6) == 0.6)
    }

    @Test func languageNormalizationMatchesGetTokenizer() {
        #expect(WhisperLanguages.resolve("EN") == "en")
        #expect(WhisperLanguages.resolve("Chinese") == "zh")
        #expect(WhisperLanguages.resolve("mandarin") == "zh")
        #expect(WhisperLanguages.resolve("klingon") == nil)
        #expect(WhisperLanguages.codes.count == 100)
        #expect(WhisperLanguages.codes.first == "en" && WhisperLanguages.codes.last == "yue")
    }

    @Test func defaultOptionsMatchTheWhisperToolsCall() {
        let options = TranscriptionOptions()
        #expect(options.language == nil)
        #expect(options.initialPrompt == nil)
        #expect(options.conditionOnPreviousText == false)
        #expect(options.temperatures == [0])
        #expect(options.compressionRatioThreshold == 2.4)
        #expect(options.logprobThreshold == -1.0)
        #expect(options.noSpeechThreshold == 0.6)
        #expect(options.hallucinationSilenceThreshold == 2.0)
    }
}
