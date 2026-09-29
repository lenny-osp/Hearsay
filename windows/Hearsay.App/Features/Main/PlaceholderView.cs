using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Main;

/// <summary>
/// A tab that is not built yet: its name and one sentence. Stands in for
/// mac/Hearsay/Features/Recording/RecordView.swift,
/// FileTranscription/FileView.swift and the AI settings section until W5
/// and W6 replace it.
/// </summary>
internal sealed partial class PlaceholderView : UserControl
{
    public PlaceholderView(string title, string sentence)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            Padding = new Thickness(24),
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = sentence,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Content = panel;
    }
}
