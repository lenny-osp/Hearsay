using System.ComponentModel;
using System.Diagnostics;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hearsay.App.Features.Models;

/// <summary>
/// A stand-in for the store's download states, the active model and the
/// size on disk, so the UI snapshots show a mixed state without a real
/// download (as the shell stubs the settings banner with sample text).
/// </summary>
/// <param name="States">State by entry id; entries not listed are not installed.</param>
internal sealed record ModelsSample(IReadOnlyDictionary<string, DownloadState> States, string? ActiveId, long TotalSize);

/// <summary>
/// Models tab: the catalog grouped by family in <see cref="ModelCatalog.FamilyOrder"/>
/// (unknown families after them, sorted), with Download, Cancel, Use and
/// Delete per row, where the models are stored and their size on disk, and
/// the first-run sheet (PLAN.md section 5). Mirrors
/// mac/Hearsay/Features/Models/ModelManagerView.swift; the Mac's
/// <c>List</c> sections are a header and a card per family.
/// <para>
/// As on the Mac, the first time the tab appears in a run with no model
/// installed, nothing downloading and a recommended entry in the catalog,
/// <see cref="OnboardingModelSheet"/> offers the recommended model.
/// </para>
/// </summary>
internal sealed partial class ModelManagerView : UserControl
{
    private readonly AppShell shell;
    private readonly ModelStore store;
    private readonly Dictionary<string, ModelRowView> rows = new(StringComparer.Ordinal);
    private readonly TextBlock rootPath;
    private readonly TextBlock sizeOnDisk;
    private readonly Button showInExplorer;
    private ModelsSample? sample;
    private bool didOfferOnboarding;

    public ModelManagerView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        store = shell.Models;

        var page = new StackPanel { Spacing = 0, MaxWidth = 720, Margin = new Thickness(24, 12, 24, 24) };

