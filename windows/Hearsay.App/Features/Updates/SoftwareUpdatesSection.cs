using System.ComponentModel;
using Hearsay.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Updates;

/// <summary>
/// "Software updates" in Settings > General: the version, the
/// automatic-check toggle and its caption, Check Now with a spinner and the
/// last successful check, and the result line. Port of
/// <c>SoftwareUpdatesSection</c> in mac/Hearsay/Features/Updates/UpdateService.swift
/// (placed by mac/Hearsay/Features/Settings/SettingsView.swift). Windows
/// adds Install Update beside the result line while a newer version is
/// offered, so a dismissed dialog is not the only way to install it (the
/// Mac has the app menu for that).
/// </summary>
internal sealed class SoftwareUpdatesSection
{
    private readonly UpdateService updates;
    private readonly AppSettings settings;
    private readonly ToggleSwitch automatic;
    private readonly Button checkNow;
    private readonly ProgressRing spinner;
    private readonly TextBlock lastCheck;
    private readonly TextBlock result;
    private readonly Button install;
    private readonly Grid resultRow;
    private bool refreshing;
    private Hearsay.Core.Updates.ReleaseInfo? checkedRelease;
    private bool canInstall;

    public SoftwareUpdatesSection(UpdateService updates, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(settings);
        this.updates = updates;
        this.settings = settings;

        var version = new TextBlock { Text = updates.VersionLine, IsTextSelectionEnabled = true };
        var (automaticRow, automaticSwitch) = Toggle(Strings.UpdateAutomaticallyCheck);
        automatic = automaticSwitch;
        automatic.Toggled += (_, _) =>
        {
            if (!refreshing) settings.AutomaticUpdateChecks = automatic.IsOn;
        };

        checkNow = new Button { Content = Strings.UpdateCheckNow };
        checkNow.Click += (_, _) => _ = updates.CheckNowAsync(userInitiated: true);
        spinner = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(checkNow);
        left.Children.Add(spinner);
        lastCheck = Caption("");
        lastCheck.VerticalAlignment = VerticalAlignment.Center;
        lastCheck.TextWrapping = TextWrapping.NoWrap;
        var checkRow = new Grid { ColumnSpacing = 12 };
        checkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        checkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        checkRow.Children.Add(left);
        Grid.SetColumn(lastCheck, 1);
        checkRow.Children.Add(lastCheck);

        result = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        install = new Button { Content = Strings.UpdateInstallUpdate, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        install.Click += (_, _) =>
        {
            if (updates.OfferedRelease is { } release) _ = updates.InstallAsync(release);
        };
        resultRow = Labeled("", install);
        resultRow.Children.RemoveAt(0);
        result.VerticalAlignment = VerticalAlignment.Center;
        resultRow.Children.Insert(0, result);

        Card = Card(version, automaticRow, Caption(Strings.UpdateAutomaticallyCheckCaption), checkRow, resultRow);
        Refresh();
        updates.PropertyChanged += OnChanged;
        settings.PropertyChanged += OnChanged;
    }

    /// <summary>The card, below the "Software updates" header.</summary>
    public Border Card { get; }

    /// <summary>The result line as shown, for the UI snapshots' checks.</summary>
    public string ResultText => result.Text;

    /// <summary>Whether Install Update shows, for the UI snapshots' checks.</summary>
    public bool OffersInstall => install.Visibility == Visibility.Visible;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Card.DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(Refresh);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        refreshing = true;
        automatic.IsOn = settings.AutomaticUpdateChecks;
        refreshing = false;
        var busy = updates.IsChecking;
        checkNow.IsEnabled = !busy;
        spinner.IsActive = busy;
        spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        lastCheck.Text = updates.LastCheckLine;
        result.Text = updates.ResultLine ?? "";
        var offered = updates.OfferedRelease;
        if (!ReferenceEquals(offered, checkedRelease))
        {
            // The location check writes a probe file: once per release.
            checkedRelease = offered;
            canInstall = offered is not null && updates.InstallProblem(offered) is null;
        }
        install.Visibility = offered is not null && canInstall ? Visibility.Visible : Visibility.Collapsed;
        resultRow.Visibility = result.Text.Length == 0 && install.Visibility == Visibility.Collapsed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
