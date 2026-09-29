using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hearsay.Core.Notes;

/// <summary>
/// The file access <see cref="AntigravityHousekeeping"/> needs; injected in
/// tests. Port of the <c>AntigravityFileSystem</c> protocol in
/// mac/HearsayCore/Sources/HearsayCore/Notes/AntigravityHousekeeping.swift.
/// </summary>
public interface IAntigravityFileSystem
{
    /// <summary>Entry names; empty when the folder is missing or unreadable.</summary>
    IReadOnlyList<string> ContentsOfDirectory(string path);

    /// <summary>The file's bytes, or null when it cannot be read.</summary>
    byte[]? Contents(string path);

    /// <summary>Replaces the file atomically, creating its folder when missing.</summary>
    void Write(byte[] data, string path);

    /// <summary>Removes a file or a whole folder.</summary>
    void RemoveItem(string path);

    /// <summary>
    /// Replaces an existing store of agy's: first the current bytes become
    /// <c>&lt;path&gt;.hearsay-backup</c> (replacing an older backup), then
    /// <paramref name="data"/> replaces <paramref name="path"/>. Each step
    /// writes a temporary file in the same folder, flushes it to disk, and
    /// moves it into place. Throws, changing nothing, when the file no longer
    /// holds <paramref name="expectedOriginal"/> (agy wrote it meanwhile).
    /// </summary>
    void ReplaceKeepingBackup(byte[] data, string path, byte[] expectedOriginal);
}

/// <summary>A housekeeping write was refused or failed (Swift <c>ChangedMeanwhile</c> and write errors).</summary>
public sealed class AntigravityWriteException : Exception
{
    public AntigravityWriteException(string message)
        : base(message)
    {
    }

    public AntigravityWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The real file system. Port of <c>LocalAntigravityFileSystem</c>.</summary>
public sealed class LocalAntigravityFileSystem : IAntigravityFileSystem
{
    /// <summary>The suffix of the one backup Hearsay keeps next to each agy store it edits.</summary>
    public const string BackupSuffix = ".hearsay-backup";

    public IReadOnlyList<string> ContentsOfDirectory(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry)).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public byte[]? Contents(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(byte[] data, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? path);
        WriteAtomically(data, path);
    }

    public void RemoveItem(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            throw new FileNotFoundException($"{path} does not exist.", path);
        }
    }

    public void ReplaceKeepingBackup(byte[] data, string path, byte[] expectedOriginal)
    {
        if (!Holds(path, expectedOriginal)) throw ChangedMeanwhile(path);
        WriteAtomically(expectedOriginal, path + BackupSuffix);
        // Checked again right before the move, to narrow the window.
        if (!Holds(path, expectedOriginal)) throw ChangedMeanwhile(path);
        WriteAtomically(data, path);
    }

    private static AntigravityWriteException ChangedMeanwhile(string path) =>
        new($"{path} changed while it was being edited; left as it is.");

    private static bool Holds(string path, byte[] expected)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Temporary file in the same folder, flushed to disk, then moved over
    /// <paramref name="path"/> (MoveFileEx with MOVEFILE_REPLACE_EXISTING;
    /// the Mac's <c>rename</c>).
    /// </summary>
    internal static void WriteAtomically(byte[] data, string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.hearsay-tmp-{Guid.NewGuid():D}");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }
}

/// <summary>
/// Whether an <c>agy</c> process is running. Port of <c>AntigravityProcesses</c>;
/// Windows lists processes by image name (<c>agy.exe</c> is <c>agy</c>).
/// </summary>
public static class AntigravityProcesses
{
    public static bool AgyIsRunning() => IsRunning("agy");

    /// <summary>Whether a process other than those in <paramref name="excluded"/> has <paramref name="name"/> as its image name (no extension).</summary>
    public static bool IsRunning(string name, IReadOnlySet<int>? excluded = null)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Any(process => excluded is null || !excluded.Contains(process.Id));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// agy's conversation list, <c>conversation_summaries.db</c> (SQLite), keyed
/// by <c>conversation_id</c>. Injected in tests. Port of <c>AntigravityConversationIndex</c>.
/// </summary>
public interface IAntigravityConversationIndex
{
    /// <summary>Conversations whose <c>parent_conversation_id</c> is <paramref name="id"/> (subagents).</summary>
    IReadOnlyList<string> ChildConversations(string id);

