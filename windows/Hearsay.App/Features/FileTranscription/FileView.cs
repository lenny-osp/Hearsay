using System.ComponentModel;
using System.Globalization;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Transcription;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.FileTranscription;

/// <summary>
/// The File tab (PLAN.md 4.2): drop or choose an audio or video file,
/// transcribe it, and continue to the notes flow. Port of
/// mac/Hearsay/Features/FileTranscription/FileView.swift. The notes flow is
/// W6: <see cref="GenerateNotes"/> is the hook it sets (the Mac starts the
/// flow by itself from <c>notesRequest</c>; see <see cref="FileViewModel.NotesRequested"/>).
/// </summary>
internal sealed partial class FileView : UserControl
{
    /// <summary>Extensions the drop target and the picker accept (Media Foundation's audio and video containers).</summary>
    public static readonly IReadOnlyList<string> AcceptedExtensions =
        [".wav", ".m4a", ".mp3", ".aac", ".wma", ".flac", ".aif", ".aiff", ".caf", ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".wmv", ".ogg", ".opus", ".webm"];

    private readonly AppShell shell;
    private readonly FileViewModel model;
    private readonly LanguageChoicePicker picker;
    private readonly Border dropZone;
    private readonly Rectangle dropOutline;
    private readonly Button choose;
    private readonly StackPanel statusArea = new() { Spacing = 8 };
    private readonly Border statusCard;
    private readonly LanguageNoticeView noticeView;
    private readonly Border noticeCard;
    private string? shownStatus;
    private ProgressBar? fileProgress;
    private TextBlock? filePercent;

    public FileView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        model = shell.FileModel;
        var page = Page();
        page.Padding = new Thickness(24, 4, 24, 24);
        page.MaxWidth = 760;

        // Same picker and shared setting as the Record tab.
        picker = new LanguageChoicePicker(shell.Settings);

        var dropContent = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dropContent.Children.Add(new FontIcon { Glyph = "", FontSize = 32, Foreground = Secondary() });
        dropContent.Children.Add(new TextBlock
        {
            Text = Strings.DropFileHere,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        dropContent.Children.Add(new TextBlock
        {
            Text = Strings.AcceptedFileTypes,
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });
        choose = new Button { Content = Strings.ChooseFile, HorizontalAlignment = HorizontalAlignment.Center };
        choose.Click += async (_, _) => await ChooseAsync().ConfigureAwait(true);
        dropContent.Children.Add(choose);
        dropOutline = new Rectangle
        {
            StrokeThickness = 2,
            StrokeDashArray = [3, 3],
            RadiusX = 10,
            RadiusY = 10,
            Stroke = Secondary(),
        };
        var dropGrid = new Grid { MinHeight = 150 };
        dropGrid.Children.Add(dropOutline);
        dropGrid.Children.Add(dropContent);
        dropZone = new Border { Child = dropGrid, AllowDrop = true, Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(0) };
        dropZone.DragOver += OnDragOver;
        dropZone.DragLeave += (_, _) => SetDropTargeted(false);
        dropZone.Drop += OnDrop;

        page.Children.Add(Header(Strings.TabFile));
        page.Children.Add(Card(picker, dropZone));
        statusCard = Card(statusArea);
        page.Children.Add(statusCard);
        noticeView = new LanguageNoticeView(
            language =>
            {
                model.TranscribeAgain(language);
            },
            model.DismissLanguageNotice);
        noticeCard = Card(noticeView);
        page.Children.Add(noticeCard);

        Content = new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled };
        model.PropertyChanged += OnModelChanged;
        model.NotesRequested += (_, _) => Render();
        Render();
    }

