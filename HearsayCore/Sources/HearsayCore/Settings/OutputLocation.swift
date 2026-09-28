import Foundation

/// A resolved output folder. When `isSecurityScoped` is true the caller owns
/// one `startAccessingSecurityScopedResource` balance and must call
/// `stopAccessing()` when done writing.
public struct ResolvedOutputFolder: Sendable, Equatable {
    public let url: URL
    public let isSecurityScoped: Bool
    /// A refreshed bookmark when the stored one was stale; the caller should
    /// persist it in place of the old one.
    public let refreshedBookmark: Data?

    public func stopAccessing() {
        if isSecurityScoped {
            url.stopAccessingSecurityScopedResource()
        }
    }
}

/// Resolves where Hearsay writes its outputs (PLAN.md section 8).
public enum OutputLocation {
    /// `Documents/Hearsay`. Inside the App Sandbox this is the container's
    /// Documents folder, the only Documents folder writable without a
    /// user-selected bookmark.
    public static func defaultFolder(fileManager: FileManager = .default) -> URL {
        let documents = fileManager.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Documents", isDirectory: true)
        return documents.appendingPathComponent("Hearsay", isDirectory: true)
    }

    /// Creates a security-scoped bookmark for a folder the user picked.
    public static func makeBookmark(for url: URL) throws -> Data {
        try url.bookmarkData(
            options: [.withSecurityScope],
            includingResourceValuesForKeys: nil,
            relativeTo: nil
        )
    }

    /// Resolves `bookmark` and starts accessing it. Falls back to
    /// `fallback` (created if missing) when there is no bookmark or it can
    /// no longer be resolved or accessed.
    public static func resolve(
        bookmark: Data?,
        fallback: URL = defaultFolder(),
        fileManager: FileManager = .default
    ) throws -> ResolvedOutputFolder {
        if let bookmark, let resolved = resolveBookmark(bookmark) {
            return resolved
        }
        try fileManager.createDirectory(at: fallback, withIntermediateDirectories: true)
        return ResolvedOutputFolder(url: fallback, isSecurityScoped: false, refreshedBookmark: nil)
    }

    private static func resolveBookmark(_ bookmark: Data) -> ResolvedOutputFolder? {
        var isStale = false
        guard let url = try? URL(
            resolvingBookmarkData: bookmark,
            options: [.withSecurityScope],
            relativeTo: nil,
            bookmarkDataIsStale: &isStale
        ) else {
            return nil
        }
        // Outside the sandbox this returns false yet the URL is usable, so
        // only fail when the folder is actually unreachable.
        let accessing = url.startAccessingSecurityScopedResource()
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            if accessing { url.stopAccessingSecurityScopedResource() }
            return nil
        }
        let refreshed = isStale ? try? makeBookmark(for: url) : nil
        return ResolvedOutputFolder(url: url, isSecurityScoped: accessing, refreshedBookmark: refreshed)
    }
}
