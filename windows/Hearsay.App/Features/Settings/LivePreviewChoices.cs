using Hearsay.Core.Settings;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// The rows of the "Live preview" picker in Settings > General >
/// Transcription (PLAN.md 4.12 and 18.9; the Mac has a switch instead,
/// <c>LivePreviewToggle</c> in mac/Hearsay/Features/Settings/SettingsView.swift):
/// the labels and the mapping between a row and <see cref="LivePreviewMode"/>.
/// </summary>
internal static class LivePreviewChoices
{
    /// <summary>The picker rows, in order: Automatic, Always on, Off.</summary>
    public static IReadOnlyList<LivePreviewMode> All => LivePreviewModes.All;

    /// <summary>The label of <paramref name="mode"/> in the interface language.</summary>
    public static string Label(LivePreviewMode mode) => mode switch
    {
        LivePreviewMode.On => Strings.LivePreviewAlwaysOn,
        LivePreviewMode.Off => Strings.LivePreviewOffRow,
        _ => Strings.LivePreviewAutomatic,
    };

    /// <summary>The row of <paramref name="mode"/>.</summary>
    public static int IndexOf(LivePreviewMode mode)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == mode) return i;
        }
        return -1;
    }

    /// <summary>The mode of row <paramref name="index"/>; null when nothing is selected.</summary>
    public static LivePreviewMode? At(int index) => index >= 0 && index < All.Count ? All[index] : null;
}
