using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Hearsay.Core.Audio;

/// <summary>
/// A chunk of 16 kHz mono Float32 samples stamped with the host time of its
/// first sample (PLAN.md 4.1, step 3). Port of <c>TimedChunk</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/AudioMixer.swift.
/// </summary>
/// <remarks>
/// This is the plain interface W3 feeds from NAudio instead of the Mac's
/// AVFoundation and ScreenCaptureKit buffers: already resampled 16 kHz mono
/// samples (see <see cref="MonoResampler"/>) plus a timestamp. <see cref="HostTime"/>
/// is in seconds on one host clock shared by both sources (on Windows the
/// QPC clock of <see cref="HostClock"/>, which WASAPI's
/// <c>IAudioCaptureClient::GetBuffer</c> QPC position also uses), with the
/// recorder's paused time removed, so a pause leaves no gap between sources.
/// Equality compares the samples, not the array reference.
/// </remarks>
public sealed class TimedChunk : IEquatable<TimedChunk>
{
    public TimedChunk(float[] samples, double hostTime)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Samples = samples;
        HostTime = hostTime;
    }

    public float[] Samples { get; }

    public double HostTime { get; }

    public bool Equals(TimedChunk? other) =>
        other is not null && HostTime.Equals(other.HostTime) && Samples.AsSpan().SequenceEqual(other.Samples);

    public override bool Equals(object? obj) => Equals(obj as TimedChunk);

    public override int GetHashCode() => HashCode.Combine(HostTime, Samples.Length);
}

/// <summary>
/// One fixed-size block of mixed output plus the level of each source inside
/// it, for the main meter and the two small per-source meters. Port of
/// <c>MixedChunk</c> in AudioMixer.swift.
/// </summary>
public sealed class MixedChunk : IEquatable<MixedChunk>
{
    public MixedChunk(float[] mixed, double? micRmsDB, double? systemRmsDB)
    {
        ArgumentNullException.ThrowIfNull(mixed);
        Mixed = mixed;
        MicRmsDB = micRmsDB;
        SystemRmsDB = systemRmsDB;
    }

    /// <summary>16 kHz mono samples within [-1, 1].</summary>
    public float[] Mixed { get; }

    /// <summary>
    /// dBFS RMS of the microphone's contribution; null when the microphone
    /// supplied no samples to this block (not started, ended, or lagging).
    /// </summary>
    public double? MicRmsDB { get; }

    /// <summary>dBFS RMS of the system audio's contribution; null as for <see cref="MicRmsDB"/>.</summary>
    public double? SystemRmsDB { get; }

    public bool Equals(MixedChunk? other) =>
        other is not null && Nullable.Equals(MicRmsDB, other.MicRmsDB)
        && Nullable.Equals(SystemRmsDB, other.SystemRmsDB) && Mixed.AsSpan().SequenceEqual(other.Mixed);

    public override bool Equals(object? obj) => Equals(obj as MixedChunk);

    public override int GetHashCode() => HashCode.Combine(Mixed.Length, MicRmsDB, SystemRmsDB);
}

/// <summary>The two recording sources the mixer combines.</summary>
public enum AudioSource
{
    Mic,
    System,
}

