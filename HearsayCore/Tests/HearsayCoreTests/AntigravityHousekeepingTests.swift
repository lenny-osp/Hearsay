import Foundation
import SQLite3
import Testing
@testable import HearsayCore

/// An in-memory file tree: files by absolute path; folders are implied.
final class FakeAntigravityFileSystem: AntigravityFileSystem, @unchecked Sendable {
    struct Failure: Error {}

    private let lock = NSLock()
    private var files: [String: Data] = [:]
    private var removedPaths: [String] = []
    private var writtenPaths: [String] = []
    private var failing: Set<String> = []
    var failWrites = false

    func add(_ path: String, _ text: String = "") {
        lock.withLock { files[path] = Data(text.utf8) }
    }

    /// `removeItem` fails for this path.
    func failRemoval(of path: String) {
        lock.withLock { _ = failing.insert(path) }
    }

    var paths: [String] { lock.withLock { files.keys.sorted() } }
    var removed: [String] { lock.withLock { removedPaths } }
    var written: [String] { lock.withLock { writtenPaths } }

    func text(_ path: String) -> String? {
        lock.withLock { files[path].map { String(decoding: $0, as: UTF8.self) } }
    }

    func json(_ path: String) -> [String: Any]? {
        lock.withLock { files[path] }.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    }

    func contentsOfDirectory(atPath path: String) -> [String] {
        let prefix = path + "/"
        return lock.withLock {
            Array(Set(files.keys.filter { $0.hasPrefix(prefix) }.compactMap {
                $0.dropFirst(prefix.count).split(separator: "/").first.map(String.init)
            }))
        }
    }

    func contents(atPath path: String) -> Data? {
        lock.withLock { files[path] }
    }

    func write(_ data: Data, toPath path: String) throws {
        try lock.withLock {
            if failWrites { throw Failure() }
            files[path] = data
            writtenPaths.append(path)
        }
    }

    func removeItem(atPath path: String) throws {
        try lock.withLock {
            if failing.contains(path) { throw Failure() }
            let matching = files.keys.filter { $0 == path || $0.hasPrefix(path + "/") }
            guard !matching.isEmpty else { throw Failure() }
            matching.forEach { files[$0] = nil }
            removedPaths.append(path)
        }
    }
}

final class FakeConversationIndex: AntigravityConversationIndex, @unchecked Sendable {
    private let lock = NSLock()
    private var childrenByParent: [String: [String]]
    private var removedIDs: [String] = []
    var failRemovals = false

    init(children: [String: [String]] = [:]) {
        childrenByParent = children
    }

    var removed: [String] { lock.withLock { removedIDs } }

    func childConversations(of id: String) throws -> [String] {
        lock.withLock { childrenByParent[id] ?? [] }
    }

    func removeSummary(id: String) throws {
        try lock.withLock {
            if failRemovals { throw FakeAntigravityFileSystem.Failure() }
            removedIDs.append(id)
        }
    }
}

private let projects = "/Users/test/.gemini/config/projects"
private let appData = "/Users/test/.gemini/antigravity-cli"
private let runID = "19851dbe-0d49-465b-8bb4-f2ffd2eab4c4"
private let otherID = "0cb76a4c-28ef-4515-99ad-6f6ee04c3723"

