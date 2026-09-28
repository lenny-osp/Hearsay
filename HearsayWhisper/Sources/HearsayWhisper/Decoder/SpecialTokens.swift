import Foundation

/// The special token ids the decoder needs; the `Tokenizer` properties of
/// mlx_whisper `tokenizer.py` (`eot`, `sot`, `sot_prev`, `timestamp_begin`, ...).
public struct WhisperSpecialTokens: Sendable, Equatable {
    public var eot: Int
    public var sot: Int
    public var translate: Int
    public var transcribe: Int
    public var sotLm: Int
    public var sotPrev: Int
    public var noSpeech: Int?
    public var noTimestamps: Int
    public var timestampBegin: Int
    public var isMultilingual: Bool
    /// `n_vocab - 51765 - int(is_multilingual)`, as in `Whisper.num_languages`.
    public var numLanguages: Int

    public init(
        eot: Int, sot: Int, translate: Int, transcribe: Int, sotLm: Int, sotPrev: Int,
        noSpeech: Int?, noTimestamps: Int, timestampBegin: Int,
        isMultilingual: Bool, numLanguages: Int
    ) {
        self.eot = eot
        self.sot = sot
        self.translate = translate
        self.transcribe = transcribe
        self.sotLm = sotLm
        self.sotPrev = sotPrev
        self.noSpeech = noSpeech
        self.noTimestamps = noTimestamps
        self.timestampBegin = timestampBegin
        self.isMultilingual = isMultilingual
        self.numLanguages = numLanguages
    }

    /// Token ids for the English/large-v3 multilingual layout (n_vocab 51866);
    /// handy for tests.
    public static let largeV3 = WhisperSpecialTokens(
        eot: 50257, sot: 50258, translate: 50359, transcribe: 50360, sotLm: 50361,
        sotPrev: 50362, noSpeech: 50363, noTimestamps: 50364, timestampBegin: 50365,
        isMultilingual: true, numLanguages: 100
    )

    /// Language codes usable with this model, `LANGUAGES` keys limited to
    /// `num_languages`.
    public var languageCodes: [String] {
        guard isMultilingual else { return [] }
        return Array(WhisperLanguages.codes.prefix(numLanguages))
    }

    /// `sot + 1 + langs.index(language)`; nil when not available.
    public func languageToken(_ code: String) -> Int? {
        guard let index = languageCodes.firstIndex(of: code) else { return nil }
        return sot + 1 + index
    }

    /// `Tokenizer.sot_sequence` for `task="transcribe"`: `[sot]` for
    /// English-only models, `[sot, <|lang|>, <|transcribe|>]` otherwise.
    public func sotSequence(language: String?) -> [Int] {
        guard isMultilingual else { return [sot] }
        var sequence = [sot]
        if let language, let token = languageToken(language) {
            sequence.append(token)
        }
        sequence.append(transcribe)
        return sequence
    }

    /// Specials appended by `DecodingTask._get_suppress_tokens`, on top of the
    /// non-speech tokens.
    public var alwaysSuppressed: [Int] {
        var ids = [transcribe, translate, sot, sotPrev, sotLm]
        if let noSpeech { ids.append(noSpeech) }
        return ids
    }
}

/// Port of `Tokenizer.non_speech_tokens`, given an encoder equivalent to
/// tiktoken's `Encoding.encode`. Used when `generation_config.json` has no
/// `suppress_tokens` list.
func nonSpeechTokens(encode: (String) -> [Int]) -> [Int] {
    var symbols = "\"#()*+/:;<=>@[\\]^_`{|}~「」『』".map { String($0) }
    symbols += "<< >> <<< >>> -- --- -( -[ (' (\" (( )) ((( ))) [[ ]] {{ }} ♪♪ ♪♪♪"
        .split(separator: " ").map(String.init)
    let miscellaneous = "♩♪♫♬♭♮♯".map { String($0) }

    var result = Set<Int>()
    if let first = encode(" -").first { result.insert(first) }
    if let first = encode(" '").first { result.insert(first) }
    for symbol in symbols + miscellaneous {
        for tokens in [encode(symbol), encode(" " + symbol)] {
            if tokens.count == 1 || miscellaneous.contains(symbol), let first = tokens.first {
                result.insert(first)
            }
        }
    }
    return result.sorted()
}

/// Port of `DecodingTask._get_suppress_tokens` for `suppress_tokens="-1"`.
func suppressTokenSet(nonSpeech: [Int], specials: WhisperSpecialTokens) -> [Int] {
    Array(Set(nonSpeech.filter { $0 >= 0 } + specials.alwaysSuppressed)).sorted()
}
