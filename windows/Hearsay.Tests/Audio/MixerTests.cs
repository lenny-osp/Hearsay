using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hearsay.Core.Audio;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/MixerTests.swift: pure tests
/// of <see cref="AudioMixer"/> with synthetic <see cref="TimedChunk"/>s. None
/// of them touches a capture device.
/// </summary>
public class MixerTests
{
    private const double Rate = AudioMixer.SampleRate;

    private static float[] Sine(float amplitude, int count, double frequency = 440, int offset = 0) =>
        Enumerable.Range(0, count)
            .Select(index => amplitude * (float)Math.Sin(2 * Math.PI * frequency * (index + offset) / Rate))
            .ToArray();

    private static float[] Repeat(float value, int count) => Enumerable.Repeat(value, count).ToArray();

    /// <summary>Splits <paramref name="samples"/> into chunks of <paramref name="size"/> stamped from <paramref name="start"/> seconds.</summary>
    private static List<TimedChunk> Chunks(float[] samples, int size, double start = 100)
    {
        var chunks = new List<TimedChunk>();
        for (var index = 0; index < samples.Length; index += size)
        {
            chunks.Add(new TimedChunk(samples[index..Math.Min(samples.Length, index + size)], start + index / Rate));
        }
        return chunks;
    }

    /// <summary>Feeds both sources interleaved by host time, then ends both.</summary>
    private static List<MixedChunk> Run(IEnumerable<TimedChunk> mic, IEnumerable<TimedChunk> system, AudioMixer? mixer = null)
    {
        mixer ??= new AudioMixer();
        var output = new List<MixedChunk>();
        var events = mic.Select(c => (Chunk: c, Source: AudioSource.Mic))
            .Concat(system.Select(c => (Chunk: c, Source: AudioSource.System)))
            .OrderBy(e => e.Chunk.HostTime); // stable, like Swift's sort on these inputs
        foreach (var (chunk, source) in events)
        {
            output.AddRange(mixer.Append(chunk, source));
        }
        output.AddRange(mixer.Finish(AudioSource.Mic));
        output.AddRange(mixer.Finish(AudioSource.System));
        Assert.True(mixer.IsFinished);
        return output;
    }

    private static float[] Flat(IEnumerable<MixedChunk> chunks) => chunks.SelectMany(c => c.Mixed).ToArray();

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void InPhaseSinesSumToDoubleBelowTheKnee()
    {
        var wave = Sine(0.2f, 16_000);
        var output = Flat(Run(Chunks(wave, 1600), Chunks(wave, 1600)));
        Assert.Equal(wave.Length, output.Length);
        for (var index = 0; index < wave.Length; index++)
        {
            Near(2 * wave[index], output[index], 1e-6);
        }
        Near(0.4, output.Max(Math.Abs), 0.001);
    }

    [Fact]
    public void InPhaseSinesAboveTheKneeAreLimited()
    {
        var wave = Sine(0.45f, 16_000);
        var output = Flat(Run(Chunks(wave, 1600), Chunks(wave, 1600)));
        Assert.Equal(wave.Length, output.Length);
        var peak = output.Max(Math.Abs);
        // About double (0.9) before limiting, then pulled below it.
        Assert.True(peak > 0.8);
        Assert.True(peak < 0.9);
        Assert.All(output, value => Assert.True(Math.Abs(value) < 1));
        for (var index = 0; index < wave.Length; index++)
        {
            Near(AudioMixer.SoftLimit(2 * wave[index]), output[index], 1e-6);
        }
    }

    [Fact]
    public void ConstantNearFullScaleOnBothStaysWithinOne()
    {
        var constant = Repeat(0.9f, 8000);
        var output = Flat(Run(Chunks(constant, 800), Chunks(constant, 800)));
        Assert.Equal(8000, output.Length);
        Assert.All(output, value => Assert.True(value <= 1.0f && value > 0.9f));
        var negative = constant.Select(v => -v).ToArray();
        var negativeOutput = Flat(Run(Chunks(negative, 800), Chunks(negative, 800)));
        Assert.All(negativeOutput, value => Assert.True(value >= -1.0f && value < -0.9f));
    }

