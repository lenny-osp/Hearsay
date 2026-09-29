import Foundation
import Testing
@testable import HearsayCore

/// The GitHub update check (PLAN.md 4.6). Network responses come from
/// `ChatStubURLProtocol` (NotesPipelineTests.swift), keyed by URL: every
/// test uses its own repository slug, so parallel tests never share one.
struct UpdateCheckerTests {
    private func uniqueRepository() -> String {
        "tests/\(UUID().uuidString.lowercased())"
    }

    private func checker(_ repository: String) -> UpdateChecker {
        UpdateChecker(repository: repository, session: URLSession(configuration: ChatStubURLProtocol.session()))
    }

    private func url(_ repository: String) -> String {
        "https://api.github.com/repos/\(repository)/releases/latest"
    }

    private let releaseJSON = """
        {"tag_name": "v0.2.0", "name": "Hearsay 0.2.0",
         "html_url": "https://github.com/lenny-osp/Hearsay/releases/tag/v0.2.0",
         "published_at": "2026-09-28T10:30:00Z", "body": "First public build.",
         "draft": false, "prerelease": false, "assets": []}
        """

    // MARK: - Version comparison

    @Test(arguments: [
        ("0.2.0", "0.1.0"), ("v0.2.0", "0.1.0"), ("0.1.1", "0.1.0"), ("1.0.0", "0.9.9"),
        ("0.10.0", "0.9.0"), ("V1.2", "1.1.9"), ("1.2.1", "1.2"), ("1.0.0", "1.0.0-beta.1"),
        ("1.0.0-beta.2", "1.0.0-beta.1"), ("1.0.0-beta.10", "1.0.0-beta.9"),
        ("1.0.0-rc.1", "1.0.0-beta.3"), ("1.0.0-beta.1", "1.0.0-beta"), ("1.0.1-beta.1", "1.0.0"),
    ])
    func newer(remote: String, current: String) {
        #expect(UpdateChecker.isNewer(remote, than: current))
        #expect(!UpdateChecker.isNewer(current, than: remote))
    }

    @Test(arguments: [
        ("0.1.0", "0.1.0"), ("v0.1.0", "0.1.0"), ("0.1", "0.1.0"), ("1.0.0+42", "1.0.0"),
        ("1.0.0-beta.1", "1.0.0-beta.1"),
    ])
    func equal(remote: String, current: String) {
        #expect(!UpdateChecker.isNewer(remote, than: current))
        #expect(!UpdateChecker.isNewer(current, than: remote))
    }

    @Test func preReleaseIsOlderThanTheSameRelease() {
        #expect(!UpdateChecker.isNewer("v0.2.0-beta.1", than: "0.2.0"))
        #expect(UpdateChecker.isNewer("0.2.0", than: "0.2.0-beta.1"))
    }

    // MARK: - Automatic schedule

    @Test func automaticCheckIsDueOncePerDay() {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        #expect(UpdateChecker.isAutomaticCheckDue(enabled: true, lastCheck: nil, now: now))
        #expect(!UpdateChecker.isAutomaticCheckDue(enabled: false, lastCheck: nil, now: now))
        #expect(!UpdateChecker.isAutomaticCheckDue(enabled: true, lastCheck: now.addingTimeInterval(-3600), now: now))
        #expect(UpdateChecker.isAutomaticCheckDue(enabled: true, lastCheck: now.addingTimeInterval(-86_400), now: now))
        // A last check in the future (the clock was set back) does not block checks.
        #expect(UpdateChecker.isAutomaticCheckDue(enabled: true, lastCheck: now.addingTimeInterval(3600), now: now))
    }

    // MARK: - Request and responses

    @Test func requestURLAndHeaders() async throws {
        let repository = uniqueRepository()
        ChatStubURLProtocol.register(url(repository), status: 200, body: Data(releaseJSON.utf8))
        _ = try await checker(repository).latestRelease()
        let recorded = ChatStubURLProtocol.requests(for: url(repository))
        let request = try #require(recorded.first?.0)
        #expect(recorded.count == 1)
        #expect(request.url?.absoluteString == url(repository))
        #expect(request.httpMethod == "GET")
        #expect(request.value(forHTTPHeaderField: "Accept") == "application/vnd.github+json")
        #expect(request.value(forHTTPHeaderField: "Authorization") == nil)
        #expect(request.timeoutInterval == 15)
    }

