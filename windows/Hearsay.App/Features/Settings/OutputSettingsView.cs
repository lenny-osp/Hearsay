using System.ComponentModel;
using Hearsay.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// Settings > Output: the output folder (shown resolved, chosen with the
/// system folder picker, or reset to the default) and whether the WAV is
/// kept after a successful transcription. Mirrors <c>OutputSettingsView</c>
/// in mac/Hearsay/Features/Settings/SettingsView.swift; the Mac's
/// <c>NSOpenPanel</c> and bookmark become <see cref="FolderPicker"/> and
/// <see cref="OutputLocation.MakeStoredPath"/> (PLAN.md 18.4, Settings).
/// </summary>
internal sealed partial class OutputSettingsView : UserControl
{
    private readonly AppShell shell;
    private readonly AppSettings settings;
    private readonly TextBlock folderPath;
    private readonly Button useDefault;
    private readonly TextBlock error;
    private readonly ToggleSwitch keepRecording;
    private bool refreshing;

    public OutputSettingsView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        settings = shell.Settings;
        var page = Page();
        error = Warning();

        folderPath = new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderRow = new StackPanel { Spacing = 4 };
        folderRow.Children.Add(new TextBlock { Text = Strings.OutputFolder });
        folderRow.Children.Add(folderPath);

        var choose = new Button { Content = Strings.ChooseFolder };
        choose.Click += async (_, _) => await ChooseFolderAsync();
        useDefault = new Button { Content = Strings.UseDefaultFolder };
        useDefault.Click += (_, _) =>
        {
            settings.OutputFolder = null;
            SetWarning(error, null);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(choose);
        buttons.Children.Add(useDefault);

        var (keepRow, keepSwitch) = Toggle(Strings.KeepRecording);
        keepRecording = keepSwitch;
        keepRecording.Toggled += (_, _) =>
        {
            if (!refreshing) settings.KeepRecording = keepRecording.IsOn;
        };

        page.Children.Add(Header(Strings.PaneOutput));
        page.Children.Add(Card(folderRow, buttons, error));
        page.Children.Add(Card(keepRow, Caption(Strings.KeepRecordingCaption)));
        Content = page;
        Refresh();
        settings.PropertyChanged += OnSettingsChanged;
        Loaded += (_, _) => Refresh();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.OutputFolder) or nameof(AppSettings.KeepRecording)) Refresh();
    }

    /// <summary>The resolved folder, as the Mac's <c>refresh()</c>: the default is created when it is used.</summary>
    private void Refresh()
    {
        refreshing = true;
        try
        {
            folderPath.Text = OutputLocation.Resolve(settings.OutputFolder).Path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            folderPath.Text = OutputLocation.DefaultFolder();
            SetWarning(error, Strings.OutputFolderCreateFailed(failure.Message));
        }
        useDefault.IsEnabled = settings.OutputFolder is not null;
        keepRecording.IsOn = settings.KeepRecording;
        refreshing = false;
    }

    private async Task ChooseFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        // An unpackaged app must tell the picker its owner window.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(shell.MainWindow.AppWindow.Id));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            settings.OutputFolder = OutputLocation.MakeStoredPath(folder.Path);
            SetWarning(error, null);
        }
        catch (ArgumentException failure)
        {
            SetWarning(error, Strings.OutputFolderRememberFailed(failure.Message));
        }
    }
}
