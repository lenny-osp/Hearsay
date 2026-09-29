using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// The building blocks of a Settings section, the Windows 11 Settings look:
/// a header, then a card per group. Stands in for SwiftUI's grouped
/// <c>Form</c> and <c>Section</c> in mac/Hearsay/Features/Settings/SettingsView.swift.
/// </summary>
internal static class SettingsLayout
{
    public static StackPanel Page() => new() { Spacing = 0 };

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("SectionHeaderStyle"),
    };

    /// <summary>A card holding <paramref name="rows"/>, one under the other.</summary>
    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel { Spacing = 10 };
        foreach (var row in rows) stack.Children.Add(row);
        return new Border { Style = Resource<Style>("CardStyle"), Child = stack, Margin = new Thickness(0, 0, 0, 4) };
    }

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("CaptionStyle"),
    };

    /// <summary>A caption in the warning colour, hidden while empty.</summary>
    public static TextBlock Warning() => new()
    {
        Style = Resource<Style>("CaptionStyle"),
        Foreground = Resource<Brush>("SystemFillColorCautionBrush"),
        Visibility = Visibility.Collapsed,
    };

    public static void SetWarning(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A label on the left and <paramref name="control"/> on the right (SwiftUI's <c>LabeledContent</c>).</summary>
    public static Grid Labeled(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A toggle row: the label left, the switch right, no On/Off text.</summary>
    public static (Grid Row, ToggleSwitch Switch) Toggle(string label)
    {
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        return (Labeled(label, toggle), toggle);
    }

    private static T Resource<T>(string key) where T : class =>
        Application.Current.Resources[key] as T ?? throw new InvalidOperationException($"Missing resource {key}.");
}
