using Hearsay.Core.Audio;

namespace Hearsay.Tests.Audio;

/// <summary>Port of mac/HearsayCore/Tests/HearsayCoreTests/LevelMeterTests.swift.</summary>
public class LevelMeterTests
{
    private const string Full = "█";
    private const string Empty = "░";

    [Fact]
    public void ConstantsMatchPython()
    {
        Assert.Equal(-60, LevelMeter.FloorDB);
        Assert.Equal(20, LevelMeter.Width);
        Assert.Equal(0.2, LevelMeter.RefreshSeconds);
        Assert.Equal(-55, LevelMeter.SilenceThresholdDB);
        Assert.Equal(5, LevelMeter.SilenceWarningSeconds);
    }

    // test_pcm_rms_db_measures_silence_and_full_scale
    [Fact]
    public void RmsDBMeasuresSilenceAndFullScale()
    {
        Assert.Equal(double.NegativeInfinity, LevelMeter.RmsDB(new short[160].AsSpan()));
        var fullScale = LevelMeter.RmsDB(Enumerable.Repeat((short)-32768, 160).ToArray().AsSpan());
        Assert.NotNull(fullScale);
        Assert.True(Math.Abs(fullScale.Value - 0.0) < 1e-6);
        var halfScale = LevelMeter.RmsDB(Enumerable.Repeat((short)16384, 160).ToArray().AsSpan());
        Assert.NotNull(halfScale);
        Assert.True(Math.Abs(halfScale.Value - -6.0) < 0.05);
        Assert.Null(LevelMeter.RmsDB(ReadOnlySpan<short>.Empty));
    }

    [Fact]
    public void RmsDBForFloatSamples()
    {
        Assert.Equal(double.NegativeInfinity, LevelMeter.RmsDB(new float[160].AsSpan()));
        var fullScale = LevelMeter.RmsDB(Enumerable.Repeat(-1f, 160).ToArray().AsSpan());
        Assert.NotNull(fullScale);
        Assert.True(Math.Abs(fullScale.Value) < 1e-6);
        var halfScale = LevelMeter.RmsDB(Enumerable.Repeat(0.5f, 160).ToArray().AsSpan());
        Assert.NotNull(halfScale);
        Assert.True(Math.Abs(halfScale.Value - -6.0) < 0.05);
        Assert.Null(LevelMeter.RmsDB(ReadOnlySpan<float>.Empty));
    }

    // test_format_level_bar_spans_floor_to_full_scale
    [Fact]
    public void LevelBarSpansFloorToFullScale()
    {
        const int width = 10;
        var empty = string.Concat(Enumerable.Repeat(Empty, width));
        var full = string.Concat(Enumerable.Repeat(Full, width));
        Assert.Equal(empty, LevelMeter.LevelBar(double.NegativeInfinity, width));
        Assert.Equal(empty, LevelMeter.LevelBar(LevelMeter.FloorDB - 5, width));
        Assert.Equal(empty, LevelMeter.LevelBar(null, width));
        Assert.Equal(full, LevelMeter.LevelBar(0.0, width));
        Assert.Equal(full, LevelMeter.LevelBar(5.0, width));
        var half = LevelMeter.LevelBar(LevelMeter.FloorDB / 2, width);
        Assert.Equal(width, half.Length);
        Assert.Equal(width / 2, half.Count(c => c == '█'));
        Assert.Equal(LevelMeter.Width, LevelMeter.LevelBar(-30).Length);
    }

    [Fact]
    public void LevelBarRoundsHalfToEvenLikePython()
    {
        // (x + 60) / 60 * 4 == 2.5 at x = -22.5; Python round(2.5) == 2.
        Assert.Equal(Full + Full + Empty + Empty, LevelMeter.LevelBar(-22.5, 4));
    }

    [Fact]
    public void FormatElapsed()
    {
        Assert.Equal("00:01:23", LevelMeter.FormatElapsed(83));
        Assert.Equal("01:01:01", LevelMeter.FormatElapsed(3661.9));
        Assert.Equal("00:00:00", LevelMeter.FormatElapsed(-5));
    }

