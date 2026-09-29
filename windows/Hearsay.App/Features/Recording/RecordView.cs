using System.ComponentModel;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Transcription;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// The Record tab: microphone, system audio, language, controls, level
/// meters, the live preview, the language banner, the final-pass progress and
/// the saved recording (PLAN.md 4.1). The session lives in the app-level
/// <see cref="RecordingController"/>, so this view only reflects it and
/// closing the window never stops a recording. Port of
/// mac/Hearsay/Features/Recording/RecordView.swift. The Mac's permission
/// section has no Windows counterpart (PLAN.md 18.4, W3), and the notes flow
/// is W6: <see cref="GenerateNotes"/> is its hook (the Mac starts the flow by
/// itself from <c>notesRequest</c>; see <see cref="RecordingController.NotesRequested"/>).
/// </summary>
internal sealed partial class RecordView : UserControl
{
    private readonly AppShell shell;
    private readonly RecordingController model;

    private readonly ComboBox microphone = new() { MinWidth = 260, MaxWidth = 380 };
    private readonly ToggleSwitch systemAudio;
    private readonly LanguageChoicePicker picker;
    private readonly TextBlock elapsed = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 34,
        FontWeight = FontWeights.SemiLight,
    };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ProgressBar level = new() { Minimum = 0, Maximum = 1, Height = 6 };
    private readonly ProgressBar micMeter = new() { Minimum = 0, Maximum = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar systemMeter = new() { Minimum = 0, Maximum = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel systemMeterRow;
    private readonly StackPanel noticeArea = new() { Spacing = 6 };
    private readonly StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    private readonly Border liveCard;
    private readonly TextBlock liveIndicator = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel liveLines = new() { Spacing = 6, Padding = new Thickness(0, 4, 12, 4) };
    private readonly ScrollViewer liveScroll;
    private readonly TextBlock livePlaceholder = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock liveFooter;
    private int shownLiveCount = -1;

    private readonly Border noticeCard;
    private readonly LanguageNoticeView noticeView;
    private readonly Border progressCard;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock progressText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button useLivePreview = new() { Content = Strings.UseLivePreviewInstead };
    private readonly TextBlock savingLivePreview = new() { Text = Strings.SavingLivePreview, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border errorCard;
    private readonly StackPanel errorArea = new() { Spacing = 8 };
    private readonly Border savedCard;
    private readonly StackPanel savedArea = new() { Spacing = 8 };
    private bool updating;
    // What each rebuilt area last showed: the areas are rebuilt only when it
    // changes, never at the meters' 5 Hz, so a button is never replaced
    // under the pointer.
    private string? shownNotices;
    private string? shownControls;
    private string? shownError;
    private string? shownSaved;

    public RecordView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
        model = shell.RecordingController;
        var page = Page();
        page.Padding = new Thickness(24, 4, 24, 24);
        page.MaxWidth = 760;

        // Devices and language.
        microphone.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            if (microphone.SelectedItem is ComboBoxItem { Tag: string uid }) model.SelectedDeviceUid = uid;
        };
        var (systemRow, systemSwitch) = Toggle(Strings.AlsoCaptureSystemAudio);
        systemAudio = systemSwitch;
        systemAudio.Toggled += (_, _) =>
        {
            if (!updating) model.CaptureSystemAudio = systemAudio.IsOn;
        };
        picker = new LanguageChoicePicker(shell.Settings);
        page.Children.Add(Header(Strings.TabRecord));
        page.Children.Add(Card(Labeled(Strings.Microphone, microphone), systemRow, picker));

        // Elapsed, meters, controls.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(elapsed);
        Grid.SetColumn(status, 1);
        top.Children.Add(status);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(level, Strings.InputLevel);
        var meters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        meters.Children.Add(SourceMeter("\uE720", Strings.MicMeter, micMeter));
        systemMeterRow = SourceMeter("\uE767", Strings.SystemMeter, systemMeter);
        meters.Children.Add(systemMeterRow);
        page.Children.Add(Card(top, level, meters, noticeArea, controls));

        // Live preview.
        var liveHeader = new Grid();
        liveHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        liveHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        liveHeader.Children.Add(new TextBlock { Text = Strings.LivePreview, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Grid.SetColumn(liveIndicator, 1);
        liveHeader.Children.Add(liveIndicator);
        liveScroll = new ScrollViewer { Content = liveLines, Height = 200, HorizontalScrollMode = ScrollMode.Disabled };
        var liveBox = new Grid();
        liveBox.Children.Add(liveScroll);
        liveBox.Children.Add(livePlaceholder);
        livePlaceholder.Foreground = FileView.Secondary();
        liveFooter = Caption("");
        liveCard = Card(liveHeader, liveBox, liveFooter);
        page.Children.Add(liveCard);

        // Language banner.
        noticeView = new LanguageNoticeView(model.TranscribeAgain, model.DismissLanguageNotice);
        noticeCard = Card(noticeView);
        page.Children.Add(noticeCard);

        // Final pass.
        useLivePreview.Click += (_, _) => model.UseLivePreviewInstead();
        ToolTipService.SetToolTip(useLivePreview, Strings.UseLivePreviewTooltip);
        var progressRow = new Grid();
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressText.Foreground = FileView.Secondary();
        progressRow.Children.Add(progressText);
        var progressButtons = new StackPanel { Orientation = Orientation.Horizontal };
        progressButtons.Children.Add(useLivePreview);
        progressButtons.Children.Add(savingLivePreview);
        Grid.SetColumn(progressButtons, 1);
        progressRow.Children.Add(progressButtons);
        progressCard = Card(
            new TextBlock { Text = Strings.Transcribing, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            progress, progressRow);
        page.Children.Add(progressCard);

        errorCard = Card(errorArea);
        page.Children.Add(errorCard);
        savedCard = Card(savedArea);
        page.Children.Add(savedCard);

        Content = new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled };
        model.PropertyChanged += OnModelChanged;
        model.NotesRequested += (_, _) => Render();
        Loaded += (_, _) =>
        {
            model.Activate();
            Render();
        };
        Render();
    }

    /// <summary>
    /// The notes flow's hook (W6): called with the finished SRT and the
    /// session language when "Generate notes…" is pressed. Null leaves the
    /// button disabled. The automatic start the Mac does after every finished
    /// transcript is <see cref="RecordingController.NotesRequested"/>.
    /// </summary>
    public Action<NotesRequest>? GenerateNotes { get; set; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        updating = true;
        try
        {
            RenderDevices();
            systemAudio.IsOn = model.CaptureSystemAudio;
            systemAudio.IsEnabled = !model.IsSessionActive;
            microphone.IsEnabled = !model.IsSessionActive && model.Devices.Count > 0;
            picker.IsDisabled = model.IsSessionActive;
        }
        finally
        {
            updating = false;
        }

        elapsed.Text = LevelMeter.FormatElapsed(model.Elapsed);
        RenderStatus();
        level.Value = model.LevelFraction;
        level.Foreground = model.SilenceWarning is null ? Green() : FileView.Caution();
        micMeter.Value = model.MicLevelFraction;
        micMeter.Foreground = Green();
        systemMeterRow.Visibility = model.SystemLevelFraction is null ? Visibility.Collapsed : Visibility.Visible;
        systemMeter.Value = model.SystemLevelFraction ?? 0;
        systemMeter.Foreground = Green();
        var notices = $"{model.SystemAudioNotice}|{model.SilenceWarning}";
        if (notices != shownNotices)
        {
            shownNotices = notices;
            RenderNotices();
        }
        var controlState = $"{model.Phase.GetType().Name}|{model.CanStart}";
        if (controlState != shownControls)
        {
            shownControls = controlState;
            RenderControls();
        }
        RenderLive();

        if (model.LanguageNotice is { } notice)
        {
            noticeView.Show(notice, model.CanChangeSessionLanguage);
            noticeCard.Visibility = Visibility.Visible;
        }
        else
        {
            noticeCard.Visibility = Visibility.Collapsed;
        }

        if (model.TranscriptionProgress is { } fraction)
        {
            progressCard.Visibility = Visibility.Visible;
            progress.Value = fraction;
            progressText.Text = FileView.Percent(fraction);
            useLivePreview.Visibility = model.CanUseLivePreview ? Visibility.Visible : Visibility.Collapsed;
            savingLivePreview.Visibility = !model.CanUseLivePreview && model.IsUsingLivePreview ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            progressCard.Visibility = Visibility.Collapsed;
        }

        var error = $"{model.ErrorMessage}|{model.NeedsModel}|{model.CanRetryTranscription}|{model.FinishedRecording}";
        if (error != shownError)
        {
            shownError = error;
            RenderError();
        }
        var saved = $"{model.FinishedTranscript}|{model.FinishedRecording}|{model.SessionLanguage}|{model.RerunError}|{model.IsTranscribing}|{GenerateNotes is null}";
        if (saved != shownSaved)
        {
            shownSaved = saved;
            RenderSaved();
        }
    }

    private void RenderNotices()
    {
        noticeArea.Children.Clear();
        if (model.SystemAudioNotice is { } systemNotice) noticeArea.Children.Add(FileView.IconLine("\uE74F", systemNotice, FileView.Secondary()));
        if (model.SilenceWarning is { } warning) noticeArea.Children.Add(FileView.IconLine("\uE7BA", warning, FileView.Caution()));
        noticeArea.Visibility = noticeArea.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderDevices()
    {
        List<string> wanted = model.Devices.Count == 0 ? [Strings.NoInputDevice] : [.. model.Devices.Select(d => d.Uid)];
        var shown = microphone.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string ?? Strings.NoInputDevice).ToList();
        if (!wanted.SequenceEqual(shown))
        {
            microphone.Items.Clear();
            if (model.Devices.Count == 0)
            {
                microphone.Items.Add(new ComboBoxItem { Content = Strings.NoInputDevice, Tag = null });
            }
            foreach (var device in model.Devices) microphone.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Uid });
        }
        var selected = microphone.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == model.SelectedDeviceUid)
            ?? microphone.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (!ReferenceEquals(microphone.SelectedItem, selected)) microphone.SelectedItem = selected;
    }

    private void RenderStatus()
    {
        var (text, brush) = model.Phase switch
        {
            ControllerPhase.Transcribing => (Strings.Finalizing, FileView.Secondary()),
            ControllerPhase.Finished => (Strings.Saved, FileView.Secondary()),
            ControllerPhase.Starting => (Strings.StartingState, FileView.Secondary()),
            ControllerPhase.Recording => ("● " + Strings.RecordingState, FileView.Critical()),
            ControllerPhase.Paused => ("❚❚ " + Strings.PausedState, FileView.Secondary()),
            ControllerPhase.Stopping => (Strings.Saving, FileView.Secondary()),
            _ => (Strings.Ready, FileView.Secondary()),
        };
        status.Text = text;
        status.Foreground = brush;
    }

    private void RenderControls()
    {
        controls.Children.Clear();
        switch (model.Phase)
        {
            case ControllerPhase.Recording:
                controls.Children.Add(IconButton("\uE769", Strings.Pause, model.Pause, accent: false));
                controls.Children.Add(IconButton("\uE71A", Strings.Stop, () => _ = model.StopAsync(), accent: true));
                break;
            case ControllerPhase.Paused:
                controls.Children.Add(IconButton("\uE768", Strings.Resume, model.Resume, accent: false));
                controls.Children.Add(IconButton("\uE71A", Strings.Stop, () => _ = model.StopAsync(), accent: true));
                break;
            case ControllerPhase.Stopping:
                controls.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
                break;
            default:
                var start = IconButton("\uE7C8", Strings.Start, model.Start, accent: true);
                start.IsEnabled = model.CanStart;
                controls.Children.Add(start);
                break;
        }
    }

    private void RenderLive()
    {
        var shown = model.IsCapturing || model.Phase is ControllerPhase.Stopping || model.LiveSegments.Count > 0 || model.LiveNotice is not null;
        liveCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        if (model.IsDetectingLanguage)
        {
            liveIndicator.Text = Strings.DetectingLanguage;
            liveIndicator.Foreground = FileView.Secondary();
        }
        else if (model.IsLiveLagging)
        {
            liveIndicator.Text = Strings.ChunksWaiting(model.LiveChunksWaiting);
            liveIndicator.Foreground = FileView.Caution();
            ToolTipService.SetToolTip(liveIndicator, Strings.ChunksWaitingTooltip);
        }
        else
        {
            liveIndicator.Text = model.LiveChunksWaiting == 1 ? Strings.TranscribingChunk : "";
            liveIndicator.Foreground = FileView.Secondary();
        }
        var cues = model.LiveSegments.Where(cue => cue.Text.Length > 0).ToList();
        if (cues.Count != shownLiveCount)
        {
            shownLiveCount = cues.Count;
            liveLines.Children.Clear();
            foreach (var cue in cues)
            {
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.Children.Add(new TextBlock
                {
                    Text = LevelMeter.FormatElapsed(cue.Start),
                    Style = (Style)Application.Current.Resources["CaptionStyle"],
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Margin = new Thickness(0, 2, 0, 0),
                });
                var text = new TextBlock { Text = cue.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                Grid.SetColumn(text, 1);
                line.Children.Add(text);
                liveLines.Children.Add(line);
            }
            // Keep the newest line in view.
            liveScroll.UpdateLayout();
            liveScroll.ChangeView(null, liveScroll.ScrollableHeight, null, disableAnimation: true);
        }
        livePlaceholder.Visibility = cues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        livePlaceholder.Text = model.IsDetectingLanguage
            ? Strings.DetectingLanguage
            : model.IsLivePreviewEnabled ? Strings.FirstLinesAppear : "";
        liveFooter.Text = model.LiveNotice ?? "";
        liveFooter.Visibility = model.LiveNotice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderError()
    {
        errorArea.Children.Clear();
        if (model.ErrorMessage is not { } message)
        {
            errorCard.Visibility = Visibility.Collapsed;
            return;
        }
        errorCard.Visibility = Visibility.Visible;
        errorArea.Children.Add(FileView.IconLine("\uEA39", message, FileView.Critical()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (model.NeedsModel) buttons.Children.Add(IconButton("\uE896", Strings.OpenModels, () => shell.Tabs.Tab = MainTab.Models, accent: false));
        if (model.CanRetryTranscription) buttons.Children.Add(IconButton("\uE72C", Strings.TryAgain, model.RetryTranscription, accent: false));
        if (model.FinishedRecording is not null)
        {
            buttons.Children.Add(IconButton("\uE8E5", Strings.TranscribeThisFile, model.RequestTranscribeFile, accent: false));
        }
        if (buttons.Children.Count > 0) errorArea.Children.Add(buttons);
    }

    private void RenderSaved()
    {
        savedArea.Children.Clear();
        if (model.FinishedTranscript is null && model.FinishedRecording is null)
        {
            savedCard.Visibility = Visibility.Collapsed;
            return;
        }
        savedCard.Visibility = Visibility.Visible;
        savedArea.Children.Add(new TextBlock { Text = Strings.Saved, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (model.FinishedTranscript is { } srt) savedArea.Children.Add(PathRow(Strings.Transcript, srt));
        if (model.FinishedRecording is { } wav) savedArea.Children.Add(PathRow(Strings.RecordingLabel, wav));
        if (model.SessionLanguage is { } language && model.FinishedTranscript is not null)
        {
            savedArea.Children.Add(Labeled(Strings.LanguageLabel, new TextBlock { Text = language.DisplayName() }));
        }
        if (model.RerunError is { } rerunError) savedArea.Children.Add(FileView.IconLine("\uE7BA", rerunError, FileView.Caution()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(IconButton("\uE838", Strings.RevealInExplorer, () =>
            ResultActions.Reveal(shell, new[] { model.FinishedTranscript, model.FinishedRecording }.OfType<string>().ToList()), accent: false));
        if (model.FinishedTranscript is { } transcript)
        {
            buttons.Children.Add(IconButton("\uE8A5", Strings.OpenSrt, () => ResultActions.Open(shell, transcript), accent: false));
            var canNotes = GenerateNotes is not null && model.SessionLanguage is not null && !model.IsTranscribing;
            var notes = IconButton("\uE70B", Strings.GenerateNotes, () =>
            {
                if (GenerateNotes is { } generate && model.SessionLanguage is { } lang) generate(new NotesRequest(transcript, lang));
            }, accent: false);
            notes.IsEnabled = canNotes;
            if (GenerateNotes is null) ToolTipService.SetToolTip(notes, Strings.GenerateNotesUnavailable);
            buttons.Children.Add(notes);
        }
        savedArea.Children.Add(buttons);
    }

    /// <summary>A label with the path under it (the Mac's <c>LabeledContent</c> with a two-line path).</summary>
    private static StackPanel PathRow(string label, string path)
    {
        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(new TextBlock { Text = label, Foreground = FileView.Secondary() });
        row.Children.Add(FileView.PathText(path));
        return row;
    }

    private static StackPanel SourceMeter(string glyph, string title, ProgressBar bar)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = FileView.Secondary() });
        row.Children.Add(bar);
        ToolTipService.SetToolTip(row, title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, Strings.SourceLevel(title));
        return row;
    }

    private static Button IconButton(string glyph, string text, Action action, bool accent)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = content };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accentStyle)
        {
            button.Style = accentStyle;
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static Brush Green() => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
}
