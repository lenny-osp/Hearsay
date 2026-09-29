using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Hearsay.App.Features.Debug;
using Hearsay.Core.Audio;

namespace Hearsay.App.Features.Recording;

/// <summary>
/// Debug only. When Hearsay is launched with <c>HEARSAY_RECORD_SECONDS=&lt;n&gt;</c>
/// and <c>HEARSAY_RECORD_DEVICE=&lt;part of a device name&gt;</c>, records
/// <c>n</c> seconds from the first active input endpoint whose name contains
/// that text (ignoring case), writes them to a WAV in the throwaway settings
/// folder that is deleted afterwards (the Mac uses the temp folder), prints the recorder diagnostics and the RMS level of what was
/// captured to stdout, and quits with status 0, or 1 when nothing was
/// captured or the device was not found (the list of devices is printed, so
/// a machine with none, such as an RDP session without audio input, says so).
/// <c>HEARSAY_RECORD_KEEP=&lt;path&gt;</c> keeps a copy of the WAV there.
/// Port of mac/Hearsay/Features/Recording/RecordingDebug.swift; Windows has
/// no microphone permission to request first (PLAN.md 18.4, W3).
/// </summary>
internal static class RecordingDebug
{
    public const string Variable = "HEARSAY_RECORD_SECONDS";

    /// <summary>Returns false (and does nothing) when the variable is not set.</summary>
    public static bool RunIfRequested(AppShell shell)
    {
        var environment = DebugEnvironment.Environment;
        if (!environment.TryGetValue(Variable, out var secondsText) || secondsText.Length == 0) return false;
        if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0
            || !environment.TryGetValue("HEARSAY_RECORD_DEVICE", out var query) || query.Length == 0)
        {
            Say($"{Variable} needs a positive number of seconds and HEARSAY_RECORD_DEVICE=<part of a device name>");
            shell.FinishDebugRun(1);
            return true;
        }
        _ = RunAsync(shell, seconds, query);
        return true;
    }

    private static async Task RunAsync(AppShell shell, double seconds, string query)
    {
        var status = 1;
        try
        {
            status = await RecordAsync(seconds, query, shell.SettingsFile.Folder).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Say($"failed: {error}");
        }
        shell.FinishDebugRun(status);
    }

    private static async Task<int> RecordAsync(double seconds, string query, string scratch)
    {
        IReadOnlyList<AudioInputDevice> devices;
        try
        {
            devices = AudioDeviceList.InputDevices();
        }
        catch (COMException error)
        {
            Say($"cannot list input devices: {error.Message}");
            return 1;
        }
        if (devices.FirstOrDefault(d => d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) is not { } device)
        {
            Say($"no input device matches \"{query}\"; devices: "
                + (devices.Count == 0 ? "(none: this session has no active audio input endpoint)" : string.Join(", ", devices.Select(d => d.Name))));
            return 1;
        }

        var path = Path.Combine(scratch, $"hearsay-record-{Guid.NewGuid():N}.wav");
        try
        {
            using var writer = new WavWriter(path);
            var recorder = new MicrophoneRecorder();
            try
            {
                await Task.Run(() => recorder.Start(device)).ConfigureAwait(true);
            }
            catch (MicrophoneRecorderException error)
            {
                Say($"start failed: {error.Message}");
                return 1;
            }
            Say($"recording {seconds.ToString(CultureInfo.InvariantCulture)} s from {device.Name} ({device.Uid})");
            var consumer = Task.Run(async () =>
            {
                var captured = new List<float>();
                double? firstChunkAfter = null;
                var watch = Stopwatch.StartNew();
                await foreach (var chunk in recorder.TimedSamples.ReadAllAsync().ConfigureAwait(false))
                {
                    firstChunkAfter ??= watch.Elapsed.TotalSeconds;
                    captured.AddRange(chunk.Samples);
                    writer.Append(chunk.Samples);
                }
                return (captured, firstChunkAfter);
            });
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(true);
            recorder.Stop();
            var (samples, first) = await consumer.ConfigureAwait(true);
            writer.Close();
            if (DebugEnvironment.Environment.TryGetValue("HEARSAY_RECORD_KEEP", out var keep) && keep.Length > 0)
            {
                try
                {
                    File.Copy(path, keep, overwrite: true);
                    Say($"kept a copy at {keep}");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Say($"could not keep a copy: {error.Message}");
                }
            }
            var size = new FileInfo(path).Length;
            Say(recorder.Diagnostics.Description);
            var rms = LevelMeter.RmsDB(CollectionsMarshal.AsSpan(samples)) is { } db
                ? db.ToString("F1", CultureInfo.InvariantCulture) + " dBFS"
                : "no samples";
            var peak = samples.Count == 0 ? 0 : samples.Max(Math.Abs);
            Say(string.Create(CultureInfo.InvariantCulture,
                $"captured {samples.Count} samples ({samples.Count / 16_000.0:F2} s at 16 kHz), RMS {rms}, peak {peak:F4}, first chunk after {(first is { } f ? f.ToString("F2", CultureInfo.InvariantCulture) + " s" : "never")}, WAV {size} bytes"));
            if (recorder.Failure is { } failure) Say($"recorder failure: {failure.Message}");
            return samples.Count == 0 ? 1 : 0;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Say($"could not delete {path}: {error.Message}");
            }
        }
    }

    private static void Say(string line)
    {
        DebugOutput.Out.WriteLine("hearsay record debug: " + line);
        DebugOutput.Out.Flush();
    }
}
