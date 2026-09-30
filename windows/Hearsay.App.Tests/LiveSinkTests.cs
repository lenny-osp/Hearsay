using Hearsay.App.Features.Transcription;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="LiveSink"/> (the port of mac/Hearsay/Features/Transcription/LiveSink.swift,
/// PLAN.md 4.9): chunks wait for the session language, a closed sink drops
/// them, results reach whoever owns the sink now, and the drain waits for the
/// live task. Runs on a test UI thread, as the sink is used on the real one.
/// </summary>
public sealed class LiveSinkTests
{
    private static async Task RunAsync(Func<Task> body)
    {
        using var ui = new UiThread();
        await ui.RunAsync(body);
    }

    [Fact]
    public Task ChunksWaitForTheLanguageAndTheWaitingCountFollowsThem() => RunAsync(async () =>
    {
        var sink = new LiveSink(null);
        Assert.Null(sink.Language);
        sink.ChunkQueued();
        sink.ChunkQueued();
        Assert.Equal(2, sink.Waiting);
        Assert.True(sink.HasPendingChunks);
        var waiting = sink.WaitForLanguageAsync();
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);

        sink.SetLanguage(TranscriptLanguage.German);
        Assert.Equal(TranscriptLanguage.German, await waiting);
        Assert.Equal(TranscriptLanguage.German, sink.Language);
        Assert.Equal(ChineseScript.Traditional, new LiveSink(TranscriptLanguage.ChineseTaiwan).Script);
        Assert.Null(sink.Script);

        var results = new List<int>();
        sink.OnResult = (index, outcome) =>
        {
            results.Add(index);
            Assert.Equal(["cue"], outcome.Cues!.Select(cue => cue.Text));
        };
        sink.Deliver(1, new SinkOutcome([new TranscriptSegment(0, 1, "cue")], null));
        sink.Deliver(2, null);
        Assert.Equal([1], results);
        Assert.Equal(0, sink.Waiting);
        sink.Deliver(3, null);
        Assert.Equal(0, sink.Waiting);
    });

    [Fact]
    public Task ClosingReleasesAWaitingChunkWithoutALanguage() => RunAsync(async () =>
    {
        var sink = new LiveSink(null);
        var waiting = sink.WaitForLanguageAsync();
        sink.Close();
        Assert.Null(await waiting);
        Assert.True(sink.IsClosed);
        Assert.Null(await new LiveSink(TranscriptLanguage.English).Also(s => s.Close()).WaitForLanguageAsync());
    });

    [Fact]
    public Task TheResultHandlerCanMoveToAnotherOwner() => RunAsync(() =>
    {
        var sink = new LiveSink(TranscriptLanguage.English);
        var session = new List<string>();
        var job = new List<string>();
        sink.OnResult = (_, outcome) => session.Add(outcome.Cues![0].Text);
        sink.Deliver(1, new SinkOutcome([new TranscriptSegment(0, 1, "before")], null));
        sink.OnResult = (_, outcome) => job.Add(outcome.Cues![0].Text);
        sink.Deliver(2, new SinkOutcome([new TranscriptSegment(0, 1, "after")], null));
        Assert.Equal(["before"], session);
        Assert.Equal(["after"], job);
        return Task.CompletedTask;
    });

    [Fact]
    public Task DrainWaitsForTheLiveTask() => RunAsync(async () =>
    {
        var sink = new LiveSink(TranscriptLanguage.English);
        await sink.DrainAsync();
        var release = new TaskCompletionSource();
        sink.Task = release.Task;
        var drained = sink.DrainAsync();
        await Task.Delay(30);
        Assert.False(drained.IsCompleted);
        release.SetResult();
        await drained;
    });

    [Fact]
    public Task ChangedIsRaisedWhenTheWaitingCountChanges() => RunAsync(() =>
    {
        var sink = new LiveSink(TranscriptLanguage.English);
        var changes = 0;
        sink.Changed += (_, _) => changes++;
        sink.ChunkQueued();
        sink.Deliver(1, null);
        Assert.Equal(2, changes);
        return Task.CompletedTask;
    });
}

internal static class LiveSinkTestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
