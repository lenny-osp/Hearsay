using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hearsay.Core.Settings;
using Hearsay.Core.Updates;
using Hearsay.Tests.Settings;

namespace Hearsay.Tests.Updates;

/// <summary>
/// The GitHub update check (PLAN.md 4.6). Port of <c>UpdateCheckerTests</c>
/// in mac/HearsayCore/Tests/HearsayCoreTests/UpdateCheckerTests.swift, one
/// test per Swift test (a parameterized Swift test is a theory). Responses
/// come from <see cref="FakeGitHub"/>; every test uses its own repository
/// slug. The Windows-only tests (the zip asset rule is in
/// <see cref="UpdateInstallTests"/>, <see cref="UpdateChecker.CheckAsync"/>,
/// the repository slug) are at the end.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private readonly FakeGitHub hub = new();
    private readonly ScratchSettings scratch = new();

    public void Dispose()
    {
        hub.Dispose();
        scratch.Dispose();
    }

    private static string UniqueRepository() => $"tests/{Guid.NewGuid():N}";

    private UpdateChecker Checker(string repository, TimeSpan? timeout = null) => new(repository, hub, timeout);

    private static string Url(string repository) => $"https://api.github.com/repos/{repository}/releases/latest";

    private const string ReleaseJson = """
        {"tag_name": "v0.2.0", "name": "Hearsay 0.2.0",
         "html_url": "https://github.com/lenny-osp/Hearsay/releases/tag/v0.2.0",
         "published_at": "2026-09-28T10:30:00Z", "body": "First public build.",
         "draft": false, "prerelease": false, "assets": []}
        """;

    private static ReleaseInfo Parse(string json) => UpdateChecker.Parse(Encoding.UTF8.GetBytes(json));

    // Version comparison

    [Theory]
    [InlineData("0.2.0", "0.1.0")]
    [InlineData("v0.2.0", "0.1.0")]
    [InlineData("0.1.1", "0.1.0")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("V1.2", "1.1.9")]
    [InlineData("1.2.1", "1.2")]
    [InlineData("1.0.0", "1.0.0-beta.1")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1")]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.9")]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.3")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta")]
    [InlineData("1.0.1-beta.1", "1.0.0")]
    public void Newer(string remote, string current)
    {
        Assert.True(UpdateChecker.IsNewer(remote, current));
        Assert.False(UpdateChecker.IsNewer(current, remote));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1", "0.1.0")]
    [InlineData("1.0.0+42", "1.0.0")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.1")]
    public void Equal(string remote, string current)
    {
        Assert.False(UpdateChecker.IsNewer(remote, current));
        Assert.False(UpdateChecker.IsNewer(current, remote));
    }

    [Fact]
    public void PreReleaseIsOlderThanTheSameRelease()
    {
        Assert.False(UpdateChecker.IsNewer("v0.2.0-beta.1", "0.2.0"));
        Assert.True(UpdateChecker.IsNewer("0.2.0", "0.2.0-beta.1"));
    }

    // Automatic schedule

    [Fact]
    public void AutomaticCheckIsDueOncePerDay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        Assert.True(UpdateChecker.IsAutomaticCheckDue(true, null, now));
        Assert.False(UpdateChecker.IsAutomaticCheckDue(false, null, now));
        Assert.False(UpdateChecker.IsAutomaticCheckDue(true, now.AddSeconds(-3600), now));
        Assert.True(UpdateChecker.IsAutomaticCheckDue(true, now.AddSeconds(-86_400), now));
        // A last check in the future (the clock was set back) does not block checks.
        Assert.True(UpdateChecker.IsAutomaticCheckDue(true, now.AddSeconds(3600), now));
    }

    // Request and responses

    [Fact]
    public async Task RequestUrlAndHeaders()
    {
        var repository = UniqueRepository();
        hub.Register(Url(repository), HttpStatusCode.OK, ReleaseJson);
        using var checker = Checker(repository);
        _ = await checker.LatestReleaseAsync();
        var recorded = hub.Requests(Url(repository));
        var request = Assert.Single(recorded);
        Assert.Equal(Url(repository), request.RequestUri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(TimeSpan.FromSeconds(15), checker.Timeout);
        // Windows only: GitHub's API refuses requests without a User-Agent.
        Assert.NotEmpty(request.Headers.UserAgent);
    }

    [Fact]
    public async Task ParsesTheLatestRelease()
    {
        var repository = UniqueRepository();
        hub.Register(Url(repository), HttpStatusCode.OK, ReleaseJson);
        using var checker = Checker(repository);
        var release = await checker.LatestReleaseAsync();
        Assert.Equal("0.2.0", release.Version);
        Assert.Equal("v0.2.0", release.TagName);
        Assert.Equal("https://github.com/lenny-osp/Hearsay/releases/tag/v0.2.0", release.HtmlUri.AbsoluteUri);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_591_400), release.PublishedAt);
        Assert.Equal("First public build.", release.Notes);
    }

    [Fact]
    public void MissingOptionalFieldsAreNil()
    {
        var release = Parse("""{"tag_name": "0.3.0", "html_url": "https://github.com/o/r/releases/tag/0.3.0", "published_at": null, "body": ""}""");
        Assert.Equal("0.3.0", release.Version);
        Assert.Null(release.PublishedAt);
        Assert.Null(release.Notes);
    }

    [Fact]
    public void ParsesAssets()
    {
        var json = """
            {"tag_name": "v0.3.0", "html_url": "https://github.com/o/r/releases/tag/v0.3.0",
             "assets": [
               {"name": "Hearsay-0.3.0-win-x64.zip", "size": 48123456,
                "browser_download_url": "https://github.com/o/r/releases/download/v0.3.0/Hearsay-0.3.0-win-x64.zip"},
               {"name": "SHA256SUMS.txt", "size": 84,
                "browser_download_url": "https://github.com/o/r/releases/download/v0.3.0/SHA256SUMS.txt"},
               {"name": "broken-win-x64.zip", "browser_download_url": "http://example.com/broken-win-x64.zip"},
               {"size": 1}
             ]}
            """;
        var release = Parse(json);
        Assert.Equal(
            [
                new ReleaseAsset("Hearsay-0.3.0-win-x64.zip", new Uri("https://github.com/o/r/releases/download/v0.3.0/Hearsay-0.3.0-win-x64.zip"), 48_123_456),
                new ReleaseAsset("SHA256SUMS.txt", new Uri("https://github.com/o/r/releases/download/v0.3.0/SHA256SUMS.txt"), 84),
            ],
            release.Assets);
        Assert.Equal(48_123_456, release.ZipAsset(release.Version)?.Size);
        Assert.Equal("SHA256SUMS.txt", release.ChecksumsAsset?.Name);
    }

    [Fact]
    public void MissingAssetsAreEmpty()
    {
        Assert.Empty(Parse("""{"tag_name": "v0.3.0", "html_url": "https://github.com/o/r/releases/tag/v0.3.0"}""").Assets);
    }

    [Fact]
    public async Task NotFoundMeansNoReleasesYet()
    {
        var repository = UniqueRepository();
        hub.Register(Url(repository), HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        using var checker = Checker(repository);
        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => checker.LatestReleaseAsync());
        Assert.Equal(new UpdateCheckError.NoRelease(), error.Error);
        Assert.Equal("No releases have been published yet.", new UpdateCheckError.NoRelease().Description);
    }

    [Fact]
    public async Task OtherStatusIsReported()
    {
        var repository = UniqueRepository();
        hub.Register(Url(repository), HttpStatusCode.Forbidden, """{"message": "rate limit"}""");
        using var checker = Checker(repository);
        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => checker.LatestReleaseAsync());
        Assert.Equal(new UpdateCheckError.HttpStatus(403), error.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tag_name": "v1.0.0"}""")]
    [InlineData("""{"tag_name": "", "html_url": "https://github.com/o/r"}""")]
    [InlineData("""{"tag_name": "v1", "html_url": "http://github.com/o/r"}""")]
    [InlineData("[]")]
    public async Task MalformedJsonIsAParseError(string body)
    {
        var repository = UniqueRepository();
        hub.Register(Url(repository), HttpStatusCode.OK, body);
        using var checker = Checker(repository);
        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => checker.LatestReleaseAsync());
        Assert.Equal(new UpdateCheckError.Parse(), error.Error);
    }

    /// <summary>The Swift test's URLError codes: not connected, timed out, cannot find host.</summary>
    [Theory]
    [InlineData("notConnectedToInternet")]
    [InlineData("timedOut")]
    [InlineData("cannotFindHost")]
    public async Task NetworkFailureIsOffline(string code)
    {
        var repository = UniqueRepository();
        switch (code)
        {
            case "timedOut":
                hub.RegisterStall(Url(repository));
                break;
            case "cannotFindHost":
                hub.RegisterFailure(Url(repository), new HttpRequestException("No such host is known."));
                break;
            default:
                hub.RegisterFailure(Url(repository), new HttpRequestException(
                    "A socket operation was attempted to an unreachable network.", new System.Net.Sockets.SocketException(10051)));
                break;
        }
        using var checker = Checker(repository, TimeSpan.FromMilliseconds(200));
        var error = await Assert.ThrowsAsync<UpdateCheckException>(() => checker.LatestReleaseAsync());
        Assert.Equal(new UpdateCheckError.Offline(), error.Error);
    }

    // Windows only

    [Fact]
    public async Task CancellationIsNotOffline()
    {
        var repository = UniqueRepository();
        hub.RegisterStall(Url(repository));
        using var checker = Checker(repository);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.LatestReleaseAsync(cancel.Token));
    }

    [Fact]
    public void WrongTypesFailLikeJsonDecoder()
    {
        foreach (var body in new[]
        {
            """{"tag_name": 1, "html_url": "https://github.com/o/r"}""",
            """{"tag_name": "v1", "html_url": "https://github.com/o/r", "body": 5}""",
            """{"tag_name": "v1", "html_url": "https://github.com/o/r", "assets": {}}""",
            """{"tag_name": "v1", "html_url": "https://github.com/o/r", "assets": [{"name": "a", "size": "big"}]}""",
        })
        {
            var error = Assert.Throws<UpdateCheckException>(() => Parse(body));
            Assert.Equal(new UpdateCheckError.Parse(), error.Error);
        }
        // A date Swift's ISO 8601 strategy cannot read is nil, not an error.
        Assert.Null(Parse("""{"tag_name": "v1", "html_url": "https://github.com/o/r", "published_at": "yesterday"}""").PublishedAt);
    }

    [Fact]
    public void ErrorTextsAreTheMacKeys()
    {
        Assert.Equal("Could not reach GitHub. Check your internet connection and try again.", new UpdateCheckError.Offline().Description);
        Assert.Equal("GitHub answered the update check with HTTP status 403.", new UpdateCheckError.HttpStatus(403).Description);
        Assert.Equal("GitHub sent a release description Hearsay could not read.", new UpdateCheckError.Parse().Description);
    }

    [Fact]
    public async Task CheckFindsANewerVersionAndStoresTheTime()
    {
        var repository = UniqueRepository();
        hub.Register(Url(repository), HttpStatusCode.OK, ReleaseJson);
        var settings = new AppSettings(scratch.Make());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_600_000);
        using var checker = Checker(repository);

        var outcome = await checker.CheckAsync("0.1.0", settings, new FixedClock(now));
        var available = Assert.IsType<UpdateCheckOutcome.Available>(outcome);
        Assert.Equal("0.2.0", available.Release.Version);
        Assert.Equal(now, settings.LastUpdateCheck);

        Assert.IsType<UpdateCheckOutcome.UpToDate>(await checker.CheckAsync("0.2.0", settings, new FixedClock(now)));
        Assert.IsType<UpdateCheckOutcome.UpToDate>(await checker.CheckAsync("0.3.0-beta.1", settings, new FixedClock(now)));
    }

    [Fact]
    public async Task NoReleaseCountsAsACheckButOtherErrorsDoNot()
    {
        var settings = new AppSettings(scratch.Make());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_600_000);

        var missing = UniqueRepository();
        hub.Register(Url(missing), HttpStatusCode.NotFound, "{}");
        using (var checker = Checker(missing))
        {
            var failed = Assert.IsType<UpdateCheckOutcome.Failed>(await checker.CheckAsync("0.1.0", settings, new FixedClock(now)));
            Assert.Equal("No releases have been published yet.", failed.Message);
        }
        Assert.Equal(now, settings.LastUpdateCheck);

        var broken = UniqueRepository();
        hub.Register(Url(broken), HttpStatusCode.InternalServerError, "{}");
        using (var checker = Checker(broken))
        {
            var failed = Assert.IsType<UpdateCheckOutcome.Failed>(await checker.CheckAsync("0.1.0", settings, new FixedClock(now.AddDays(2))));
            Assert.Equal("GitHub answered the update check with HTTP status 500.", failed.Message);
        }
        Assert.Equal(now, settings.LastUpdateCheck);
    }

    [Fact]
    public void RepositoryMatchesTheMacProject()
    {
        var project = File.ReadAllText(Path.Combine(SharedFiles.Directory, "..", "mac", "project.yml"));
        var match = Regex.Match(project, @"HEARSAY_UPDATE_REPOSITORY:\s*(\S+)");
        Assert.True(match.Success);
        Assert.Equal(UpdateChecker.HearsayRepository, match.Groups[1].Value);
    }

    [Fact]
    public void BuildMetadataIsDropped()
    {
        Assert.Equal("0.3.0", UpdateChecker.WithoutBuildMetadata("0.3.0+0123abc"));
        Assert.Equal("0.3.0-beta.1", UpdateChecker.WithoutBuildMetadata("0.3.0-beta.1"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