    [Fact]
    public void SoftLimitIsIdentityBelowKneeAndBounded()
    {
        Assert.Equal(0.3f, AudioMixer.SoftLimit(0.3f));
        Assert.Equal(-0.5f, AudioMixer.SoftLimit(-0.5f));
        Assert.True(AudioMixer.SoftLimit(1.8f) < 1);
        Assert.True(AudioMixer.SoftLimit(-1.8f) > -1);
        Assert.True(AudioMixer.SoftLimit(100) <= 1);
        Assert.True(AudioMixer.SoftLimit(0.6f) < 0.6f);
        Assert.True(AudioMixer.SoftLimit(0.6f) > AudioMixer.SoftLimit(0.55f));
    }

    [Fact]
    public void SourceStartingLateIsPaddedWithSilenceNotDropped()
    {
        var mic = Sine(0.2f, 16_000);
        const int late = 4800; // 0.3 s
        var system = Repeat(0.1f, 16_000 - late);
        var blocks = Run(Chunks(mic, 1600), Chunks(system, 1600, start: 100 + 0.3));
        var output = Flat(blocks);
        Assert.Equal(16_000, output.Length);
        for (var index = 0; index < late; index++)
        {
            Near(mic[index], output[index], 1e-6);
        }
        for (var index = late; index < 16_000; index++)
        {
            Near(mic[index] + 0.1f, output[index], 1e-6);
        }
        // Every system sample made it into the mix.
        var systemEnergy = output.Zip(mic).Sum(pair => (double)(pair.First - pair.Second));
        Near(0.1 * system.Length, systemEnergy, 0.01);
        Assert.Null(blocks[0].SystemRmsDB);
        Assert.NotNull(blocks[0].MicRmsDB);
        Assert.NotNull(blocks[4].SystemRmsDB);
    }

    [Fact]
    public void LateSourceArrivingFirstStillLinesUp()
    {
        // The system chunk stamped 0.3 s later is delivered before any mic
        // chunk; the anchor moves back so the mic keeps its start.
        var mic = Repeat(0.2f, 3200);
        var system = Repeat(0.1f, 1600);
        var mixer = new AudioMixer();
        var output = new List<MixedChunk>();
        output.AddRange(mixer.Append(new TimedChunk(system, 10.3), AudioSource.System));
        output.AddRange(mixer.Append(new TimedChunk(mic, 10.0), AudioSource.Mic));
        output.AddRange(mixer.Finish(AudioSource.System));
        output.AddRange(mixer.Finish(AudioSource.Mic));
        var flat = Flat(output);
        Assert.Equal(6400, flat.Length);
        Assert.Equal(0.2f, flat[0]);
        Near(0.1, flat[4800], 1e-6);
        Near(0.2, flat[3199], 1e-6);
        Assert.Equal(0f, flat[3200]);
    }

    [Fact]
    public void SourceEndingEarlyLeavesTheOtherUnchanged()
    {
        var mic = Sine(0.3f, 32_000);
        var system = Sine(0.1f, 8000, frequency: 1000);
        var mixer = new AudioMixer();
        var output = new List<MixedChunk>();
        var micChunks = Chunks(mic, 1600);
        var systemChunks = Chunks(system, 1600);
        for (var index = 0; index < micChunks.Count; index++)
        {
            output.AddRange(mixer.Append(micChunks[index], AudioSource.Mic));
            if (index < systemChunks.Count)
            {
                output.AddRange(mixer.Append(systemChunks[index], AudioSource.System));
            }
            else if (index == systemChunks.Count)
            {
                output.AddRange(mixer.Finish(AudioSource.System));
            }
        }
        output.AddRange(mixer.Finish(AudioSource.Mic));
        var flat = Flat(output);
        Assert.Equal(mic.Length, flat.Length);
        for (var index = 0; index < 8000; index++)
        {
            Near(mic[index] + system[index], flat[index], 1e-6);
        }
        for (var index = 8000; index < mic.Length; index++)
        {
            Assert.Equal(mic[index], flat[index]);
        }
        Assert.Null(output[^1].SystemRmsDB);
    }

