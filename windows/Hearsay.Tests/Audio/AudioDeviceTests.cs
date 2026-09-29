using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Hearsay.Core.Audio;
using Xunit.Abstractions;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Port of <c>AudioDeviceListTests</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/AudioTests.swift plus the picker
/// rule of <c>RecordingController.refreshDevices()</c>. The enumeration and
/// observation tests touch real endpoints, so they run only with
/// <c>HEARSAY_TEST_AUDIO_DEVICES=1</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public class AudioDeviceListTests(ITestOutputHelper output)
{
    private static readonly AudioInputDevice Headset = new("{0.0.1.00000000}.{a}", "Headset Microphone");
    private static readonly AudioInputDevice BuiltIn = new("{0.0.1.00000000}.{b}", "Microphone Array");

    [Fact]
    public void SelectionKeepsTheChosenDeviceWhileItIsConnected() =>
        Assert.Equal(Headset.Uid, AudioDeviceList.ResolveSelection(Headset.Uid, [BuiltIn, Headset], BuiltIn.Uid));

    [Fact]
    public void SelectionFallsBackToTheDefaultThenTheFirstDevice()
    {
        Assert.Equal(BuiltIn.Uid, AudioDeviceList.ResolveSelection("{gone}", [Headset, BuiltIn], BuiltIn.Uid));
        Assert.Equal(Headset.Uid, AudioDeviceList.ResolveSelection(null, [Headset, BuiltIn], null));
        Assert.Null(AudioDeviceList.ResolveSelection("{gone}", [], null));
    }

    [EnvironmentFact("HEARSAY_TEST_AUDIO_DEVICES", "1")]
    public void EnumerationListsActiveCaptureEndpoints()
    {
        var devices = AudioDeviceList.InputDevices();
        foreach (var device in devices)
        {
            output.WriteLine($"input: {device.Name} [{device.Uid}]");
            Assert.False(string.IsNullOrEmpty(device.Uid));
            Assert.False(string.IsNullOrEmpty(device.Name));
            Assert.Equal(device, AudioDeviceList.InputDevice(device.Uid));
        }
        if (AudioDeviceList.DefaultInputDevice() is AudioInputDevice preferred)
        {
            output.WriteLine($"default input: {preferred.Name}");
            Assert.Contains(preferred, devices);
        }
        output.WriteLine($"default output: {AudioDeviceList.DefaultOutputDevice()?.Name ?? "none"}");
        Assert.Null(AudioDeviceList.InputDevice("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
    }

    [EnvironmentFact("HEARSAY_TEST_AUDIO_DEVICES", "1")]
    public void ObservationRegistersAndUnregisters()
    {
        var observation = AudioDeviceList.ObserveChanges(() => { });
        Assert.NotNull(observation);
        observation.Dispose();
        observation.Dispose();
    }
}

/// <summary>
/// Real recordings, the Windows counterpart of the Mac's
/// <c>HEARSAY_RECORD_SECONDS</c> debug entry point: run only with
/// <c>HEARSAY_TEST_RECORD_SECONDS=&lt;n&gt;</c>. Each writes a WAV into a
/// scratch folder, checks its length against the time recorded (within 10 %),
/// and deletes it.
/// </summary>
[SupportedOSPlatform("windows")]
public class AudioRecordingTests(ITestOutputHelper output)
{
    private const string Variable = "HEARSAY_TEST_RECORD_SECONDS";

    [MicrophoneFact(Variable)]
    public async Task MicrophoneRecordingHasTheRecordedLength()
    {
        var recorder = new MicrophoneRecorder();
        await Record(recorder.TimedSamples, () => recorder.Start(null), recorder.Stop, "microphone");
        output.WriteLine(recorder.Diagnostics.Description);
        Assert.Null(recorder.Failure);
    }

    [EnvironmentFact(Variable)]
    public async Task SystemAudioRecordingHasTheRecordedLength()
    {
        var recorder = new SystemAudioRecorder();
        await Record(recorder.TimedSamples, recorder.Start, recorder.Stop, "system audio");
        var value = recorder.Diagnostics;
        output.WriteLine(value.Description);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"silence frames synthesized: {value.SilenceFramesSynthesized}, real frames received: {value.FramesReceived}"));
        Assert.Null(recorder.Failure);
    }

    private async Task Record(ChannelReader<TimedChunk> reader, Action start, Action stop, string label)
    {
        var seconds = EnvironmentFactAttribute.Seconds(Variable, 2);
        using var scratch = new ScratchDirectory();
        var path = scratch.File(label.Replace(' ', '-') + ".wav");
        long samples;
        double? firstHostTime = null;
        double lastEnd = 0;
        using (var writer = new WavWriter(path))
        {
            // Timed from when capture runs, as the Mac's HEARSAY_RECORD_SECONDS does.
            start();
            var clock = Stopwatch.StartNew();
            var consumer = Task.Run(async () =>
            {
                await foreach (var chunk in reader.ReadAllAsync())
                {
                    firstHostTime ??= chunk.HostTime;
                    lastEnd = chunk.HostTime + chunk.Samples.Length / 16_000.0;
                    writer.Append(chunk.Samples);
                }
            });
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            stop();
            var elapsed = clock.Elapsed.TotalSeconds;
            await consumer.WaitAsync(TimeSpan.FromSeconds(10));
            writer.Close();
            samples = writer.SampleCount;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{label}: {samples} samples = {samples / 16_000.0:F3} s in {elapsed:F3} s of wall time; "
                + $"stamped span {(firstHostTime is double first ? lastEnd - first : 0):F3} s; file {new FileInfo(path).Length} bytes"));
            var expected = elapsed * 16_000;
            Assert.InRange(samples, (long)(expected * 0.9), (long)(expected * 1.1));
        }
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
