import Foundation
import os
import SQLite3

/// The file access `AntigravityHousekeeping` needs; injected in tests.
public protocol AntigravityFileSystem: Sendable {
    /// Entry names; empty when the folder is missing or unreadable.
    func contentsOfDirectory(atPath path: String) -> [String]
    func contents(atPath path: String) -> Data?
    /// Replaces the file atomically, creating its folder when missing.
    func write(_ data: Data, toPath path: String) throws
    /// Removes a file or a whole folder.
    func removeItem(atPath path: String) throws
}

public struct LocalAntigravityFileSystem: AntigravityFileSystem {
    public init() {}

    public func contentsOfDirectory(atPath path: String) -> [String] {
        (try? FileManager.default.contentsOfDirectory(atPath: path)) ?? []
    }

    public func contents(atPath path: String) -> Data? {
        FileManager.default.contents(atPath: path)
    }

    public func write(_ data: Data, toPath path: String) throws {
        let url = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(), withIntermediateDirectories: true
        )
        try data.write(to: url, options: .atomic)
    }

    public func removeItem(atPath path: String) throws {
        try FileManager.default.removeItem(atPath: path)
    }
}

/// agy's conversation list, `conversation_summaries.db` (SQLite), keyed by
/// `conversation_id`. Injected in tests.
public protocol AntigravityConversationIndex: Sendable {
    /// Conversations whose `parent_conversation_id` is `id` (subagents).
    func childConversations(of id: String) throws -> [String]
    /// Deletes the row of `id`; no row is not an error.
    func removeSummary(id: String) throws
}

/// The real `conversation_summaries.db`. The file is never created: a
/// missing database means there is nothing to remove.
public struct SQLiteAntigravityConversationIndex: AntigravityConversationIndex {
    public var path: String

    public init(path: String) {
        self.path = path
    }

    struct SQLiteError: Error, LocalizedError {
        var message: String
        var errorDescription: String? { message }
    }

    public func childConversations(of id: String) throws -> [String] {
        try withDatabase { database in
            var children: [String] = []
            try statement(database, "SELECT conversation_id FROM conversation_summaries WHERE parent_conversation_id = ?", id) { row in
                while sqlite3_step(row) == SQLITE_ROW {
                    if let text = sqlite3_column_text(row, 0) { children.append(String(cString: text)) }
                }
            }
            return children
        } ?? []
    }

    public func removeSummary(id: String) throws {
        _ = try withDatabase { database in
            try statement(database, "DELETE FROM conversation_summaries WHERE conversation_id = ?", id) { row in
                let status = sqlite3_step(row)
                guard status == SQLITE_DONE else {
                    throw SQLiteError(message: String(cString: sqlite3_errmsg(database)))
                }
            }
        }
    }

    /// Nil when the database file does not exist.
    private func withDatabase<Value>(_ body: (OpaquePointer) throws -> Value) throws -> Value? {
        guard FileManager.default.fileExists(atPath: path) else { return nil }
        var handle: OpaquePointer?
        guard sqlite3_open_v2(path, &handle, SQLITE_OPEN_READWRITE, nil) == SQLITE_OK, let database = handle else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "cannot open \(path)"
            sqlite3_close(handle)
            throw SQLiteError(message: message)
        }
        defer { sqlite3_close(database) }
        // agy may hold the database briefly; wait rather than fail at once.
        sqlite3_busy_timeout(database, 3000)
        return try body(database)
    }

    private func statement(
        _ database: OpaquePointer, _ sql: String, _ id: String, _ body: (OpaquePointer) throws -> Void
    ) throws {
        var handle: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &handle, nil) == SQLITE_OK, let prepared = handle else {
            throw SQLiteError(message: String(cString: sqlite3_errmsg(database)))
        }
        defer { sqlite3_finalize(prepared) }
        // SQLITE_TRANSIENT: SQLite copies the string.
        sqlite3_bind_text(prepared, 1, id, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self))
        try body(prepared)
    }
}

/// What Hearsay keeps in order in agy's own data (owner decisions,
/// 2026-09-28):
///
/// - The `hearsay-notes` project under `~/.gemini/config/projects/` carries
///   deny rules for every tool that runs programs, touches files, or fetches
///   URLs. Project rules take precedence over the user's global
///   `permissions.allow` (verified with agy 1.2.12), so the model can only
///   reply. Only that one project file is written; other keys in it are kept.
/// - After each run, that run's conversation is deleted: every entry named
///   after its id under `~/.gemini/antigravity-cli/{conversations,brain,
///   annotations,presence,worktrees}`, its entries in `cache/
///   last_conversations.json` and `cache/conversation_metadata.json`, its
///   entry in `jetbox_summaries_proto.pb` (from which agy rebuilds the
///   database on its next start), its row in `conversation_summaries.db`,
///   and the same for its subagents. Files in `implicit/` are not named or
///   keyed by the conversation id and are left alone.
///   agy has no subcommand for this. Failures are logged, never thrown.
public struct AntigravityHousekeeping: Sendable {
    /// Rule kinds from agy's permission checks: `command` (run_command),
    /// `read_file` (view_file), `write_file` (write_to_file,
    /// replace_file_content), `read_url` (read_url_content), `execute_url`,
    /// and `mcp` (MCP tools). agy accepts `search_web(*)` but does not
    /// apply it, so web search stays available.
    public static let denyRules = [
        "command(*)", "read_file(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "mcp(*)",
    ]