    /// <summary>Deletes the row of <paramref name="id"/>; no row is not an error.</summary>
    void RemoveSummary(string id);
}

/// <summary>A SQLite call on agy's database failed.</summary>
public sealed class AntigravitySqliteException : Exception
{
    public AntigravitySqliteException(string message)
        : base(message)
    {
    }

    public AntigravitySqliteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The real <c>conversation_summaries.db</c>, through Windows' own SQLite
/// (<c>C:\Windows\System32\winsqlite3.dll</c>; the Mac uses the system
/// <c>libsqlite3</c>, and PLAN.md 18.3 allows no package). The file is never
/// created: a missing database means there is nothing to remove. Port of
/// <c>SQLiteAntigravityConversationIndex</c>.
/// </summary>
public sealed class SqliteAntigravityConversationIndex : IAntigravityConversationIndex
{
    public SqliteAntigravityConversationIndex(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    public string Path { get; }

    public IReadOnlyList<string> ChildConversations(string id) => WithDatabase(database =>
    {
        List<string> children = [];
        Statement(database, "SELECT conversation_id FROM conversation_summaries WHERE parent_conversation_id = ?", id, row =>
        {
            while (WinSqlite.sqlite3_step(row) == WinSqlite.Row)
            {
                if (WinSqlite.ColumnText(row, 0) is { } text) children.Add(text);
            }
        });
        return children;
    }) ?? [];

    /// <summary>
    /// Backs the database up to <c>&lt;path&gt;.hearsay-backup</c> (<c>VACUUM
    /// INTO</c> a temporary file, then a replacing move), then deletes the
    /// row inside a transaction that is committed only when
    /// <c>PRAGMA quick_check</c> says "ok"; otherwise it is rolled back and
    /// the error thrown.
    /// </summary>
    public void RemoveSummary(string id) => WithDatabase<object?>(database =>
    {
        var count = 0;
        Statement(database, "SELECT count(*) FROM conversation_summaries WHERE conversation_id = ?", id, row =>
        {
            if (WinSqlite.sqlite3_step(row) == WinSqlite.Row) count = WinSqlite.sqlite3_column_int(row, 0);
        });
        if (count == 0) return null;
        var backup = Path + LocalAntigravityFileSystem.BackupSuffix;
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? string.Empty,
            $".conversation_summaries.hearsay-tmp-{Guid.NewGuid():D}.db");
        try
        {
            Statement(database, "VACUUM INTO ?", temporary, row =>
            {
                if (WinSqlite.sqlite3_step(row) != WinSqlite.Done) throw new AntigravitySqliteException(WinSqlite.ErrorMessage(database));
            });
            File.Move(temporary, backup, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AntigravitySqliteException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ignored) when (ignored is IOException or UnauthorizedAccessException)
            {
            }
            if (error is AntigravitySqliteException) throw;
            throw new AntigravitySqliteException($"could not move the backup into place ({error.Message})", error);
        }
        Execute(database, "BEGIN IMMEDIATE");
        try
        {
            Statement(database, "DELETE FROM conversation_summaries WHERE conversation_id = ?", id, row =>
            {
                if (WinSqlite.sqlite3_step(row) != WinSqlite.Done) throw new AntigravitySqliteException(WinSqlite.ErrorMessage(database));
            });
            var check = QuickCheck(database);
            if (check.Count != 1 || check[0] != "ok")
            {
                throw new AntigravitySqliteException("quick_check: " + string.Join("; ", check));
            }
            Execute(database, "COMMIT");
        }
        catch
        {
            _ = WinSqlite.sqlite3_exec(database, "ROLLBACK", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            throw;
        }
        return null;
    });

    private static void Execute(IntPtr database, string sql)
    {
        if (WinSqlite.sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != WinSqlite.Ok)
        {
            throw new AntigravitySqliteException(WinSqlite.ErrorMessage(database));
        }
    }

    private static List<string> QuickCheck(IntPtr database)
    {
        if (WinSqlite.sqlite3_prepare_v2(database, "PRAGMA quick_check", -1, out var prepared, IntPtr.Zero) != WinSqlite.Ok
            || prepared == IntPtr.Zero)
        {
            throw new AntigravitySqliteException(WinSqlite.ErrorMessage(database));
        }
        try
        {
            List<string> lines = [];
            while (WinSqlite.sqlite3_step(prepared) == WinSqlite.Row)
            {
                if (WinSqlite.ColumnText(prepared, 0) is { } text) lines.Add(text);
            }
            return lines;
        }
        finally
        {
            _ = WinSqlite.sqlite3_finalize(prepared);
        }
    }

    /// <summary>Default when the database file does not exist.</summary>
    private T? WithDatabase<T>(Func<IntPtr, T> body)
    {
        if (!File.Exists(Path)) return default;
        IntPtr database;
        try
        {
            if (WinSqlite.sqlite3_open_v2(Path, out database, WinSqlite.OpenReadWrite, IntPtr.Zero) != WinSqlite.Ok)
            {
                var message = database == IntPtr.Zero ? $"cannot open {Path}" : WinSqlite.ErrorMessage(database);
                _ = WinSqlite.sqlite3_close(database);
                throw new AntigravitySqliteException(message);
            }
        }
        catch (DllNotFoundException error)
        {
            throw new AntigravitySqliteException("winsqlite3.dll is not available.", error);
        }
        try
        {
            // agy may hold the database briefly; wait rather than fail at once.
            _ = WinSqlite.sqlite3_busy_timeout(database, 3000);
            return body(database);
        }
        finally
        {
            _ = WinSqlite.sqlite3_close(database);
        }
    }

    private static void Statement(IntPtr database, string sql, string parameter, Action<IntPtr> body)
    {
        if (WinSqlite.sqlite3_prepare_v2(database, sql, -1, out var prepared, IntPtr.Zero) != WinSqlite.Ok
            || prepared == IntPtr.Zero)
        {
            throw new AntigravitySqliteException(WinSqlite.ErrorMessage(database));
        }
        try
        {
            // SQLITE_TRANSIENT: SQLite copies the string.
            _ = WinSqlite.sqlite3_bind_text(prepared, 1, parameter, -1, WinSqlite.Transient);
            body(prepared);
        }
        finally
        {
            _ = WinSqlite.sqlite3_finalize(prepared);
        }
    }
}

/// <summary>The few <c>winsqlite3.dll</c> entry points Hearsay uses (strings are UTF-8).</summary>
internal static class WinSqlite
{
    public const int Ok = 0;
    public const int Row = 100;
    public const int Done = 101;
    public const int OpenReadOnly = 0x1;
    public const int OpenReadWrite = 0x2;
    public const int OpenCreate = 0x4;