/// <summary>
/// Aligns the microphone and system audio on host time and sums them into
/// one 16 kHz mono stream (PLAN.md 4.1, step 3). Port of <c>AudioMixer</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/AudioMixer.swift.
/// </summary>
/// <remarks>
/// <para>The class is the pure mixing state machine: feed it chunks with
/// <see cref="Append(ReadOnlySpan{float}, double, AudioSource)"/>, tell it a
/// source ended with <see cref="Finish"/>, and collect the
/// <see cref="MixedChunk"/>s each call returns. <see cref="Mix"/> runs the same
/// machine over two async sequences. Not thread-safe: use one instance from
/// one context.</para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Positions are sample offsets from the first chunk's host time. Each
/// source keeps a buffer of samples not yet emitted, anchored at an absolute
/// position.</item>
/// <item>A chunk that lands within <see cref="ResyncToleranceSeconds"/> of where
/// the source's previous chunk ended is placed right after it, so timestamp
/// jitter never opens gaps or overlaps. Beyond the tolerance the source
/// resyncs to the chunk's host time: a gap is filled with silence, an overlap
/// is trimmed. This also absorbs slow clock drift between the two devices.</item>
/// <item>A chunk stamped more than <see cref="ClockSanitySeconds"/> away from the
/// other live source is clamped to that distance, so a foreign clock cannot
/// produce hours of padding.</item>
/// <item>Output waits for the slower source, but never by more than
/// <see cref="MaxLagSeconds"/>: a source that is further behind is padded with
/// silence and whatever arrives later for already emitted time is dropped.</item>
/// <item>Where both sources supply samples they are summed and soft-limited
/// (<see cref="SoftLimit"/>), so the result stays within [-1, 1]. Where only one
/// does, its samples pass through unchanged (clamped to [-1, 1]), so a
/// mic-only recording is bit-identical to the unmixed microphone.</item>
/// <item>Output comes in blocks of <see cref="ChunkSize"/> samples; only the
/// final block, emitted once every source has ended, may be shorter.</item>
/// </list>
/// </remarks>
public sealed class AudioMixer
{
    public const double SampleRate = 16_000;

    /// <summary>0.1 s at 16 kHz.</summary>
    public const int ChunkSize = 1600;

    public const double MaxLagSeconds = 0.5;
    public const double ResyncToleranceSeconds = 0.05;

    /// <summary>
    /// A chunk stamped further than this from the other live source is
    /// clamped to this distance (guards against mismatched clocks).
    /// </summary>
    public const double ClockSanitySeconds = 5;

    /// <summary>Below this magnitude the limiter is the identity.</summary>
    public const float LimiterKnee = 0.5f;

    public static readonly int MaxLagSamples = (int)(MaxLagSeconds * SampleRate);
    public static readonly int ResyncToleranceSamples = (int)(ResyncToleranceSeconds * SampleRate);
    public static readonly int ClockSanitySamples = (int)(ClockSanitySeconds * SampleRate);

    private sealed class Lane
    {
        public bool Started;
        public bool Ended;

        /// <summary>Absolute position just after the last placed chunk.</summary>
        public int NextPosition;

        /// <summary>Samples not yet emitted; <c>Buffer[BufferHead]</c> sits at <see cref="BufferStart"/>.</summary>
        public readonly List<float> Buffer = [];
        public int BufferHead;
        public int BufferStart;

        public int BufferedCount => Buffer.Count - BufferHead;

        public int BufferEnd => BufferStart + BufferedCount;

        public void CompactIfNeeded()
        {
            if (BufferHead > 0 && BufferHead >= Buffer.Count / 2)
            {
                Buffer.RemoveRange(0, BufferHead);
                BufferHead = 0;
            }
        }
    }

    private double? anchor;

    /// <summary>Absolute position of the next output sample.</summary>
    private int emitted;

    private readonly Lane mic = new();
    private readonly Lane system = new();

    /// <summary>
    /// <paramref name="sources"/> lists the sources that will deliver chunks;
    /// any other source counts as already ended. Null means both.
    /// </summary>
    public AudioMixer(IEnumerable<AudioSource>? sources = null)
    {
        HashSet<AudioSource> active = sources is null ? [AudioSource.Mic, AudioSource.System] : [.. sources];
        foreach (var source in AllSources)
        {
            if (!active.Contains(source))
            {
                LaneFor(source).Ended = true;
            }
        }
    }

    private static readonly AudioSource[] AllSources = [AudioSource.Mic, AudioSource.System];

    private Lane LaneFor(AudioSource source) => source == AudioSource.Mic ? mic : system;

    /// <summary>True once every source has ended and all output was returned.</summary>
    public bool IsFinished =>
        mic.Ended && system.Ended && mic.BufferedCount == 0 && system.BufferedCount == 0;

    // Input

