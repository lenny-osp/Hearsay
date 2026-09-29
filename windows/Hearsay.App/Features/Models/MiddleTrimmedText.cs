using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Hearsay.App.Features.Models;

/// <summary>
/// One line of text shortened in the middle ("C:\Users\…\Models") when it
/// does not fit, with the full text in a tooltip: the Mac's
/// <c>.lineLimit(1).truncationMode(.middle)</c> on the models path
/// (mac/Hearsay/Features/Models/ModelManagerView.swift). WinUI's
/// <see cref="TextTrimming"/> only trims at the end, which hides the path's
/// last folder. It never asks for more width than it is given, so the
/// "Show in Explorer" button beside it stays in view.
/// </summary>
internal sealed partial class MiddleTrimmedText : Grid
{
    private const string Ellipsis = "\u2026";
    private readonly TextBlock display;
    private readonly TextBlock probe;
    private string text = "";

    public MiddleTrimmedText(FontFamily fontFamily, double fontSize)
    {
        display = new TextBlock { FontFamily = fontFamily, FontSize = fontSize, TextWrapping = TextWrapping.NoWrap };
        probe = new TextBlock { FontFamily = fontFamily, FontSize = fontSize, TextWrapping = TextWrapping.NoWrap };
        Children.Add(display);
    }

    /// <summary>The full text; shown whole when it fits, else shortened in the middle.</summary>
    public string Text
    {
        get => text;
        set
        {
            if (value == text) return;
            text = value;
            ToolTipService.SetToolTip(this, value.Length == 0 ? null : value);
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// <paramref name="text"/> with only <paramref name="keep"/> of its
    /// characters, the first half (rounded up) and the last half, joined by
    /// an ellipsis; the text itself when it has no more than
    /// <paramref name="keep"/> characters.
    /// </summary>
    internal static string Shorten(string text, int keep)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (keep >= text.Length) return text;
        keep = Math.Max(0, keep);
        var head = (keep + 1) / 2;
        var tail = keep - head;
        return string.Concat(text.AsSpan(0, head), Ellipsis, text.AsSpan(text.Length - tail, tail));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Fit(availableSize.Width);
        if (display.Text != shown) display.Text = shown;
        display.Measure(availableSize);
        return new Size(Math.Min(display.DesiredSize.Width, availableSize.Width), display.DesiredSize.Height);
    }

    /// <summary>The longest middle-shortened form of the text that fits in <paramref name="width"/>.</summary>
    private string Fit(double width)
    {
        if (double.IsInfinity(width) || TextWidth(text) <= width) return text;
        // Binary search on the number of characters kept; 0 (a lone ellipsis) is the floor.
        int low = 0, high = text.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (TextWidth(Shorten(text, middle)) <= width) low = middle;
            else high = middle - 1;
        }
        return Shorten(text, low);
    }

    private double TextWidth(string candidate)
    {
        probe.Text = candidate;
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }
}
