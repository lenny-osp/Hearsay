using System.ComponentModel;
using Hearsay.App.Features.Main;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Settings;

/// <summary>A Settings section, the Mac's <c>SettingsView.Pane</c>.</summary>
internal enum SettingsPane
{
    General,
    Window,
    Output,
    AI,
}

/// <summary>
/// The Settings tab of the main window (PLAN.md section 8): General, Window,
/// Output and AI, switched with a <see cref="SelectorBar"/> (the Mac's
/// segmented picker). Every section scrolls inside the tab.
/// Mirrors <c>SettingsView</c> in mac/Hearsay/Features/Settings/SettingsView.swift.
/// The Mac has no Models section (models are the main window's Models tab),
/// and neither does this page.
/// </summary>
internal sealed partial class SettingsPage : UserControl
{
    private readonly AppShell shell;
    private readonly Dictionary<SettingsPane, SelectorBarItem> items;
    private readonly Dictionary<SettingsPane, UserControl> panes;
    private bool updating;

    public SettingsPage(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        InitializeComponent();
        GeneralItem.Text = Strings.PaneGeneral;
        WindowItem.Text = Strings.PaneWindow;
        OutputItem.Text = Strings.PaneOutput;
        AIItem.Text = Strings.PaneAI;
        items = new()
        {
            [SettingsPane.General] = GeneralItem,
            [SettingsPane.Window] = WindowItem,
            [SettingsPane.Output] = OutputItem,
            [SettingsPane.AI] = AIItem,
        };
        panes = new()
        {
            [SettingsPane.General] = new GeneralSettingsView(shell),
            [SettingsPane.Window] = new WindowSettingsView(shell),
            [SettingsPane.Output] = new OutputSettingsView(shell),
            [SettingsPane.AI] = new AISettingsView(shell),
        };
        Show(SettingsPane.General);
        TakeRequestedPane();
        shell.Tabs.PropertyChanged += OnTabsChanged;
        // A SelectorBar outside the visual tree keeps its old selection when
        // it is set (a help link switches the section before the tab shows).
        Loaded += (_, _) =>
        {
            updating = true;
            Sections.SelectedItem = null;
            Sections.SelectedItem = items[Pane];
            updating = false;
        };
    }

    public SettingsPane Pane { get; private set; }

    /// <summary>Shows a section (also used by the UI snapshots).</summary>
    public void Show(SettingsPane pane)
    {
        Pane = pane;
        SelectItem(pane);
        PaneContent.Content = panes[pane];
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void SelectItem(SettingsPane pane)
    {
        updating = true;
        Sections.SelectedItem = items[pane];
        updating = false;
    }

    private void OnTabsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainTabSelection.SettingsPane)) TakeRequestedPane();
    }

    /// <summary>A help link asked for a section.</summary>
    private void TakeRequestedPane()
    {
        if (shell.Tabs.SettingsPane is not { } requested) return;
        Show(requested);
        shell.Tabs.SettingsPane = null;
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (updating) return;
        if (sender.SelectedItem is SelectorBarItem { Tag: string tag } && Enum.TryParse<SettingsPane>(tag, out var pane))
        {
            Show(pane);
        }
    }
}
