using System.ComponentModel;
using Hearsay.App.Features.FileTranscription;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Notes;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Audio;
using Hearsay.Core.Settings;
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
/// the saved recording (PLAN.md 4.1, 4.9). The session lives in the app-level
/// <see cref="RecordingController"/> and the finished recordings in the
/// <see cref="TranscriptionQueue"/>, so this view only reflects them and
/// closing the window never stops a recording or a transcription. With no
/// session and one recording in the queue, that recording is shown as the
/// single meeting always was (live preview, progress, "Use live preview
/// instead", the saved files, the language banner, the notes); otherwise the
/// "Transcription queue" below the session lists every recording as a
/// <see cref="QueueRowView"/> (<see cref="QueueRows"/>). Port of
/// mac/Hearsay/Features/Recording/RecordView.swift. The Mac's permission
/// section has no Windows counterpart (PLAN.md 18.4, W3); <see cref="GenerateNotes"/>
/// is the hook of the notes flow (the Mac starts the flow by itself from the
/// queue's notes request; see <see cref="TranscriptionQueue.NotesRequested"/>).
/// </summary>
internal sealed partial class RecordView : UserControl
{
    private readonly AppShell shell;
    private readonly RecordingController model;
    private readonly TranscriptionQueue queue;

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
    private object? shownLiveSource;
    /// <summary>The job whose language notice the banner shows; null for the session's.</summary>
    private TranscriptionJob? noticeJob;

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
    private readonly Border queueCard;
    private readonly StackPanel queueArea = new() { Spacing = 10 };
    private readonly Dictionary<string, QueueRowView> queueRows = [];
    private List<string> shownQueueRows = [];
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
        queue = shell.Queue;
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
        noticeView = new LanguageNoticeView(
            language =>
            {
                if (noticeJob is { } job) queue.TranscribeAgain(job, language);
                else model.TranscribeAgain(language);
            },
            () =>
            {
                if (noticeJob is { } job) queue.DismissLanguageNotice(job);
                else model.DismissLanguageNotice();
            });
        noticeCard = Card(noticeView);
        page.Children.Add(noticeCard);

        // Final pass.
        useLivePreview.Click += (_, _) =>
        {
            if (queue.FeaturedJob is { } job) queue.UseLivePreviewInstead(job);
        };
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

        // The queue: one row per recording below the session (PLAN.md 4.9 "UI").
        queueCard = Card(
            new TextBlock { Text = Strings.TranscriptionQueue, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            queueArea);
        page.Children.Add(queueCard);

        Content = new ScrollViewer { Content = page, HorizontalScrollMode = ScrollMode.Disabled };
        model.PropertyChanged += OnModelChanged;
        queue.Changed += (_, _) => Render();
        Loaded += (_, _) =>
        {
            model.Activate();
            Render();
        };
        Render();
    }