    [Fact]
    public void MicOnlyPassesThroughBitIdentical()
    {
        var mic = Sine(0.95f, 10_000);
        var mixer = new AudioMixer([AudioSource.Mic]);
        var output = new List<MixedChunk>();
        foreach (var chunk in Chunks(mic, 1024))
        {
            output.AddRange(mixer.Append(chunk, AudioSource.Mic));
        }
        // No waiting on an absent source: full blocks come out right away.
        Assert.Equal(10_000 / 1600, output.Count);
        output.AddRange(mixer.Finish(AudioSource.Mic));
        Assert.Equal(mic, Flat(output));
        Assert.All(output, block => Assert.Null(block.SystemRmsDB));
    }

    [Fact]
    public void OutputChunksAreAlways1600ExceptTheFinalPartial()
    {
        const int total = 12_345;
        var mic = Repeat(0.1f, total);
        var system = Repeat(0.05f, 9000);
        var blocks = Run(Chunks(mic, 1000), Chunks(system, 441, start: 100 + 0.05));
        Assert.Equal(total, blocks.Sum(b => b.Mixed.Length));
        foreach (var block in blocks.Take(blocks.Count - 1))
        {
            Assert.Equal(AudioMixer.ChunkSize, block.Mixed.Length);
        }
        Assert.Equal(total % AudioMixer.ChunkSize, blocks[^1].Mixed.Length);
    }

    [Fact]
    public void LaggingSourceIsPaddedUpToTheCapInsteadOfBlocking()
    {
        var mixer = new AudioMixer();
        var output = new List<MixedChunk>();
        output.AddRange(mixer.Append(new TimedChunk(Repeat(0.1f, 160), 5), AudioSource.System));
        var mic = Repeat(0.2f, 32_000);
        foreach (var chunk in Chunks(mic, 1600, start: 5))
        {
            output.AddRange(mixer.Append(chunk, AudioSource.Mic));
        }
        // 2 s of mic, the system stuck at 0.01 s: output runs to 1.5 s.
        var emitted = output.Sum(b => b.Mixed.Length);
        Assert.Equal(32_000 - AudioMixer.MaxLagSamples, emitted);
        // System audio for time already emitted is dropped; the rest lines up.
        output.AddRange(mixer.Append(new TimedChunk(Repeat(0.1f, 32_000 - 160), 5.01), AudioSource.System));
        output.AddRange(mixer.Finish(AudioSource.Mic));
        output.AddRange(mixer.Finish(AudioSource.System));
        var flat = Flat(output);
        Assert.Equal(32_000, flat.Length);
        Near(0.3, flat[100], 1e-6);
        Assert.Equal(0.2f, flat[1000]);
        Near(0.3, flat[31_000], 1e-6);
    }

    [Fact]
    public void TimestampJitterKeepsChunksContiguous()
    {
        var mic = Repeat(0.2f, 16_000);
        double[] jitter = [0, 0.002, -0.001, 0.003, -0.002];
        var micChunks = Chunks(mic, 1600)
            .Select((chunk, index) => new TimedChunk(chunk.Samples, chunk.HostTime + jitter[index % jitter.Length]));
        var output = Flat(Run(micChunks, []));
        Assert.Equal(mic, output);
    }

