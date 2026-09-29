using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearsay.Core.Settings;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// Debug entry points (<c>HEARSAY_TRANSCRIBE_FILE</c>, <c>HEARSAY_REPLAY_FILE</c>,
/// <c>HEARSAY_RECORD_SECONDS</c>, <c>HEARSAY_UI_SNAPSHOTS</c>,
/// <c>HEARSAY_INSTALL_UPDATE</c>) run on a throwaway settings folder under
/// the temp folder, never <c>%APPDATA%\Hearsay</c> (AGENTS.md). The copy
/// starts with the user's values for the keys that shape a transcription,
/// read without writing anything back (not even moving a corrupt file aside,
/// which <see cref="SettingsFile"/> would do).
/// Port of <c>DebugDefaults</c> in mac/Hearsay/AppDelegate.swift.
/// </summary>
internal static class DebugEnvironment
{
    public const string SnapshotsVariable = "HEARSAY_UI_SNAPSHOTS";

    public static readonly IReadOnlyList<string> DebugVariables =
    [
        "HEARSAY_TRANSCRIBE_FILE", "HEARSAY_REPLAY_FILE", "HEARSAY_RECORD_SECONDS", SnapshotsVariable,
        "HEARSAY_INSTALL_UPDATE",
    ];

    /// <summary>The entry points this build implements (all of them since W7).</summary>
    public static readonly IReadOnlyList<string> ImplementedVariables =
        [SnapshotsVariable, "HEARSAY_TRANSCRIBE_FILE", "HEARSAY_REPLAY_FILE", "HEARSAY_RECORD_SECONDS", "HEARSAY_INSTALL_UPDATE"];

    public static readonly IReadOnlyList<string> CopiedKeys =
    [
        AppSettings.Key.InterfaceLanguage,
        AppSettings.Key.OutputFolder,
        AppSettings.Key.DefaultLanguageCode,
        AppSettings.Key.LanguageChoice,
        AppSettings.Key.PreferredLanguage,
        AppSettings.Key.ActiveModelRepo,
        AppSettings.Key.ChineseScript,
    ];

    /// <summary>The process environment as a dictionary (for <see cref="InterfaceLanguages.ResolveAtLaunch"/>).</summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = ReadEnvironment();

    /// <summary>A debug entry point was requested through the environment.</summary>
    public static bool IsDebugRun => RequestedVariable is not null;

    /// <summary>The first debug variable that is set and not empty, or null.</summary>
    public static string? RequestedVariable =>
        DebugVariables.FirstOrDefault(name => Environment.TryGetValue(name, out var value) && value.Length > 0);

    /// <summary>The user's settings folder, <c>%APPDATA%\Hearsay</c>.</summary>
    public static string UserSettingsFolder =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Hearsay");

    /// <summary>
    /// A new empty folder under the temp folder holding a <c>settings.json</c>
    /// with the copied keys. The caller deletes it with <see cref="RemoveScratchFolder"/>.
    /// </summary>
    public static string CreateScratchSettingsFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hearsay-debug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var copied = new JsonObject();
        if (ReadUserSettings() is { } user)
        {
            foreach (var key in CopiedKeys)
            {
                if (user[key] is { } value) copied[key] = value.DeepClone();
            }
        }
        if (copied.Count > 0)
        {
            var file = new SettingsFile(folder);
            foreach (var (key, value) in copied)
            {
                file.Set(key, value);
            }
        }
        return folder;
    }

    /// <summary>Deletes a scratch folder; a file another process still holds is left and logged.</summary>
    public static void RemoveScratchFolder(string folder)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
            catch (IOException error)
            {
                AppLog.Write($"could not remove {folder}: {error.Message}");
            }
            catch (UnauthorizedAccessException error)
            {
                AppLog.Write($"could not remove {folder}: {error.Message}");
            }
        }
    }

    /// <summary>The user's settings.json parsed read-only; null when missing or unreadable.</summary>
    private static JsonObject? ReadUserSettings()
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(UserSettingsFolder, SettingsFile.FileName));
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) result[key] = value;
        }
        return result;
    }
}
