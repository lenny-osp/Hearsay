import Foundation
import Testing
@testable import HearsayWhisper

/// Pure tests for the candidate restriction and window averaging on top of
/// `detect_language`'s distribution. No model needed.
struct LanguageDetectionTests {
    static let sample = LanguageProbabilities(probabilities: [
        "en": 0.5, "zh": 0.1, "de": 0.2, "es": 0.1, "fr": 0.1,
    ])

    @Test func restrictedRenormalizesOverCandidates() {
        let r = Self.sample.restricted(to: ["en", "zh", "de", "es"])
        #expect(r.count == 4)
        #expect(abs(r.values.reduce(0, +) - 1) < 1e-6)
        #expect(abs((r["en"] ?? 0) - 0.5 / 0.9) < 1e-6)
        #expect(abs((r["de"] ?? 0) - 0.2 / 0.9) < 1e-6)
        #expect(r["fr"] == nil)
    }

    @Test func restrictedIgnoresUnknownAndDuplicateCodes() {
        let r = Self.sample.restricted(to: ["de", "xx", "de", "es"])
        #expect(r.keys.sorted() == ["de", "es"])
        #expect(abs((r["de"] ?? 0) - 2.0 / 3.0) < 1e-6)
    }

    @Test func restrictedIsEmptyWithoutProbabilityMass() {
        #expect(Self.sample.restricted(to: []).isEmpty)
        #expect(Self.sample.restricted(to: ["xx"]).isEmpty)
        let zeros = LanguageProbabilities(probabilities: ["en": 0, "de": 0])
        #expect(zeros.restricted(to: ["en", "de"]).isEmpty)
    }

    @Test func bestPicksHighestRestrictedProbability() throws {
        let best = try #require(Self.sample.best(among: ["zh", "de", "es"]))
        #expect(best.code == "de")
        #expect(abs(best.confidence - 0.5) < 1e-6)
        #expect(Self.sample.best(among: ["xx"]) == nil)
    }

    @Test func bestBreaksTiesByCandidateOrder() throws {
        #expect(try #require(Self.sample.best(among: ["zh", "es"])).code == "zh")
        #expect(try #require(Self.sample.best(among: ["es", "zh"])).code == "es")
    }

    @Test func averagingKeepsOneOutlierWindowFromVetoing() throws {
        let windows: [[String: Float]] = [
            ["en": 0.05, "de": 0.95],
            ["en": 0.10, "de": 0.90],
            ["en": 0.999, "de": 0.001],  // e.g. an English phrase in a German meeting
        ]
        let averaged = averageDistributions(windows, codes: ["en", "de"])
        let best = try #require(bestCandidate(in: averaged, order: ["en", "de"]))
        #expect(best.code == "de")
        #expect(abs(best.confidence - (0.95 + 0.90 + 0.001) / 3) < 1e-6)
        #expect(averageDistributions([], codes: ["en"]).isEmpty)
    }

    @Test func windowSliceClampsToBuffer() {
        let n = WhisperAudioConfig.chunkLengthSamples
        let samples = [Float](repeating: 1, count: n + 10)
        #expect(Transcriber.windowSlice(samples, start: 0).count == n)
        #expect(Transcriber.windowSlice(samples, start: n).count == 10)
        #expect(Transcriber.windowSlice(samples, start: n + 50).isEmpty)
        #expect(Transcriber.windowSlice(samples, start: -5).count == n)
    }

    @Test func rootMeanSquareOfSilenceIsZero() {
        #expect(rootMeanSquare([Float](repeating: 0, count: 100)[...]) == 0)
        #expect(rootMeanSquare([][...]) == 0)
        #expect(abs(rootMeanSquare([0.5, -0.5][...]) - 0.5) < 1e-6)
    }
}
