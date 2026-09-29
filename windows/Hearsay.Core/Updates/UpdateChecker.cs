using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hearsay.Core.Updates;

/// <summary>
/// One file attached to a GitHub release. Port of <c>ReleaseAsset</c> in
/// mac/HearsayCore/Sources/HearsayCore/Updates/UpdateChecker.swift.
/// </summary>
/// <param name="Name">The file name, for example <c>Hearsay-0.3.0-win-x64.zip</c>.</param>
/// <param name="DownloadUri"><c>browser_download_url</c>.</param>
/// <param name="Size">Bytes, as GitHub reports them.</param>
public sealed record ReleaseAsset(string Name, Uri DownloadUri, long Size);

/// <summary>
/// The latest published release of Hearsay on GitHub (PLAN.md 4.6). Port of
/// <c>ReleaseInfo</c> in UpdateChecker.swift. The Mac picks the DMG; Windows
/// picks the zip named by <see cref="ZipAssetName"/> (PLAN.md 18.4,
/// "Updates and packaging").
/// </summary>
public sealed record ReleaseInfo
{
    /// <summary>The checksum list the release workflow publishes.</summary>
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    /// <summary>What every Windows release zip name ends with.</summary>
    public const string ZipSuffix = "-win-x64.zip";

    /// <summary>The tag without a leading "v", for example "0.2.0".</summary>
    public required string Version { get; init; }

    /// <summary>The tag as published, for example "v0.2.0".</summary>
    public required string TagName { get; init; }

