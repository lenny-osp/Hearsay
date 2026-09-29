using Hearsay.App.Features.MenuBar;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;

namespace Hearsay.App.Tests;

/// <summary>
/// The tray icon's picture and tooltip rule (PLAN.md 18.3, "Tray status"),
/// the Windows form of <c>MenuBarLabel</c> in
/// mac/Hearsay/Features/MenuBar/MenuBarView.swift: plain when idle, red while
/// recording, the pause variant while paused, always plain with the status
/// setting off. Constructing <see cref="TrayIcon"/> shows nothing; only
/// <see cref="TrayIcon.SetVisible"/> would.
/// </summary>
public sealed class TrayIconTests : IDisposable
{
    private readonly ScratchFolder folder = new();
    private readonly AppSettings settings;
    private readonly RecordingStatus recording = new();
    private readonly TrayIcon tray;

    public TrayIconTests()
    {
        settings = new AppSettings(folder.Path);
        tray = new TrayIcon(settings, recording, () => { }, () => { });
    }

    public void Dispose()
    {
        tray.Dispose();
        folder.Dispose();
    }

    [Theory]
    [InlineData(nameof(RecordingPhase.Idle), "Hearsay.ico")]
    [InlineData(nameof(RecordingPhase.Starting), "Hearsay.ico")]
    [InlineData(nameof(RecordingPhase.Recording), "Hearsay-recording.ico")]
    [InlineData(nameof(RecordingPhase.Paused), "Hearsay-paused.ico")]
    [InlineData(nameof(RecordingPhase.Stopping), "Hearsay.ico")]
    [InlineData(nameof(RecordingPhase.Transcribing), "Hearsay.ico")]
    public void IconFollowsThePhase(string phaseName, string icon)
    {
        var phase = Enum.Parse<RecordingPhase>(phaseName);
        Assert.True(settings.MenuBarShowsStatus);
        recording.Update(phase, TimeSpan.FromSeconds(5), null);
        Assert.Equal(icon, tray.CurrentIconName);
        Assert.False(tray.IsVisible);
    }

    [Theory]
    [InlineData(nameof(RecordingPhase.Recording))]
    [InlineData(nameof(RecordingPhase.Paused))]
    [InlineData(nameof(RecordingPhase.Transcribing))]
    public void StatusOffKeepsThePlainIconAndTooltip(string phaseName)
    {
        var phase = Enum.Parse<RecordingPhase>(phaseName);
        settings.MenuBarShowsStatus = false;
        recording.Update(phase, TimeSpan.FromSeconds(65), 0.5);
        Assert.Equal("Hearsay.ico", tray.CurrentIconName);
        Assert.Equal("Hearsay", tray.CurrentTooltip);
    }

    [Fact]
    public void TooltipShowsTheElapsedTimeWhileCapturing()
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        Assert.Equal("Hearsay", tray.CurrentTooltip);
        recording.Update(RecordingPhase.Recording, TimeSpan.FromSeconds(3_725), null);
        Assert.Equal("Hearsay: Recording " + LevelMeter.FormatElapsed(3_725), tray.CurrentTooltip);
        Assert.Equal("Hearsay: Recording 01:02:05", tray.CurrentTooltip);
        recording.Update(RecordingPhase.Paused, TimeSpan.FromSeconds(3_725), null);
        Assert.Equal("Hearsay: Paused 01:02:05", tray.CurrentTooltip);
        recording.Update(RecordingPhase.Starting, TimeSpan.Zero, null);
        Assert.Equal("Hearsay", tray.CurrentTooltip);
        recording.Update(RecordingPhase.Stopping, TimeSpan.Zero, null);
        Assert.Equal("Hearsay", tray.CurrentTooltip);
    }

    [Fact]
    public void TooltipShowsTheFinalPassPercentage()
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        recording.Update(RecordingPhase.Transcribing, TimeSpan.FromSeconds(60), 0.425);
        Assert.Equal("Hearsay: Transcribing… 43%", tray.CurrentTooltip);
    }

    [Fact]
    public void TooltipIsTranslated()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German);
        recording.Update(RecordingPhase.Recording, TimeSpan.FromSeconds(5), null);
        var state = Translations.Format(InterfaceLanguage.German, "app", "Recording %@", "00:00:05");
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "windows", "Hearsay: %@", state), tray.CurrentTooltip);
    }
}
