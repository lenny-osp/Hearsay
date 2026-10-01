using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// Hosts one page in a column that is exactly min(available width,
/// <see cref="MaxColumnWidth"/>) wide and centered. A StackPanel with a
/// MaxWidth and the default Stretch inside a ScrollViewer was not reliably
/// centered after the window was resized (the Record, File and Models tabs
/// sat off-center, by a different amount each); arranging the child by hand
/// leaves nothing to a stale extent or to Stretch's fall-back to Center.
/// Use it as the ScrollViewer's content with horizontal scrolling off; the
/// page's own margin or padding stays inside the column width.
/// </summary>
internal sealed partial class CenteredColumn : Panel
{
    private double maxColumnWidth = double.PositiveInfinity;

    public double MaxColumnWidth
    {
        get => maxColumnWidth;
        set
        {
            if (maxColumnWidth == value) return;
            maxColumnWidth = value;
            InvalidateMeasure();
        }
    }

    public static CenteredColumn Host(UIElement page, double maxWidth)
    {
        var column = new CenteredColumn { MaxColumnWidth = maxWidth };
        column.Children.Add(page);
        return column;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(availableSize.Width, maxColumnWidth);
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        // Report the full available width so the host never sizes us to the
        // column; with an unbounded width (no host limit) report the column.
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(finalSize.Width, maxColumnWidth);
        var left = Math.Max(0, (finalSize.Width - width) / 2);
        foreach (var child in Children)
        {
            child.Arrange(new Rect(left, 0, width, Math.Max(finalSize.Height, child.DesiredSize.Height)));
        }
        return finalSize;
    }
}
