import Foundation

/// The latest published release of Hearsay on GitHub (PLAN.md 4.6).
public struct ReleaseInfo: Sendable, Equatable {
    /// The tag without a leading "v", for example "0.2.0".
    public var version: String
    /// The tag as published, for example "v0.2.0".
    public var tagName: String
    /// The release page, where the DMG is downloaded.
    public var htmlURL: URL
    public var publishedAt: Date?
    /// The release notes (Markdown), if any.
    public var notes: String?

    public init(version: String, tagName: String, htmlURL: URL, publishedAt: Date?, notes: String?) {
        self.version = version
        self.tagName = tagName
        self.htmlURL = htmlURL
        self.publishedAt = publishedAt
        self.notes = notes
    }
}

/// Why an update check produced no release.
public enum UpdateCheckError: Error, Equatable, LocalizedError {
    /// No network, or GitHub could not be reached or timed out.
    case offline
    /// A status other than 200 (and other than 404).
    case httpStatus(Int)
    /// 404: the repository has no published release yet.
    case noRelease
    /// The response was not the release JSON GitHub documents.
    case parse

    public var errorDescription: String? {
        switch self {
        case .offline:
            String(localized: "Could not reach GitHub. Check your internet connection and try again.",
                   bundle: .module, comment: "Update check error")
        case .httpStatus(let status):
            String(localized: "GitHub answered the update check with HTTP status \(status).",
                   bundle: .module, comment: "Update check error. %lld is an HTTP status code.")
        case .noRelease:
            String(localized: "No releases have been published yet.",
                   bundle: .module, comment: "Update check result: the GitHub repository has no release")
        case .parse:
            String(localized: "GitHub sent a release description Hearsay could not read.",
                   bundle: .module, comment: "Update check error")
        }
    }
}

/// Asks GitHub for the latest release of a repository
/// (`GET /repos/<slug>/releases/latest`, no authentication). Drafts and
/// pre-releases are never "latest" on GitHub, so they are never offered.
public actor UpdateChecker {
    /// Seconds before the request gives up.
    public static let timeout: TimeInterval = 15
    /// How long an automatic check waits after the previous successful one.
    public static let automaticInterval: TimeInterval = 24 * 60 * 60

    public nonisolated let repository: String
    private let session: URLSession

    /// `repository` is the "owner/name" slug (Info.plist
    /// `HearsayUpdateRepository`).
    public init(repository: String, session: URLSession = .shared) {
        self.repository = repository
        self.session = session
    }

    /// The API endpoint for `repository`, or nil when the slug cannot form a URL.
    public nonisolated var requestURL: URL? {
        URL(string: "https://api.github.com/repos/\(repository)/releases/latest")
    }

    public nonisolated func makeRequest() throws -> URLRequest {
        guard let url = requestURL else { throw UpdateCheckError.parse }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: Self.timeout)
        request.httpMethod = "GET"
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        return request
    }

    /// The latest release. Throws `UpdateCheckError`.
    public func latestRelease() async throws -> ReleaseInfo {
        let request = try makeRequest()
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch is URLError {
            throw UpdateCheckError.offline
        }
        guard let http = response as? HTTPURLResponse else { throw UpdateCheckError.parse }
        switch http.statusCode {
        case 200: return try Self.parse(data)
        case 404: throw UpdateCheckError.noRelease
        default: throw UpdateCheckError.httpStatus(http.statusCode)
        }
    }

    /// Parses the body of `releases/latest`.
    public static func parse(_ data: Data) throws -> ReleaseInfo {
        struct Payload: Decodable {
            var tag_name: String
            var html_url: String
            var published_at: String?
            var body: String?
        }
        guard let payload = try? JSONDecoder().decode(Payload.self, from: data),
              !payload.tag_name.isEmpty,
              let url = URL(string: payload.html_url), url.scheme == "https" else {
            throw UpdateCheckError.parse
        }
        let published = payload.published_at.flatMap { try? Date($0, strategy: .iso8601) }
        let notes = payload.body.flatMap { $0.isEmpty ? nil : $0 }
        return ReleaseInfo(
            version: stripV(payload.tag_name), tagName: payload.tag_name, htmlURL: url,
            publishedAt: published, notes: notes
        )
    }

    /// Whether an automatic check is due: the setting is on and the last
    /// successful check is missing, in the future (clock change), or at
    /// least `automaticInterval` old.
    public static func isAutomaticCheckDue(enabled: Bool, lastCheck: Date?, now: Date = Date()) -> Bool {
        guard enabled else { return false }
        guard let lastCheck else { return true }
        let elapsed = now.timeIntervalSince(lastCheck)
        return elapsed < 0 || elapsed >= automaticInterval
    }

    // MARK: - Version comparison

    /// Whether `remote` is a later version than `current`. Semver-ish: a
    /// leading "v" is ignored, numeric components are compared as numbers
    /// (missing ones count as 0, so 1.2 == 1.2.0), build metadata after "+"
    /// is ignored, and a pre-release suffix ("-beta.1") is older than the
    /// same numeric version without one. Two pre-releases compare by their
    /// dot-separated identifiers (numbers numerically, others as text).
    public static func isNewer(_ remote: String, than current: String) -> Bool {
        compare(Version(remote), Version(current)) == .orderedDescending
    }

    private struct Version {
        var numbers: [Int]
        var preRelease: [String]

        init(_ text: String) {
            var core = Substring(UpdateChecker.stripV(text.trimmingCharacters(in: .whitespaces)))
            if let plus = core.firstIndex(of: "+") { core = core[..<plus] }
            var pre = ""
            if let dash = core.firstIndex(of: "-") {
                pre = String(core[core.index(after: dash)...])
                core = core[..<dash]
            }
            numbers = core.split(separator: ".", omittingEmptySubsequences: false).map { part in
                Int(part.prefix { $0.isNumber }) ?? 0
            }
            preRelease = pre.isEmpty ? [] : pre.split(separator: ".").map(String.init)
        }
    }

    private static func compare(_ lhs: Version, _ rhs: Version) -> ComparisonResult {
        let count = max(lhs.numbers.count, rhs.numbers.count)
        for index in 0..<count {
            let left = index < lhs.numbers.count ? lhs.numbers[index] : 0
            let right = index < rhs.numbers.count ? rhs.numbers[index] : 0
            if left != right { return left < right ? .orderedAscending : .orderedDescending }
        }
        switch (lhs.preRelease.isEmpty, rhs.preRelease.isEmpty) {
        case (true, true): return .orderedSame
        case (true, false): return .orderedDescending
        case (false, true): return .orderedAscending
        case (false, false): break
        }
        for (left, right) in zip(lhs.preRelease, rhs.preRelease) where left != right {
            switch (Int(left), Int(right)) {
            case let (l?, r?): return l < r ? .orderedAscending : .orderedDescending
            case (.some, nil): return .orderedAscending
            case (nil, .some): return .orderedDescending
            case (nil, nil): return left < right ? .orderedAscending : .orderedDescending
            }
        }
        if lhs.preRelease.count == rhs.preRelease.count { return .orderedSame }
        return lhs.preRelease.count < rhs.preRelease.count ? .orderedAscending : .orderedDescending
    }

    private static func stripV(_ text: String) -> String {
        if let first = text.first, first == "v" || first == "V" { return String(text.dropFirst()) }
        return text
    }
}
