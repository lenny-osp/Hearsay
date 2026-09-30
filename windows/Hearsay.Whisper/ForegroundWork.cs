namespace Hearsay.Whisper;

/// <summary>
/// Counts foreground calls (live chunks, language detection, File mode,
/// History re-runs) that are waiting for or running in
/// <see cref="WhisperEngine"/>. Lock-free, so a background decode that holds
/// the engine's lock can read it from its <c>shouldYield</c> callback between
/// windows (PLAN.md 4.9 and 18.10). Port of <c>ForegroundWork</c> in
/// mac/Hearsay/Features/Transcription/WhisperEngine.swift.
/// </summary>
public sealed class ForegroundWork
{
    private int count;

    /// <summary>Foreground calls counted now.</summary>
    public int Waiting => Volatile.Read(ref count);

    public void Begin() => Interlocked.Increment(ref count);

    public void End() => Interlocked.Decrement(ref count);

    /// <summary>Counts one foreground call until the returned token is disposed (idempotent).</summary>
    public ForegroundScope Enter()
    {
        Begin();
        return new ForegroundScope(this);
    }
}

/// <summary>
/// One counted foreground call: <c>using var fg = engine.EnterForeground();</c>
/// before calling the engine, so the call is counted while it waits for the
/// engine's lock. Disposing twice decrements once.
/// </summary>
public sealed class ForegroundScope : IDisposable
{
    private ForegroundWork? owner;

    internal ForegroundScope(ForegroundWork owner) => this.owner = owner;

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.End();
}
