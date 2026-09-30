using Hearsay.App.Features.Transcription;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// One recording in the Record tab's queue list: its name, its state and its
/// actions (PLAN.md 4.9 "UI"). Port of <c>QueueRow</c> in
/// mac/Hearsay/Features/Recording/RecordView.swift. The controls are built
/// once per job and <see cref="Update"/> only changes their texts and
/// visibility, so a button is never replaced under the pointer while the
/// progress ticks. What each state offers is <see cref="QueueRows"/>.
/// Use from the UI thread.
/// </summary>
internal sealed class QueueRowView : UserControl
{
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock state;
    private readonly Button revealButton;
    private readonly Button dismissButton;
    private readonly Button useLivePreview = new() { Content = Strings.UseLivePreviewInstead, Padding = new Thickness(10, 3, 10, 4) };
    private readonly TextBlock savingLivePreview;
    private readonly Button open = new() { Content = Strings.OpenTranscript, Padding = new Thickness(10, 3, 10, 4) };
    private readonly Button notes = new() { Content = Strings.GenerateNotes, Padding = new Thickness(10, 3, 10, 4) };
    private readonly Button tryAgain = new() { Content = Strings.TryAgain, Padding = new Thickness(10, 3, 10, 4) };
    private readonly WrapRow actions;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock error;
    private readonly LanguageNoticeView notice;
    private readonly TextBlock rerunError;

    /// <param name="queue">The queue whose job this row shows.</param>
    /// <param name="job">The job.</param>
    /// <param name="generateNotes">"Generate Notes…" (the Record tab opens the notes flow).</param>
    /// <param name="reveal">"Reveal in Explorer".</param>
    /// <param name="openTranscript">"Open Transcript".</param>
    public QueueRowView(
        TranscriptionQueue queue, TranscriptionJob job, Action<TranscriptionJob> generateNotes,
        Action<TranscriptionJob> reveal, Action<TranscriptionJob> openTranscript)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(generateNotes);
        ArgumentNullException.ThrowIfNull(reveal);
        ArgumentNullException.ThrowIfNull(openTranscript);
        Job = job;

        state = new TextBlock { Style = (Style)Application.Current.Resources["CaptionStyle"], Foreground = Secondary() };
        revealButton = IconButton("", Strings.RevealInExplorer, () => reveal(job));
        dismissButton = IconButton("", Strings.Dismiss, () => queue.Dismiss(job));
        savingLivePreview = new TextBlock
        {
            Text = Strings.SavingLivePreview,
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            Foreground = Secondary(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        error = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            Foreground = Critical(),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            IsTextSelectionEnabled = true,
        };
        rerunError = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        notice = new LanguageNoticeView(language => queue.TranscribeAgain(job, language), () => queue.DismissLanguageNotice(job));

        ToolTipService.SetToolTip(useLivePreview, Strings.UseLivePreviewTooltip);
        useLivePreview.Click += (_, _) => queue.UseLivePreviewInstead(job);
        open.Click += (_, _) => openTranscript(job);
        notes.Click += (_, _) => generateNotes(job);
        tryAgain.Click += (_, _) => queue.Retry(job);

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel { Spacing = 2 };
        names.Children.Add(title);
        names.Children.Add(state);
        header.Children.Add(names);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        icons.Children.Add(revealButton);
        icons.Children.Add(dismissButton);
        Grid.SetColumn(icons, 1);
        header.Children.Add(icons);

        actions = new WrapRow { HorizontalSpacing = 8, VerticalSpacing = 6 };
        actions.Children.Add(useLivePreview);
        actions.Children.Add(savingLivePreview);
        actions.Children.Add(open);
        actions.Children.Add(notes);
        actions.Children.Add(tryAgain);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(header);
        stack.Children.Add(actions);
        stack.Children.Add(progress);
        stack.Children.Add(error);
        stack.Children.Add(notice);
        stack.Children.Add(rerunError);
        Content = stack;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    public TranscriptionJob Job { get; }

    /// <summary>The state text now shown (for the UI snapshots).</summary>
    public string StateText => state.Text;

    /// <summary>The labels of the buttons showing now (for the UI snapshots).</summary>
    public IReadOnlyList<string> ShownButtons
    {
        get
        {
            var shown = actions.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible)
                .Select(b => b.Content as string ?? "").ToList();
            if (revealButton.Visibility == Visibility.Visible) shown.Add(Strings.RevealInExplorer);
            if (dismissButton.Visibility == Visibility.Visible) shown.Add(Strings.Dismiss);
            return shown;
        }
    }