    @Test func parsesTheLatestRelease() async throws {
        let repository = uniqueRepository()
        ChatStubURLProtocol.register(url(repository), status: 200, body: Data(releaseJSON.utf8))
        let release = try await checker(repository).latestRelease()
        #expect(release.version == "0.2.0")
        #expect(release.tagName == "v0.2.0")
        #expect(release.htmlURL.absoluteString == "https://github.com/lenny-osp/Hearsay/releases/tag/v0.2.0")
        #expect(release.publishedAt == Date(timeIntervalSince1970: 1_790_591_400))
        #expect(release.notes == "First public build.")
    }

    @Test func missingOptionalFieldsAreNil() throws {
        let json = #"{"tag_name": "0.3.0", "html_url": "https://github.com/o/r/releases/tag/0.3.0", "published_at": null, "body": ""}"#
        let release = try UpdateChecker.parse(Data(json.utf8))
        #expect(release.version == "0.3.0")
        #expect(release.publishedAt == nil)
        #expect(release.notes == nil)
    }

    @Test func parsesAssets() throws {
        let json = """
            {"tag_name": "v0.3.0", "html_url": "https://github.com/o/r/releases/tag/v0.3.0",
             "assets": [
               {"name": "Hearsay-0.3.0.dmg", "size": 48123456,
                "browser_download_url": "https://github.com/o/r/releases/download/v0.3.0/Hearsay-0.3.0.dmg"},
               {"name": "SHA256SUMS.txt", "size": 84,
                "browser_download_url": "https://github.com/o/r/releases/download/v0.3.0/SHA256SUMS.txt"},
               {"name": "broken.dmg", "browser_download_url": "http://example.com/broken.dmg"},
               {"size": 1}
             ]}
            """
        let release = try UpdateChecker.parse(Data(json.utf8))
        #expect(release.assets == [
            ReleaseAsset(
                name: "Hearsay-0.3.0.dmg",
                downloadURL: try #require(URL(string: "https://github.com/o/r/releases/download/v0.3.0/Hearsay-0.3.0.dmg")),
                size: 48_123_456
            ),
            ReleaseAsset(
                name: "SHA256SUMS.txt",
                downloadURL: try #require(URL(string: "https://github.com/o/r/releases/download/v0.3.0/SHA256SUMS.txt")),
                size: 84
            ),
        ])
        #expect(release.dmgAsset(forVersion: release.version)?.size == 48_123_456)
        #expect(release.checksumsAsset?.name == "SHA256SUMS.txt")
    }

    @Test func missingAssetsAreEmpty() throws {
        let json = #"{"tag_name": "v0.3.0", "html_url": "https://github.com/o/r/releases/tag/v0.3.0"}"#
        #expect(try UpdateChecker.parse(Data(json.utf8)).assets.isEmpty)
    }

    @Test func notFoundMeansNoReleasesYet() async {
        let repository = uniqueRepository()
        ChatStubURLProtocol.register(url(repository), status: 404, body: Data(#"{"message": "Not Found"}"#.utf8))
        await #expect(throws: UpdateCheckError.noRelease) {
            try await checker(repository).latestRelease()
        }
        #expect(UpdateCheckError.noRelease.localizedDescription == "No releases have been published yet.")
    }

    @Test func otherStatusIsReported() async {
        let repository = uniqueRepository()
        ChatStubURLProtocol.register(url(repository), status: 403, body: Data(#"{"message": "rate limit"}"#.utf8))
        await #expect(throws: UpdateCheckError.httpStatus(403)) {
            try await checker(repository).latestRelease()
        }
    }

    @Test(arguments: [
        "not json", "{}", #"{"tag_name": "v1.0.0"}"#, #"{"tag_name": "", "html_url": "https://github.com/o/r"}"#,
        #"{"tag_name": "v1", "html_url": "http://github.com/o/r"}"#, "[]",
    ])
    func malformedJSONIsAParseError(body: String) async {
        let repository = uniqueRepository()
        ChatStubURLProtocol.register(url(repository), status: 200, body: Data(body.utf8))
        await #expect(throws: UpdateCheckError.parse) {
            try await checker(repository).latestRelease()
        }
    }

    @Test(arguments: [URLError.Code.notConnectedToInternet, .timedOut, .cannotFindHost])
    func networkFailureIsOffline(code: URLError.Code) async {
        let repository = uniqueRepository()
        ChatStubURLProtocol.registerFailure(url(repository), code: code)
        await #expect(throws: UpdateCheckError.offline) {
            try await checker(repository).latestRelease()
        }
    }
}
