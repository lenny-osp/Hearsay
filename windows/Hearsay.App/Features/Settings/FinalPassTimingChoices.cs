using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// The rows of the "Transcribe finished recordings" picker in Settings >
/// General > Transcription (PLAN.md 4.9 item 3): the labels and the mapping
/// between a row and <see cref="FinalPassTiming"/>. Port of the model side of
/// <c>FinalPassTimingPicker</c> in mac/Hearsay/Features/Settings/SettingsView.swift
/// (<c>FinalPassTiming.displayName</c> in TranscriptionQueuePolicy.swift).
/// </summary>
internal static class FinalPassTimingChoices
{
    /// <summary>
    /// The picker rows, in order: right away, when no recording is running, and
    /// "When I start them" (<see cref="FinalPassTiming.Manual"/>, PLAN.md 4.11).
    /// </summary>
    public static IReadOnlyList<FinalPassTiming> All { get; } = [FinalPassTiming.Immediate, FinalPassTiming.WhenIdle, FinalPassTiming.Manual];

    /// <summary>The label of <paramref name="timing"/> in the interface language.</summary>
    public static string Label(FinalPassTiming timing) => timing switch
    {
        FinalPassTiming.Immediate => Strings.TimingImmediate,
        FinalPassTiming.Manual => Strings.TimingManual,
        _ => Strings.TimingWhenIdle,
    };

    /// <summary>The row of <paramref name="timing"/>.</summary>
    public static int IndexOf(FinalPassTiming timing)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == timing) return i;
        }
        return -1;
    }

    /// <summary>The timing of row <paramref name="index"/>; null when nothing is selected.</summary>
    public static FinalPassTiming? At(int index) => index >= 0 && index < All.Count ? All[index] : null;
}
