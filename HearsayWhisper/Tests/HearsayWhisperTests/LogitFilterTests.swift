import Foundation
import Testing
@testable import HearsayWhisper

/// Mirrors mlx_whisper `decoding.py` logit filters. Expected masks were
/// produced by running Python's `ApplyTimestampRules.apply` on the same
/// synthetic inputs (vocab 20, eot 5, <|notimestamps|> 9, timestamp_begin 10).
struct LogitFilterTests {
    static let specials = WhisperSpecialTokens(
        eot: 5, sot: 1, translate: 6, transcribe: 7, sotLm: 8, sotPrev: 4,
        noSpeech: nil, noTimestamps: 9, timestampBegin: 10,
        isMultilingual: false, numLanguages: 0
    )
    /// Text tokens clearly preferred over the timestamp mass.
    static let textHeavy: [Float] = Array(repeating: 5, count: 9) + Array(repeating: 0, count: 11)

    static func masked(
        _ tokens: [Int], sampleBegin: Int = 2, logits: [Float] = textHeavy, maxInitial: Int? = nil
    ) -> [Int] {
        let filters = LogitFilters(
            specials: specials, sampleBegin: sampleBegin, blankTokens: nil,
            suppressTokens: [], maxInitialTimestampIndex: maxInitial
        )
        var values = logits
        filters.applyTimestampRules(logits: &values, tokens: tokens)
        return values.indices.filter { values[$0] == -.infinity }
    }

    @Test func firstStepMustBeATimestampWithinMaxInitial() {
        // Python case A2: text and <|notimestamps|> masked, timestamps past index 3 masked.
        #expect(Self.masked([1, 2], maxInitial: 3) == [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 14, 15, 16, 17, 18, 19])
    }

    @Test func afterTextThenTimestampOnlyTimestampOrEotMayFollow() {
        // Python case B: [ts, text, ts] masks tokens below eot.
        #expect(Self.masked([1, 2, 10, 3, 14]) == [0, 1, 2, 3, 4, 9])
    }

    @Test func afterATimestampPairTextMustFollow() {
        // Python case C: [ts, text, ts, ts] masks every timestamp.
        #expect(Self.masked([1, 2, 10, 3, 14, 14]) == [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19])
        // Python case H: a single leading timestamp counts as a pair start.
        #expect(Self.masked([1, 2, 12]) == [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19])
    }

    @Test func afterTextOnlyNoTimestampsIsMasked() {
        // Python case D.
        #expect(Self.masked([1, 2, 12, 3]) == [9])
    }

    @Test func earlierTimestampsStayAllowedLikeMlxWhisper() {
        // Python case G: mlx_whisper 0.4.3 uses timestamp positions, not
        // values, so timestamps below the last one (16) are not masked.
        #expect(Self.masked([1, 2, 15, 3, 16, 16, 3]) == [9])
    }

    @Test func timestampMassAboveBestTextForcesATimestamp() {
        // Python case E: flat text 0, timestamps 1 -> all text masked.
        let logits: [Float] = Array(repeating: 0, count: 10) + Array(repeating: 1, count: 10)
        #expect(Self.masked([1, 2, 12, 3], logits: logits) == [0, 1, 2, 3, 4, 5, 6, 7, 8, 9])
        // Python case F: one dominant text token keeps text allowed.
        var textWins = [Float](repeating: 0, count: 20)
        textWins[3] = 10
        #expect(Self.masked([1, 2, 12, 3], logits: textWins) == [9])
    }

    @Test func suppressBlankOnlyAtTheFirstStep() {
        let filters = LogitFilters(
            specials: Self.specials, sampleBegin: 2, blankTokens: [0, 5],
            suppressTokens: [], maxInitialTimestampIndex: nil
        )
        var first = [Float](repeating: 0, count: 20)
        filters.apply(logits: &first, tokens: [1, 2])
        #expect(first[0] == -.infinity && first[5] == -.infinity)

        var later = Self.textHeavy
        filters.apply(logits: &later, tokens: [1, 2, 12, 3])
        #expect(later[0] == 5 && later[5] == 5)
    }

    @Test func suppressTokensEveryStep() {
        let filters = LogitFilters(
            specials: Self.specials, sampleBegin: 2, blankTokens: nil,
            suppressTokens: [3, 7], maxInitialTimestampIndex: nil
        )
        var values = Self.textHeavy
        filters.apply(logits: &values, tokens: [1, 2, 12, 3])
        #expect(values[3] == -.infinity && values[7] == -.infinity)
        #expect(values[2] == 5)
    }

    @Test func suppressSetAddsTaskAndStartTokens() {
        // DecodingTask._get_suppress_tokens: non-speech + transcribe, translate,
        // sot, sot_prev, sot_lm, no_speech; sorted, deduplicated.
        let set = suppressTokenSet(nonSpeech: [1, 220, 220, 50257], specials: .largeV3)
        #expect(set == [1, 220, 50257, 50258, 50359, 50360, 50361, 50362, 50363])
    }

    @Test func greedyPicksArgmaxAndReportsItsLogprob() {
        var generator = SystemRandomNumberGenerator()
        let logits: [Float] = [0, 2, -.infinity, 2]
        let (token, logprob) = greedySelect(logits: logits, temperature: 0, generator: &generator)
        #expect(token == 1)  // first of the tied maxima, like argmax
        let expected = 2 - log(exp(0.0) + 2 * exp(2.0))
        #expect(abs(logprob - expected) < 1e-9)
    }

    @Test func samplingNeverPicksAMaskedToken() {
        var generator = SystemRandomNumberGenerator()
        let logits: [Float] = [-.infinity, 0, -.infinity, 0.5]
        for _ in 0..<200 {
            let (token, _) = greedySelect(logits: logits, temperature: 1.0, generator: &generator)
            #expect(token == 1 || token == 3)
        }
    }
}
