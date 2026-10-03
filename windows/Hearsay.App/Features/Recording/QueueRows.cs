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
    bool Dismiss,
    bool Transcribe = false,
    bool Hold = false,
    bool Trash = false);

/// <summary>
/// The rules of the Record tab's "Transcription queue" list (PLAN.md 4.9
/// "UI"), free of any control so they can be tested: when the list shows,
/// each row's state text, and its actions per state. Port of the logic in
/// <c>QueueRow</c> (<c>stateText</c>, <c>actions</c>) and of the
/// <c>featuredJob</c> / <c>jobs.isEmpty</c> branch of <c>body</c> in
/// mac/Hearsay/Features/Recording/RecordView.swift. The row control is
/// <see cref="QueueRowView"/>. With "When I start them" (PLAN.md 4.11, Windows
/// 18.12) a held job reads "Not transcribed yet" or "On hold · N%" and offers
/// Transcribe; a released pending job offers Hold; the other timings change nothing. A held,
/// idle row also offers "Move to Recycle Bin…" (<see cref="CanTrash"/>, 4.11 of 2026-10-02).
/// </summary>
internal static class QueueRows
{
    /// <summary>
    /// The list shows below the session whenever the single-meeting view does
    /// not: a session is active, two or more jobs exist, or the timing is Manual (PLAN.md
    /// 4.11, 2026-10-02: every recording is a row there, even the only one). With no
    /// session and one job under another timing that job is shown as the single meeting
    /// (<see cref="TranscriptionQueue.FeaturedJob"/>); an empty queue shows nothing.
    /// </summary>
    public static bool ShowsList(TranscriptionQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return queue.FeaturedJob is null && queue.Jobs.Count > 0;
    }

    /// <summary>
    /// Held (PLAN.md 4.11): the timing is <see cref="FinalPassTiming.Manual"/> and the job
    /// is pending and not released. The queue's own rule, so the row never disagrees with it.
    /// </summary>
    public static bool IsHeld(TranscriptionJob job, FinalPassTiming timing)
    {
        ArgumentNullException.ThrowIfNull(job);
        return TranscriptionQueuePolicy.IsHeld(timing, new(job.Id, job.State, job.IsReleased));
    }

    /// <summary>
    /// The state under the row's name: Waiting, Paused while recording, Transcribing N%, Done or
    /// Failed; for a held job "Not transcribed yet", or "On hold · N%" once it had started.
    /// </summary>
    public static string StateText(TranscriptionJob job, bool pausedForSession, bool held = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsRerunning) return Strings.QueueTranscribing(Percent(job.RerunProgress));
        return job.State switch
        {
            TranscriptionJobState.Waiting => held ? Strings.QueueNotTranscribedYet : pausedForSession ? Strings.QueuePausedWhileRecording : Strings.QueueWaiting,
            TranscriptionJobState.Running => held ? Strings.QueueOnHold(Percent(job.Progress)) : Strings.QueueTranscribing(Percent(job.Progress)),
            TranscriptionJobState.Suspended => held ? Strings.QueueOnHold(Percent(job.Progress))
                : pausedForSession ? Strings.QueuePausedWhileRecording : Strings.QueueTranscribing(Percent(job.Progress)),
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

    /// <summary>
    /// The buttons of the row by state. <paramref name="timing"/>: Transcribe on a held job
    /// (Manual only); Hold on a released pending job while the timing is Manual and the job is not
    /// saving its live preview. Trash: <see cref="CanTrash"/>. The Mac's other actions are unchanged.
    /// </summary>
    public static QueueRowActions Actions(TranscriptionJob job, FinalPassTiming timing = FinalPassTiming.Immediate)
    {
        ArgumentNullException.ThrowIfNull(job);
        var done = job.State == TranscriptionJobState.Done && job.Srt is not null;
        var held = IsHeld(job, timing);
        return new QueueRowActions(
            UseLivePreview: job.CanUseLivePreview,
            SavingLivePreview: !job.CanUseLivePreview && job.IsUsingLivePreview && job.IsPending,
            OpenTranscript: done,
            GenerateNotes: done && job.OffersNotes,
            TryAgain: job.State == TranscriptionJobState.Failed,
            Reveal: !job.IsPending,
            Dismiss: job.CanDismiss,
            Transcribe: held,
            Hold: CanHold(job, timing),
            Trash: CanTrash(job, timing));
    }

    /// <summary>
    /// "Move to Recycle Bin…" applies: the job is held and idle (waiting or suspended, no step
    /// running, not saving its live preview). The queue's own rule
    /// (<see cref="TranscriptionQueuePolicy.CanTrash"/>), so the row never disagrees with it.
    /// </summary>
    public static bool CanTrash(TranscriptionJob job, FinalPassTiming timing)
    {
        ArgumentNullException.ThrowIfNull(job);
        return TranscriptionQueuePolicy.CanTrash(timing, new(job.Id, job.State, job.IsReleased), job.IsBusy);
    }

    /// <summary>Hold applies: the timing is Manual and the job is pending, released, and not saving its live preview.</summary>
    public static bool CanHold(TranscriptionJob job, FinalPassTiming timing)
    {
        ArgumentNullException.ThrowIfNull(job);
        return timing == FinalPassTiming.Manual && job.IsPending && job.IsReleased && !job.IsUsingLivePreview;
    }

    private static int Percent(double fraction) => (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
}