    /// <summary>The release page.</summary>
    public required Uri HtmlUri { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The release notes (Markdown), if any.</summary>
    public string? Notes { get; init; }

    /// <summary>The files attached to the release (the zip, the DMG and <c>SHA256SUMS.txt</c>).</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary><c>Hearsay-&lt;version&gt;-win-x64.zip</c>.</summary>
    public static string ZipAssetName(string version) => $"Hearsay-{version}{ZipSuffix}";

    /// <summary>
    /// The Windows zip for <paramref name="version"/>: the asset named exactly
    /// <see cref="ZipAssetName"/>, else the only asset whose name ends in
    /// <c>-win-x64.zip</c> (ignoring case), else null. The Mac's
    /// <c>dmgAsset(forVersion:)</c> with the Windows name; a plain ".zip"
    /// fallback would be ambiguous, so the suffix is required.
    /// </summary>
    public ReleaseAsset? ZipAsset(string version)
    {
        var exact = Assets.FirstOrDefault(a => a.Name == ZipAssetName(version));
        if (exact is not null)
        {
            return exact;
        }
        var zips = Assets.Where(a => a.Name.EndsWith(ZipSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
        return zips.Count == 1 ? zips[0] : null;
    }

    /// <summary><c>SHA256SUMS.txt</c>, if the release has it (exact name).</summary>
    public ReleaseAsset? ChecksumsAsset => Assets.FirstOrDefault(a => a.Name == ChecksumsFileName);

    public bool Equals(ReleaseInfo? other) =>
        other is not null && Version == other.Version && TagName == other.TagName && HtmlUri == other.HtmlUri
        && PublishedAt == other.PublishedAt && Notes == other.Notes && Assets.SequenceEqual(other.Assets);

    public override int GetHashCode() => HashCode.Combine(Version, TagName, HtmlUri, Assets.Count);
}

/// <summary>
/// Why an update check produced no release. Port of <c>UpdateCheckError</c>
/// in UpdateChecker.swift; the English text is the Swift string catalog key.
/// </summary>
public abstract record UpdateCheckError
{
    private UpdateCheckError()
    {
    }

    /// <summary>No network, or GitHub could not be reached or timed out.</summary>
    public sealed record Offline : UpdateCheckError;

    /// <summary>A status other than 200 (and other than 404).</summary>
    public sealed record HttpStatus(int Status) : UpdateCheckError;

    /// <summary>404: the repository has no published release yet.</summary>
    public sealed record NoRelease : UpdateCheckError;

    /// <summary>The response was not the release JSON GitHub documents.</summary>
    public sealed record Parse : UpdateCheckError;

    /// <summary>The message shown to the user (Swift <c>errorDescription</c>).</summary>
    public string Description => this switch
    {
        Offline => "Could not reach GitHub. Check your internet connection and try again.",
        HttpStatus e => string.Format(CultureInfo.CurrentCulture, "GitHub answered the update check with HTTP status {0}.", e.Status),
        NoRelease => "No releases have been published yet.",
        Parse => "GitHub sent a release description Hearsay could not read.",
        _ => throw new InvalidOperationException("Unknown UpdateCheckError."),
    };

    /// <summary><see cref="Description"/> as its catalog key and values, for the app to translate.</summary>
    public LocalizedMessage Localized => this switch
    {
        Offline => new LocalizedMessage("Could not reach GitHub. Check your internet connection and try again."),
        HttpStatus e => new LocalizedMessage("GitHub answered the update check with HTTP status %lld.", e.Status),
        NoRelease => new LocalizedMessage("No releases have been published yet."),
        Parse => new LocalizedMessage("GitHub sent a release description Hearsay could not read."),
        _ => throw new InvalidOperationException("Unknown UpdateCheckError."),
    };
}

/// <summary>Thrown by <see cref="UpdateChecker"/>; <see cref="Error"/> says what failed.</summary>
public sealed class UpdateCheckException : Exception, ILocalizedError
{
    public UpdateCheckException(UpdateCheckError error)
        : base(error?.Description)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public UpdateCheckException(UpdateCheckError error, Exception inner)
        : base(error?.Description, inner)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public UpdateCheckError Error { get; }

    public ILocalizedMessage LocalizedMessage => Error.Localized;
}

/// <summary>What one check found (the Mac's <c>UpdateService.Outcome</c>).</summary>
public abstract record UpdateCheckOutcome
{
    private UpdateCheckOutcome()
    {
    }

    public sealed record Available(ReleaseInfo Release) : UpdateCheckOutcome;

    public sealed record UpToDate : UpdateCheckOutcome;

    /// <param name="Message">The text for the alert or the Settings line.</param>
    /// <param name="Localized"><paramref name="Message"/> as its catalog key and values, for the app to translate.</param>
    public sealed record Failed(string Message, ILocalizedMessage? Localized = null) : UpdateCheckOutcome, ILocalizedError
    {
        ILocalizedMessage? ILocalizedError.LocalizedMessage => Localized;
    }
}

/// <summary>
/// Asks GitHub for the latest release of a repository
/// (<c>GET /repos/&lt;slug&gt;/releases/latest</c>, no authentication). Drafts
/// and pre-releases are never "latest" on GitHub, so they are never offered.
/// Port of the <c>UpdateChecker</c> actor in
/// mac/HearsayCore/Sources/HearsayCore/Updates/UpdateChecker.swift on
/// <see cref="HttpClient"/>, plus <see cref="CheckAsync"/>, the part of the
/// Mac's <c>UpdateService.fetchOutcome</c> that does not show an alert.
/// Thread-safe.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>
    /// The repository Windows builds check. Must equal
    /// <c>HEARSAY_UPDATE_REPOSITORY</c> in mac/project.yml (a test compares
    /// them): both platforms publish to the same releases.
    /// </summary>
    public const string HearsayRepository = "lenny-osp/Hearsay";

    /// <summary>Before the request gives up.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an automatic check waits after the previous successful one.</summary>
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);

    /// <summary>The Mac's <c>UpdateService</c>: the first automatic check this long after launch...</summary>
    public static readonly TimeSpan FirstAutomaticCheckDelay = TimeSpan.FromSeconds(10);

    /// <summary>...then <see cref="IsAutomaticCheckDue"/> is asked this often.</summary>
    public static readonly TimeSpan AutomaticCheckPollInterval = TimeSpan.FromHours(1);

    private readonly HttpClient client;

    /// <param name="repository">The "owner/name" slug, normally <see cref="HearsayRepository"/>.</param>
    /// <param name="handler">HTTP handler; tests pass a fake. Null uses the default handler. The caller keeps ownership.</param>
    /// <param name="timeout">Request timeout; defaults to <see cref="DefaultTimeout"/> (tests shorten it).</param>
    public UpdateChecker(string repository, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        Repository = repository;
        Timeout = timeout ?? DefaultTimeout;
        client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public string Repository { get; }

    /// <summary>The Swift request's <c>timeoutInterval</c>.</summary>
    public TimeSpan Timeout { get; }

    public void Dispose() => client.Dispose();

    /// <summary>The API endpoint for <see cref="Repository"/>, or null when the slug cannot form a URL.</summary>
    public Uri? RequestUri =>
        Uri.TryCreate($"https://api.github.com/repos/{Repository}/releases/latest", UriKind.Absolute, out var uri)
            ? uri
            : null;

    /// <exception cref="UpdateCheckException">The slug cannot form a URL (<see cref="UpdateCheckError.Parse"/>).</exception>
    public HttpRequestMessage MakeRequest()
    {
        var uri = RequestUri ?? throw new UpdateCheckException(new UpdateCheckError.Parse());
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub rejects API requests without a User-Agent (URLSession sends
        // one by itself; HttpClient does not).
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Hearsay", "1"));
        // URLRequest.reloadIgnoringLocalCacheData.
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    /// <summary>The latest release.</summary>
    /// <exception cref="UpdateCheckException">Offline, HTTP status, no release, or unreadable JSON.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ReleaseInfo> LatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = MakeRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        HttpResponseMessage response;
        byte[] body;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request timed out: URLError.timedOut on the Mac.
            throw new UpdateCheckException(new UpdateCheckError.Offline());
        }
        catch (HttpRequestException error)
        {
            throw new UpdateCheckException(new UpdateCheckError.Offline(), error);
        }
        using (response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.OK => Parse(body),
                HttpStatusCode.NotFound => throw new UpdateCheckException(new UpdateCheckError.NoRelease()),
                _ => throw new UpdateCheckException(new UpdateCheckError.HttpStatus((int)response.StatusCode)),
            };
        }
    }

    /// <summary>
    /// One check against <paramref name="currentVersion"/> (the Mac's
    /// <c>UpdateService.fetchOutcome</c>): a successful answer, and a 404,
    /// store the time in <paramref name="settings"/>' <c>lastUpdateCheck</c>;
    /// other errors do not. Never throws except for cancellation.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckAsync(
        string currentVersion, Settings.AppSettings settings, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(settings);
        var now = clock ?? TimeProvider.System;
        try
        {
            var release = await LatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            settings.LastUpdateCheck = now.GetUtcNow();
            return IsNewer(release.Version, currentVersion)
                ? new UpdateCheckOutcome.Available(release)
                : new UpdateCheckOutcome.UpToDate();
        }
        catch (UpdateCheckException error) when (error.Error is UpdateCheckError.NoRelease)
        {
            // The check itself worked; there is just nothing to offer.
            settings.LastUpdateCheck = now.GetUtcNow();
            return new UpdateCheckOutcome.Failed(error.Error.Description, error.Error.Localized);
        }
        catch (UpdateCheckException error)
        {
            return new UpdateCheckOutcome.Failed(error.Error.Description, error.Error.Localized);
        }
    }

