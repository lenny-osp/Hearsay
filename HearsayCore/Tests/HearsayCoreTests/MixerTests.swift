import Foundation
import os
import Testing
@testable import HearsayCore

/// Pure tests of `AudioMixer` with synthetic `TimedChunk`s. None of them
/// touches ScreenCaptureKit or needs the Screen Recording permission.
@Suite struct MixerTests {
    static let rate = AudioMixer.sampleRate

    static func sine(amplitude: Float, frequency: Double = 440, count: Int, offset: Int = 0) -> [Float] {
        (0..<count).map { index in
            amplitude * Float(sin(2 * Double.pi * frequency * Double(index + offset) / rate))
        }
    }

    /// Splits `samples` into chunks of `size` stamped from `start` seconds.
    static func chunks(_ samples: [Float], size: Int, start: TimeInterval = 100) -> [TimedChunk] {
        stride(from: 0, to: samples.count, by: size).map { index in
            TimedChunk(
                samples: Array(samples[index..<min(samples.count, index + size)]),
                hostTime: start + Double(index) / rate
            )
        }
    }

    /// Feeds both sources interleaved by host time, then ends both.
    static func run(
        mic: [TimedChunk],
        system: [TimedChunk],
        mixer: AudioMixer = AudioMixer()
    ) -> [MixedChunk] {
        var mixer = mixer
        var output: [MixedChunk] = []
        let events = mic.map { ($0, AudioMixer.Source.mic) } + system.map { ($0, AudioMixer.Source.system) }
        for (chunk, source) in events.sorted(by: { $0.0.hostTime < $1.0.hostTime }) {
            output += mixer.append(chunk, from: source)
        }
        output += mixer.finish(.mic)
        output += mixer.finish(.system)
        #expect(mixer.isFinished)
        return output
    }

    static func flat(_ chunks: [MixedChunk]) -> [Float] {
        chunks.flatMap(\.mixed)
    }

    @Test func inPhaseSinesSumToDoubleBelowTheKnee() {
        let wave = Self.sine(amplitude: 0.2, count: 16_000)
        let output = Self.flat(Self.run(mic: Self.chunks(wave, size: 1600), system: Self.chunks(wave, size: 1600)))
        #expect(output.count == wave.count)
        for index in wave.indices {
            #expect(abs(output[index] - 2 * wave[index]) < 1e-6)
        }
        #expect(abs((output.map(abs).max() ?? 0) - 0.4) < 0.001)
    }

    @Test func inPhaseSinesAboveTheKneeAreLimited() {
        let wave = Self.sine(amplitude: 0.45, count: 16_000)
        let output = Self.flat(Self.run(mic: Self.chunks(wave, size: 1600), system: Self.chunks(wave, size: 1600)))
        #expect(output.count == wave.count)
        let peak = output.map(abs).max() ?? 0
        // About double (0.9) before limiting, then pulled below it.
        #expect(peak > 0.8)
        #expect(peak < 0.9)
        #expect(output.allSatisfy { abs($0) < 1 })
        for index in wave.indices {
            #expect(abs(output[index] - AudioMixer.softLimit(2 * wave[index])) < 1e-6)
        }
    }

    @Test func constantNearFullScaleOnBothStaysWithinOne() {
        let constant = [Float](repeating: 0.9, count: 8000)
        let output = Self.flat(Self.run(mic: Self.chunks(constant, size: 800), system: Self.chunks(constant, size: 800)))
        #expect(output.count == 8000)
        #expect(output.allSatisfy { $0 <= 1.0 && $0 > 0.9 })
        let negative = constant.map { -$0 }
        let negativeOutput = Self.flat(Self.run(
            mic: Self.chunks(negative, size: 800), system: Self.chunks(negative, size: 800)
        ))
        #expect(negativeOutput.allSatisfy { $0 >= -1.0 && $0 < -0.9 })
    }

