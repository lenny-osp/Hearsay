import Foundation

/// Port of `StreamingLevelMeter` and its helpers from `run_whisper.py`
/// (`pcm_rms_db`, `format_level_bar`, `format_elapsed`, `format_meter_line`).
///
/// `LevelMeter` is a value type. Each recording source owns its own copy and
/// mutates it from a single context (the audio pipeline or the view model that
/// receives its levels); copies are independent, so there is no shared mutable
/// state across threads. Time is always injected as a `TimeInterval` so the
/// meter never reads the clock and tests stay deterministic.
///
/// Console rendering (`_render`, `finish`) is intentionally not ported: the app
/// draws the bar in SwiftUI from the state exposed here.
public struct LevelMeter: Sendable, Equatable {
    // Constants copied from run_whisper.py (METER_* and SILENCE_*).
    public static let floorDB: Double = -60.0
    public static let width: Int = 20
    public static let refreshSeconds: TimeInterval = 0.2
    public static let silenceThresholdDB: Double = -55.0
    public static let silenceWarningSeconds: TimeInterval = 5.0

    public let refreshInterval: TimeInterval

    public private(set) var heardSound = false
    public private(set) var peakDB: Double = -.infinity
    public private(set) var observations = 0
    /// The most recent level passed to `observe`, `nil` when none was measurable.
    public private(set) var lastRMSDB: Double?

    private var start: TimeInterval?
    private var lastRender: TimeInterval?
    private var lastSoundAt: TimeInterval?
    private var lastObservedAt: TimeInterval?

    public init(refreshInterval: TimeInterval = LevelMeter.refreshSeconds) {
        self.refreshInterval = refreshInterval
    }

    // MARK: - Instance API

    /// Seconds since the first observation, as of the latest observation.
    public var elapsedSeconds: TimeInterval {
        guard let start, let lastObservedAt else { return 0 }
        return lastObservedAt - start
    }

    /// Seconds since sound above the silence threshold was last observed, as
    /// of the latest observation (the first observation counts as sound, like
    /// the Python meter).
    public var silenceSeconds: TimeInterval {
        guard let lastObservedAt else { return 0 }
        return lastObservedAt - (lastSoundAt ?? lastObservedAt)
    }

    /// True once `silenceSeconds` reaches `silenceWarningSeconds`.
    public var isSilenceWarning: Bool {
        silenceSeconds >= Self.silenceWarningSeconds
    }

    /// Whether a redraw at `time` honors the refresh interval.
    public func shouldRedraw(at time: TimeInterval) -> Bool {
        guard let lastRender else { return true }
        return !(time - lastRender < refreshInterval)
    }

    /// Record one level sample. Returns whether the UI should redraw now; when
    /// it returns `true` the redraw is counted against the refresh interval.
    @discardableResult
    public mutating func observe(rmsDB: Double?, at time: TimeInterval, force: Bool = false) -> Bool {
        if start == nil {
            start = time
            lastSoundAt = time
        }
        lastObservedAt = time
        lastRMSDB = rmsDB
        observations += 1
        if let rmsDB, rmsDB > Self.silenceThresholdDB {
            heardSound = true
            lastSoundAt = time
        }
        if let rmsDB, rmsDB > peakDB {
            peakDB = rmsDB
        }
        if !force && !shouldRedraw(at: time) {
            return false
        }
        lastRender = time
        return true
    }

    // MARK: - Static helpers

    /// dBFS RMS of 16-bit PCM samples. `nil` for no samples, `-infinity` for
    /// digital silence. Port of `pcm_rms_db`.
    public static func rmsDB(int16Samples: [Int16]) -> Double? {
        guard !int16Samples.isEmpty else { return nil }
        var total = 0
        for sample in int16Samples {
            let value = Int(sample)
            total += value * value
        }
        let meanSquare = Double(total) / Double(int16Samples.count)
        if meanSquare <= 0 {
            return -.infinity
        }
        return 10.0 * log10(meanSquare / (32768.0 * 32768.0))
    }

    /// dBFS RMS of Float32 samples where 1.0 is full scale.
    public static func rmsDB(floatSamples: [Float]) -> Double? {
        guard !floatSamples.isEmpty else { return nil }
        var total = 0.0
        for sample in floatSamples {
            let value = Double(sample)
            total += value * value
        }
        let meanSquare = total / Double(floatSamples.count)
        if !(meanSquare > 0) {
            return -.infinity
        }
        return 10.0 * log10(meanSquare)
    }

    /// Fixed-width bar for a level between the floor and 0 dB. Port of
    /// `format_level_bar` (Python `round` is half-to-even, matched here).
    public static func levelBar(rmsDB: Double?, width: Int = LevelMeter.width) -> String {
        let width = max(0, width)
        var filled: Int
        if let rmsDB, !rmsDB.isNaN, rmsDB > floorDB {
            if rmsDB >= 0 {
                filled = width
            } else {
                let fraction = (rmsDB - floorDB) / -floorDB * Double(width)
                filled = Int(fraction.rounded(.toNearestOrEven))
            }
        } else {
            filled = 0
        }
        filled = max(0, min(width, filled))
        return String(repeating: "\u{2588}", count: filled)
            + String(repeating: "\u{2591}", count: width - filled)
    }

    /// Fraction of the bar that is filled, for SwiftUI drawing (0...1).
    public static func levelFraction(rmsDB: Double?) -> Double {
        guard let rmsDB, !rmsDB.isNaN, rmsDB > floorDB else { return 0 }
        if rmsDB >= 0 { return 1 }
        return (rmsDB - floorDB) / -floorDB
    }

    /// `HH:MM:SS` for a non-negative number of seconds. Port of `format_elapsed`.
    public static func formatElapsed(_ seconds: TimeInterval) -> String {
        let total: Int
        if seconds.isFinite, seconds > 0 {
            total = Int(seconds)
        } else {
            total = 0
        }
        return String(format: "%02d:%02d:%02d", total / 3600, total / 60 % 60, total % 60)
    }

    /// The level as printed by the Python meter: `%6.1f`, or `  -inf`.
    public static func formatLevel(rmsDB: Double?) -> String {
        guard let rmsDB, rmsDB != -.infinity else { return "  -inf" }
        return String(format: "%6.1f", rmsDB)
    }

    /// Port of `format_meter_line`, the single-line recording indicator.
    public static func meterLine(
        elapsedSeconds: TimeInterval,
        rmsDB: Double?,
        silentSeconds: TimeInterval = 0,
        width: Int = LevelMeter.width
    ) -> String {
        let bar = levelBar(rmsDB: rmsDB, width: width)
        let level = formatLevel(rmsDB: rmsDB)
        let elapsed = formatElapsed(elapsedSeconds)
        if silentSeconds >= silenceWarningSeconds {
            let silentWhole = silentSeconds.isFinite ? Int(silentSeconds) : 0
            return "\u{26A0}\u{FE0F}  REC \(elapsed)  [\(bar)] \(level) dBFS  "
                + "silent for \(silentWhole)s \u{2014} check the input device"
        }
        return "\u{1F534} REC \(elapsed)  [\(bar)] \(level) dBFS"
    }
}
