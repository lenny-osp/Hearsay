using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Transcription;

/// <summary>
/// Where one recording session's live chunks go (PLAN.md 4.1 step 4, 4.9).
/// <c>RecordingController</c> creates one per session; its live task waits
/// here for the session language and hands every result to
/// <see cref="OnResult"/>. When the session ends normally the sink moves to
/// the session's queue job with the live task still running, so the tail of
/// the live preview keeps being transcribed (as foreground work) and lands in
/// the job, while the next session gets a fresh sink.
/// Port of mac/Hearsay/Features/Transcription/LiveSink.swift. Use from the UI
/// thread; the live task's continuations return to it.
/// </summary>
internal sealed class LiveSink
{
    private readonly List<TaskCompletionSource> waiters = [];

    public LiveSink(TranscriptLanguage? language)
    {
        SetLanguage(language);
    }

    /// <summary>Something the views show changed (<see cref="Waiting"/>).</summary>
    public event EventHandler? Changed;

    /// <summary>The language live chunks are transcribed in; null while Auto is undecided (chunks wait).</summary>
    public TranscriptLanguage? Language { get; private set; }

    /// <summary>The script the cues are converted to (zh only).</summary>
    public ChineseScript? Script { get; private set; }

    /// <summary>Live chunks queued or being transcribed.</summary>
    public int Waiting { get; private set; }

    /// <summary>Closed: chunks still queued are dropped without being transcribed.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Receives each chunk's cues (already offset and converted) or error.</summary>
    public Action<int, SinkOutcome>? OnResult { get; set; }

    /// <summary>The task that transcribes the chunks in order; it ends once the session's chunk stream is finished and drained.</summary>
    public Task? Task { get; set; }

    /// <summary>Chunks still to come: queued, being transcribed, or waiting for the language.</summary>
    public bool HasPendingChunks => Waiting > 0;

    public void SetLanguage(TranscriptLanguage? language)
    {
        Language = language;
        if (language is { } known) Script = known.ChineseScript();
        if (language is null) return;
        ResumeWaiters();
    }

    /// <summary>Drops the chunks still queued (a chunk already in the engine finishes, and its result is still delivered).</summary>
    public void Close()
    {
        IsClosed = true;
        ResumeWaiters();
    }

    public void ChunkQueued()
    {
        Waiting++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The language once it is known; null when the sink was closed first.</summary>
    public async Task<TranscriptLanguage?> WaitForLanguageAsync()
    {
        while (!IsClosed && Language is null)
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(waiter);
            await waiter.Task.ConfigureAwait(true);
        }
        return IsClosed ? null : Language;
    }

    /// <summary>A chunk was skipped (null outcome: closed) or transcribed.</summary>
    public void Deliver(int index, SinkOutcome? outcome)
    {
        Waiting = Math.Max(0, Waiting - 1);
        if (outcome is not null) OnResult?.Invoke(index, outcome);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Waits until every chunk of the session is done (or dropped).</summary>
    public async Task DrainAsync()
    {
        if (Task is { } running) await running.ConfigureAwait(true);
    }

    private void ResumeWaiters()
    {
        var pending = waiters.ToArray();
        waiters.Clear();
        foreach (var waiter in pending) waiter.TrySetResult();
    }
}

/// <summary>One live chunk's result: its cues, or the error that stopped it.</summary>
internal sealed record SinkOutcome(IReadOnlyList<TranscriptSegment>? Cues, Exception? Error);