        // Header: where the models are, how much they take, Show in Explorer.
        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(4, 0, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var location = new StackPanel { Spacing = 2 };
        location.Children.Add(Caption(Strings.ModelsStoredIn));
        rootPath = new TextBlock
        {
            Text = store.RootPath,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(rootPath, store.RootPath);
        location.Children.Add(rootPath);
        sizeOnDisk = Caption("");
        location.Children.Add(sizeOnDisk);
        header.Children.Add(location);
        showInExplorer = new Button { Content = Strings.ShowInExplorer, VerticalAlignment = VerticalAlignment.Top };
        showInExplorer.Click += (_, _) => OpenRootFolder();
        Grid.SetColumn(showInExplorer, 1);
        header.Children.Add(showInExplorer);
        page.Children.Add(header);

        if (store.CatalogError is { } error)
        {
            page.Children.Add(new TextBlock
            {
                Text = error,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Resource<Brush>("SystemFillColorCriticalBrush"),
                Margin = new Thickness(4, 8, 4, 0),
            });
        }

        foreach (var (family, entries) in Groups(store.Catalog))
        {
            page.Children.Add(new TextBlock { Text = FamilyTitle(family), Style = Resource<Style>("SectionHeaderStyle") });
            var list = new StackPanel { Spacing = 0 };
            for (var index = 0; index < entries.Count; index++)
            {
                var row = new ModelRowView(this, entries[index]);
                rows[entries[index].Id] = row;
                if (index > 0)
                {
                    list.Children.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 8, 0, 8),
                        Background = Resource<Brush>("DividerStrokeColorDefaultBrush"),
                    });
                }
                list.Children.Add(row);
            }
            page.Children.Add(new Border { Style = Resource<Style>("CardStyle"), Child = list, Margin = new Thickness(0, 0, 0, 4) });
        }

        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        store.StateChanged += OnStateChanged;
        store.PropertyChanged += OnStoreChanged;
        shell.Settings.PropertyChanged += OnSettingsChanged;
        Loaded += OnLoaded;
        UpdateHeader();
    }

    public ModelStore Store => store;

    /// <summary>The window the rows' dialogs belong to.</summary>
    public XamlRoot? DialogRoot => XamlRoot;

    /// <summary>The first-run sheet while it is open, for the UI snapshots.</summary>
    public OnboardingModelSheet? Onboarding { get; private set; }

    /// <summary>Replaces the store's states for the UI snapshots; null shows the store again.</summary>
    public void ShowSample(ModelsSample? value)
    {
        sample = value;
        UpdateHeader();
        foreach (var row in rows.Values) row.Update();
    }

    /// <summary>The entry's state (or the sample's).</summary>
    public DownloadState StateOf(ModelCatalogEntry entry)
    {
        if (sample is { } stub) return stub.States.GetValueOrDefault(entry.Id) ?? DownloadState.NotInstalled;
        return store.State(entry);
    }

    public bool IsActive(ModelCatalogEntry entry) => (sample is { } stub ? stub.ActiveId : store.ActiveModelId) == entry.Id;

    /// <summary>
    /// Shows the first-run sheet (the Mac's <c>showOnboarding</c>); Download
    /// starts the recommended model, unless <paramref name="startsDownload"/>
    /// is false (the UI snapshots). Returns when the sheet closes.
    /// </summary>
    public async Task OfferOnboardingAsync(bool startsDownload = true)
    {
        if (XamlRoot is not { } root || store.Catalog.Recommended is not { } recommended) return;
        didOfferOnboarding = true;
        var sheet = new OnboardingModelSheet(root, recommended);
        Onboarding = sheet;
        var download = await sheet.AskAsync().ConfigureAwait(true);
        Onboarding = null;
        if (download && startsDownload) store.Download(recommended);
    }

    /// <summary>Families in <see cref="ModelCatalog.FamilyOrder"/>, then any others sorted, each in catalog order.</summary>
    internal static IReadOnlyList<(string Family, IReadOnlyList<ModelCatalogEntry> Entries)> Groups(ModelCatalog catalog)
    {
        var byFamily = catalog.Entries.GroupBy(entry => entry.Family, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ModelCatalogEntry>)[.. group], StringComparer.Ordinal);
        var known = ModelCatalog.FamilyOrder.Where(byFamily.ContainsKey);
        var others = byFamily.Keys.Where(family => !ModelCatalog.FamilyOrder.Contains(family)).Order(StringComparer.Ordinal);
        return [.. known.Concat(others).Select(family => (family, byFamily[family]))];
    }

    /// <summary>Section titles (product names, not localized, as on the Mac).</summary>
    internal static string FamilyTitle(string family) => family switch
    {
        "tiny" => "Tiny",
        "base" => "Base",
        "small" => "Small",
        "medium" => "Medium",
        "large-v3" => "Large v3",
        "large-v3-turbo" => "Large v3 Turbo",
        _ => family,
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        store.Refresh();
        UpdateHeader();
        var downloading = store.Catalog.Entries.Any(entry => StateOf(entry) is DownloadState.Downloading);
        var installed = sample is { } stub ? stub.States.Values.Any(state => state is DownloadState.InstalledState)
            : store.Installed.Count > 0;
        if (!didOfferOnboarding && !installed && !downloading && store.Catalog.Recommended is not null)
        {
            await OfferOnboardingAsync().ConfigureAwait(true);
        }
    }

    private void OnStateChanged(object? sender, string id)
    {
        if (rows.TryGetValue(id, out var row)) row.Update();
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateHeader();
        if (e.PropertyName is nameof(ModelStore.Installed) or nameof(ModelStore.IsTokenizerInstalled))
        {
            foreach (var row in rows.Values) row.Update();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ActiveModelRepo))
        {
            foreach (var row in rows.Values) row.Update();
        }
    }

    private void UpdateHeader()
    {
        sizeOnDisk.Text = Strings.ModelsOnDisk(ByteSize.Format(sample?.TotalSize ?? store.TotalSizeOnDisk));
        showInExplorer.Visibility = Directory.Exists(store.RootPath) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens the models folder in File Explorer (the Mac's "Show in Finder").</summary>
    private void OpenRootFolder()
    {
        if (!Directory.Exists(store.RootPath)) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(store.RootPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            AppLog.Write($"models: could not open {store.RootPath}: {error.Message}");
        }
    }

    private static TextBlock Caption(string text) => new() { Text = text, Style = Resource<Style>("CaptionStyle") };

    private static T Resource<T>(string key) where T : class =>
        Application.Current.Resources[key] as T ?? throw new InvalidOperationException($"Missing resource {key}.");
}
