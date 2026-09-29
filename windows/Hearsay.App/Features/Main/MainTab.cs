using System.ComponentModel;
using Hearsay.App.Features.Settings;

namespace Hearsay.App.Features.Main;

/// <summary>
/// The main window's tabs, in display order.
/// Mirrors <c>MainTab</c> in mac/Hearsay/Features/Main/MainView.swift.
/// </summary>
internal enum MainTab
{
    Record,
    File,
    Models,
    History,
    Settings,
}

/// <summary>
/// Which main-window tab is showing, owned by the app so code outside the
/// window (the tray menu, help links) can switch tabs.
/// Mirrors <c>MainTabSelection</c> in mac/Hearsay/Features/Main/MainView.swift.
/// </summary>
internal sealed class MainTabSelection : INotifyPropertyChanged
{
    private MainTab tab = MainTab.Record;
    private SettingsPane? settingsPane;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainTab Tab
    {
        get => tab;
        set
        {
            if (tab == value) return;
            tab = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tab)));
        }
    }

    /// <summary>
    /// A Settings section to switch to (a help link such as
    /// <c>hearsay://open/settings-ai</c>); the Settings tab takes it and clears it.
    /// </summary>
    public SettingsPane? SettingsPane
    {
        get => settingsPane;
        set
        {
            if (settingsPane == value) return;
            settingsPane = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingsPane)));
        }
    }
}
