using System.Diagnostics;
using System.Globalization;
using Hearsay.App.Features.History;
using Hearsay.Core.Audio;
using Hearsay.Core.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hearsay.App.Features.Recovery;

/// <summary>
/// Queue of unfinished spool recordings found at launch (PLAN.md 4.5). The
/// sheet shows the first one; each answer moves on to the next. Port of
/// <c>UnfinishedRecordingQueue</c> in
/// mac/Hearsay/Features/Recovery/UnfinishedRecordingSheet.swift.
/// </summary>
internal sealed class UnfinishedRecordingQueue
{
    /// <summary>Only the first check of a launch looks at the spool.</summary>
    private static bool hasChecked;

    private readonly List<string> pending;

    public UnfinishedRecordingQueue(IReadOnlyList<string> recordings, RecordingSpool spool)
    {
        pending = [.. recordings];
        Spool = spool;
    }

    public RecordingSpool Spool { get; }

    public IReadOnlyList<string> Pending => pending;

    public string? Current => pending.Count > 0 ? pending[0] : null;

    /// <summary>
    /// The spool's unfinished recordings the first time this is called in a
    /// launch, null afterwards or when there are none. Spool files created
    /// after this process started belong to a recording of this session
    /// (started from the tray or a hotkey before the window opened), so they
    /// are left alone.
    /// </summary>
    public static UnfinishedRecordingQueue? CheckOnce(RecordingSpool spool)
    {
        ArgumentNullException.ThrowIfNull(spool);
        if (hasChecked) return null;
        hasChecked = true;
        DateTime? launch = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            launch = process.StartTime;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"recovery: no process start time: {error.Message}");
        }
        var recordings = spool.UnfinishedRecordings().Where(path =>
        {
            if (launch is not { } started) return true;
            try
            {
                return File.GetCreationTime(path) < started;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }).ToList();
        return recordings.Count == 0 ? null : new UnfinishedRecordingQueue(recordings, spool);
    }

    public void Advance()
    {
        if (pending.Count > 0) pending.RemoveAt(0);
    }
}

/// <summary>
/// "A recording from &lt;date&gt; was not finished (&lt;duration&gt;). What do
/// you want to do?" with Delete, Transcribe and Keep, one recording after the
/// other until the queue is empty. Port of <c>UnfinishedRecordingSheet</c> in
/// mac/Hearsay/Features/Recovery/UnfinishedRecordingSheet.swift, as a
/// <see cref="ContentDialog"/> that Escape cannot close (the Mac's
/// <c>interactiveDismissDisabled</c>): its buttons are in the content, so
/// Escape never picks Delete.
/// </summary>
internal sealed partial class UnfinishedRecordingSheet : ContentDialog
{
    private readonly UnfinishedRecordingQueue queue;
    private readonly Func<string> outputFolder;
    private readonly Action<string>? onTranscribe;
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock fileName = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock more = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button delete = new() { Content = Strings.Delete };
    private readonly Button transcribe = new() { Content = Strings.TranscribeButton };
    private readonly Button keep = new() { Content = Strings.Keep };
    private bool allowClose;

    /// <param name="queue">The recordings to ask about.</param>
    /// <param name="outputFolder">Resolves the output folder Keep moves the WAV into.</param>
    /// <param name="onTranscribe">Runs the File flow on the repaired recording; null disables Transcribe.</param>
    public UnfinishedRecordingSheet(XamlRoot root, UnfinishedRecordingQueue queue, Func<string> outputFolder, Action<string>? onTranscribe)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(outputFolder);
        this.queue = queue;
        this.outputFolder = outputFolder;
        this.onTranscribe = onTranscribe;
        Alert.Prepare(this, root);
        Title = Strings.UnfinishedRecording;

        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        fileName.Foreground = secondary;
        more.Foreground = secondary;
        error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) keep.Style = accent;
        delete.Click += (_, _) => Delete();
        transcribe.Click += (_, _) => Transcribe();
        keep.Click += (_, _) => Keep();
        transcribe.IsEnabled = onTranscribe is not null;

        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(delete);
        Grid.SetColumn(transcribe, 2);
        buttons.Children.Add(transcribe);
        Grid.SetColumn(keep, 3);
        buttons.Children.Add(keep);

        var stack = new StackPanel { Spacing = 10, Width = 420 };
        stack.Children.Add(message);
        stack.Children.Add(fileName);
        stack.Children.Add(more);
        stack.Children.Add(error);
        stack.Children.Add(buttons);
        Content = stack;
        Closing += (_, args) =>
        {
            if (!allowClose) args.Cancel = true;
        };
        Refresh();
    }

    /// <summary>The recording on screen now.</summary>
    public string? Recording => queue.Current;

    /// <summary>Shows the sheet until every recording in the queue has an answer.</summary>
    public async Task RunAsync()
    {
        if (queue.Current is null) return;
        await Alert.PresentAsync(this).ConfigureAwait(true);
    }

    /// <summary>Closes the sheet whatever is left (debug runs).</summary>
    public void Dismiss()
    {
        allowClose = true;
        Hide();
    }

    private void Refresh()
    {
        if (queue.Current is not { } recording)
        {
            Dismiss();
            return;
        }
        message.Text = Strings.UnfinishedRecordingMessage(DateText(recording), DurationText(recording));
        fileName.Text = Path.GetFileName(recording);
        more.Text = queue.Pending.Count > 1 ? Strings.MoreAfterThisOne(queue.Pending.Count - 1) : "";
        more.Visibility = queue.Pending.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SetError(null);
    }

    private void SetError(string? text)
    {
        error.Text = text ?? "";
        error.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string DateText(string recording)
    {
        if (HistoryIndex.TimestampDate(Path.GetFileNameWithoutExtension(recording)) is not { } date) return Strings.AnUnknownTime;
        // The Mac's .long date (no weekday) with a short time, in the interface language.
        var culture = CultureInfo.CurrentCulture;
        var pattern = culture.DateTimeFormat.LongDatePattern;
        foreach (var weekday in new[] { "dddd, ", "dddd ", ", dddd", "dddd" }) pattern = pattern.Replace(weekday, "", StringComparison.Ordinal);
        return date.ToString(pattern.Trim(), culture) + " " + date.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
    }

    private static string DurationText(string recording)
    {
        double duration;
        try
        {
            duration = WavWriter.DurationOf(recording);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or WavException)
        {
            return Strings.UnknownLength;
        }
        var total = (int)Math.Round(duration, MidpointRounding.AwayFromZero);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:D2}");
    }

    /// <summary>Patches the header from the file size and moves the WAV into the output folder.</summary>
    private void Keep()
    {
        if (queue.Current is not { } recording) return;
        try
        {
            WavWriter.PatchHeader(recording);
            RecordingSpool.Finalize(recording, keep: true, outputFolder());
            queue.Advance();
            Refresh();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or WavException)
        {
            SetError(Strings.CouldNotKeepRecording(failure.Message, recording));
        }
    }

    private void Delete()
    {
        if (queue.Current is not { } recording) return;
        try
        {
            File.Delete(recording);
            queue.Advance();
            Refresh();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            SetError(Strings.CouldNotDeleteRecording(failure.Message));
        }
    }

    private void Transcribe()
    {
        if (onTranscribe is null || queue.Current is not { } recording) return;
        try
        {
            WavWriter.PatchHeader(recording);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or WavException)
        {
            SetError(Strings.CouldNotRepairRecording(failure.Message));
            return;
        }
        queue.Advance();
        onTranscribe(recording);
        Refresh();
    }
}
