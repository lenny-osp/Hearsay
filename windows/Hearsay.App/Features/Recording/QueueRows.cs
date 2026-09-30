using Hearsay.App.Features.Transcription;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Recording;

/// <summary>Which buttons and texts one row of the Record tab's queue list offers (<see cref="QueueRows.Actions"/>).</summary>
internal readonly record struct QueueRowActions(
    bool UseLivePreview,
    bool SavingLivePreview,
    bool OpenTranscript,
    bool GenerateNotes,
    bool TryAgain,
    bool Reveal,
    bool Dismiss);

/// <summary>
/// The rules of the Record tab's "Transcription queue" list (PLAN.md 4.9
/// "UI"), free of any control so they can be tested: when the list shows,
/// each row's state text, and its actions per state. Port of the logic in
/// <c>QueueRow</c> (<c>stateText</c>, <c>actions</c>) and of the
/// <c>featuredJob</c> / <c>jobs.isEmpty</c> branch of <c>body</c> in
/// mac/Hearsay/Features/Recording/RecordView.swift. The row control is
/// <see cref="QueueRowView"/>.
/// </summary>
internal static class QueueRows
{
    /// <summary>
    /// The list shows below the session whenever the single-meeting view does
    /// not: a session is active or two or more jobs exist. With no session
    /// and one job that job is shown as the single meeting
    /// (<see cref="TranscriptionQueue.FeaturedJob"/>); an empty queue shows nothing.
    /// </summary>
    public static bool ShowsList(TranscriptionQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return queue.FeaturedJob is null && queue.Jobs.Count > 0;
    }

    /// <summary>The state under the row's name: Waiting, Paused while recording, Transcribing N%, Done or Failed.</summary>
    public static string StateText(TranscriptionJob job, bool pausedForSession)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsRerunning) return Strings.QueueTranscribing(Percent(job.RerunProgress));
        return job.State switch
        {
            TranscriptionJobState.Waiting => pausedForSession ? Strings.QueuePausedWhileRecording : Strings.QueueWaiting,
            TranscriptionJobState.Running => Strings.QueueTranscribing(Percent(job.Progress)),
            TranscriptionJobState.Suspended => pausedForSession ? Strings.QueuePausedWhileRecording : Strings.QueueTranscribing(Percent(job.Progress)),
            TranscriptionJobState.Done => Strings.QueueDone,
            _ => Strings.QueueFailed,
        };
    }

    /// <summary>The row's progress bar: a running pass or re-run, not a waiting job and not one paused for the session.</summary>
    public static bool ShowsProgress(TranscriptionJob job, bool pausedForSession)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.DisplayProgress is not null && (job.State != TranscriptionJobState.Waiting || job.IsRerunning) && !pausedForSession;
    }

    /// <summary>The language banner (Transcribe again) shows on a finished or waiting job, as on the Mac.</summary>
    public static bool ShowsLanguageNotice(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.LanguageNotice is not null && job.State is TranscriptionJobState.Done or TranscriptionJobState.Waiting;
    }

    /// <summary>The buttons of the row by state.</summary>
    public static QueueRowActions Actions(TranscriptionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var done = job.State == TranscriptionJobState.Done && job.Srt is not null;
        return new QueueRowActions(
            UseLivePreview: job.CanUseLivePreview,
            SavingLivePreview: !job.CanUseLivePreview && job.IsUsingLivePreview && job.IsPending,
            OpenTranscript: done,
            GenerateNotes: done && job.OffersNotes,
            TryAgain: job.State == TranscriptionJobState.Failed,
            Reveal: !job.IsPending,
            Dismiss: job.CanDismiss);
    }

    private static int Percent(double fraction) => (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
}