    /// <summary>Applies the job's current state. <paramref name="canGenerateNotes"/>: a notes flow is connected and none runs.</summary>
    public void Update(bool pausedForSession, bool canGenerateNotes)
    {
        var job = Job;
        title.Text = job.Title;
        ToolTipService.SetToolTip(title, job.Title);
        state.Text = QueueRows.StateText(job, pausedForSession);
        state.Foreground = job.State == TranscriptionJobState.Failed ? Critical() : Secondary();
        var shown = QueueRows.Actions(job);
        SetVisible(useLivePreview, shown.UseLivePreview);
        SetVisible(savingLivePreview, shown.SavingLivePreview);
        SetVisible(open, shown.OpenTranscript);
        SetVisible(notes, shown.GenerateNotes);
        notes.IsEnabled = canGenerateNotes;
        SetVisible(tryAgain, shown.TryAgain);
        SetVisible(revealButton, shown.Reveal);
        SetVisible(dismissButton, shown.Dismiss);
        SetVisible(actions, shown.UseLivePreview || shown.SavingLivePreview || shown.OpenTranscript || shown.GenerateNotes || shown.TryAgain);

        if (QueueRows.ShowsProgress(job, pausedForSession) && job.DisplayProgress is { } fraction)
        {
            progress.Value = fraction;
            progress.Visibility = Visibility.Visible;
        }
        else
        {
            progress.Visibility = Visibility.Collapsed;
        }

        var failure = job.State == TranscriptionJobState.Failed ? job.ErrorMessage : null;
        error.Text = failure ?? "";
        SetVisible(error, !string.IsNullOrEmpty(failure));

        if (QueueRows.ShowsLanguageNotice(job) && job.LanguageNotice is { } languageNotice)
        {
            notice.Show(languageNotice, job.CanChangeLanguage);
            notice.Visibility = Visibility.Visible;
        }
        else
        {
            notice.Visibility = Visibility.Collapsed;
        }

        rerunError.Text = job.RerunError ?? "";
        SetVisible(rerunError, job.RerunError is not null);
    }

    private static void SetVisible(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Secondary() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static Brush Critical() => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

    /// <summary>A small icon-only button with a tooltip and an accessible name.</summary>
    private static Button IconButton(string glyph, string label, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(8, 5, 8, 5),
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A left-to-right panel that continues on the next line when the row is full (WinUI has no wrap panel).</summary>
    private sealed class WrapRow : Panel
    {
        public double HorizontalSpacing { get; init; }

        public double VerticalSpacing { get; init; }

        protected override Size MeasureOverride(Size availableSize)
        {
            double x = 0, rowHeight = 0, height = 0, width = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                if (child.Visibility == Visibility.Collapsed) continue;
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > availableSize.Width)
                {
                    height += rowHeight + VerticalSpacing;
                    x = 0;
                    rowHeight = 0;
                }
                x += size.Width + HorizontalSpacing;
                rowHeight = Math.Max(rowHeight, size.Height);
                width = Math.Max(width, Math.Min(x - HorizontalSpacing, availableSize.Width));
            }
            return new Size(width, height + rowHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, rowHeight = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width)
                {
                    y += rowHeight + VerticalSpacing;
                    x = 0;
                    rowHeight = 0;
                }
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width + HorizontalSpacing;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            return finalSize;
        }
    }
}
