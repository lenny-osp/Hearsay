using System.Diagnostics;
using System.Globalization;
using Hearsay.App.Features.Debug;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.FileTranscription;

/// <summary>
/// Debug only. When Hearsay is launched with <c>HEARSAY_TRANSCRIBE_FILE=&lt;audio&gt;</c>
/// and <c>HEARSAY_MODEL_DIR=&lt;model file or folder&gt;</c> (see
/// <see cref="DebugModel"/>), transcribes that file with that model through
/// the real File flow (<see cref="FileViewModel"/>), bypassing the model
/// store, prints the SRT path and the language decision to stdout and the
/// timing to stderr, and quits with status 0, or 1 on failure.
/// <c>HEARSAY_LANGUAGE</c> (auto, en, zh-TW, zh-CN, de, es; zh is an alias
/// for zh-TW) is optional and overrides the Language picker for this run.
/// Port of <c>FileViewModel.runDebugTranscriptionIfRequested</c> in
/// mac/Hearsay/Features/FileTranscription/FileViewModel.swift.
/// </summary>
/// <remarks>
/// Windows differences: the SRT goes into an <c>output</c> folder inside the
/// throwaway settings folder (never <c>Documents\Hearsay</c>), which is
/// removed when the run ends, so the SRT text is printed to stdout after the
/// decision lines, between <c>--- srt ---</c> and <c>--- end ---</c>.
/// <code>
/// $env:HEARSAY_TRANSCRIBE_FILE="shared\fixtures\en-30s.wav"; $env:HEARSAY_LANGUAGE="auto"
/// $env:HEARSAY_MODEL_DIR="windows\Spike\models"
/// windows\Hearsay.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Hearsay.exe | Out-String
/// </code>
/// </remarks>
internal static class FileTranscriptionDebug
{
    public const string Variable = "HEARSAY_TRANSCRIBE_FILE";

    /// <summary>Returns false (and does nothing) when the variables are not set.</summary>
    public static bool RunIfRequested(AppShell shell)
    {
        var environment = DebugEnvironment.Environment;
        if (!environment.TryGetValue(Variable, out var file) || file.Length == 0) return false;
        if (!environment.TryGetValue(DebugModel.Variable, out var model) || model.Length == 0)
        {
            DebugOutput.Error.WriteLine($"hearsay debug: {Variable} needs {DebugModel.Variable}");
            shell.FinishDebugRun(1);
            return true;
        }
        _ = RunAsync(shell, file, model);
        return true;
    }

    private static async Task RunAsync(AppShell shell, string file, string modelValue)
    {
        var status = 1;
        try
        {
            status = await TranscribeAsync(shell, file, modelValue).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DebugOutput.Error.WriteLine($"hearsay debug: {error}");
        }
        shell.FinishDebugRun(status);
    }

    private static async Task<int> TranscribeAsync(AppShell shell, string file, string modelValue)
    {
        var (modelPath, modelError) = DebugModel.Resolve(modelValue, shell.Models.Catalog);
        if (modelPath is null)
        {
            DebugOutput.Error.WriteLine($"hearsay debug: {modelError}");
            return 1;
        }
        var settings = shell.Settings;
        var output = Path.Combine(shell.SettingsFile.Folder, "output");
        Directory.CreateDirectory(output);
        settings.OutputFolder = OutputLocation.MakeStoredPath(output);
        LanguageChoice? choice = DebugEnvironment.Environment.TryGetValue("HEARSAY_LANGUAGE", out var value) && value.Length > 0
            ? LanguageChoice.FromDebugValue(value)
            : null;
        var model = new FileViewModel(settings, shell.Models, shell.Engine);
        var source = Path.GetFullPath(file);
        var watch = Stopwatch.StartNew();
        var srt = await model.RunAsync(source, modelPath, choice).ConfigureAwait(true);
        var elapsed = watch.Elapsed.TotalSeconds;
        if (srt is null)
        {
            DebugOutput.Error.WriteLine($"hearsay debug: {model.ErrorMessage ?? "failed"}");
            return 1;
        }
        var lines = new List<string>
        {
            srt,
            $"choice {(choice ?? model.LanguageChoice).StorageValue}, preferred {settings.PreferredLanguage.Code()}",
            model.LastDetection is { } detection
                ? TranscriptionEngine.DebugSummary(detection)
                : "detection did not run or failed",
        };
        if (model.Tracker?.Decision is { } decision) lines.Add("decision: " + decision.DebugSummary());
        if (model.LanguageNotice is { } notice) lines.Add("notice: " + notice.Message);
        lines.Add("--- srt ---");
        lines.Add((await File.ReadAllTextAsync(srt).ConfigureAwait(true)).TrimEnd('\n'));
        lines.Add("--- end ---");
        DebugOutput.Out.Write(string.Join("\n", lines) + "\n");
        DebugOutput.Out.Flush();
        DebugOutput.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"hearsay debug: load + decode + transcribe {elapsed:F2} s (runtime {Hearsay.Whisper.WhisperRuntime.LibraryName ?? "unknown"})"));
        return 0;
    }
}