    /// <summary>
    /// The notes flow's hook (W6): called with the finished SRT and the
    /// job's language when "Generate notes…" is pressed on a finished job
    /// that could not open them by itself. Null leaves the button disabled.
    /// The automatic start after every finished transcript is
    /// <see cref="TranscriptionQueue.NotesRequested"/>.
    /// </summary>
    public Action<NotesRequest>? GenerateNotes { get; set; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        // With no session and one job in the queue, that job is shown as the
        // single meeting always was (PLAN.md 4.9 item 5).
        var job = queue.FeaturedJob;
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
        RenderStatus(job);
        level.Value = model.LevelFraction;
        level.Foreground = model.SilenceWarning is null ? Green() : FileView.Caution();
        micMeter.Value = model.MicLevelFraction;
        micMeter.Foreground = Green();
        systemMeterRow.Visibility = model.SystemLevelFraction is null ? Visibility.Collapsed : Visibility.Visible;
        systemMeter.Value = model.SystemLevelFraction ?? 0;
        systemMeter.Foreground = Green();
        var notices = $"{model.SystemAudioNotice}|{model.SilenceWarning}|{model.CpuSpeedNotice}";
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
        RenderLive(job);

        if (model.LanguageNotice is { } sessionNotice)
        {
            noticeJob = null;
            noticeView.Show(sessionNotice, model.CanChangeSessionLanguage);
            noticeCard.Visibility = Visibility.Visible;
        }
        else if (job?.LanguageNotice is { } jobNotice)
        {
            noticeJob = job;
            noticeView.Show(jobNotice, job.CanChangeLanguage);
            noticeCard.Visibility = Visibility.Visible;
        }
        else
        {
            noticeJob = null;
            noticeCard.Visibility = Visibility.Collapsed;
        }

        if (job?.DisplayProgress is { } fraction)
        {
            progressCard.Visibility = Visibility.Visible;
            progress.Value = fraction;
            progressText.Text = FileView.Percent(fraction);
            useLivePreview.Visibility = job.CanUseLivePreview ? Visibility.Visible : Visibility.Collapsed;
            savingLivePreview.Visibility = !job.CanUseLivePreview && job.IsUsingLivePreview && job.IsPending ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            progressCard.Visibility = Visibility.Collapsed;
        }

        var error = $"{model.ErrorMessage}|{model.NeedsModel}|{model.CanRetryTranscription}|{model.FinishedRecording}|{job?.Id}|{job?.State}|{job?.ErrorMessage}|{job?.NeedsModel}|{job?.Wav}";
        if (error != shownError)
        {
            shownError = error;
            RenderError(job);
        }
        var saved = $"{model.ErrorMessage}|{model.FinishedTranscript}|{model.FinishedRecording}|{job?.Id}|{job?.State}|{job?.Srt}|{job?.Wav}|{job?.Language}|{job?.RerunError}|{job?.OffersNotes}|{job?.IsRerunning}|{GenerateNotes is null}|{NotesFlowViewModel.IsAnyRunning}";
        if (saved != shownSaved)
        {
            shownSaved = saved;
            RenderSaved(job);
        }
        RenderQueue();
    }

    /// <summary>"Generate Notes…" on the finished card or a queue row: the notes flow opens for that job's SRT.</summary>
    private void StartNotes(TranscriptionJob job)
    {
        if (GenerateNotes is not { } generate || job.Srt is not { } srt || job.Language is not { } language) return;
        queue.NotesStarted(job);
        generate(new NotesRequest(srt, language, job.Id));
    }