    @Test func softLimitIsIdentityBelowKneeAndBounded() {
        #expect(AudioMixer.softLimit(0.3) == 0.3)
        #expect(AudioMixer.softLimit(-0.5) == -0.5)
        #expect(AudioMixer.softLimit(1.8) < 1)
        #expect(AudioMixer.softLimit(-1.8) > -1)
        #expect(AudioMixer.softLimit(100) <= 1)
        #expect(AudioMixer.softLimit(0.6) < 0.6)
        #expect(AudioMixer.softLimit(0.6) > AudioMixer.softLimit(0.55))
    }

    @Test func sourceStartingLateIsPaddedWithSilenceNotDropped() {
        let mic = Self.sine(amplitude: 0.2, count: 16_000)
        let late = 4800 // 0.3 s
        let system = [Float](repeating: 0.1, count: 16_000 - late)
        let blocks = Self.run(
            mic: Self.chunks(mic, size: 1600),
            system: Self.chunks(system, size: 1600, start: 100 + 0.3)
        )
        let output = Self.flat(blocks)
        #expect(output.count == 16_000)
        for index in 0..<late {
            #expect(abs(output[index] - mic[index]) < 1e-6)
        }
        for index in late..<16_000 {
            #expect(abs(output[index] - (mic[index] + 0.1)) < 1e-6)
        }
        // Every system sample made it into the mix.
        let systemEnergy = zip(output, mic).reduce(0) { $0 + Double($1.0 - $1.1) }
        #expect(abs(systemEnergy - 0.1 * Double(system.count)) < 0.01)
        #expect(blocks[0].systemRMSDB == nil)
        #expect(blocks[0].micRMSDB != nil)
        #expect(blocks[4].systemRMSDB != nil)
    }

    @Test func lateSourceArrivingFirstStillLinesUp() {
        // The system chunk stamped 0.3 s later is delivered before any mic
        // chunk; the anchor moves back so the mic keeps its start.
        let mic = [Float](repeating: 0.2, count: 3200)
        let system = [Float](repeating: 0.1, count: 1600)
        var mixer = AudioMixer()
        var output: [MixedChunk] = []
        output += mixer.append(TimedChunk(samples: system, hostTime: 10.3), from: .system)
        output += mixer.append(TimedChunk(samples: mic, hostTime: 10.0), from: .mic)
        output += mixer.finish(.system)
        output += mixer.finish(.mic)
        let flat = Self.flat(output)
        #expect(flat.count == 6400)
        #expect(flat[0] == 0.2)
        #expect(abs(flat[4800] - 0.1) < 1e-6)
        #expect(abs(flat[3199] - 0.2) < 1e-6)
        #expect(flat[3200] == 0)
    }

    @Test func sourceEndingEarlyLeavesTheOtherUnchanged() {
        let mic = Self.sine(amplitude: 0.3, count: 32_000)
        let system = Self.sine(amplitude: 0.1, frequency: 1000, count: 8000)
        var mixer = AudioMixer()
        var output: [MixedChunk] = []
        let micChunks = Self.chunks(mic, size: 1600)
        let systemChunks = Self.chunks(system, size: 1600)
        for (index, chunk) in micChunks.enumerated() {
            output += mixer.append(chunk, from: .mic)
            if index < systemChunks.count {
                output += mixer.append(systemChunks[index], from: .system)
            } else if index == systemChunks.count {
                output += mixer.finish(.system)
            }
        }
        output += mixer.finish(.mic)
        let flat = Self.flat(output)
        #expect(flat.count == mic.count)
        for index in 0..<8000 {
            #expect(abs(flat[index] - (mic[index] + system[index])) < 1e-6)
        }
        for index in 8000..<mic.count {
            #expect(flat[index] == mic[index])
        }
        #expect(output.last?.systemRMSDB == nil)
    }

    @Test func micOnlyPassesThroughBitIdentical() {
        let mic = Self.sine(amplitude: 0.95, count: 10_000)
        var mixer = AudioMixer(sources: [.mic])
        var output: [MixedChunk] = []
        for chunk in Self.chunks(mic, size: 1024) {
            output += mixer.append(chunk, from: .mic)
        }
        // No waiting on an absent source: full blocks come out right away.
        #expect(output.count == 10_000 / 1600)
        output += mixer.finish(.mic)
        #expect(Self.flat(output) == mic)
        #expect(output.allSatisfy { $0.systemRMSDB == nil })
    }

