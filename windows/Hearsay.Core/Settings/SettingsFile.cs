using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearsay.Core.Settings;

/// <summary>
/// The Windows stand-in for the <c>UserDefaults</c> domain the Mac's
/// <c>AppSettings</c> (mac/HearsayCore/Sources/HearsayCore/Settings/AppSettings.swift)
/// writes to: one JSON object in <c>settings.json</c> inside a folder given to
/// the constructor (the app passes <c>%APPDATA%\Hearsay</c>, tests a scratch
/// folder; PLAN.md 18.3). Keys are the Mac's key names; values are JSON
/// strings, booleans, numbers or objects.
/// <para>
/// Every change is written at once, atomically: the whole object goes to a
/// temporary file in the same folder, which then replaces
/// <c>settings.json</c>. Keys this class does not know (written by another
/// store sharing the file, or a newer version) are kept. The folder is
/// created on the first write; reading never creates anything.
/// </para>
/// <para>
/// A <c>settings.json</c> that is not a JSON object is moved aside to
/// <c>settings.corrupt.json</c> (replacing an older one) and the settings
/// start empty, so the next write cannot destroy what the user may want to
/// recover. Share one instance per folder: two instances on the same file
/// would overwrite each other's keys. Thread-safe.
/// </para>
/// </summary>
public sealed class SettingsFile
{
    public const string FileName = "settings.json";
    public const string CorruptFileName = "settings.corrupt.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Lock gate = new();
    private readonly JsonObject root;

    /// <summary>Loads <c>settings.json</c> from <paramref name="folder"/>; a missing file or folder gives empty settings.</summary>
    /// <exception cref="IOException">The file exists but cannot be read.</exception>
    public SettingsFile(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = Path.GetFullPath(folder);
        FilePath = Path.Combine(Folder, FileName);
        root = Load(FilePath, Path.Combine(Folder, CorruptFileName), out var backup);
        CorruptFileBackup = backup;
    }

    /// <summary>The folder that holds <c>settings.json</c>.</summary>
    public string Folder { get; }

    /// <summary>Full path of <c>settings.json</c>.</summary>
    public string FilePath { get; }

    /// <summary>Where an unreadable <c>settings.json</c> was moved at load, or null when none was.</summary>
    public string? CorruptFileBackup { get; }

    /// <summary>A copy of the stored value, or null when the key is absent.</summary>
    public JsonNode? Get(string key)
    {
        lock (gate)
        {
            return root[key]?.DeepClone();
        }
    }

    /// <summary>The stored string, or null when absent or not a string.</summary>
    public string? GetString(string key)
    {
        lock (gate)
        {
            return root[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
    }

    /// <summary>The stored boolean, or null when absent or not a boolean.</summary>
    public bool? GetBool(string key)
    {
        lock (gate)
        {
            return root[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
        }
    }

    public bool Contains(string key)
    {
        lock (gate)
        {
            return root.ContainsKey(key);
        }
    }

    /// <summary>Stores <paramref name="value"/> (a copy) under <paramref name="key"/>, or removes the key for null, and saves.</summary>
    /// <exception cref="IOException">The file could not be written; the change stays in memory.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or file is not writable; the change stays in memory.</exception>
    public void Set(string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (gate)
        {
            if (value is null)
            {
                if (!root.Remove(key)) return;
            }
            else
            {
                root[key] = value.DeepClone();
            }
            Save();
        }
    }

    public void SetString(string key, string? value) => Set(key, value is null ? null : JsonValue.Create(value));

    public void SetBool(string key, bool value) => Set(key, JsonValue.Create(value));

    public void Remove(string key) => Set(key, null);

    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, WriteOptions);
        Directory.CreateDirectory(Folder);
        var temporary = Path.Combine(Folder, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            MoveReplacing(temporary, FilePath);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> (MoveFileEx with
    /// MOVEFILE_REPLACE_EXISTING, atomic within one NTFS volume). A virus
    /// scanner or indexer can hold the old file open for a moment, so a
    /// sharing violation is retried briefly before it is reported.
    /// </summary>
    private static void MoveReplacing(string source, string destination)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(20 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static JsonObject Load(string path, string corruptPath, out string? backup)
    {
        backup = null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(bytes, documentOptions: ReadOptions);
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is JsonObject settings) return settings;

        try
        {
            File.Move(path, corruptPath, overwrite: true);
            backup = corruptPath;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return [];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
