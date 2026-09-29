using Hearsay.App.Features.History;
using Hearsay.Core.ModelStore;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hearsay.App.Features.Models;

/// <summary>
/// One catalog entry: name, quantization, "Recommended", the state line
/// (size; progress and bytes while downloading; the error after a failure;
/// "in use" or "installed"), and one primary action (Download, Retry,
/// Cancel, Use, or the active-model check mark). Delete lives in the
/// context menu (right click, or Shift+F10 / the menu key on the row's
/// button), disabled while the entry has no files on disk.
/// Mirrors mac/Hearsay/Features/Models/ModelRowView.swift.
/// <para>
/// Windows difference: Delete asks first (the Mac deletes at once), since
/// a right-click menu item is easy to hit by accident and a model is a
/// large download to repeat.
/// </para>
/// </summary>
internal sealed partial class ModelRowView : UserControl
{
    private readonly ModelManagerView owner;
    private readonly ModelCatalogEntry entry;
    private readonly ContentControl detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl action = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuFlyoutItem deleteItem;

    public ModelRowView(ModelManagerView owner, ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        this.owner = owner;
        this.entry = entry;

        var grid = new Grid { ColumnSpacing = 12, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 4 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = entry.DisplayName, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border
        {
            Background = Resource<Brush>("ControlFillColorSecondaryBrush"),
            BorderBrush = Resource<Brush>("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Quantization,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
            },
        });
        if (entry.Recommended)
        {
            title.Children.Add(new TextBlock
            {
                Text = Strings.Recommended,
                FontSize = 12,
                Foreground = Resource<Brush>("AccentTextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        text.Children.Add(title);
        text.Children.Add(detail);
        grid.Children.Add(text);
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        deleteItem = new MenuFlyoutItem { Text = Strings.Delete, Icon = new FontIcon { Glyph = "" } };
        deleteItem.Click += async (_, _) => await DeleteAsync().ConfigureAwait(true);
        var menu = new MenuFlyout();
        menu.Items.Add(deleteItem);
        menu.Opening += (_, _) => deleteItem.IsEnabled = owner.Store.HasLocalFiles(entry);
        grid.ContextFlyout = menu;
        Content = grid;
        Update();
    }

    public ModelCatalogEntry Entry => entry;

    /// <summary>Redraws the state line and the action for the current state.</summary>
    public void Update()
    {
        var state = owner.StateOf(entry);
        var active = owner.IsActive(entry);
        detail.Content = state switch
        {
            DownloadState.Downloading downloading => Progress(downloading.Progress),
            DownloadState.Failed failed => new TextBlock
            {
                Text = failed.Message,
                FontSize = 12,
                Foreground = Resource<Brush>("SystemFillColorCriticalBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            DownloadState.InstalledState => Caption(active
                ? Strings.ModelInUse(ByteSize.Format(entry.SizeBytes))
                : Strings.ModelInstalled(ByteSize.Format(entry.SizeBytes))),
            _ => Caption(ByteSize.Format(entry.SizeBytes)),
        };
        action.Content = state switch
        {
            DownloadState.Downloading => Button(Strings.Cancel, () => owner.Store.CancelDownload(entry)),
            DownloadState.Failed => Button(Strings.Retry, () => owner.Store.Download(entry)),
            DownloadState.InstalledState when active => ActiveMark(),
            DownloadState.InstalledState => Button(Strings.Use, () => owner.Store.Use(entry)),
            _ => Button(Strings.Download, () => owner.Store.Download(entry)),
        };
    }

    private static StackPanel Progress(DownloadProgress progress)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = progress.FractionCompleted,
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 260,
        });
        panel.Children.Add(Caption(Strings.DownloadProgress(ByteSize.Format(progress.BytesReceived), ByteSize.Format(progress.TotalBytes))));
        return panel;
    }

    private static Button Button(string title, Action onClick)
    {
        var button = new Button { Content = title };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static FontIcon ActiveMark()
    {
        var mark = new FontIcon
        {
            Glyph = "",
            FontSize = 20,
            Foreground = Resource<Brush>("SystemFillColorSuccessBrush"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        ToolTipService.SetToolTip(mark, Strings.ActiveModel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mark, Strings.ActiveModel);
        return mark;
    }

    /// <summary>Asks, then deletes; an error is shown in a dialog (the Mac's alert).</summary>
    private async Task DeleteAsync()
    {
        if (owner.DialogRoot is not { } root || !owner.Store.HasLocalFiles(entry)) return;
        var confirmed = await Alert.ConfirmAsync(root, Strings.DeleteModelTitle(entry.DisplayName),
            Strings.DeleteModelMessage, Strings.Delete).ConfigureAwait(true);
        if (!confirmed) return;
        try
        {
            await owner.Store.DeleteAsync(entry).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        {
            await Alert.ShowAsync(root, Strings.CouldNotDeleteModel, error.Message).ConfigureAwait(true);
        }
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("CaptionStyle"),
    };

    private static T Resource<T>(string key) where T : class =>
        Application.Current.Resources[key] as T ?? throw new InvalidOperationException($"Missing resource {key}.");
}
