using System.Net;

namespace Hearsay.Tests.ModelStore;

/// <summary>
/// Serves files for one fake host through a <see cref="HttpMessageHandler"/>,
/// the counterpart of the <c>FakeHub</c> + <c>StubURLProtocol</c> pair in
/// mac/HearsayCore/Tests/HearsayCoreTests/ModelStoreTests.swift. Supports
/// <c>Range</c>, an announced size that disagrees with the body, a "stall
/// after N bytes" mode for cancellation, and redirects. Never touches the
/// network.
/// </summary>
internal sealed class FakeHub : HttpMessageHandler
{
    internal sealed record Behavior
    {
        public long? LinkedSizeOverride { get; init; }

        /// <summary>Send only this many bytes on the first request, then stall.</summary>
        public int? HangAfterBytes { get; init; }

        /// <summary>Answer with a 302 to this path (relative), carrying the linked size and commit headers.</summary>
        public string? RedirectTo { get; init; }
    }

    internal sealed record Request(string Path, string? Range, string? AcceptEncoding);

    private readonly Lock gate = new();
    private readonly Dictionary<string, (byte[] Data, Behavior Behavior)> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> hung = new(StringComparer.Ordinal);
    private readonly List<Request> requests = [];

    public string Host { get; } = $"hub-{Guid.NewGuid():N}.test";

    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public Uri BaseUri => new($"https://{Host}");

    public void Serve(string path, byte[] data, Behavior? behavior = null)
    {
        lock (gate)
        {
            files[path] = (data, behavior ?? new Behavior());
        }
    }

    public IReadOnlyList<Request> RecordedRequests()
    {
        lock (gate)
        {
            return [.. requests];
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("no URI");
        if (uri.Host != Host)
        {
            throw new HttpRequestException("No such host is known.");
        }
        lock (gate)
        {
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var range = request.Headers.Range?.ToString();
            requests.Add(new Request(path, range, request.Headers.AcceptEncoding.ToString()));
            if (!files.TryGetValue(path, out var file))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
            }
            var (data, behavior) = file;
            var response = new HttpResponseMessage();
            if (behavior.RedirectTo is { } target)
            {
                response.StatusCode = HttpStatusCode.Found;
                response.Headers.Location = new Uri(target, UriKind.Relative);
                response.Headers.Add("x-repo-commit", Commit);
                if (behavior.LinkedSizeOverride is { } redirectedSize)
                {
                    response.Headers.Add("x-linked-size", redirectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                response.Content = new ByteArrayContent([]);
                return Task.FromResult(response);
            }
            response.Headers.Add("x-repo-commit", Commit);
            if (behavior.LinkedSizeOverride is { } linked)
            {
                response.Headers.Add("x-linked-size", linked.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (behavior.HangAfterBytes is { } hangAfter && hung.Add(path))
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Content = new StreamContent(new StallingStream(data.AsMemory(0, hangAfter).ToArray()));
                response.Content.Headers.ContentLength = data.Length;
                return Task.FromResult(response);
            }
            if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } start, To: null })
            {
                if (start >= data.Length)
                {
                    response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.Content = new ByteArrayContent([]);
                    return Task.FromResult(response);
                }
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content = new ByteArrayContent(data[(int)start..]);
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(start, data.Length - 1, data.Length);
                return Task.FromResult(response);
            }
            response.StatusCode = HttpStatusCode.OK;
            response.Content = new ByteArrayContent(data);
            return Task.FromResult(response);
        }
    }

    /// <summary>Returns its bytes, then blocks every read until cancelled.</summary>
    private sealed class StallingStream(byte[] head) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - position);
                head.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