    /// <summary>
    /// The notes flow's hook (W6): called with the finished SRT and its
    /// language when "Generate notes…" is pressed. Null leaves the button
    /// disabled.
    /// </summary>
    public Action<NotesRequest>? GenerateNotes { get; set; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Render();

    /// <summary>Starts <paramref name="path"/> as if it had been dropped (recovery sheet, "Transcribe this file").</summary>
    public void Start(string path) => model.Transcribe(path);

    private void Render()
    {
        picker.IsDisabled = model.IsBusy;
        choose.IsEnabled = !model.IsBusy;
        // Rebuilt only when something but the progress changed, so Cancel is
        // never replaced under the pointer; progress updates in place.
        var signature = model.Phase switch
        {
            FilePhase.Transcribing t => $"transcribing|{t.Source}",
            var other => $"{other}|{model.Note}|{model.NeedsModel}|{model.SessionLanguage}|{model.RerunError}|{GenerateNotes is null}",
        };
        if (signature == shownStatus && model.Phase is FilePhase.Transcribing current && fileProgress is not null && filePercent is not null)
        {
            fileProgress.Value = current.Progress;
            filePercent.Text = Percent(current.Progress);
        }
        else if (signature != shownStatus)
        {
            shownStatus = signature;
            RenderStatus();
        }
        RenderNotice();
    }

    private void RenderStatus()
    {
        statusArea.Children.Clear();
        fileProgress = null;
        filePercent = null;
        switch (model.Phase)
        {
            case FilePhase.Idle:
                if (model.Note is { } note) statusArea.Children.Add(IconLine("", note, Secondary()));
                break;
            case FilePhase.Loading loading:
                statusArea.Children.Add(BusyLine(Strings.ReadingFile(Path.GetFileName(loading.Source))));
                break;
            case FilePhase.Detecting detecting:
                statusArea.Children.Add(BusyLine(Strings.DetectingLanguageOf(Path.GetFileName(detecting.Source))));
                break;
            case FilePhase.Transcribing transcribing:
                statusArea.Children.Add(new TextBlock { Text = Strings.TranscribingFile(Path.GetFileName(transcribing.Source)) });
                fileProgress = new ProgressBar { Minimum = 0, Maximum = 1, Value = transcribing.Progress };
                statusArea.Children.Add(fileProgress);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                filePercent = new TextBlock
                {
                    Text = Percent(transcribing.Progress),
                    Style = (Style)Application.Current.Resources["CaptionStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(filePercent);
                var cancel = new Button { Content = Strings.Cancel };
                ToolTipService.SetToolTip(cancel, Strings.CancelFileTooltip);
                cancel.Click += (_, _) => model.Cancel();
                Grid.SetColumn(cancel, 1);
                row.Children.Add(cancel);
                statusArea.Children.Add(row);
                break;
            case FilePhase.Finished finished:
                statusArea.Children.Add(new TextBlock { Text = Strings.Transcript, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                statusArea.Children.Add(PathText(finished.Srt));
                if (model.SessionLanguage is { } language)
                {
                    statusArea.Children.Add(Labeled(Strings.LanguageLabel, new TextBlock { Text = language.DisplayName() }));
                }
                if (model.RerunError is { } rerunError)
                {
                    statusArea.Children.Add(IconLine("", rerunError, Caution()));
                }
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var reveal = new Button { Content = Strings.RevealInExplorer };
                reveal.Click += (_, _) => ResultActions.Reveal(shell, [finished.Srt]);
                buttons.Children.Add(reveal);
                var open = new Button { Content = Strings.OpenSrt };
                open.Click += (_, _) => ResultActions.Open(shell, finished.Srt);
                buttons.Children.Add(open);
                var notes = new Button { Content = Strings.GenerateNotes, IsEnabled = GenerateNotes is not null && model.SessionLanguage is not null };
                if (GenerateNotes is null) ToolTipService.SetToolTip(notes, Strings.GenerateNotesUnavailable);
                notes.Click += (_, _) =>
                {
                    if (GenerateNotes is { } generate && model.SessionLanguage is { } lang) generate(new NotesRequest(finished.Srt, lang));
                };
                buttons.Children.Add(notes);
                statusArea.Children.Add(buttons);
                break;
            case FilePhase.Failed failed:
                statusArea.Children.Add(IconLine("", failed.Message, Critical()));
                if (model.NeedsModel)
                {
                    var openModels = new Button { Content = Strings.OpenModels, HorizontalAlignment = HorizontalAlignment.Left };
                    openModels.Click += (_, _) => shell.Tabs.Tab = MainTab.Models;
                    statusArea.Children.Add(openModels);
                }
                break;
        }
        statusCard.Visibility = statusArea.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderNotice()
    {
        if (model.LanguageNotice is { } notice)
        {
            noticeView.Show(notice, model.CanRerun);
            noticeCard.Visibility = Visibility.Visible;
        }
        else
        {
            noticeCard.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!model.IsBusy && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            SetDropTargeted(true);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropTargeted(false);
        if (model.IsBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault(file => IsAccepted(file.Path)) is { } accepted) model.Transcribe(accepted.Path);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Write($"file: drop failed: {error.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task ChooseAsync()
    {
        var filePicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (var extension in AcceptedExtensions) filePicker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(filePicker, Win32Interop.GetWindowFromWindowId(shell.MainWindow.AppWindow.Id));
        var file = await filePicker.PickSingleFileAsync();
        if (file is not null) model.Transcribe(file.Path);
    }

    public static bool IsAccepted(string path) =>
        AcceptedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void SetDropTargeted(bool targeted) =>
        dropOutline.Stroke = targeted ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : Secondary();

    private static StackPanel BusyLine(string text)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        line.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    internal static Grid IconLine(string glyph, string text, Brush brush)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = brush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = brush };
        Grid.SetColumn(block, 1);
        grid.Children.Add(block);
        return grid;
    }

    internal static TextBlock PathText(string path) => new()
    {
        Text = path,
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    internal static string Percent(double fraction) => (fraction).ToString("P0", CultureInfo.CurrentCulture);

    internal static Brush Secondary() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    internal static Brush Caution() => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

    internal static Brush Critical() => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
}
