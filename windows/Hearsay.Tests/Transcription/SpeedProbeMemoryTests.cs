using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>
/// The re-probe rule of PLAN.md 4.12 "As built (Windows, 2026-10-03)": a fast
/// result is remembered for the process, a "too slow" one for 10 minutes.
/// Windows only (the Mac has no speed probe), so there is no Swift test.
/// </summary>
public sealed class SpeedProbeMemoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TooSlowIsRememberedForTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), SpeedProbeMemory.TooSlowMemory);
        Assert.True(SpeedProbeMemory.IsRemembered(false, Start, Start));
        Assert.True(SpeedProbeMemory.IsRemembered(false, Start, Start + TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59)));
        Assert.True(SpeedProbeMemory.IsRemembered(false, Start, Start + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void TooSlowExpiresAfterTenMinutes()
    {
        Assert.False(SpeedProbeMemory.IsRemembered(false, Start, Start + TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1)));
        Assert.False(SpeedProbeMemory.IsRemembered(false, Start, Start + TimeSpan.FromHours(3)));
    }

    [Fact]
    public void FastIsRememberedForever()
    {
        Assert.True(SpeedProbeMemory.IsRemembered(true, Start, Start));
        Assert.True(SpeedProbeMemory.IsRemembered(true, Start, Start + TimeSpan.FromDays(30)));
    }

    [Fact]
    public void ClockGoingBackwardsDoesNotExpireAResult()
    {
        Assert.True(SpeedProbeMemory.IsRemembered(false, Start, Start - TimeSpan.FromHours(1)));
    }
}
