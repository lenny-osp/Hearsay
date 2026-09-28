import Foundation
import Testing
@testable import HearsayCore

struct ChineseScriptTests {
    static let simplified = "那天我来到了中国最冷的城市"
    static let traditional = "那天我來到了中國最冷的城市"

    @Test func simplifiedToTraditional() {
        #expect(ChineseScript.convert(Self.simplified, to: .traditional) == Self.traditional)
    }

    @Test func traditionalToSimplified() {
        #expect(ChineseScript.convert(Self.traditional, to: .simplified) == Self.simplified)
    }

    @Test func asIsLeavesTextUnchanged() {
        #expect(ChineseScript.convert(Self.simplified, to: .asIs) == Self.simplified)
        #expect(ChineseScript.convert(Self.traditional, to: .asIs) == Self.traditional)
    }

    @Test func pickerOffersTraditionalAndSimplifiedOnly() {
        #expect(ChineseScript.pickerCases == [.traditional, .simplified])
        #expect(ChineseScript.pickerCases.map(\.displayName) == ["繁體中文", "简体中文"])
        #expect(ChineseScript.asIs.pickerValue == .traditional)
        #expect(ChineseScript.simplified.pickerValue == .simplified)
    }

    @Test func asciiAndEnglishUnchanged() {
        for text in ["", "Hello, world. 123 -> ok!", "The quick brown fox jumps over the lazy dog."] {
            for script in ChineseScript.allCases {
                #expect(ChineseScript.convert(text, to: script) == text)
            }
        }
    }

    @Test func mixedTextKeepsEnglish() {
        #expect(ChineseScript.convert("我们用 Swift 和 MLX 开发软件", to: .traditional)
            == "我們用 Swift 和 MLX 開發軟件")
        #expect(ChineseScript.convert("我們用 Swift 和 MLX 開發軟件", to: .simplified)
            == "我们用 Swift 和 MLX 开发软件")
    }

    @Test func onlyZhSessionsConvert() {
        #expect(ChineseScript.forSession(languageCode: "zh", preference: .traditional) == .traditional)
        #expect(ChineseScript.forSession(languageCode: "zh", preference: .simplified) == .simplified)
        #expect(ChineseScript.forSession(languageCode: "zh", preference: .asIs) == .traditional)
        #expect(ChineseScript.forSession(languageCode: "en", preference: .traditional) == .asIs)
        #expect(ChineseScript.forSession(languageCode: "en", preference: .simplified) == .asIs)
    }

    @Test func segmentsKeepTimings() {
        let segments = [
            TranscriptSegment(start: 0, end: 1.5, text: Self.simplified),
            TranscriptSegment(start: 1.5, end: 3, text: "OK"),
        ]
        let converted = ChineseScript.convert(segments, to: .traditional)
        #expect(converted == [
            TranscriptSegment(start: 0, end: 1.5, text: Self.traditional),
            TranscriptSegment(start: 1.5, end: 3, text: "OK"),
        ])
        #expect(ChineseScript.convert(segments, to: .asIs) == segments)
    }
}