    [Fact]
    public void GapBeyondToleranceIsFilledWithSilence()
    {
        var mixer = new AudioMixer([AudioSource.Mic]);
        var output = new List<MixedChunk>();
        output.AddRange(mixer.Append(new TimedChunk(Repeat(0.2f, 1600), 1), AudioSource.Mic));
        output.AddRange(mixer.Append(new TimedChunk(Repeat(0.2f, 1600), 1.2), AudioSource.Mic));
        output.AddRange(mixer.Finish(AudioSource.Mic));
        var flat = Flat(output);
        Assert.Equal(4800, flat.Length);
        Assert.Equal(0.2f, flat[1599]);
        Assert.Equal(0f, flat[1600]);
        Assert.Equal(0f, flat[3199]);
        Assert.Equal(0.2f, flat[3200]);
    }

    [Fact]
    public void StampsFromAForeignClockAreClampedNotPaddedForHours()
    {
        var mixer = new AudioMixer();
        var output = new List<MixedChunk>();
        output.AddRange(mixer.Append(new TimedChunk(Repeat(0.2f, 1600), 100), AudioSource.Mic));
        output.AddRange(mixer.Append(new TimedChunk(Repeat(0.1f, 1600), 90_000), AudioSource.System));
        output.AddRange(mixer.Finish(AudioSource.Mic));
        output.AddRange(mixer.Finish(AudioSource.System));
        Assert.Equal(1600 + AudioMixer.ClockSanitySamples + 1600, output.Sum(b => b.Mixed.Length));
    }

    [Fact]
    public void PerSourceLevelsAreReported()
    {
        var blocks = Run(
            [new TimedChunk(Repeat(0.5f, 1600), 0)],
            [new TimedChunk(Repeat(0f, 1600), 0)]);
        Assert.Single(blocks);
        Near(20 * Math.Log10(0.5), blocks[0].MicRmsDB ?? 0, 1e-6);
        Assert.Equal(double.NegativeInfinity, blocks[0].SystemRmsDB);
    }

    [Fact]
    public async Task MixStreamsFinishesAfterBothSources()
    {
        var micChannel = Channel.CreateUnbounded<TimedChunk>();
        var systemChannel = Channel.CreateUnbounded<TimedChunk>();
        var ended = new ConcurrentBag<AudioSource>();
        var mixed = AudioMixer.Mix(micChannel.Reader.ReadAllAsync(), systemChannel.Reader.ReadAllAsync(), ended.Add);
        foreach (var chunk in Chunks(Repeat(0.2f, 4000), 1000))
        {
            micChannel.Writer.TryWrite(chunk);
        }
        micChannel.Writer.Complete();
        systemChannel.Writer.TryWrite(new TimedChunk(Repeat(0.1f, 1600), 100));
        systemChannel.Writer.Complete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var blocks = new List<MixedChunk>();
        await foreach (var block in mixed.WithCancellation(timeout.Token))
        {
            blocks.Add(block);
        }
        var flat = Flat(blocks);
        Assert.Equal(4000, flat.Length);
        Near(0.3, flat[0], 1e-6);
        Assert.Equal(0.2f, flat[1600]);
        Assert.Equal([AudioSource.Mic, AudioSource.System], ended.Order().ToArray());
    }

    [Fact]
    public async Task MixStreamsWithMicOnly()
    {
        var micChannel = Channel.CreateUnbounded<TimedChunk>();
        var mixed = AudioMixer.Mix(micChannel.Reader.ReadAllAsync(), null);
        var mic = Sine(0.5f, 5000);
        foreach (var chunk in Chunks(mic, 700))
        {
            micChannel.Writer.TryWrite(chunk);
        }
        micChannel.Writer.Complete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var flat = new List<float>();
        await foreach (var block in mixed.WithCancellation(timeout.Token))
        {
            flat.AddRange(block.Mixed);
        }
        Assert.Equal(mic, flat);
    }