    /// <summary>Parses the body of <c>releases/latest</c>.</summary>
    /// <exception cref="UpdateCheckException">Not the documented JSON (<see cref="UpdateCheckError.Parse"/>).</exception>
    public static ReleaseInfo Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            return ParseRoot(document.RootElement) ?? throw new UpdateCheckException(new UpdateCheckError.Parse());
        }
        catch (JsonException error)
        {
            throw new UpdateCheckException(new UpdateCheckError.Parse(), error);
        }
    }

    /// <summary>
    /// Mirrors the Swift <c>Decodable</c> payload: <c>tag_name</c> and
    /// <c>html_url</c> are required strings; the optional fields may be
    /// missing or null but a wrong type fails the whole decode, as
    /// <c>JSONDecoder</c> does. An asset without a name or an https download
    /// URL is skipped.
    /// </summary>
    private static ReleaseInfo? ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryRequiredString(root, "tag_name", out var tag)
            || !TryRequiredString(root, "html_url", out var html)
            || !TryOptionalString(root, "published_at", out var published)
            || !TryOptionalString(root, "body", out var body))
        {
            return null;
        }
        if (tag.Length == 0 || !Uri.TryCreate(html, UriKind.Absolute, out var htmlUri) || htmlUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryOptionalString(item, "name", out var name)
                    || !TryOptionalString(item, "browser_download_url", out var link)
                    || !TryOptionalInt64(item, "size", out var size))
                {
                    return null;
                }
                if (string.IsNullOrEmpty(name) || link is null
                    || !Uri.TryCreate(link, UriKind.Absolute, out var download) || download.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }
                assets.Add(new ReleaseAsset(name, download, size ?? 0));
            }
        }
        return new ReleaseInfo
        {
            Version = StripV(tag),
            TagName = tag,
            HtmlUri = htmlUri,
            PublishedAt = published is null ? null : ParseIso8601(published),
            Notes = string.IsNullOrEmpty(body) ? null : body,
            Assets = assets,
        };
    }

    private static bool TryRequiredString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? "";
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return true;
    }

    private static bool TryOptionalInt64(JsonElement element, string name, out long? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number))
        {
            return false;
        }
        value = number;
        return true;
    }

    /// <summary>
    /// Swift's <c>Date(_, strategy: .iso8601)</c>: date, "T", time in whole
    /// seconds, and "Z" or an offset; anything else gives null.
    /// </summary>
    private static DateTimeOffset? ParseIso8601(string text) =>
        DateTimeOffset.TryParseExact(
            text, ["yyyy-MM-dd'T'HH:mm:ssZ", "yyyy-MM-dd'T'HH:mm:sszzz"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    /// <summary>
    /// Whether an automatic check is due: the setting is on and the last
    /// successful check is missing, in the future (clock change), or at
    /// least <see cref="AutomaticInterval"/> old.
    /// </summary>
    public static bool IsAutomaticCheckDue(bool enabled, DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (!enabled)
        {
            return false;
        }
        if (lastCheck is not { } last)
        {
            return true;
        }
        var elapsed = now - last;
        return elapsed < TimeSpan.Zero || elapsed >= AutomaticInterval;
    }

    // Version comparison

    /// <summary>
    /// Whether <paramref name="remote"/> is a later version than
    /// <paramref name="current"/>. Semver-ish: a leading "v" is ignored,
    /// numeric components are compared as numbers (missing ones count as 0,
    /// so 1.2 == 1.2.0), build metadata after "+" is ignored, and a
    /// pre-release suffix ("-beta.1") is older than the same numeric version
    /// without one. Two pre-releases compare by their dot-separated
    /// identifiers (numbers numerically, others as text).
    /// </summary>
    public static bool IsNewer(string remote, string current)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(current);
        return Compare(new Version(remote), new Version(current)) > 0;
    }

    /// <summary>
    /// The version as the user sees it: an assembly's informational version
    /// without the "+&lt;commit&gt;" the .NET SDK appends ("0.3.0+abc" → "0.3.0").
    /// </summary>
    public static string WithoutBuildMetadata(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    private readonly struct Version
    {
        public Version(string text)
        {
            var core = StripV(TrimWhitespace(text));
            var plus = core.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                core = core[..plus];
            }
            var pre = "";
            var dash = core.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                pre = core[(dash + 1)..];
                core = core[..dash];
            }
            Numbers = [.. core.Split('.').Select(LeadingNumber)];
            PreRelease = pre.Length == 0 ? [] : pre.Split('.', StringSplitOptions.RemoveEmptyEntries);
        }

        public long[] Numbers { get; }

        public string[] PreRelease { get; }

        /// <summary>Swift <c>Int(part.prefix { $0.isNumber }) ?? 0</c>.</summary>
        private static long LeadingNumber(string part)
        {
            var digits = new string([.. part.TakeWhile(char.IsDigit)]);
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }

    private static int Compare(Version lhs, Version rhs)
    {
        var count = Math.Max(lhs.Numbers.Length, rhs.Numbers.Length);
        for (var index = 0; index < count; index++)
        {
            var left = index < lhs.Numbers.Length ? lhs.Numbers[index] : 0;
            var right = index < rhs.Numbers.Length ? rhs.Numbers[index] : 0;
            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }
        switch (lhs.PreRelease.Length == 0, rhs.PreRelease.Length == 0)
        {
            case (true, true): return 0;
            case (true, false): return 1;
            case (false, true): return -1;
        }
        foreach (var (left, right) in lhs.PreRelease.Zip(rhs.PreRelease))
        {
            if (left == right)
            {
                continue;
            }
            var leftIsNumber = TryParseSwiftInt(left, out var l);
            var rightIsNumber = TryParseSwiftInt(right, out var r);
            return (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => l < r ? -1 : 1,
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left, right) < 0 ? -1 : 1,
            };
        }
        if (lhs.PreRelease.Length == rhs.PreRelease.Length)
        {
            return 0;
        }
        return lhs.PreRelease.Length < rhs.PreRelease.Length ? -1 : 1;
    }

    /// <summary>Swift <c>Int(text)</c>: optional sign, ASCII digits only.</summary>
    private static bool TryParseSwiftInt(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>Swift <c>trimmingCharacters(in: .whitespaces)</c>: spaces and tabs, not newlines.</summary>
    private static string TrimWhitespace(string text) => text.Trim(HorizontalWhitespace);

    private static readonly char[] HorizontalWhitespace =
        [' ', '\t', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006',
         '\u2007', '\u2008', '\u2009', '\u200A', '\u202F', '\u205F', '\u3000'];

    private static string StripV(string text) =>
        text.Length > 0 && (text[0] == 'v' || text[0] == 'V') ? text[1..] : text;
}