    /// <summary>Adds one chunk from <paramref name="source"/> and returns the blocks that became ready.</summary>
    public IReadOnlyList<MixedChunk> Append(TimedChunk chunk, AudioSource source)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return Append(chunk.Samples, chunk.HostTime, source);
    }

    /// <summary>
    /// Adds <paramref name="samples"/> from <paramref name="source"/>, whose first
    /// sample was captured at <paramref name="hostTime"/>, and returns the blocks
    /// that became ready. The samples are copied.
    /// </summary>
    public IReadOnlyList<MixedChunk> Append(ReadOnlySpan<float> samples, double hostTime, AudioSource source)
    {
        var lane = LaneFor(source);
        if (lane.Ended || samples.IsEmpty)
        {
            return [];
        }
        anchor ??= hostTime;
        var baseTime = anchor ?? hostTime;
        var position = (int)Math.Round((hostTime - baseTime) * SampleRate, MidpointRounding.AwayFromZero);
        var other = LaneFor(source == AudioSource.Mic ? AudioSource.System : AudioSource.Mic);
        if (other.Started && !other.Ended)
        {
            // Both sources run in real time, so a stamp far from the other
            // source means a clock mismatch, not a real offset.
            position = Math.Min(Math.Max(position, other.NextPosition - ClockSanitySamples),
                                other.NextPosition + ClockSanitySamples);
        }
        if (position < 0 && emitted == 0)
        {
            // Earlier than anything seen and nothing emitted yet: move the
            // anchor back instead of losing the start of this source.
            Rebase(-position);
            position = 0;
        }

        if (lane.Started && Math.Abs(position - lane.NextPosition) <= ResyncToleranceSamples)
        {
            position = lane.NextPosition;
        }
        lane.Started = true;
        lane.NextPosition = position + samples.Length;

        if (position < emitted)
        {
            samples = samples[Math.Min(samples.Length, emitted - position)..];
            position = emitted;
        }
        if (lane.BufferedCount == 0)
        {
            lane.Buffer.Clear();
            lane.BufferHead = 0;
            lane.BufferStart = position;
        }
        else if (position > lane.BufferEnd)
        {
            AppendZeros(lane.Buffer, position - lane.BufferEnd);
        }
        else if (position < lane.BufferEnd)
        {
            samples = samples[Math.Min(samples.Length, lane.BufferEnd - position)..];
        }
        lane.Buffer.AddRange(samples);
        return Drain();
    }

    /// <summary>
    /// Marks <paramref name="source"/> as ended and returns the blocks that
    /// became ready; once both sources ended this includes the final, possibly
    /// short, block.
    /// </summary>
    public IReadOnlyList<MixedChunk> Finish(AudioSource source)
    {
        LaneFor(source).Ended = true;
        return Drain();
    }

    private static void AppendZeros(List<float> buffer, int count)
    {
        buffer.EnsureCapacity(buffer.Count + count);
        for (var index = 0; index < count; index++)
        {
            buffer.Add(0);
        }
    }

    private void Rebase(int shift)
    {
        if (anchor is double value)
        {
            anchor = value - shift / SampleRate;
        }
        foreach (var source in AllSources)
        {
            var lane = LaneFor(source);
            if (lane.Started)
            {
                lane.NextPosition += shift;
                lane.BufferStart += shift;
            }
        }
    }

    // Output

    private int Frontier(Lane lane) => lane.BufferedCount == 0 ? emitted : lane.BufferEnd;

    private List<MixedChunk> Drain()
    {
        var highest = Math.Max(Frontier(mic), Frontier(system));
        var output = new List<MixedChunk>();
        if (mic.Ended && system.Ended)
        {
            while (emitted < highest)
            {
                output.Add(TakeBlock(Math.Min(ChunkSize, highest - emitted)));
            }
            return output;
        }
        var slowest = int.MaxValue;
        foreach (var lane in new[] { mic, system })
        {
            if (!lane.Ended)
            {
                slowest = Math.Min(slowest, Frontier(lane));
            }
        }
        var ready = Math.Max(slowest, highest - MaxLagSamples);
        while (emitted + ChunkSize <= ready)
        {
            output.Add(TakeBlock(ChunkSize));
        }
        return output;
    }

    private MixedChunk TakeBlock(int count)
    {
        var micPart = Take(count, mic);
        var systemPart = Take(count, system);
        emitted += count;
        var mixed = new float[count];
        if (micPart is not null && systemPart is not null)
        {
            for (var index = 0; index < count; index++)
            {
                mixed[index] = SoftLimit(micPart[index] + systemPart[index]);
            }
        }
        else if ((micPart ?? systemPart) is float[] only)
        {
            for (var index = 0; index < count; index++)
            {
                mixed[index] = Math.Min(1f, Math.Max(-1f, only[index]));
            }
        }
        return new MixedChunk(
            mixed,
            micPart is null ? null : LevelMeter.RmsDB(micPart.AsSpan()),
            systemPart is null ? null : LevelMeter.RmsDB(systemPart.AsSpan()));
    }

    /// <summary>
    /// Removes the samples covering [emitted, emitted + count) from the lane.
    /// Returns null when the lane has none there; uncovered samples are 0.
    /// </summary>
    private float[]? Take(int count, Lane lane)
    {
        var end = emitted + count;
        if (lane.BufferedCount <= 0 || lane.BufferStart >= end)
        {
            return null;
        }
        if (lane.BufferStart < emitted)
        {
            // Not expected (input is trimmed to `emitted`), but never index
            // before the window.
            var stale = Math.Min(lane.BufferedCount, emitted - lane.BufferStart);
            lane.BufferHead += stale;
            lane.BufferStart += stale;
            if (lane.BufferedCount <= 0)
            {
                lane.CompactIfNeeded();
                return null;
            }
        }
        var window = new float[count];
        var offset = lane.BufferStart - emitted;
        var available = Math.Min(lane.BufferedCount, end - lane.BufferStart);
        lane.Buffer.CopyTo(lane.BufferHead, window, offset, available);
        lane.BufferHead += available;
        lane.BufferStart += available;
        lane.CompactIfNeeded();
        return window;
    }

    /// <summary>
    /// Identity below <see cref="LimiterKnee"/>, then a tanh curve that
    /// approaches 1: continuous, with slope 1 at the knee, and always within (-1, 1).
    /// </summary>
    public static float SoftLimit(float value)
    {
        var magnitude = Math.Abs(value);
        if (!(magnitude > LimiterKnee))
        {
            return value;
        }
        const float headroom = 1 - LimiterKnee;
        var limited = LimiterKnee + headroom * MathF.Tanh((magnitude - LimiterKnee) / headroom);
        return value < 0 ? -limited : limited;
    }

    // Streams

    private readonly record struct MixEvent(AudioSource Source, TimedChunk? Chunk);

    /// <summary>
    /// Mixes two recorder streams. A null source is treated as absent, so
    /// <c>Mix(stream, null)</c> re-blocks the microphone unchanged. The result
    /// finishes after both sources have finished. <paramref name="onSourceEnd"/>
    /// is called (on an arbitrary thread) when each source's stream finishes,
    /// before the mixer sees the end, so the owner can react to a device that
    /// went away. Ending the enumeration early cancels the feeders.
    /// </summary>
    public static async IAsyncEnumerable<MixedChunk> Mix(
        IAsyncEnumerable<TimedChunk>? mic,
        IAsyncEnumerable<TimedChunk>? system,
        Action<AudioSource>? onSourceEnd = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var events = Channel.CreateUnbounded<MixEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var feederCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sources = new List<AudioSource>();
        var feeders = new List<Task>();
        foreach (var (source, stream) in new[] { (AudioSource.Mic, mic), (AudioSource.System, system) })
        {
            if (stream is null)
            {
                continue;
            }
            sources.Add(source);
            var token = feederCancellation.Token;
            feeders.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in stream.WithCancellation(token).ConfigureAwait(false))
                    {
                        events.Writer.TryWrite(new MixEvent(source, chunk));
                    }
                }
                finally
                {
                    // A source that fails counts as ended, like a finished
                    // stream; a cancelled one is being torn down.
                    if (!token.IsCancellationRequested)
                    {
                        onSourceEnd?.Invoke(source);
                        events.Writer.TryWrite(new MixEvent(source, null));
                    }
                }
            }, CancellationToken.None));
        }
        if (sources.Count == 0)
        {
            yield break;
        }

        var mixer = new AudioMixer(sources);
        try
        {
            while (!mixer.IsFinished
                   && await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!mixer.IsFinished && events.Reader.TryRead(out var item))
                {
                    var blocks = item.Chunk is TimedChunk chunk
                        ? mixer.Append(chunk, item.Source)
                        : mixer.Finish(item.Source);
                    foreach (var block in blocks)
                    {
                        yield return block;
                    }
                }
            }
        }
        finally
        {
            await feederCancellation.CancelAsync().ConfigureAwait(false);
            events.Writer.TryComplete();
            // Cancelled or failed feeders have nothing left to deliver.
            await Task.WhenAll(feeders).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

/// <summary>
/// Shared output of the microphone and system audio recorders: an untimed and
/// a timed stream of the same chunks, plus pause bookkeeping. Port of
/// <c>ChunkFanout</c> in AudioMixer.swift, with <see cref="Channel{T}"/> in
/// place of <c>AsyncStream</c>.
/// </summary>
/// <remarks>
/// Both streams buffer from the moment the recorder exists. A recording is
/// read through one of them: the first one accessed claims the chunks and the
/// other is completed and released, so an unread stream never grows for the
/// length of a meeting. Thread-safe; the capture callbacks yield here.
/// </remarks>
public sealed class ChunkFanout
{
    private readonly Lock gate = new();
    private Channel<float[]>? plain = Channel.CreateUnbounded<float[]>();
    private Channel<TimedChunk>? timed = Channel.CreateUnbounded<TimedChunk>();
    private bool claimed;
    private bool paused;
    private double pausedAt;
    private double pausedTotal;

    /// <summary>The untimed chunks; the first of the two streams accessed claims them.</summary>
    public ChannelReader<float[]> Samples
    {
        get
        {
            lock (gate)
            {
                if (!claimed)
                {
                    claimed = true;
                    timed?.Writer.TryComplete();
                    timed = null;
                }
                return plain?.Reader ?? FinishedReader<float[]>();
            }
        }
    }

    /// <summary>The chunks with paused time removed from their host time.</summary>
    public ChannelReader<TimedChunk> TimedSamples
    {
        get
        {
            lock (gate)
            {
                if (!claimed)
                {
                    claimed = true;
                    plain?.Writer.TryComplete();
                    plain = null;
                }
                return timed?.Reader ?? FinishedReader<TimedChunk>();
            }
        }
    }

    /// <summary>
    /// Yields a chunk whose first sample was captured at <paramref name="hostTime"/>.
    /// Chunks that arrive while paused are dropped.
    /// </summary>
    public void Yield(float[] samples, double hostTime)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
        {
            return;
        }
        lock (gate)
        {
            if (paused)
            {
                return;
            }
            plain?.Writer.TryWrite(samples);
            timed?.Writer.TryWrite(new TimedChunk(samples, hostTime - pausedTotal));
        }
    }

    public void Pause(double hostTime)
    {
        lock (gate)
        {
            if (paused)
            {
                return;
            }
            paused = true;
            pausedAt = hostTime;
        }
    }

    public void Resume(double hostTime)
    {
        lock (gate)
        {
            if (!paused)
            {
                return;
            }
            paused = false;
            pausedTotal += Math.Max(0, hostTime - pausedAt);
        }
    }

    public void Finish()
    {
        lock (gate)
        {
            plain?.Writer.TryComplete();
            timed?.Writer.TryComplete();
        }
    }

    private static ChannelReader<T> FinishedReader<T>()
    {
        var channel = Channel.CreateUnbounded<T>();
        channel.Writer.TryComplete();
        return channel.Reader;
    }
}

/// <summary>
/// The host clock <see cref="TimedChunk.HostTime"/> uses, in seconds. Port of
/// <c>currentHostTimeSeconds()</c> in AudioMixer.swift: the Mac reads
/// <c>mach_absolute_time</c>; Windows reads the performance counter (QPC),
/// the clock WASAPI stamps capture buffers with.
/// </summary>
public static class HostClock
{
    /// <summary>Current host time in seconds.</summary>
    public static double NowSeconds() => FromTicks(Stopwatch.GetTimestamp());

    /// <summary>Converts a <see cref="Stopwatch"/> (QPC) timestamp to seconds.</summary>
    public static double FromTicks(long ticks) => (double)ticks / Stopwatch.Frequency;
}