    public var homeDirectory: String
    public var fileSystem: any AntigravityFileSystem
    public var index: any AntigravityConversationIndex

    private static let logger = Logger(subsystem: "tw.og1o.hearsay", category: "antigravity")

    public init(
        homeDirectory: String = NSHomeDirectory(),
        fileSystem: any AntigravityFileSystem = LocalAntigravityFileSystem(),
        index: (any AntigravityConversationIndex)? = nil
    ) {
        self.homeDirectory = homeDirectory
        self.fileSystem = fileSystem
        self.index = index ?? SQLiteAntigravityConversationIndex(
            path: (homeDirectory as NSString).appendingPathComponent(".gemini/antigravity-cli/conversation_summaries.db")
        )
    }

    public var projectsFolder: String {
        (homeDirectory as NSString).appendingPathComponent(".gemini/config/projects")
    }

    public var appDataFolder: String {
        (homeDirectory as NSString).appendingPathComponent(".gemini/antigravity-cli")
    }

    // MARK: - Project

    /// Makes sure a project named `CLIArguments.antigravityProjectName`
    /// exists and denies `denyRules`. Writes only when something is missing:
    /// a new `<uuid>.json` when no project has the name, else the existing
    /// file with the rules merged in. Returns the path written, or nil.
    @discardableResult
    public func ensureProject(makeID: () -> String = { UUID().uuidString.lowercased() }) throws -> String? {
        let name = CLIArguments.antigravityProjectName
        for file in fileSystem.contentsOfDirectory(atPath: projectsFolder).sorted() where file.hasSuffix(".json") {
            let path = (projectsFolder as NSString).appendingPathComponent(file)
            guard let data = fileSystem.contents(atPath: path),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  object["name"] as? String == name else { continue }
            guard let merged = Self.mergingDenyRules(into: object) else { return nil }
            try fileSystem.write(try Self.encode(merged), toPath: path)
            return path
        }
        let id = makeID()
        let project: [String: Any] = [
            "id": id,
            "name": name,
            "projectResources": [String: Any](),
            "permissionGrants": ["permissionGrants": ["deny": Self.denyRules]],
        ]
        let path = (projectsFolder as NSString).appendingPathComponent("\(id).json")
        try fileSystem.write(try Self.encode(project), toPath: path)
        return path
    }

    /// `project` with `permissionGrants.permissionGrants.deny` holding every
    /// rule in `denyRules` (existing rules kept, in order), or nil when it
    /// already does. Every other key is left as it is.
    static func mergingDenyRules(into project: [String: Any]) -> [String: Any]? {
        var outer = project["permissionGrants"] as? [String: Any] ?? [:]
        var grants = outer["permissionGrants"] as? [String: Any] ?? [:]
        let existing = grants["deny"] as? [String] ?? []
        let missing = denyRules.filter { !existing.contains($0) }
        guard !missing.isEmpty else { return nil }
        grants["deny"] = existing + missing
        outer["permissionGrants"] = grants
        var merged = project
        merged["permissionGrants"] = outer
        return merged
    }

    private static func encode(_ object: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    }

    // MARK: - Conversations

