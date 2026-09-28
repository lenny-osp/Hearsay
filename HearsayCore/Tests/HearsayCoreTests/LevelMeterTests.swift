import Foundation
import Testing
@testable import HearsayCore

@Suite struct LevelMeterTests {
    @Test func constantsMatchPython() {
        #expect(LevelMeter.floorDB == -60)
        #expect(LevelMeter.width == 20)
        #expect(LevelMeter.refreshSeconds == 0.2)
        #expect(LevelMeter.silenceThresholdDB == -55)
        #expect(LevelMeter.silenceWarningSeconds == 5)
    }

    // test_pcm_rms_db_measures_silence_and_full_scale
    @Test func rmsDBMeasuresSilenceAndFullScale() throws {
        #expect(LevelMeter.rmsDB(int16Samples: Array(repeating: 0, count: 160)) == -.infinity)
        let fullScale = try #require(LevelMeter.rmsDB(int16Samples: Array(repeating: -32768, count: 160)))
        #expect(abs(fullScale - 0.0) < 1e-6)
        let halfScale = try #require(LevelMeter.rmsDB(int16Samples: Array(repeating: 16384, count: 160)))
        #expect(abs(halfScale - -6.0) < 0.05)
        #expect(LevelMeter.rmsDB(int16Samples: []) == nil)
    }

    @Test func rmsDBForFloatSamples() throws {
        #expect(LevelMeter.rmsDB(floatSamples: Array(repeating: 0, count: 160)) == -.infinity)
        let fullScale = try #require(LevelMeter.rmsDB(floatSamples: Array(repeating: -1, count: 160)))
        #expect(abs(fullScale) < 1e-6)
        let halfScale = try #require(LevelMeter.rmsDB(floatSamples: Array(repeating: 0.5, count: 160)))
        #expect(abs(halfScale - -6.0) < 0.05)
        #expect(LevelMeter.rmsDB(floatSamples: []) == nil)
    }

    // test_format_level_bar_spans_floor_to_full_scale
    @Test func levelBarSpansFloorToFullScale() {
        let width = 10
        let empty = String(repeating: "\u{2591}", count: width)
        let full = String(repeating: "\u{2588}", count: width)
        #expect(LevelMeter.levelBar(rmsDB: -.infinity, width: width) == empty)
        #expect(LevelMeter.levelBar(rmsDB: LevelMeter.floorDB - 5, width: width) == empty)
        #expect(LevelMeter.levelBar(rmsDB: nil, width: width) == empty)
        #expect(LevelMeter.levelBar(rmsDB: 0.0, width: width) == full)
        #expect(LevelMeter.levelBar(rmsDB: 5.0, width: width) == full)
        let half = LevelMeter.levelBar(rmsDB: LevelMeter.floorDB / 2, width: width)
        #expect(half.count == width)
        #expect(half.filter { $0 == "\u{2588}" }.count == width / 2)
        #expect(LevelMeter.levelBar(rmsDB: -30).count == LevelMeter.width)
    }

    @Test func levelBarRoundsHalfToEvenLikePython() {
        // (x + 60) / 60 * 4 == 2.5 at x = -22.5; Python round(2.5) == 2.
        let bar = LevelMeter.levelBar(rmsDB: -22.5, width: 4)
        #expect(bar == "\u{2588}\u{2588}\u{2591}\u{2591}")
    }

    @Test func formatElapsed() {
        #expect(LevelMeter.formatElapsed(83) == "00:01:23")
        #expect(LevelMeter.formatElapsed(3661.9) == "01:01:01")
        #expect(LevelMeter.formatElapsed(-5) == "00:00:00")
    }

    // test_format_meter_line_switches_to_a_silence_warning
    @Test func meterLineSwitchesToASilenceWarning() {
        let active = LevelMeter.meterLine(elapsedSeconds: 83, rmsDB: -18.0, silentSeconds: 0)
        #expect(active.contains("00:01:23"))
        #expect(active.contains("-18.0 dBFS"))
        #expect(!active.contains("silent"))

        let silent = LevelMeter.meterLine(
            elapsedSeconds: 83, rmsDB: -.infinity, silentSeconds: LevelMeter.silenceWarningSeconds
        )
        #expect(silent.contains("silent for"))
        #expect(silent.contains("check the input device"))
        #expect(silent.contains("-inf"))
    }

    // test_meter_tracks_peak_sound_and_silence_duration
    @Test func meterTracksPeakSoundAndSilenceDuration() {
        var meter = LevelMeter()
        var now: TimeInterval = 100
        meter.observe(rmsDB: -70.0, at: now)
        #expect(!meter.heardSound)
        now += 1.0
        meter.observe(rmsDB: -12.0, at: now)
        #expect(meter.heardSound)
        #expect(meter.peakDB == -12.0)
        #expect(!meter.isSilenceWarning)

        now += LevelMeter.silenceWarningSeconds + 1
        let redrew = meter.observe(rmsDB: -.infinity, at: now)
        #expect(redrew)
        #expect(meter.isSilenceWarning)
        #expect(meter.silenceSeconds == LevelMeter.silenceWarningSeconds + 1)
        #expect(meter.elapsedSeconds == LevelMeter.silenceWarningSeconds + 2)
        #expect(meter.peakDB == -12.0)
        #expect(meter.observations == 3)
        let line = LevelMeter.meterLine(
            elapsedSeconds: meter.elapsedSeconds, rmsDB: meter.lastRMSDB, silentSeconds: meter.silenceSeconds
        )
        #expect(line.contains("silent for"))
    }

    // test_meter_refresh_interval_limits_redraws
    @Test func meterRefreshIntervalLimitsRedraws() {
        var meter = LevelMeter(refreshInterval: 1.0)
        var now: TimeInterval = 0
        #expect(meter.shouldRedraw(at: now))
        let r1 = meter.observe(rmsDB: -20.0, at: now)
        #expect(r1)
        #expect(!meter.shouldRedraw(at: now))
        let r2 = meter.observe(rmsDB: -21.0, at: now)
        #expect(!r2)
        now += 1.5
        #expect(meter.shouldRedraw(at: now))
        let r3 = meter.observe(rmsDB: -22.0, at: now)
        #expect(r3)
        #expect(meter.observations == 3)
        // force redraws regardless of the interval, like the Python meter.
        let r4 = meter.observe(rmsDB: -22.0, at: now, force: true)
        #expect(r4)
    }

    @Test func defaultRefreshIntervalIsTwoTenths() {
        var meter = LevelMeter()
        let r5 = meter.observe(rmsDB: -20, at: 0)
        #expect(r5)
        let r6 = meter.observe(rmsDB: -20, at: 0.1)
        #expect(!r6)
        let r7 = meter.observe(rmsDB: -20, at: 0.25)
        #expect(r7)
    }
}
