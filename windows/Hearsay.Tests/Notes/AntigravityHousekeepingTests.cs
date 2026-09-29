using System.Diagnostics;
using System.Text;
using Hearsay.Core.Notes;
using Hearsay.Tests.Naming;
using static Hearsay.Tests.Notes.CliFixtures;

namespace Hearsay.Tests.Notes;

// Port of mac/HearsayCore/Tests/HearsayCoreTests/AntigravityHousekeepingTests.swift,
// one class per Swift suite and one test per Swift test. Paths are the
// Windows ones (%USERPROFILE%\.gemini); the real-disk tests use a scratch
// folder, never the owner's .gemini.

internal static class HousekeepingPaths
{
    public const string Projects = @"C:\Users\test\.gemini\config\projects";
    public const string AppData = @"C:\Users\test\.gemini\antigravity-cli";
    public const string RunId = "19851dbe-0d49-465b-8bb4-f2ffd2eab4c4";
    public const string OtherId = "0cb76a4c-28ef-4515-99ad-6f6ee04c3723";
    public const string ChildId = "76866d03-d7c1-4324-b625-27ef0c74f6b5";
}

public sealed class AntigravityProjectTests
{
    private const string Projects = HousekeepingPaths.Projects;

    [Fact]
    public void DenyRulesCoverCommandsFilesUrlsAndMcp()
    {
        Assert.Equal(["command(*)", "read_file(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "mcp(*)"],
            AntigravityHousekeeping.DenyRules);
    }

    [Fact]
    public void CreatesTheProjectWhenNoneHasTheName()
    {
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{Projects}\default-cli-project.json", """{"id": "default-cli-project", "name": "CLI Project", "projectResources": {}}""");
        files.Add($@"{Projects}\broken.json", "not json");
        var housekeeping = FakeHousekeeping(files);
        var written = housekeeping.EnsureProject(() => "5bef095b-5485-4e42-a36f-753ed0794d7f");
        var path = $@"{Projects}\5bef095b-5485-4e42-a36f-753ed0794d7f.json";
        Assert.Equal(path, written);
        var project = files.Json(path);
        Assert.NotNull(project);
        Assert.Equal("5bef095b-5485-4e42-a36f-753ed0794d7f", (string?)project["id"]);
        Assert.Equal("hearsay-notes", (string?)project["name"]);
        Assert.Empty(project["projectResources"]?.AsObject() ?? throw new InvalidOperationException("no projectResources"));
        var grants = project["permissionGrants"]?["permissionGrants"]?.AsObject()
            ?? throw new InvalidOperationException("no grants");
        Assert.Equal(AntigravityHousekeeping.DenyRules, grants["deny"]?.AsArray().Select(rule => (string?)rule));
        Assert.False(grants.ContainsKey("allow"));
        Assert.Equal([path], files.Written);
        Assert.Contains("CLI Project", files.Text($@"{Projects}\default-cli-project.json"), StringComparison.Ordinal);

        // Already in place: nothing is written again.
        Assert.Null(housekeeping.EnsureProject(() => "never-used"));
        Assert.Equal([path], files.Written);
    }

    /// <summary>
    /// The existing project keeps every key and rule it has; only missing
    /// deny rules are added. Other project files are never written.
    /// </summary>
    [Fact]
    public void MergesIntoTheExistingProject()
    {
        var files = new FakeAntigravityFileSystem();
        var path = $@"{Projects}\0062828e-9a08-4a42-b326-6b1dc1b62970.json";
        files.Add(path, """
            {"id": "0062828e-9a08-4a42-b326-6b1dc1b62970", "name": "hearsay-notes",
             "projectResources": {"resources": [{"folderUri": "file:///tmp/x"}]},
             "settings": {"sandboxMode": true},
             "permissionGrants": {"v2Migrated": true, "permissionGrants": {"allow": ["command(git status)"], "deny": ["read_file(*)", "search_web(*)"]}}}
            """);
        files.Add($@"{Projects}\86a1313d-757d-499e-9a57-026dc92ed777.json", """{"id": "86a1313d", "name": "C:\\Users\\test\\repo"}""");
        Assert.Equal(path, FakeHousekeeping(files).EnsureProject(() => "unused"));
        Assert.Equal([path], files.Written);
        var project = files.Json(path) ?? throw new InvalidOperationException("no project");
        Assert.Equal("0062828e-9a08-4a42-b326-6b1dc1b62970", (string?)project["id"]);
        Assert.Equal("""{"resources":[{"folderUri":"file:///tmp/x"}]}""", project["projectResources"]?.ToJsonString());
        Assert.Equal("""{"sandboxMode":true}""", project["settings"]?.ToJsonString());
        var outer = project["permissionGrants"]?.AsObject() ?? throw new InvalidOperationException("no outer");
        Assert.True((bool?)outer["v2Migrated"]);
        var grants = outer["permissionGrants"]?.AsObject() ?? throw new InvalidOperationException("no grants");
        Assert.Equal(["command(git status)"], grants["allow"]?.AsArray().Select(rule => (string?)rule));
        Assert.Equal(
            ["read_file(*)", "search_web(*)", "command(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "mcp(*)"],
            grants["deny"]?.AsArray().Select(rule => (string?)rule));
    }

    [Fact]
    public void MergeIsNullWhenEveryRuleIsPresent()
    {
        var complete = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "hearsay-notes",
            ["permissionGrants"] = new System.Text.Json.Nodes.JsonObject
            {
                ["permissionGrants"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["deny"] = new System.Text.Json.Nodes.JsonArray(
                        AntigravityHousekeeping.DenyRules.Reverse().Select(rule => (System.Text.Json.Nodes.JsonNode?)rule).ToArray()),
                },
            },
        };
        Assert.Null(AntigravityHousekeeping.MergingDenyRules(complete));
        var merged = AntigravityHousekeeping.MergingDenyRules(new System.Text.Json.Nodes.JsonObject { ["name"] = "hearsay-notes" });
        Assert.Equal(AntigravityHousekeeping.DenyRules,
            merged?["permissionGrants"]?["permissionGrants"]?["deny"]?.AsArray().Select(rule => (string?)rule));
    }
}

public sealed class AntigravityConversationCleanupTests
{
    private const string AppData = HousekeepingPaths.AppData;
    private const string RunId = HousekeepingPaths.RunId;
    private const string OtherId = HousekeepingPaths.OtherId;

    [Fact]
    public void ConversationIdIsReadFromTheJsonOutputOnly()
    {
        Assert.Equal(RunId, AntigravityHousekeeping.ConversationId($$"""{"conversation_id":"{{RunId}}","status":"SUCCESS"}"""));
        Assert.Null(AntigravityHousekeeping.ConversationId(""));
        Assert.Null(AntigravityHousekeeping.ConversationId(LaunchNotes));
        Assert.Null(AntigravityHousekeeping.ConversationId("""{"conversation_id":""}"""));
        foreach (var bad in new[] { "../../x", "19851DBE-0D49-465B-8BB4-F2FFD2EAB4C4", RunId + "/..", RunId + "x", "*", RunId + "\n", @"..\x" })
        {
            Assert.False(AntigravityHousekeeping.IsConversationId(bad), bad);
            var escaped = System.Text.Json.JsonSerializer.Serialize(bad);
            Assert.Null(AntigravityHousekeeping.ConversationId($$"""{"conversation_id":{{escaped}}}"""));
        }
    }

    [Fact]
    public void PathsAreOnlyThoseNamedAfterTheId()
    {
        var files = new FakeAntigravityFileSystem();
        foreach (var path in new[]
        {
            $@"conversations\{RunId}.db", $@"conversations\{RunId}.db-wal", $@"conversations\{OtherId}.db",
            $@"brain\{RunId}\.system_generated\logs\transcript.jsonl", $@"brain\{OtherId}\scratch\a",
            $@"annotations\{RunId}.pbtxt", $@"presence\{RunId}.lock", @"implicit\8c7759e3-f6ca-4d27-be1e-56bee5063c5e.pb",
            @"log\cli-20260928_164808.log", "history.jsonl",
        })
        {
            files.Add($@"{AppData}\{path}");
        }
        Assert.Equal(
            [
                $@"{AppData}\conversations\{RunId}.db", $@"{AppData}\conversations\{RunId}.db-wal",
                $@"{AppData}\brain\{RunId}", $@"{AppData}\annotations\{RunId}.pbtxt", $@"{AppData}\presence\{RunId}.lock",
            ],
            FakeHousekeeping(files).ConversationPaths(RunId));
        Assert.Empty(FakeHousekeeping(files).ConversationPaths(".."));
    }

    [Fact]
    public void RemovalDeletesFilesCacheEntriesSummaryAndSubagents()
    {
        const string child = HousekeepingPaths.ChildId;
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\conversations\{RunId}.db");
        files.Add($@"{AppData}\brain\{RunId}\scratch\x");
        files.Add($@"{AppData}\conversations\{child}.db");
        files.Add($@"{AppData}\conversations\{OtherId}.db");
        files.Add($@"{AppData}\cache\last_conversations.json", $$"""
            {
              "C:\\Users\\test\\repo": "{{OtherId}}",
              "C:\\Users\\test\\AppData\\Local\\Temp\\Hearsay-antigravity-1\\hearsay-notes": "{{RunId}}",
              "C:\\Users\\test\\AppData\\Local\\Temp\\Hearsay-antigravity-2\\hearsay-notes": "{{RunId}}"
            }
            """);
        files.Add($@"{AppData}\cache\conversation_metadata.json",
            $$"""{"conversations": {"{{OtherId}}": {"is_internal": false}, "{{child}}": {"is_internal": true} } }""");
        var index = new FakeConversationIndex(new() { [RunId] = [child], [child] = [RunId] });
        var removed = new AntigravityHousekeeping(Home, files, index, () => false).RemoveConversation(RunId, "1.2.12");
        Assert.Equal(
            [
                $@"{AppData}\cache\conversation_metadata.json", $@"{AppData}\cache\conversation_metadata.json.hearsay-backup",
                $@"{AppData}\cache\last_conversations.json", $@"{AppData}\cache\last_conversations.json.hearsay-backup",
                $@"{AppData}\conversations\{OtherId}.db",
            ],
            files.Paths);
        Assert.Equal([child, RunId], index.Removed);
        Assert.Equal($$"""{"C:\\Users\\test\\repo":"{{OtherId}}"}""", files.Json($@"{AppData}\cache\last_conversations.json")?.ToJsonString());
        var metadata = files.Json($@"{AppData}\cache\conversation_metadata.json")?["conversations"]?.AsObject()
            ?? throw new InvalidOperationException("no metadata");
        Assert.Equal([OtherId], metadata.Select(pair => pair.Key));
        Assert.Contains($"summary:{RunId}", removed);
        Assert.Contains("cache/last_conversations.json", removed);
    }

    /// <summary>Nothing to find, or every step failing, still returns normally.</summary>
    [Fact]
    public void FailuresAreSwallowed()
    {
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\conversations\{RunId}.db");
        files.Add($@"{AppData}\presence\{RunId}.lock");
        files.FailRemoval($@"{AppData}\conversations\{RunId}.db");
        files.Add($@"{AppData}\cache\last_conversations.json", $$"""{"/w": "{{RunId}}"}""");
        files.FailWrites = true;
        var index = new FakeConversationIndex { FailRemovals = true };
        var removed = new AntigravityHousekeeping(Home, files, index, () => false).RemoveConversation(RunId, "1.2.12");
        Assert.Equal([$@"{AppData}\presence\{RunId}.lock"], removed);
        Assert.Contains($@"{AppData}\conversations\{RunId}.db", files.Paths);
        Assert.Equal([$"summary:{RunId}"], FakeHousekeeping().RemoveConversation(RunId, "1.2.12"));
        Assert.Empty(FakeHousekeeping().RemoveConversation("not-an-id", "1.2.12"));
    }

    [Fact]
    public void CacheEditsLeaveUnrelatedFilesAlone()
    {
        Assert.Null(AntigravityHousekeeping.RemovingLastConversation(Encoding.UTF8.GetBytes($$"""{"/a": "{{OtherId}}"}"""), RunId));
        Assert.Null(AntigravityHousekeeping.RemovingLastConversation("[1]"u8.ToArray(), RunId));
        Assert.Null(AntigravityHousekeeping.RemovingMetadata("""{"conversations": {}}"""u8.ToArray(), RunId));
        Assert.Null(AntigravityHousekeeping.RemovingMetadata("x"u8.ToArray(), RunId));
    }

    /// <summary>Field 1 entries, each led by field 1 = id; only the matching entry goes, the other bytes stay as they are.</summary>
    [Fact]
    public void SummaryProtoEntryIsDropped()
    {
        static byte[] Entry(string id, byte[] rest)
        {
            byte[] inner = [0x0a, (byte)Encoding.UTF8.GetByteCount(id), .. Encoding.UTF8.GetBytes(id), .. rest];
            return [0x0a, (byte)inner.Length, .. inner];
        }
        var mine = Entry(RunId, [0x12, 0x03, .. "abc"u8.ToArray()]);
        var other = Entry(OtherId, [0x18, 0x01]);
        byte[] file = [.. other, .. mine, .. other];
        Assert.Equal([.. other, .. other], AntigravityHousekeeping.RemovingSummaryEntry(file, RunId) ?? []);
        Assert.Null(AntigravityHousekeeping.RemovingSummaryEntry(other, RunId));
        Assert.Null(AntigravityHousekeeping.RemovingSummaryEntry([.. mine, 0x0a, 0x7f], RunId));
        Assert.Null(AntigravityHousekeeping.RemovingSummaryEntry("{}"u8.ToArray(), RunId));

        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\jetbox_summaries_proto.pb", file);
        _ = FakeHousekeeping(files).RemoveConversation(RunId, "1.2.12");
        Assert.Equal([.. other, .. other], files.Bytes($@"{AppData}\jetbox_summaries_proto.pb") ?? []);
    }

    /// <summary>The real SQLite index (winsqlite3.dll) on a throwaway copy of agy's table layout.</summary>
    [Fact]
    public void SqliteIndexListsSubagentsAndDeletesOneRow()
    {
        using var folder = new TemporaryDirectory("HearsayTests");
        var path = Path.Combine(folder.Url, "conversation_summaries.db");
        SqliteHelper.Execute(path, create: true, $"""
            CREATE TABLE conversation_summaries (conversation_id text, title text NOT NULL DEFAULT '', parent_conversation_id text NOT NULL DEFAULT '', PRIMARY KEY (conversation_id));
            INSERT INTO conversation_summaries VALUES ('{RunId}', 'Notes', '');
            INSERT INTO conversation_summaries VALUES ('{HousekeepingPaths.ChildId}', '', '{RunId}');
            INSERT INTO conversation_summaries VALUES ('{OtherId}', 'Mine', '');
            """);

        var index = new SqliteAntigravityConversationIndex(path);
        Assert.Equal([HousekeepingPaths.ChildId], index.ChildConversations(RunId));
        index.RemoveSummary(RunId);
        // No row: no backup is taken and nothing changes.
        var backup = path + LocalAntigravityFileSystem.BackupSuffix;
        var backupAfterFirst = File.ReadAllBytes(backup);
        index.RemoveSummary("a154b590-b426-43cc-9bbf-64f982342f57");
        Assert.Equal(backupAfterFirst, File.ReadAllBytes(backup));
        // The backup is the database as it was before the delete, and no temporary file is left.
        Assert.Equal(["3"], SqliteHelper.Query(backup, "SELECT count(*) FROM conversation_summaries"));
        Assert.DoesNotContain(Directory.GetFiles(folder.Url), file => file.Contains("hearsay-tmp", StringComparison.Ordinal));
        Assert.Equal([OtherId, HousekeepingPaths.ChildId],
            SqliteHelper.Query(path, "SELECT conversation_id FROM conversation_summaries ORDER BY conversation_id"));

        var missing = new SqliteAntigravityConversationIndex(Path.Combine(folder.Url, "none.db"));
        Assert.Empty(missing.ChildConversations(RunId));
        missing.RemoveSummary(RunId);
        Assert.False(File.Exists(Path.Combine(folder.Url, "none.db")));
    }
}

/// <summary>The guards around agy's shared stores: version gate, running agy, backups, atomic replacement.</summary>
public sealed class AntigravityStoreGuardTests
{
    private const string AppData = HousekeepingPaths.AppData;
    private const string RunId = HousekeepingPaths.RunId;
    private const string OtherId = HousekeepingPaths.OtherId;
    private const string Child = HousekeepingPaths.ChildId;

    /// <summary>Every kind of thing a full cleanup touches, for one run and a subagent.</summary>
    private static (FakeAntigravityFileSystem Files, FakeConversationIndex Index) Populated()
    {
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\conversations\{RunId}.db");
        files.Add($@"{AppData}\brain\{RunId}\scratch\x");
        files.Add($@"{AppData}\conversations\{Child}.db");
        files.Add($@"{AppData}\cache\last_conversations.json", $$"""{"/w": "{{RunId}}", "/mine": "{{OtherId}}"}""");
        files.Add($@"{AppData}\cache\conversation_metadata.json", $$"""{"conversations": {"{{RunId}}": {} } }""");
        return (files, new FakeConversationIndex(new() { [RunId] = [Child] }));
    }

    [Fact]
    public void MajorMinorIsReadFromVersionOutput()
    {
        Assert.Equal("1.2", AntigravityHousekeeping.MajorMinor("1.2.12"));
        Assert.Equal("1.2", AntigravityHousekeeping.MajorMinor("agy 1.2.0\r\n"));
        Assert.Equal("1.10", AntigravityHousekeeping.MajorMinor("1.10.3"));
        Assert.Equal("2.0", AntigravityHousekeeping.MajorMinor("2.0"));
        Assert.Null(AntigravityHousekeeping.MajorMinor("unknown"));
        Assert.Null(AntigravityHousekeeping.MajorMinor(null));
        Assert.Equal("1.2", AntigravityHousekeeping.VerifiedVersion);
    }

    /// <summary>Another version, or none known: only entries named after the id go; caches, the database, and subagents are not touched.</summary>
    [Theory]
    [InlineData("1.3.0")]
    [InlineData("2.2.1")]
    [InlineData("1.20.0")]
    [InlineData("")]
    [InlineData("garbage")]
    public void OtherVersionRemovesOnlyIdNamedEntries(string version)
    {
        var (files, index) = Populated();
        var before = files.Paths;
        var removed = FakeHousekeeping(files, index).RemoveConversation(RunId, version);
        Assert.Equal([$@"{AppData}\conversations\{RunId}.db", $@"{AppData}\brain\{RunId}"], removed);
        Assert.Equal(before.Where(path => !path.Contains($@"\{RunId}", StringComparison.Ordinal)), files.Paths);
        Assert.Empty(files.Replaced);
        Assert.Empty(index.Removed);
        Assert.Contains($@"{AppData}\conversations\{Child}.db", files.Paths);
        Assert.NotNull(FakeHousekeeping(files).StoreEditSkipReason(version));
    }

    [Fact]
    public void RunningAgySkipsTheStoreEdits()
    {
        var (files, index) = Populated();
        var housekeeping = FakeHousekeeping(files, index, agyIsRunning: true);
        Assert.Equal("another agy process is running", housekeeping.StoreEditSkipReason("1.2.12"));
        var removed = housekeeping.RemoveConversation(RunId, "1.2.12");
        Assert.Equal([$@"{AppData}\conversations\{RunId}.db", $@"{AppData}\brain\{RunId}"], removed);
        Assert.Empty(files.Replaced);
        Assert.Empty(index.Removed);
        Assert.Contains(RunId, files.Text($@"{AppData}\cache\last_conversations.json"), StringComparison.Ordinal);
    }

    /// <summary>Verified version, no other agy: stores are replaced, each keeping the previous bytes as <c>.hearsay-backup</c>.</summary>
    [Fact]
    public void StoreEditsKeepABackup()
    {
        var (files, index) = Populated();
        var original = files.Text($@"{AppData}\cache\last_conversations.json");
        var removed = FakeHousekeeping(files, index).RemoveConversation(RunId, "1.2.12");
        Assert.Contains("cache/last_conversations.json", removed);
        Assert.Equal([$@"{AppData}\cache\last_conversations.json", $@"{AppData}\cache\conversation_metadata.json"], files.Replaced);
        Assert.Equal(original, files.Text($@"{AppData}\cache\last_conversations.json{LocalAntigravityFileSystem.BackupSuffix}"));
        Assert.Equal($$"""{"/mine":"{{OtherId}}"}""", files.Json($@"{AppData}\cache\last_conversations.json")?.ToJsonString());
        Assert.Empty(files.Written);
        Assert.Equal([Child, RunId], index.Removed);
    }

    [Fact]
    public void LocalReplaceIsAtomicKeepsOneBackupAndRefusesAChangedFile()
    {
        using var folder = new TemporaryDirectory("HearsayTests");
        var path = Path.Combine(folder.Url, "last_conversations.json");
        var backup = path + LocalAntigravityFileSystem.BackupSuffix;
        var fileSystem = new LocalAntigravityFileSystem();
        File.WriteAllBytes(path, "v1"u8.ToArray());

        fileSystem.ReplaceKeepingBackup("v2"u8.ToArray(), path, "v1"u8.ToArray());
        Assert.Equal("v2"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal("v1"u8.ToArray(), File.ReadAllBytes(backup));

        fileSystem.ReplaceKeepingBackup("v3"u8.ToArray(), path, "v2"u8.ToArray());
        Assert.Equal("v3"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal("v2"u8.ToArray(), File.ReadAllBytes(backup));

        // agy wrote the file after Hearsay read it: nothing changes.
        Assert.Throws<AntigravityWriteException>(() => fileSystem.ReplaceKeepingBackup("v4"u8.ToArray(), path, "v2"u8.ToArray()));
        Assert.Equal("v3"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal("v2"u8.ToArray(), File.ReadAllBytes(backup));
        Assert.Equal(["last_conversations.json", "last_conversations.json.hearsay-backup"],
            Directory.GetFiles(folder.Url).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProcessListingFindsThisProcessByName()
    {
        using var current = Process.GetCurrentProcess();
        Assert.True(AntigravityProcesses.IsRunning(current.ProcessName));
        var sameName = Process.GetProcessesByName(current.ProcessName);
        var others = sameName.Count(process => process.Id != current.Id);
        foreach (var process in sameName)
        {
            process.Dispose();
        }
        Assert.Equal(others > 0, AntigravityProcesses.IsRunning(current.ProcessName, new HashSet<int> { current.Id }));
        Assert.False(AntigravityProcesses.IsRunning($"hearsay-no-such-process-{Guid.NewGuid():N}"[..28]));
    }

    /// <summary><c>--version</c> runs once per program and is cached; a version other than 1.2 leaves the caches alone.</summary>
    [Fact]
    public async Task ClientFetchesTheVersionOnceAndGatesOnIt()
    {
        var files = new FakeAntigravityFileSystem();
        files.Add($@"{AppData}\conversations\{RunId}.db");
        files.Add($@"{AppData}\cache\last_conversations.json", $$"""{"/w": "{{RunId}}"}""");
        var fake = new FakeCliRunner(argv => argv[^1] == "--version"
            ? new CliRunResult(0, "1.3.0\n", "")
            : new CliRunResult(0, $$"""{"conversation_id":"{{RunId}}","status":"SUCCESS","response":"x"}""", ""));
        var client = Client(fake, Locator([AgyPath]), FakeHousekeeping(files), new CliVersionCache());
        var configuration = AIProviderConfiguration.FromPreset(ProviderPreset.AntigravityCli) with { AntigravityPath = AgyPath };
        for (var run = 0; run < 2; run++)
        {
            _ = await client.CompleteAsync("s", "u", configuration, null);
        }
        Assert.Single(fake.Calls, call => call.Argv.SequenceEqual([AgyPath, "--version"]));
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal([$@"{AppData}\conversations\{RunId}.db"], files.Removed);
        Assert.Empty(files.Replaced);
        Assert.Contains(RunId, files.Text($@"{AppData}\cache\last_conversations.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPathsAreUnderTheHomeFolder()
    {
        var housekeeping = new AntigravityHousekeeping(@"C:\Users\someone", new FakeAntigravityFileSystem(), new FakeConversationIndex(), () => false);
        Assert.Equal(@"C:\Users\someone\.gemini\config\projects", housekeeping.ProjectsFolder);
        Assert.Equal(@"C:\Users\someone\.gemini\antigravity-cli", housekeeping.AppDataFolder);
        var real = new AntigravityHousekeeping(@"C:\Users\someone", new FakeAntigravityFileSystem());
        Assert.Equal(@"C:\Users\someone\.gemini\antigravity-cli\conversation_summaries.db",
            Assert.IsType<SqliteAntigravityConversationIndex>(real.Index).Path);
    }
}

/// <summary>Direct winsqlite3 access for the SQLite test (the Swift test calls SQLite3 itself).</summary>
internal static class SqliteHelper
{
    public static void Execute(string path, bool create, string sql)
    {
        var flags = WinSqlite.OpenReadWrite | (create ? WinSqlite.OpenCreate : 0);
        Assert.Equal(WinSqlite.Ok, WinSqlite.sqlite3_open_v2(path, out var database, flags, IntPtr.Zero));
        try
        {
            Assert.Equal(WinSqlite.Ok, WinSqlite.sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        }
        finally
        {
            _ = WinSqlite.sqlite3_close(database);
        }
    }

    public static List<string> Query(string path, string sql)
    {
        Assert.Equal(WinSqlite.Ok, WinSqlite.sqlite3_open_v2(path, out var database, WinSqlite.OpenReadOnly, IntPtr.Zero));
        try
        {
            Assert.Equal(WinSqlite.Ok, WinSqlite.sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero));
            List<string> rows = [];
            while (WinSqlite.sqlite3_step(statement) == WinSqlite.Row)
            {
                rows.Add(WinSqlite.ColumnText(statement, 0) ?? string.Empty);
            }
            _ = WinSqlite.sqlite3_finalize(statement);
            return rows;
        }
        finally
        {
            _ = WinSqlite.sqlite3_close(database);
        }
    }
}