    @Test func outputChunksAreAlways1600ExceptTheFinalPartial() {
        let total = 12_345
        let mic = [Float](repeating: 0.1, count: total)
        let system = [Float](repeating: 0.05, count: 9000)
        let blocks = Self.run(
            mic: Self.chunks(mic, size: 1000),
            system: Self.chunks(system, size: 441, start: 100 + 0.05)
        )
        #expect(blocks.map(\.mixed.count).reduce(0, +) == total)
        for block in blocks.dropLast() {
            #expect(block.mixed.count == AudioMixer.chunkSize)
        }
        #expect(blocks.last?.mixed.count == total % AudioMixer.chunkSize)
    }

    @Test func laggingSourceIsPaddedUpToTheCapInsteadOfBlocking() {
        var mixer = AudioMixer()
        var output: [MixedChunk] = []
        output += mixer.append(TimedChunk(samples: [Float](repeating: 0.1, count: 160), hostTime: 5), from: .system)
        let mic = [Float](repeating: 0.2, count: 32_000)
        for chunk in Self.chunks(mic, size: 1600, start: 5) {
            output += mixer.append(chunk, from: .mic)
        }
        // 2 s of mic, the system stuck at 0.01 s: output runs to 1.5 s.
        let emitted = output.map(\.mixed.count).reduce(0, +)
        #expect(emitted == 32_000 - AudioMixer.maxLagSamples)
        // System audio for time already emitted is dropped; the rest lines up.
        output += mixer.append(
            TimedChunk(samples: [Float](repeating: 0.1, count: 32_000 - 160), hostTime: 5.01),
            from: .system
        )
        output += mixer.finish(.mic)
        output += mixer.finish(.system)
        let flat = Self.flat(output)
        #expect(flat.count == 32_000)
        #expect(abs(flat[100] - 0.3) < 1e-6)
        #expect(flat[1000] == 0.2)
        #expect(abs(flat[31_000] - 0.3) < 1e-6)
    }

    @Test func timestampJitterKeepsChunksContiguous() {
        let mic = [Float](repeating: 0.2, count: 16_000)
        let jitter: [TimeInterval] = [0, 0.002, -0.001, 0.003, -0.002]
        let micChunks = Self.chunks(mic, size: 1600).enumerated().map { index, chunk in
            TimedChunk(samples: chunk.samples, hostTime: chunk.hostTime + jitter[index % jitter.count])
        }
        let output = Self.flat(Self.run(mic: micChunks, system: []))
        #expect(output == mic)
    }

    @Test func gapBeyondToleranceIsFilledWithSilence() {
        var mixer = AudioMixer(sources: [.mic])
        var output: [MixedChunk] = []
        output += mixer.append(TimedChunk(samples: [Float](repeating: 0.2, count: 1600), hostTime: 1), from: .mic)
        output += mixer.append(TimedChunk(samples: [Float](repeating: 0.2, count: 1600), hostTime: 1.2), from: .mic)
        output += mixer.finish(.mic)
        let flat = Self.flat(output)
        #expect(flat.count == 4800)
        #expect(flat[1599] == 0.2)
        #expect(flat[1600] == 0)
        #expect(flat[3199] == 0)
        #expect(flat[3200] == 0.2)
    }

    @Test func stampsFromAForeignClockAreClampedNotPaddedForHours() {
        var mixer = AudioMixer()
        var output: [MixedChunk] = []
        output += mixer.append(TimedChunk(samples: [Float](repeating: 0.2, count: 1600), hostTime: 100), from: .mic)
        output += mixer.append(TimedChunk(samples: [Float](repeating: 0.1, count: 1600), hostTime: 90_000), from: .system)
        output += mixer.finish(.mic)
        output += mixer.finish(.system)
        let total = output.map(\.mixed.count).reduce(0, +)
        #expect(total == 1600 + AudioMixer.clockSanitySamples + 1600)
    }