    // Not in the Swift suite: pins the printf spellings of %6.1f.
    [Fact]
    public void FormatLevelMatchesPrintf()
    {
        Assert.Equal(" -18.0", LevelMeter.FormatLevel(-18.0));
        Assert.Equal("  -inf", LevelMeter.FormatLevel(double.NegativeInfinity));
        Assert.Equal("  -inf", LevelMeter.FormatLevel(null));
        Assert.Equal("-100.2", LevelMeter.FormatLevel(-100.25));
        Assert.Equal("  -0.0", LevelMeter.FormatLevel(-0.04));
        Assert.Equal("   inf", LevelMeter.FormatLevel(double.PositiveInfinity));
    }

    // test_format_meter_line_switches_to_a_silence_warning
    [Fact]
    public void MeterLineSwitchesToASilenceWarning()
    {
        var active = LevelMeter.MeterLine(83, -18.0, 0);
        Assert.Contains("00:01:23", active, StringComparison.Ordinal);
        Assert.Contains("-18.0 dBFS", active, StringComparison.Ordinal);
        Assert.DoesNotContain("silent", active, StringComparison.Ordinal);

        var silent = LevelMeter.MeterLine(83, double.NegativeInfinity, LevelMeter.SilenceWarningSeconds);
        Assert.Contains("silent for", silent, StringComparison.Ordinal);
        Assert.Contains("check the input device", silent, StringComparison.Ordinal);
        Assert.Contains("-inf", silent, StringComparison.Ordinal);
    }

    // test_meter_tracks_peak_sound_and_silence_duration
    [Fact]
    public void MeterTracksPeakSoundAndSilenceDuration()
    {
        var meter = new LevelMeter();
        double now = 100;
        meter.Observe(-70.0, now);
        Assert.False(meter.HeardSound);
        now += 1.0;
        meter.Observe(-12.0, now);
        Assert.True(meter.HeardSound);
        Assert.Equal(-12.0, meter.PeakDB);
        Assert.False(meter.IsSilenceWarning);

        now += LevelMeter.SilenceWarningSeconds + 1;
        var redrew = meter.Observe(double.NegativeInfinity, now);
        Assert.True(redrew);
        Assert.True(meter.IsSilenceWarning);
        Assert.Equal(LevelMeter.SilenceWarningSeconds + 1, meter.SilenceSeconds);
        Assert.Equal(LevelMeter.SilenceWarningSeconds + 2, meter.ElapsedSeconds);
        Assert.Equal(-12.0, meter.PeakDB);
        Assert.Equal(3, meter.Observations);
        var line = LevelMeter.MeterLine(meter.ElapsedSeconds, meter.LastRmsDB, meter.SilenceSeconds);
        Assert.Contains("silent for", line, StringComparison.Ordinal);
    }

    // test_meter_refresh_interval_limits_redraws
    [Fact]
    public void MeterRefreshIntervalLimitsRedraws()
    {
        var meter = new LevelMeter(refreshInterval: 1.0);
        double now = 0;
        Assert.True(meter.ShouldRedraw(now));
        Assert.True(meter.Observe(-20.0, now));
        Assert.False(meter.ShouldRedraw(now));
        Assert.False(meter.Observe(-21.0, now));
        now += 1.5;
        Assert.True(meter.ShouldRedraw(now));
        Assert.True(meter.Observe(-22.0, now));
        Assert.Equal(3, meter.Observations);
        // force redraws regardless of the interval, like the Python meter.
        Assert.True(meter.Observe(-22.0, now, force: true));
    }

    [Fact]
    public void DefaultRefreshIntervalIsTwoTenths()
    {
        var meter = new LevelMeter();
        Assert.True(meter.Observe(-20, 0));
        Assert.False(meter.Observe(-20, 0.1));
        Assert.True(meter.Observe(-20, 0.25));
    }
}