    /// <summary>
    /// PLAN.md 4.13 (mixStreamsWithSystemOnly in the Swift suite): system
    /// audio only passes through unchanged, has no microphone level, and a
    /// gap in its stream is filled with silence.
    /// </summary>
    [Fact]
    public async Task MixStreamsWithSystemOnly()
    {
        var systemChannel = Channel.CreateUnbounded<TimedChunk>();
        var ended = new ConcurrentBag<AudioSource>();
        var mixed = AudioMixer.Mix(null, systemChannel.Reader.ReadAllAsync(), ended.Add);
        var first = Sine(0.5f, 3200);
        foreach (var chunk in Chunks(first, 800))
        {
            systemChannel.Writer.TryWrite(chunk);
        }
        // One second with no buffers, then more sound.
        var second = Sine(0.5f, 1600);
        systemChannel.Writer.TryWrite(new TimedChunk(second, 100 + 3200 / Rate + 1));
        systemChannel.Writer.Complete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var blocks = new List<MixedChunk>();
        await foreach (var block in mixed.WithCancellation(timeout.Token))
        {
            blocks.Add(block);
        }
        var flat = Flat(blocks);
        Assert.Equal(3200 + 16_000 + 1600, flat.Length);
        Assert.Equal(first, flat[..3200]);
        Assert.All(flat[3200..19_200], value => Assert.Equal(0f, value));
        Assert.Equal(second, flat[19_200..]);
        Assert.All(blocks, block => Assert.Null(block.MicRmsDB));
        Assert.Equal([AudioSource.System], ended.ToArray());
    }

    [Fact]
    public async Task NoSourcesFinishesImmediately()
    {
        var count = 0;
        await foreach (var unused in AudioMixer.Mix(null, null))
        {
            count += 1;
        }
        Assert.Equal(0, count);
    }

    // Not in the Swift suite: a consumer that stops early cancels the feeders.
    [Fact]
    public async Task StoppingTheMixEarlyCancelsTheFeeders()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mixed = AudioMixer.Mix(Endless(cancelled), null);
        await foreach (var unused in mixed)
        {
            break;
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async IAsyncEnumerable<TimedChunk> Endless(
        TaskCompletionSource cancelled, [EnumeratorCancellation] CancellationToken token = default)
    {
        double time = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                yield return new TimedChunk(Repeat(0.1f, 1600), time);
                time += 0.1;
                await Task.Yield();
            }
        }
        finally
        {
            if (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
            }
        }
    }

    // Recorder output

    [Fact]
    public async Task FanoutRemovesPausedTimeFromHostTime()
    {
        var fanout = new ChunkFanout();
        var timed = fanout.TimedSamples;
        fanout.Yield([0.1f], 10);
        fanout.Pause(11);
        fanout.Yield([0.2f], 12); // dropped while paused
        fanout.Resume(14);
        fanout.Yield([0.3f], 15);
        fanout.Finish();
        var received = new List<TimedChunk>();
        await foreach (var chunk in timed.ReadAllAsync())
        {
            received.Add(chunk);
        }
        Assert.Equal([new TimedChunk([0.1f], 10), new TimedChunk([0.3f], 12)], received);
    }

    [Fact]
    public async Task FanoutFirstAccessedStreamClaimsTheChunks()
    {
        var fanout = new ChunkFanout();
        fanout.Yield([0.5f], 1);
        var plain = fanout.Samples;
        var timed = fanout.TimedSamples;
        fanout.Yield([0.6f], 2);
        fanout.Finish();
        var plainChunks = new List<float[]>();
        await foreach (var chunk in plain.ReadAllAsync())
        {
            plainChunks.Add(chunk);
        }
        var timedCount = 0;
        await foreach (var unused in timed.ReadAllAsync())
        {
            timedCount += 1;
        }
        Assert.Equal(2, plainChunks.Count);
        Assert.Equal([0.5f], plainChunks[0]);
        Assert.Equal([0.6f], plainChunks[1]);
        Assert.Equal(0, timedCount);
    }
}