    /// <summary>The queue list: shown whenever the single-meeting view is not (<see cref="QueueRows.ShowsList"/>).</summary>
    private void RenderQueue()
    {
        if (!QueueRows.ShowsList(queue))
        {
            queueCard.Visibility = Visibility.Collapsed;
            return;
        }
        queueCard.Visibility = Visibility.Visible;
        var ids = queue.Jobs.Select(job => job.Id).ToList();
        if (!ids.SequenceEqual(shownQueueRows))
        {
            var kept = new Dictionary<string, QueueRowView>();
            queueArea.Children.Clear();
            foreach (var job in queue.Jobs)
            {
                if (!queueRows.TryGetValue(job.Id, out var row))
                {
                    row = new QueueRowView(queue, job, StartNotes, RevealJob, opened => ResultActions.Open(shell, opened.Srt ?? opened.Recording.Path));
                }
                kept[job.Id] = row;
                if (queueArea.Children.Count > 0)
                {
                    queueArea.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    });
                }
                queueArea.Children.Add(row);
            }
            queueRows.Clear();
            foreach (var (id, row) in kept) queueRows[id] = row;
            shownQueueRows = ids;
        }
        var canNotes = GenerateNotes is not null && !NotesFlowViewModel.IsAnyRunning;
        foreach (var job in queue.Jobs)
        {
            if (queueRows.TryGetValue(job.Id, out var row)) row.Update(queue.IsPausedForSession(job), canNotes);
        }
    }

    private void RevealJob(TranscriptionJob job) => ResultActions.Reveal(shell, job.Files);

    /// <summary>The rows now listed, for the UI snapshots and checks.</summary>
    internal IReadOnlyList<QueueRowView> QueueRowViews => [.. queue.Jobs.Select(job => queueRows.GetValueOrDefault(job.Id)).OfType<QueueRowView>()];

    private void RenderNotices()
    {
        noticeArea.Children.Clear();
        if (model.SystemAudioNotice is { } systemNotice) noticeArea.Children.Add(FileView.IconLine("", systemNotice, FileView.Secondary()));
        if (model.SilenceWarning is { } warning) noticeArea.Children.Add(FileView.IconLine("", warning, FileView.Caution()));
        if (model.CpuSpeedNotice is { } cpu) noticeArea.Children.Add(FileView.IconLine("", cpu, FileView.Secondary()));
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

    private void RenderStatus(TranscriptionJob? job)
    {
        var (text, brush) = model.Phase switch
        {
            ControllerPhase.Starting => (Strings.StartingState, FileView.Secondary()),
            ControllerPhase.Recording => ("● " + Strings.RecordingState, FileView.Critical()),
            ControllerPhase.Paused => ("❚❚ " + Strings.PausedState, FileView.Secondary()),
            ControllerPhase.Stopping => (Strings.Saving, FileView.Secondary()),
            _ when job is not null && model.ErrorMessage is null && (job.IsPending || job.IsRerunning) => (Strings.Finalizing, FileView.Secondary()),
            _ when job is { State: TranscriptionJobState.Done } && model.ErrorMessage is null => (Strings.Saved, FileView.Secondary()),
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
                controls.Children.Add(IconButton("", Strings.Pause, model.Pause, accent: false));
                controls.Children.Add(IconButton("", Strings.Stop, () => _ = model.StopAsync(), accent: true));
                controls.Children.Add(StopStartNextButton());
                break;
            case ControllerPhase.Paused:
                controls.Children.Add(IconButton("", Strings.Resume, model.Resume, accent: false));
                controls.Children.Add(IconButton("", Strings.Stop, () => _ = model.StopAsync(), accent: true));
                controls.Children.Add(StopStartNextButton());
                break;
            case ControllerPhase.Stopping:
                controls.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
                break;
            default:
                var start = IconButton("", Strings.Start, model.Start, accent: true);
                start.IsEnabled = model.CanStart;
                controls.Children.Add(start);
                break;
        }
    }

    /// <summary>The Mac's "Stop &amp; Start Next" button, with the shortcut in its tooltip.</summary>
    private Button StopStartNextButton()
    {
        var button = IconButton("", Strings.StopAndStartNext, model.StopAndStartNext, accent: false);
        ToolTipService.SetToolTip(button, Strings.StopAndStartNextTooltip(shell.Settings.StopStartNextHotkey.DisplayString));
        return button;
    }

    private void RenderLive(TranscriptionJob? job)
    {
        // The session's live preview while it lasts; else the single job's.
        var session = model.IsCapturing || model.Phase is ControllerPhase.Stopping || model.LiveSegments.Count > 0 || model.LiveNotice is not null;
        var jobLive = !session && job is not null && (job.LiveSegments.Count > 0 || job.LiveNotice is not null);
        var shown = session || jobLive;
        liveCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        var segments = session ? model.LiveSegments : job?.LiveSegments ?? [];
        var waiting = session ? model.LiveChunksWaiting : job?.LiveChunksWaiting ?? 0;
        var detecting = session ? model.IsDetectingLanguage : job?.IsDetectingLanguage ?? false;
        var enabled = session ? model.IsLivePreviewEnabled : job?.LiveEnabled ?? false;
        var notice = session ? model.LiveNotice : job?.LiveNotice;
        if (detecting)
        {
            liveIndicator.Text = Strings.DetectingLanguage;
            liveIndicator.Foreground = FileView.Secondary();
        }
        else if (waiting > 1)
        {
            liveIndicator.Text = Strings.ChunksWaiting(waiting);
            liveIndicator.Foreground = FileView.Caution();
            ToolTipService.SetToolTip(liveIndicator, Strings.ChunksWaitingTooltip);
        }
        else
        {
            liveIndicator.Text = waiting == 1 ? Strings.TranscribingChunk : "";
            liveIndicator.Foreground = FileView.Secondary();
        }
        var cues = segments.Where(cue => cue.Text.Length > 0).ToList();
        if (cues.Count != shownLiveCount || !ReferenceEquals(segments, shownLiveSource))
        {
            shownLiveCount = cues.Count;
            shownLiveSource = segments;
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
        livePlaceholder.Text = detecting
            ? Strings.DetectingLanguage
            : enabled ? Strings.FirstLinesAppear : "";
        liveFooter.Text = notice ?? "";
        liveFooter.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The capture failure's card, else the single job's failure (PLAN.md 4.1 failure rules).</summary>
    private void RenderError(TranscriptionJob? job)
    {
        errorArea.Children.Clear();
        string? message;
        bool needsModel;
        Action? retry;
        string? transcribe;
        if (model.ErrorMessage is { } captureError)
        {
            message = captureError;
            needsModel = model.NeedsModel;
            retry = model.CanRetryTranscription ? model.RetryTranscription : null;
            transcribe = model.FinishedRecording;
        }
        else if (job is { State: TranscriptionJobState.Failed, ErrorMessage: { } jobError })
        {
            message = jobError;
            needsModel = job.NeedsModel;
            retry = () => queue.Retry(job);
            transcribe = job.Wav;
        }
        else
        {
            errorCard.Visibility = Visibility.Collapsed;
            return;
        }
        errorCard.Visibility = Visibility.Visible;
        errorArea.Children.Add(FileView.IconLine("", message, FileView.Critical()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (needsModel) buttons.Children.Add(IconButton("", Strings.OpenModels, () => shell.Tabs.Tab = MainTab.Models, accent: false));
        if (retry is not null) buttons.Children.Add(IconButton("", Strings.TryAgain, retry, accent: false));
        if (transcribe is not null)
        {
            buttons.Children.Add(IconButton("", Strings.TranscribeThisFile, () => model.RequestTranscribeFile(transcribe), accent: false));
        }
        if (buttons.Children.Count > 0) errorArea.Children.Add(buttons);
    }

    /// <summary>The kept files of a capture failure, or the single finished (or failed) job's files.</summary>
    private void RenderSaved(TranscriptionJob? job)
    {
        savedArea.Children.Clear();
        string? srt;
        string? wav;
        if (model.ErrorMessage is not null)
        {
            srt = model.FinishedTranscript;
            wav = model.FinishedRecording;
            job = null;
        }
        else if (job is { IsPending: false })
        {
            srt = job.Srt;
            wav = job.Wav;
        }
        else
        {
            srt = null;
            wav = null;
            job = null;
        }
        if (srt is null && wav is null)
        {
            savedCard.Visibility = Visibility.Collapsed;
            return;
        }
        savedCard.Visibility = Visibility.Visible;
        savedArea.Children.Add(new TextBlock { Text = Strings.Saved, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (srt is not null) savedArea.Children.Add(PathRow(Strings.Transcript, srt));
        if (wav is not null) savedArea.Children.Add(PathRow(Strings.RecordingLabel, wav));
        if (job is { State: TranscriptionJobState.Done, Language: { } language })
        {
            savedArea.Children.Add(Labeled(Strings.LanguageLabel, new TextBlock { Text = language.DisplayName() }));
        }
        if (job?.RerunError is { } rerunError) savedArea.Children.Add(FileView.IconLine("", rerunError, FileView.Caution()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var files = new[] { srt, wav }.OfType<string>().ToList();
        buttons.Children.Add(IconButton("", Strings.RevealInExplorer, () => ResultActions.Reveal(shell, files), accent: false));
        if (job is { State: TranscriptionJobState.Done, Srt: { } transcript } done)
        {
            buttons.Children.Add(IconButton("", Strings.OpenSrt, () => ResultActions.Open(shell, transcript), accent: false));
            if (done.OffersNotes)
            {
                var notes = IconButton("", Strings.GenerateNotes, () =>
                {
                    StartNotes(done);
                }, accent: false);
                notes.IsEnabled = GenerateNotes is not null && !NotesFlowViewModel.IsAnyRunning;
                if (GenerateNotes is null) ToolTipService.SetToolTip(notes, Strings.GenerateNotesUnavailable);
                buttons.Children.Add(notes);
            }
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
