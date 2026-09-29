using System.Globalization;

namespace Hearsay.Core.Audio;

/// <summary>
/// Port of mac/HearsayCore/Sources/HearsayCore/Audio/LevelMeter.swift, itself a
/// port of <c>StreamingLevelMeter</c> and its helpers from <c>run_whisper.py</c>
/// (<c>pcm_rms_db</c>, <c>format_level_bar</c>, <c>format_elapsed</c>,
/// <c>format_meter_line</c>).
/// </summary>
/// <remarks>
/// The Swift type is a value type; here it is a class, so each recording
/// source must own its own instance and mutate it from a single context.
/// Time is always injected as seconds so the meter never reads the clock and
/// tests stay deterministic. Console rendering is not ported: the app draws
/// the bar from the state exposed here.
/// </remarks>
public sealed class LevelMeter
{
    // Constants copied from run_whisper.py (METER_* and SILENCE_*).
    public const double FloorDB = -60.0;
    public const int Width = 20;
    public const double RefreshSeconds = 0.2;
    public const double SilenceThresholdDB = -55.0;
    public const double SilenceWarningSeconds = 5.0;

    private double? start;
    private double? lastRender;
    private double? lastSoundAt;
    private double? lastObservedAt;

    public LevelMeter(double refreshInterval = RefreshSeconds)
    {
        RefreshInterval = refreshInterval;
    }

    public double RefreshInterval { get; }

    public bool HeardSound { get; private set; }

    public double PeakDB { get; private set; } = double.NegativeInfinity;

    public int Observations { get; private set; }

    /// <summary>The most recent level passed to <see cref="Observe"/>, null when none was measurable.</summary>
    public double? LastRmsDB { get; private set; }

    /// <summary>Seconds since the first observation, as of the latest observation.</summary>
    public double ElapsedSeconds =>
        start is double s && lastObservedAt is double last ? last - s : 0;

    /// <summary>
    /// Seconds since sound above the silence threshold was last observed, as
    /// of the latest observation (the first observation counts as sound, like
    /// the Python meter).
    /// </summary>
    public double SilenceSeconds =>
        lastObservedAt is double last ? last - (lastSoundAt ?? last) : 0;

    /// <summary>True once <see cref="SilenceSeconds"/> reaches <see cref="SilenceWarningSeconds"/>.</summary>
    public bool IsSilenceWarning => SilenceSeconds >= SilenceWarningSeconds;

    /// <summary>Whether a redraw at <paramref name="time"/> honors the refresh interval.</summary>
    public bool ShouldRedraw(double time) =>
        lastRender is not double render || !(time - render < RefreshInterval);

    /// <summary>
    /// Records one level sample. Returns whether the UI should redraw now; when
    /// it returns true the redraw is counted against the refresh interval.
    /// </summary>
    public bool Observe(double? rmsDB, double time, bool force = false)
    {
        if (start is null)
        {
            start = time;
            lastSoundAt = time;
        }
        lastObservedAt = time;
        LastRmsDB = rmsDB;
        Observations += 1;
        if (rmsDB is double loud && loud > SilenceThresholdDB)
        {
            HeardSound = true;
            lastSoundAt = time;
        }
        if (rmsDB is double level && level > PeakDB)
        {
            PeakDB = level;
        }
        if (!force && !ShouldRedraw(time))
        {
            return false;
        }
        lastRender = time;
        return true;
    }

    // Static helpers

    /// <summary>
    /// dBFS RMS of 16-bit PCM samples. Null for no samples, negative infinity
    /// for digital silence. Port of <c>pcm_rms_db</c>.
    /// </summary>
    public static double? RmsDB(ReadOnlySpan<short> int16Samples)
    {
        if (int16Samples.IsEmpty)
        {
            return null;
        }
        long total = 0;
        foreach (var sample in int16Samples)
        {
            long value = sample;
            total += value * value;
        }
        var meanSquare = (double)total / int16Samples.Length;
        if (meanSquare <= 0)
        {
            return double.NegativeInfinity;
        }
        return 10.0 * Math.Log10(meanSquare / (32768.0 * 32768.0));
    }