    /// <summary>SQLITE_TRANSIENT, <c>(sqlite3_destructor_type)-1</c>.</summary>
    public static readonly IntPtr Transient = new(-1);

    private const string Library = "winsqlite3.dll";

    public static string ErrorMessage(IntPtr database) =>
        Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) ?? "unknown SQLite error";

    public static string? ColumnText(IntPtr statement, int column) =>
        Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column));

    /// <summary>A NUL-terminated UTF-8 copy of <paramref name="text"/>.</summary>
    private static byte[] Utf8Z(string text)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(text) + 1];
        System.Text.Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    public static int sqlite3_open_v2(string filename, out IntPtr database, int flags, IntPtr vfs) =>
        sqlite3_open_v2(Utf8Z(filename), out database, flags, vfs);

    public static int sqlite3_exec(IntPtr database, string sql, IntPtr callback, IntPtr argument, IntPtr errorMessage) =>
        sqlite3_exec(database, Utf8Z(sql), callback, argument, errorMessage);

    public static int sqlite3_prepare_v2(IntPtr database, string sql, int length, out IntPtr statement, IntPtr tail) =>
        sqlite3_prepare_v2(database, Utf8Z(sql), length, out statement, tail);

    public static int sqlite3_bind_text(IntPtr statement, int index, string text, int length, IntPtr destructor) =>
        sqlite3_bind_text(statement, index, Utf8Z(text), length, destructor);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_open_v2(byte[] filename, out IntPtr database,
        int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_close(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_exec(IntPtr database, byte[] sql,
        IntPtr callback, IntPtr argument, IntPtr errorMessage);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql,
        int length, out IntPtr statement, IntPtr tail);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_bind_text(IntPtr statement, int index,
        byte[] text, int length, IntPtr destructor);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_column_int(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int sqlite3_finalize(IntPtr statement);
}

/// <summary>
/// What Hearsay keeps in order in agy's own data (owner decisions,
/// 2026-09-28). Port of <c>AntigravityHousekeeping</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/AntigravityHousekeeping.swift.
/// <para>
/// Paths on Windows: agy keeps its data under the user's home folder as on
/// the Mac (<c>~/.gemini/…</c> in its docs), so the root is
/// <c>%USERPROFILE%\.gemini</c>, not <c>%APPDATA%</c> or
/// <c>%LOCALAPPDATA%</c>: projects in <c>%USERPROFILE%\.gemini\config\projects</c>,
/// conversations in <c>%USERPROFILE%\.gemini\antigravity-cli</c>. Only its
/// program lives in <c>%LOCALAPPDATA%\agy\bin</c>. Hearsay never writes
/// agy's global <c>settings.json</c>.
/// </para>
/// <list type="bullet">
/// <item>The <c>hearsay-notes</c> project carries deny rules for every tool
/// that runs programs, touches files, or fetches URLs. Only that one project
/// file is written; other keys in it are kept.</item>
/// <item>After each run, that run's conversation is deleted: every entry
/// named after its id under <c>antigravity-cli\{conversations,brain,
/// annotations,presence,worktrees}</c>, its entries in
/// <c>cache\last_conversations.json</c> and <c>cache\conversation_metadata.json</c>,
/// its entry in <c>jetbox_summaries_proto.pb</c>, its row in
/// <c>conversation_summaries.db</c>, and the same for its subagents. The
/// shared-store edits run only for agy <see cref="VerifiedVersion"/> and
/// only while no other agy process runs, each store backed up first.
/// Failures are logged (<see cref="Trace"/>), never thrown.</item>
/// </list>
/// </summary>
public sealed partial class AntigravityHousekeeping
{
    /// <summary>
    /// Rule kinds from agy's permission checks: <c>command</c>, <c>read_file</c>,
    /// <c>write_file</c>, <c>read_url</c>, <c>execute_url</c>, and <c>mcp</c>.
    /// agy accepts <c>search_web(*)</c> but does not apply it.
    /// </summary>
    public static readonly IReadOnlyList<string> DenyRules =
        ["command(*)", "read_file(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "mcp(*)"];

    /// <summary>The agy version whose internal stores were examined (1.2.12). The index edits run only for this major.minor.</summary>
    public const string VerifiedVersion = "1.2";

    /// <summary>Folders under <see cref="AppDataFolder"/> whose entries are named after a conversation id.</summary>
    internal static readonly IReadOnlyList<string> ConversationFolders =
        ["conversations", "brain", "annotations", "presence", "worktrees"];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<bool> agyIsRunning;

    /// <param name="homeDirectory">The folder that holds <c>.gemini</c>; null is <c>%USERPROFILE%</c>.</param>
    /// <param name="fileSystem">null is the real disk.</param>
    /// <param name="index">null is the real <c>conversation_summaries.db</c> under <paramref name="homeDirectory"/>.</param>
    /// <param name="agyIsRunning">
    /// Whether an agy process runs now (the run Hearsay waited for has
    /// already exited, so any match is someone else's).
    /// </param>
    public AntigravityHousekeeping(
        string? homeDirectory = null,
        IAntigravityFileSystem? fileSystem = null,
        IAntigravityConversationIndex? index = null,
        Func<bool>? agyIsRunning = null)
    {
        HomeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FileSystem = fileSystem ?? new LocalAntigravityFileSystem();
        Index = index ?? new SqliteAntigravityConversationIndex(
            System.IO.Path.Combine(HomeDirectory, ".gemini", "antigravity-cli", "conversation_summaries.db"));
        this.agyIsRunning = agyIsRunning ?? AntigravityProcesses.AgyIsRunning;
    }

    public string HomeDirectory { get; }

    public IAntigravityFileSystem FileSystem { get; }

    public IAntigravityConversationIndex Index { get; }

    public string ProjectsFolder => System.IO.Path.Combine(HomeDirectory, ".gemini", "config", "projects");

    public string AppDataFolder => System.IO.Path.Combine(HomeDirectory, ".gemini", "antigravity-cli");

    [GeneratedRegex(@"\b\d+\.\d+(?=\.\d+|\b)", RegexOptions.CultureInvariant)]
    private static partial Regex MajorMinorPattern();

    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConversationIdPattern();

    /// <summary>"1.2" from <c>agy --version</c> output such as "1.2.12"; null otherwise.</summary>
    public static string? MajorMinor(string? versionOutput)
    {
        if (versionOutput is null) return null;
        var match = MajorMinorPattern().Match(versionOutput);
        return match.Success ? match.Value : null;
    }

    // MARK: - Project

    /// <summary>
    /// Makes sure a project named <see cref="CliArguments.AntigravityProjectName"/>
    /// exists and denies <see cref="DenyRules"/>. Writes only when something
    /// is missing: a new <c>&lt;uuid&gt;.json</c> when no project has the name,
    /// else the existing file with the rules merged in. Returns the path
    /// written, or null. Throws when the write fails.
    /// </summary>
    public string? EnsureProject(Func<string>? makeId = null)
    {
        const string name = CliArguments.AntigravityProjectName;
        foreach (var file in FileSystem.ContentsOfDirectory(ProjectsFolder).Order(StringComparer.Ordinal))
        {
            if (!file.EndsWith(".json", StringComparison.Ordinal)) continue;
            var path = System.IO.Path.Combine(ProjectsFolder, file);
            if (FileSystem.Contents(path) is not { } data || ParseObject(data) is not { } project) continue;
            if (project["name"] is not JsonValue value || !value.TryGetValue<string>(out var projectName) || projectName != name)
            {
                continue;
            }
            if (MergingDenyRules(project) is not { } merged) return null;
            FileSystem.Write(Encode(merged), path);
            return path;
        }
        var id = (makeId ?? (() => Guid.NewGuid().ToString("D")))();
        var created = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["projectResources"] = new JsonObject(),
            ["permissionGrants"] = new JsonObject
            {
                ["permissionGrants"] = new JsonObject { ["deny"] = new JsonArray(DenyRules.Select(rule => (JsonNode?)rule).ToArray()) },
            },
        };
        var createdPath = System.IO.Path.Combine(ProjectsFolder, $"{id}.json");
        FileSystem.Write(Encode(created), createdPath);
        return createdPath;
    }

    /// <summary>
    /// <paramref name="project"/> with <c>permissionGrants.permissionGrants.deny</c>
    /// holding every rule in <see cref="DenyRules"/> (existing rules kept, in
    /// order), or null when it already does. Every other key is left as it is.
    /// </summary>
    internal static JsonObject? MergingDenyRules(JsonObject project)
    {
        var merged = (JsonObject)project.DeepClone();
        if (merged["permissionGrants"] is not JsonObject outer)
        {
            outer = [];
            merged["permissionGrants"] = outer;
        }
        if (outer["permissionGrants"] is not JsonObject grants)
        {
            grants = [];
            outer["permissionGrants"] = grants;
        }
        var existing = StringArray(grants["deny"]);
        var missing = DenyRules.Where(rule => !existing.Contains(rule)).ToList();
        if (missing.Count == 0) return null;
        grants["deny"] = new JsonArray(existing.Concat(missing).Select(rule => (JsonNode?)rule).ToArray());
        return merged;
    }

    /// <summary>The strings of a JSON string array (Swift <c>as? [String]</c>); empty for anything else.</summary>
    private static List<string> StringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        List<string> strings = [];
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text)) return [];
            strings.Add(text);
        }
        return strings;
    }

    private static JsonObject? ParseObject(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pretty-printed, keys sorted, slashes unescaped (the Mac's JSONSerialization options).</summary>
    private static byte[] Encode(JsonNode node) =>
        JsonSerializer.SerializeToUtf8Bytes(CliClient.SortedKeys(node), WriteOptions);

    // MARK: - Conversations

    /// <summary>agy's conversation ids are lowercase UUIDs. Anything else is refused, so a path can never be built from other text.</summary>
    public static bool IsConversationId(string value) => ConversationIdPattern().IsMatch(value);

    /// <summary><c>conversation_id</c> from <c>agy --output-format json</c> output, or null.</summary>
    public static string? ConversationId(string stdout)
    {
        try
        {
            return JsonNode.Parse(stdout) is JsonObject envelope
                && envelope["conversation_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                && IsConversationId(id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every path to delete for <paramref name="id"/>: entries of
    /// <see cref="ConversationFolders"/> whose name contains the id.
    /// </summary>
    public IReadOnlyList<string> ConversationPaths(string id)
    {
        if (!IsConversationId(id)) return [];
        return ConversationFolders.SelectMany(folder =>
        {
            var path = System.IO.Path.Combine(AppDataFolder, folder);
            return FileSystem.ContentsOfDirectory(path).Order(StringComparer.Ordinal)
                .Where(name => name.Contains(id, StringComparison.Ordinal))
                .Select(name => System.IO.Path.Combine(path, name));
        }).ToList();
    }

    /// <summary>Why the shared-store edits must be skipped, or null when they may run.</summary>
    public string? StoreEditSkipReason(string? agyVersion)
    {
        if (MajorMinor(agyVersion) is not { } version) return "the agy version is unknown";
        if (version != VerifiedVersion) return $"agy {version} is not the verified {VerifiedVersion}";
        if (agyIsRunning()) return "another agy process is running";
        return null;
    }

    /// <summary>
    /// Deletes the conversation <paramref name="id"/>: always the entries named
    /// after it; its subagents and its entries in agy's shared stores only
    /// when <see cref="StoreEditSkipReason"/> allows. Returns what was removed
    /// (paths, and store labels such as <c>cache/…</c> or <c>summary:&lt;id&gt;</c>).
    /// Never throws.
    /// </summary>
    public IReadOnlyList<string> RemoveConversation(string id, string? agyVersion)
    {
        if (StoreEditSkipReason(agyVersion) is { } reason)
        {
            Log($"agy cleanup: index edits skipped ({reason}); removing only the files of {id}");
            return RemoveNamedEntries(id);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return RemoveConversation(id, visited, 0);
    }

    private List<string> RemoveNamedEntries(string id)
    {
        List<string> removed = [];
        foreach (var path in ConversationPaths(id))
        {
            try
            {
                FileSystem.RemoveItem(path);
                removed.Add(path);
            }
            catch (Exception error) when (IsHousekeepingFailure(error))
            {
                Log($"agy cleanup: removing {path} failed: {error.Message}");
            }
        }
        return removed;
    }

    private List<string> RemoveConversation(string id, HashSet<string> visited, int depth)
    {
        if (!IsConversationId(id) || depth >= 4 || !visited.Add(id)) return [];
        List<string> removed = [];
        IReadOnlyList<string> children;
        try
        {
            children = Index.ChildConversations(id);
        }
        catch (Exception error) when (IsHousekeepingFailure(error))
        {
            Log($"agy cleanup: listing subagents of {id} failed: {error.Message}");
            children = [];
        }
        foreach (var child in children)
        {
            removed.AddRange(RemoveConversation(child, visited, depth + 1));
        }
        removed.AddRange(RemoveNamedEntries(id));
        removed.AddRange(EditCache("cache/last_conversations.json", id, RemovingLastConversation));
        removed.AddRange(EditCache("cache/conversation_metadata.json", id, RemovingMetadata));
        removed.AddRange(EditCache("jetbox_summaries_proto.pb", id, RemovingSummaryEntry));
        try
        {
            Index.RemoveSummary(id);
            removed.Add($"summary:{id}");
        }
        catch (Exception error) when (IsHousekeepingFailure(error))
        {
            Log($"agy cleanup: removing the summary of {id} failed: {error.Message}");
        }
        return removed;
    }

    /// <summary>
    /// Edits one of agy's stores. <paramref name="relativePath"/> uses
    /// <c>/</c> as the label the Mac returns; the file is under
    /// <see cref="AppDataFolder"/> with Windows separators.
    /// </summary>
    private List<string> EditCache(string relativePath, string id, Func<byte[], string, byte[]?> edit)
    {
        var path = System.IO.Path.Combine([AppDataFolder, .. relativePath.Split('/')]);
        if (FileSystem.Contents(path) is not { } data || edit(data, id) is not { } edited) return [];
        try
        {
            FileSystem.ReplaceKeepingBackup(edited, path, data);
            return [relativePath];
        }
        catch (Exception error) when (IsHousekeepingFailure(error))
        {
            Log($"agy cleanup: editing {path} failed: {error.Message}");
            return [];
        }
    }

    /// <summary>Anything but a programming error: the Mac swallows every error here.</summary>
    private static bool IsHousekeepingFailure(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static void Log(string message) => Trace.WriteLine(message, "Hearsay.antigravity");

    /// <summary>
    /// <c>last_conversations.json</c> maps a working folder to its last
    /// conversation id. Removes the entries whose value is <paramref name="id"/>;
    /// null when there are none or the file is not such a map.
    /// </summary>
    internal static byte[]? RemovingLastConversation(byte[] data, string id)
    {
        if (ParseObject(data) is not { } map) return null;
        var kept = new JsonObject();
        var dropped = false;
        foreach (var (key, value) in map)
        {
            if (value is JsonValue text && text.TryGetValue<string>(out var stored) && stored == id)
            {
                dropped = true;
                continue;
            }
            kept[key] = value?.DeepClone();
        }
        return dropped ? Encode(kept) : null;
    }

    /// <summary>
    /// <c>jetbox_summaries_proto.pb</c> is a protobuf message whose field 1 is
    /// repeated, one entry per conversation, each starting with field 1 = the
    /// conversation id. Drops the entries whose id is <paramref name="id"/> and
    /// keeps every other byte as it is. Null when there is none, or when the
    /// file does not have that shape (then nothing is written).
    /// </summary>
    internal static byte[]? RemovingSummaryEntry(byte[] data, string id)
    {
        var target = System.Text.Encoding.UTF8.GetBytes(id);
        var kept = new List<byte>(data.Length);
        var index = 0;
        var dropped = false;
        while (index < data.Length)
        {
            var start = index;
            if (Varint(data, ref index) is not { } key || key != ((1 << 3) | 2)
                || Varint(data, ref index) is not { } length || length > (ulong)(data.Length - index))
            {
                return null;
            }
            var end = index + (int)length;
            var inner = index;
            if (Varint(data, ref inner) is { } innerKey && innerKey == ((1 << 3) | 2)
                && Varint(data, ref inner) is { } innerLength && innerLength <= (ulong)(end - inner)
                && data.AsSpan(inner, (int)innerLength).SequenceEqual(target))
            {
                dropped = true;
            }
            else
            {
                kept.AddRange(data.AsSpan(start, end - start).ToArray());
            }
            index = end;
        }
        return dropped ? kept.ToArray() : null;
    }

    private static ulong? Varint(byte[] bytes, ref int index)
    {
        ulong value = 0;
        var shift = 0;
        while (index < bytes.Length && shift < 64)
        {
            var current = bytes[index];
            index++;
            value |= (ulong)(current & 0x7f) << shift;
            if (current < 0x80) return value;
            shift += 7;
        }
        return null;
    }

    /// <summary>
    /// <c>conversation_metadata.json</c> is <c>{"conversations": {&lt;id&gt;: …}}</c>.
    /// Removes the <paramref name="id"/> key; null when it is absent.
    /// </summary>
    internal static byte[]? RemovingMetadata(byte[] data, string id)
    {
        if (ParseObject(data) is not { } root || root["conversations"] is not JsonObject conversations
            || !conversations.ContainsKey(id))
        {
            return null;
        }
        var edited = (JsonObject)root.DeepClone();
        if (edited["conversations"] is JsonObject copy) copy.Remove(id);
        return Encode(edited);
    }
}
