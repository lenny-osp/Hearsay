import Foundation
import Testing
@testable import HearsayCore

struct TranscriptTextLanguageTests {
    typealias T = TranscriptTextLanguage

    static func confident(_ text: String) -> (TranscriptLanguage, Double)? {
        guard let result = T.detect(text), result.1 >= T.confidenceThreshold else { return nil }
        return result
    }

    @Test func thresholds() {
        #expect(T.confidenceThreshold == 0.6)
        #expect(T.sampleLength == 4_000)
    }

    @Test func english() {
        let result = Self.confident(
            "Let's start with the budget review. We need to finish the report before Friday "
                + "and send it to the whole team.")
        #expect(result?.0 == .english)
    }

    @Test func german() {
        let result = Self.confident(
            "Wir fangen mit dem Budget an. Der Bericht muss bis Freitag fertig sein, "
                + "dann schicken wir ihn an das ganze Team.")
        #expect(result?.0 == .german)
    }

    @Test func spanish() {
        let result = Self.confident(
            "Empezamos con el presupuesto. Tenemos que terminar el informe antes del viernes "
                + "y enviarlo a todo el equipo.")
        #expect(result?.0 == .spanish)
    }

    @Test func traditionalChinese() {
        let result = Self.confident("我們先從預算開始討論。報告必須在星期五之前完成，然後寄給整個團隊。")
        #expect(result?.0 == .chinese)
    }

    @Test func simplifiedChinese() {
        let result = Self.confident("我们先从预算开始讨论。报告必须在星期五之前完成，然后发给整个团队。")
        #expect(result?.0 == .chinese)
    }

    @Test func noLettersIsNil() {
        #expect(T.detect("") == nil)
        #expect(T.detect("12 34 !?") == nil)
        #expect(T.confident("") == nil)
    }

    @Test func tooShortOrMixedIsNotConfident() {
        // One ambiguous word, and a word with a Chinese character: the
        // recognizer spreads its guess below the threshold.
        #expect(Self.confident("ok") == nil)
        #expect(Self.confident("OK 好") == nil)
        #expect(T.confident("OK 好") == nil)
        if let low = T.detect("ok") { #expect(low.1 < T.confidenceThreshold) }
    }

    @Test func srtTimingIsIgnored() {
        let srt = """
            1
            00:00:00,000 --> 00:00:04,000
            Wir fangen mit dem Budget an und besprechen dann den Bericht.

            2
            00:00:04,000 --> 00:00:08,000
            Der Bericht muss bis Freitag fertig sein.

            """
        #expect(T.detect(srtText: srt)?.0 == .german)
    }
}