    /// <summary>dBFS RMS of Float32 samples where 1.0 is full scale.</summary>
    public static double? RmsDB(ReadOnlySpan<float> floatSamples)
    {
        if (floatSamples.IsEmpty)
        {
            return null;
        }
        var total = 0.0;
        foreach (var sample in floatSamples)
        {
            double value = sample;
            total += value * value;
        }
        var meanSquare = total / floatSamples.Length;
        if (!(meanSquare > 0))
        {
            return double.NegativeInfinity;
        }
        return 10.0 * Math.Log10(meanSquare);
    }

    /// <summary>
    /// Fixed-width bar for a level between the floor and 0 dB. Port of
    /// <c>format_level_bar</c> (Python <c>round</c> is half-to-even, matched here).
    /// </summary>
    public static string LevelBar(double? rmsDB, int width = Width)
    {
        width = Math.Max(0, width);
        int filled;
        if (rmsDB is double db && !double.IsNaN(db) && db > FloorDB)
        {
            if (db >= 0)
            {
                filled = width;
            }
            else
            {
                var fraction = (db - FloorDB) / -FloorDB * width;
                filled = (int)Math.Round(fraction, MidpointRounding.ToEven);
            }
        }
        else
        {
            filled = 0;
        }
        filled = Math.Max(0, Math.Min(width, filled));
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>Fraction of the bar that is filled, for drawing (0...1).</summary>
    public static double LevelFraction(double? rmsDB)
    {
        if (rmsDB is not double db || double.IsNaN(db) || !(db > FloorDB))
        {
            return 0;
        }
        if (db >= 0)
        {
            return 1;
        }
        return (db - FloorDB) / -FloorDB;
    }

    /// <summary><c>HH:MM:SS</c> for a non-negative number of seconds. Port of <c>format_elapsed</c>.</summary>
    public static string FormatElapsed(double seconds)
    {
        long total = double.IsFinite(seconds) && seconds > 0 ? (long)seconds : 0;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            total / 3600, total / 60 % 60, total % 60);
    }

    /// <summary>The level as printed by the Python meter: <c>%6.1f</c>, or <c>  -inf</c>.</summary>
    public static string FormatLevel(double? rmsDB)
    {
        if (rmsDB is not double db || double.IsNegativeInfinity(db))
        {
            return "  -inf";
        }
        // printf spellings for the values .NET formats differently.
        if (double.IsPositiveInfinity(db))
        {
            return "   inf";
        }
        if (double.IsNaN(db))
        {
            return "   nan";
        }
        // printf rounds an exact tie (x.25, x.75: the only binary values
        // halfway between tenths) to even; .NET rounds it away from zero.
        var quarters = db * 4;
        if (Math.Abs(quarters) < 1e15 && quarters == Math.Floor(quarters) && Math.Abs(quarters % 2) == 1)
        {
            db = Math.Round(db * 10, MidpointRounding.ToEven) / 10;
        }
        return db.ToString("F1", CultureInfo.InvariantCulture).PadLeft(6);
    }

    /// <summary>Port of <c>format_meter_line</c>, the single-line recording indicator.</summary>
    public static string MeterLine(double elapsedSeconds, double? rmsDB, double silentSeconds = 0, int width = Width)
    {
        var bar = LevelBar(rmsDB, width);
        var level = FormatLevel(rmsDB);
        var elapsed = FormatElapsed(elapsedSeconds);
        if (silentSeconds >= SilenceWarningSeconds)
        {
            long silentWhole = double.IsFinite(silentSeconds) ? (long)silentSeconds : 0;
            return "⚠️  REC " + elapsed + "  [" + bar + "] " + level + " dBFS  "
                + "silent for " + silentWhole.ToString(CultureInfo.InvariantCulture)
                + "s — check the input device";
        }
        return "\U0001F534 REC " + elapsed + "  [" + bar + "] " + level + " dBFS";
    }
}