struct AntigravityProjectTests {
    @Test func denyRulesCoverCommandsFilesURLsAndMCP() {
        #expect(AntigravityHousekeeping.denyRules == [
            "command(*)", "read_file(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "mcp(*)",
        ])
    }

    @Test func createsTheProjectWhenNoneHasTheName() throws {
        let files = FakeAntigravityFileSystem()
        files.add("\(projects)/default-cli-project.json", #"{"id": "default-cli-project", "name": "CLI Project", "projectResources": {}}"#)
        files.add("\(projects)/broken.json", "not json")
        let housekeeping = fakeHousekeeping(files)
        let written = try housekeeping.ensureProject(makeID: { "5bef095b-5485-4e42-a36f-753ed0794d7f" })
        let path = "\(projects)/5bef095b-5485-4e42-a36f-753ed0794d7f.json"
        #expect(written == path)
        let project = try #require(files.json(path))
        #expect(project["id"] as? String == "5bef095b-5485-4e42-a36f-753ed0794d7f")
        #expect(project["name"] as? String == "hearsay-notes")
        #expect((project["projectResources"] as? [String: Any])?.isEmpty == true)
        let grants = try #require((project["permissionGrants"] as? [String: Any])?["permissionGrants"] as? [String: Any])
        #expect(grants["deny"] as? [String] == AntigravityHousekeeping.denyRules)
        #expect(grants["allow"] == nil)
        #expect(files.written == [path])
        #expect(files.text("\(projects)/default-cli-project.json")?.contains("CLI Project") == true)

        // Already in place: nothing is written again.
        #expect(try housekeeping.ensureProject(makeID: { "never-used" }) == nil)
        #expect(files.written == [path])
    }

    /// The existing project keeps every key and rule it has; only missing
    /// deny rules are added. Other project files are never written.
    @Test func mergesIntoTheExistingProject() throws {
        let files = FakeAntigravityFileSystem()
        let path = "\(projects)/0062828e-9a08-4a42-b326-6b1dc1b62970.json"
        files.add(path, #"""
        {"id": "0062828e-9a08-4a42-b326-6b1dc1b62970", "name": "hearsay-notes",
         "projectResources": {"resources": [{"folderUri": "file:///tmp/x"}]},
         "settings": {"sandboxMode": true},
         "permissionGrants": {"v2Migrated": true, "permissionGrants": {"allow": ["command(git status)"], "deny": ["read_file(*)", "search_web(*)"]}}}
        """#)
        files.add("\(projects)/86a1313d-757d-499e-9a57-026dc92ed777.json", #"{"id": "86a1313d", "name": "/Users/test/repo"}"#)
        #expect(try fakeHousekeeping(files).ensureProject(makeID: { "unused" }) == path)
        #expect(files.written == [path])
        let project = try #require(files.json(path))
        #expect(project["id"] as? String == "0062828e-9a08-4a42-b326-6b1dc1b62970")
        #expect(((project["projectResources"] as? [String: Any])?["resources"] as? [[String: String]]) == [["folderUri": "file:///tmp/x"]])
        #expect((project["settings"] as? [String: Bool]) == ["sandboxMode": true])
        let outer = try #require(project["permissionGrants"] as? [String: Any])
        #expect(outer["v2Migrated"] as? Bool == true)
        let grants = try #require(outer["permissionGrants"] as? [String: Any])
        #expect(grants["allow"] as? [String] == ["command(git status)"])
        #expect(grants["deny"] as? [String] == [
            "read_file(*)", "search_web(*)", "command(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "mcp(*)",
        ])
    }

    @Test func mergeIsNilWhenEveryRuleIsPresent() {
        let complete: [String: Any] = [
            "name": "hearsay-notes",
            "permissionGrants": ["permissionGrants": ["deny": Array(AntigravityHousekeeping.denyRules.reversed())]],
        ]
        #expect(AntigravityHousekeeping.mergingDenyRules(into: complete) == nil)
        let merged = AntigravityHousekeeping.mergingDenyRules(into: ["name": "hearsay-notes"])
        let deny = ((merged?["permissionGrants"] as? [String: Any])?["permissionGrants"] as? [String: Any])?["deny"] as? [String]
        #expect(deny == AntigravityHousekeeping.denyRules)
    }
}

struct AntigravityConversationCleanupTests {
    @Test func conversationIDIsReadFromTheJSONOutputOnly() {
        #expect(AntigravityHousekeeping.conversationID(inOutput: #"{"conversation_id":"\#(runID)","status":"SUCCESS"}"#) == runID)
        #expect(AntigravityHousekeeping.conversationID(inOutput: "") == nil)
        #expect(AntigravityHousekeeping.conversationID(inOutput: launchNotes) == nil)
        #expect(AntigravityHousekeeping.conversationID(inOutput: #"{"conversation_id":""}"#) == nil)
        for bad in ["../../x", "19851DBE-0D49-465B-8BB4-F2FFD2EAB4C4", "\(runID)/..", "\(runID)x", "*"] {
            #expect(!AntigravityHousekeeping.isConversationID(bad), "\(bad)")
            #expect(AntigravityHousekeeping.conversationID(inOutput: #"{"conversation_id":"\#(bad)"}"#) == nil)
        }
    }

    @Test func pathsAreOnlyThoseNamedAfterTheID() {
        let files = FakeAntigravityFileSystem()
        for path in [
            "conversations/\(runID).db", "conversations/\(runID).db-wal", "conversations/\(otherID).db",
            "brain/\(runID)/.system_generated/logs/transcript.jsonl", "brain/\(otherID)/scratch/a",
            "annotations/\(runID).pbtxt", "presence/\(runID).lock", "implicit/8c7759e3-f6ca-4d27-be1e-56bee5063c5e.pb",
            "log/cli-20260928_164808.log", "history.jsonl",
        ] {
            files.add("\(appData)/\(path)")
        }
        #expect(fakeHousekeeping(files).conversationPaths(id: runID) == [
            "\(appData)/conversations/\(runID).db", "\(appData)/conversations/\(runID).db-wal",
            "\(appData)/brain/\(runID)", "\(appData)/annotations/\(runID).pbtxt", "\(appData)/presence/\(runID).lock",
        ])
        #expect(fakeHousekeeping(files).conversationPaths(id: "..").isEmpty)
    }

    @Test func removalDeletesFilesCacheEntriesSummaryAndSubagents() throws {
        let child = "76866d03-d7c1-4324-b625-27ef0c74f6b5"
        let files = FakeAntigravityFileSystem()
        files.add("\(appData)/conversations/\(runID).db")
        files.add("\(appData)/brain/\(runID)/scratch/x")
        files.add("\(appData)/conversations/\(child).db")
        files.add("\(appData)/conversations/\(otherID).db")
        files.add("\(appData)/cache/last_conversations.json", """
        {
          "/Users/test/repo": "\(otherID)",
          "/private/var/folders/T/Hearsay-antigravity-1/hearsay-notes": "\(runID)",
          "/private/var/folders/T/Hearsay-antigravity-2/hearsay-notes": "\(runID)"
        }
        """)
        files.add("\(appData)/cache/conversation_metadata.json", """
        {"conversations": {"\(otherID)": {"is_internal": false}, "\(child)": {"is_internal": true}}}
        """)
        let index = FakeConversationIndex(children: [runID: [child], child: [runID]])
        let removed = AntigravityHousekeeping(homeDirectory: "/Users/test", fileSystem: files, index: index)
            .removeConversation(id: runID)
        #expect(files.paths == [
            "\(appData)/cache/conversation_metadata.json", "\(appData)/cache/last_conversations.json",
            "\(appData)/conversations/\(otherID).db",
        ])
        #expect(index.removed == [child, runID])
        #expect(files.json("\(appData)/cache/last_conversations.json") as? [String: String] == ["/Users/test/repo": otherID])
        let metadata = try #require(files.json("\(appData)/cache/conversation_metadata.json")?["conversations"] as? [String: Any])
        #expect(Array(metadata.keys) == [otherID])
        #expect(removed.contains("summary:\(runID)"))
        #expect(removed.contains("cache/last_conversations.json"))
    }

    /// Nothing to find, or every step failing, still returns normally.
    @Test func failuresAreSwallowed() {
        let files = FakeAntigravityFileSystem()
        files.add("\(appData)/conversations/\(runID).db")
        files.add("\(appData)/presence/\(runID).lock")
        files.failRemoval(of: "\(appData)/conversations/\(runID).db")
        files.add("\(appData)/cache/last_conversations.json", "{\"/w\": \"\(runID)\"}")
        files.failWrites = true
        let index = FakeConversationIndex()
        index.failRemovals = true
        let removed = AntigravityHousekeeping(homeDirectory: "/Users/test", fileSystem: files, index: index)
            .removeConversation(id: runID)
        #expect(removed == ["\(appData)/presence/\(runID).lock"])
        #expect(files.paths.contains("\(appData)/conversations/\(runID).db"))
        #expect(fakeHousekeeping().removeConversation(id: runID) == ["summary:\(runID)"])
        #expect(fakeHousekeeping().removeConversation(id: "not-an-id").isEmpty)
    }

    @Test func cacheEditsLeaveUnrelatedFilesAlone() {
        let unrelated = Data(#"{"/a": "\#(otherID)"}"#.utf8)
        #expect(AntigravityHousekeeping.removingLastConversation(from: unrelated, id: runID) == nil)
        #expect(AntigravityHousekeeping.removingLastConversation(from: Data("[1]".utf8), id: runID) == nil)
        #expect(AntigravityHousekeeping.removingMetadata(from: Data(#"{"conversations": {}}"#.utf8), id: runID) == nil)
        #expect(AntigravityHousekeeping.removingMetadata(from: Data("x".utf8), id: runID) == nil)
    }

    /// Field 1 entries, each led by field 1 = id; only the matching entry
    /// goes, the other bytes stay as they are.
    @Test func summaryProtoEntryIsDropped() {
        func entry(_ id: String, _ rest: [UInt8]) -> [UInt8] {
            let inner = [0x0a, UInt8(id.utf8.count)] + Array(id.utf8) + rest
            return [0x0a, UInt8(inner.count)] + inner
        }
        let mine = entry(runID, [0x12, 0x03] + Array("abc".utf8))
        let other = entry(otherID, [0x18, 0x01])
        let file = Data(other + mine + other)
        #expect(AntigravityHousekeeping.removingSummaryEntry(from: file, id: runID) == Data(other + other))
        #expect(AntigravityHousekeeping.removingSummaryEntry(from: Data(other), id: runID) == nil)
        #expect(AntigravityHousekeeping.removingSummaryEntry(from: Data(mine + [0x0a, 0x7f]), id: runID) == nil)
        #expect(AntigravityHousekeeping.removingSummaryEntry(from: Data("{}".utf8), id: runID) == nil)

        let files = FakeAntigravityFileSystem()
        files.add("\(appData)/jetbox_summaries_proto.pb", String(decoding: file, as: UTF8.self))
        _ = fakeHousekeeping(files).removeConversation(id: runID)
        #expect(files.text("\(appData)/jetbox_summaries_proto.pb") == String(decoding: other + other, as: UTF8.self))
    }

    /// The real SQLite index on a throwaway copy of agy's table layout.
    @Test func sqliteIndexListsSubagentsAndDeletesOneRow() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("HearsayTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let path = folder.appendingPathComponent("conversation_summaries.db").path
        var database: OpaquePointer?
        #expect(sqlite3_open(path, &database) == SQLITE_OK)
        let sql = """
        CREATE TABLE conversation_summaries (conversation_id text, title text NOT NULL DEFAULT "", parent_conversation_id text NOT NULL DEFAULT "", PRIMARY KEY (conversation_id));
        INSERT INTO conversation_summaries VALUES ('\(runID)', 'Notes', '');
        INSERT INTO conversation_summaries VALUES ('76866d03-d7c1-4324-b625-27ef0c74f6b5', '', '\(runID)');
        INSERT INTO conversation_summaries VALUES ('\(otherID)', 'Mine', '');
        """
        #expect(sqlite3_exec(database, sql, nil, nil, nil) == SQLITE_OK)
        sqlite3_close(database)

        let index = SQLiteAntigravityConversationIndex(path: path)
        #expect(try index.childConversations(of: runID) == ["76866d03-d7c1-4324-b625-27ef0c74f6b5"])
        try index.removeSummary(id: runID)
        try index.removeSummary(id: "a154b590-b426-43cc-9bbf-64f982342f57")
        #expect(sqlite3_open(path, &database) == SQLITE_OK)
        var statement: OpaquePointer?
        sqlite3_prepare_v2(database, "SELECT conversation_id FROM conversation_summaries ORDER BY conversation_id", -1, &statement, nil)
        var ids: [String] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            if let text = sqlite3_column_text(statement, 0) { ids.append(String(cString: text)) }
        }
        sqlite3_finalize(statement)
        sqlite3_close(database)
        #expect(ids == [otherID, "76866d03-d7c1-4324-b625-27ef0c74f6b5"])

        let missing = SQLiteAntigravityConversationIndex(path: folder.appendingPathComponent("none.db").path)
        #expect(try missing.childConversations(of: runID).isEmpty)
        try missing.removeSummary(id: runID)
        #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent("none.db").path))
    }
}