    /// agy's conversation ids are lowercase UUIDs. Anything else is refused,
    /// so a path can never be built from other text.
    public static func isConversationID(_ value: String) -> Bool {
        value.range(of: #"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z"#, options: .regularExpression) != nil
    }

    /// `conversation_id` from `agy --output-format json` output, or nil.
    public static func conversationID(inOutput stdout: String) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: Data(stdout.utf8)) as? [String: Any],
              let id = object["conversation_id"] as? String, isConversationID(id) else { return nil }
        return id
    }

    /// Folders under `appDataFolder` whose entries are named after a
    /// conversation id.
    static let conversationFolders = ["conversations", "brain", "annotations", "presence", "worktrees"]

    /// Every path to delete for `id`: entries of `conversationFolders` whose
    /// name contains the id (`<id>.db`, `<id>.db-wal`, `brain/<id>`, …).
    public func conversationPaths(id: String) -> [String] {
        guard Self.isConversationID(id) else { return [] }
        return Self.conversationFolders.flatMap { folder -> [String] in
            let path = (appDataFolder as NSString).appendingPathComponent(folder)
            return fileSystem.contentsOfDirectory(atPath: path).sorted()
                .filter { $0.contains(id) }
                .map { (path as NSString).appendingPathComponent($0) }
        }
    }

    /// Deletes the conversation `id` and its subagents' conversations.
    /// Returns what was removed (paths, and "cache/…" or "summary:<id>"
    /// labels for edited index entries). Never throws.
    @discardableResult
    public func removeConversation(id: String) -> [String] {
        var visited: Set<String> = []
        return removeConversation(id: id, visited: &visited, depth: 0)
    }

    private func removeConversation(id: String, visited: inout Set<String>, depth: Int) -> [String] {
        guard Self.isConversationID(id), depth < 4, visited.insert(id).inserted else { return [] }
        var removed: [String] = []
        let children: [String]
        do {
            children = try index.childConversations(of: id)
        } catch {
            Self.logger.error("agy cleanup: listing subagents of \(id, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            children = []
        }
        for child in children {
            removed += removeConversation(id: child, visited: &visited, depth: depth + 1)
        }
        for path in conversationPaths(id: id) {
            do {
                try fileSystem.removeItem(atPath: path)
                removed.append(path)
            } catch {
                Self.logger.error("agy cleanup: removing \(path, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        removed += editCache("cache/last_conversations.json", id: id, edit: Self.removingLastConversation)
        removed += editCache("cache/conversation_metadata.json", id: id, edit: Self.removingMetadata)
        removed += editCache("jetbox_summaries_proto.pb", id: id, edit: Self.removingSummaryEntry)
        do {
            try index.removeSummary(id: id)
            removed.append("summary:\(id)")
        } catch {
            Self.logger.error("agy cleanup: removing the summary of \(id, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
        }
        return removed
    }

    private func editCache(_ relativePath: String, id: String, edit: (Data, String) -> Data?) -> [String] {
        let path = (appDataFolder as NSString).appendingPathComponent(relativePath)
        guard let data = fileSystem.contents(atPath: path), let edited = edit(data, id) else { return [] }
        do {
            try fileSystem.write(edited, toPath: path)
            return [relativePath]
        } catch {
            Self.logger.error("agy cleanup: editing \(path, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            return []
        }
    }

    /// `last_conversations.json` maps a working folder to its last
    /// conversation id. Removes the entries whose value is `id`; nil when
    /// there are none or the file is not such a map.
    static func removingLastConversation(from data: Data, id: String) -> Data? {
        guard let map = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let kept = map.filter { ($0.value as? String) != id }
        guard kept.count != map.count else { return nil }
        return try? encode(kept)
    }

    /// `jetbox_summaries_proto.pb` is a protobuf message whose field 1 is
    /// repeated, one entry per conversation, each starting with field 1 =
    /// the conversation id. Drops the entries whose id is `id` and keeps
    /// every other byte as it is. Nil when there is none, or when the file
    /// does not have that shape (then nothing is written).
    static func removingSummaryEntry(from data: Data, id: String) -> Data? {
        let bytes = [UInt8](data)
        let target = [UInt8](id.utf8)
        var kept: [UInt8] = []
        kept.reserveCapacity(bytes.count)
        var index = 0
        var dropped = false
        while index < bytes.count {
            let start = index
            guard let key = varint(bytes, &index), key == (1 << 3 | 2),
                  let length = varint(bytes, &index), length <= UInt64(bytes.count - index) else { return nil }
            let end = index + Int(length)
            var inner = index
            if let innerKey = varint(bytes, &inner), innerKey == (1 << 3 | 2),
               let innerLength = varint(bytes, &inner), innerLength <= UInt64(end - inner),
               Array(bytes[inner..<(inner + Int(innerLength))]) == target {
                dropped = true
            } else {
                kept += bytes[start..<end]
            }
            index = end
        }
        return dropped ? Data(kept) : nil
    }

    private static func varint(_ bytes: [UInt8], _ index: inout Int) -> UInt64? {
        var value: UInt64 = 0
        var shift: UInt64 = 0
        while index < bytes.count, shift < 64 {
            let byte = bytes[index]
            index += 1
            value |= UInt64(byte & 0x7f) << shift
            if byte < 0x80 { return value }
            shift += 7
        }
        return nil
    }

    /// `conversation_metadata.json` is `{"conversations": {<id>: …}}`.
    /// Removes the `id` key; nil when it is absent.
    static func removingMetadata(from data: Data, id: String) -> Data? {
        guard var object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              var conversations = object["conversations"] as? [String: Any],
              conversations[id] != nil else { return nil }
        conversations[id] = nil
        object["conversations"] = conversations
        return try? encode(object)
    }
}
