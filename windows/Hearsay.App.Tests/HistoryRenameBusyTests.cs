using Hearsay.App.Features.History;
using Hearsay.App.Features.MenuBar;
using Hearsay.Core.History;
using Hearsay.Core.Transcription;
using static Hearsay.App.Tests.QueueRig;

namespace Hearsay.App.Tests;

/// <summary>
/// History's Rename is off for output-folder stems the transcription queue
/// works on (PLAN.md 4.9 "History's Rename"): the path the shell takes,
/// <c>Queue.BusyFiles</c> into <see cref="RecordingStatus.SetBusyFiles"/> into
/// <see cref="HistoryViewModel.BusyStems"/> and <see cref="HistoryViewModel.CanRename"/>.
/// Mirrors <c>busyStems(of:)</c> and <c>canRename</c> in
/// mac/Hearsay/Features/History/HistoryViewModel.swift. The meeting whose
/// notes History is generating is checked with the notes flow itself in the
/// UI snapshots (NotesSnapshots).
/// </summary>
public sealed class HistoryRenameBusyTests
{
    private static HistoryEntry Entry(string stem) =>
        new(stem, null, null, stem + ".srt", null, null, stem + ".wav", null);

    [Fact]
    public Task RenameIsOffForAFailedRecordingBeingRetriedAndOnAgainWhenItIsDone() => RunAsync(FinalPassTiming.WhenIdle, async rig =>
    {
        var status = new RecordingStatus();
        var history = new HistoryViewModel(null);
        var queue = rig.Queue;
        queue.SetSessionActive(true);
        var job = rig.Add();
        rig.Engine.FailStep = () => new InvalidOperationException("boom");
        queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Failed, "the failure");
        rig.Engine.FailStep = null;
        var stem = Path.GetFileNameWithoutExtension(job.Wav) ?? throw new InvalidOperationException("no WAV");

        // A failed job's files are the user's again.
        status.SetBusyFiles(queue.BusyFiles);
        Assert.True(history.CanRename(Entry(stem), HistoryViewModel.BusyStems(status)));

        // A retry reads its WAV in the output folder: not renamable, whatever the case of the stem.
        queue.SetSessionActive(true);
        queue.Retry(job);
        status.SetBusyFiles(queue.BusyFiles);
        var busy = HistoryViewModel.BusyStems(status);
        Assert.False(history.CanRename(Entry(stem), busy));
        Assert.False(history.CanRename(Entry(stem.ToUpperInvariant()), busy));
        Assert.True(history.CanRename(Entry("2020-01-01_00-00-00"), busy));

        queue.SetSessionActive(false);
        await WaitUntil(() => job.State == TranscriptionJobState.Done, "the retry");
        status.SetBusyFiles(queue.BusyFiles);
        Assert.True(history.CanRename(Entry(Path.GetFileNameWithoutExtension(job.Srt) ?? stem), HistoryViewModel.BusyStems(status)));
    });
}