    @Test func perSourceLevelsAreReported() {
        let blocks = Self.run(
            mic: [TimedChunk(samples: [Float](repeating: 0.5, count: 1600), hostTime: 0)],
            system: [TimedChunk(samples: [Float](repeating: 0, count: 1600), hostTime: 0)]
        )
        #expect(blocks.count == 1)
        #expect(abs((blocks[0].micRMSDB ?? 0) - 20 * log10(0.5)) < 1e-6)
        #expect(blocks[0].systemRMSDB == -.infinity)
    }

    @Test func mixStreamsFinishesAfterBothSources() async {
        let (micStream, micContinuation) = AsyncStream<TimedChunk>.makeStream()
        let (systemStream, systemContinuation) = AsyncStream<TimedChunk>.makeStream()
        let ended = EndedSources()
        let mixed = AudioMixer.mix(mic: micStream, system: systemStream) { source in
            ended.insert(source)
        }
        let mic = [Float](repeating: 0.2, count: 4000)
        for chunk in Self.chunks(mic, size: 1000) {
            micContinuation.yield(chunk)
        }
        micContinuation.finish()
        systemContinuation.yield(TimedChunk(samples: [Float](repeating: 0.1, count: 1600), hostTime: 100))
        systemContinuation.finish()
        var blocks: [MixedChunk] = []
        for await block in mixed {
            blocks.append(block)
        }
        let flat = Self.flat(blocks)
        #expect(flat.count == 4000)
        #expect(abs(flat[0] - 0.3) < 1e-6)
        #expect(flat[1600] == 0.2)
        #expect(ended.sources == [.mic, .system])
    }

    @Test func mixStreamsWithMicOnly() async {
        let (micStream, micContinuation) = AsyncStream<TimedChunk>.makeStream()
        let mixed = AudioMixer.mix(mic: micStream, system: nil)
        let mic = Self.sine(amplitude: 0.5, count: 5000)
        for chunk in Self.chunks(mic, size: 700) {
            micContinuation.yield(chunk)
        }
        micContinuation.finish()
        var flat: [Float] = []
        for await block in mixed {
            flat += block.mixed
        }
        #expect(flat == mic)
    }

    @Test func noSourcesFinishesImmediately() async {
        var count = 0
        for await _ in AudioMixer.mix(mic: nil, system: nil) {
            count += 1
        }
        #expect(count == 0)
    }

    // MARK: - Recorder output

    @Test func fanoutRemovesPausedTimeFromHostTime() async {
        let fanout = ChunkFanout()
        let timed = fanout.timedSamples
        fanout.yield([0.1], hostTime: 10)
        fanout.pause(at: 11)
        fanout.yield([0.2], hostTime: 12) // dropped while paused
        fanout.resume(at: 14)
        fanout.yield([0.3], hostTime: 15)
        fanout.finish()
        var received: [TimedChunk] = []
        for await chunk in timed {
            received.append(chunk)
        }
        #expect(received == [
            TimedChunk(samples: [0.1], hostTime: 10),
            TimedChunk(samples: [0.3], hostTime: 12),
        ])
    }

    @Test func fanoutFirstAccessedStreamClaimsTheChunks() async {
        let fanout = ChunkFanout()
        fanout.yield([0.5], hostTime: 1)
        let plain = fanout.samples
        let timed = fanout.timedSamples
        fanout.yield([0.6], hostTime: 2)
        fanout.finish()
        var plainChunks: [[Float]] = []
        for await chunk in plain {
            plainChunks.append(chunk)
        }
        var timedCount = 0
        for await _ in timed {
            timedCount += 1
        }
        #expect(plainChunks == [[0.5], [0.6]])
        #expect(timedCount == 0)
    }
}

private final class EndedSources: Sendable {
    private let stored = OSAllocatedUnfairLock<Set<AudioMixer.Source>>(initialState: [])

    func insert(_ source: AudioMixer.Source) {
        stored.withLock { _ = $0.insert(source) }
    }

    var sources: Set<AudioMixer.Source> {
        stored.withLock { $0 }
    }
}
