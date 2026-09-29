using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearsay.Core.ModelStore;

/// <summary>
/// Progress of one model download across all of its files. Port of
/// <c>DownloadProgress</c> in mac/HearsayCore/Sources/HearsayCore/ModelStore/ModelDownloader.swift.
/// </summary>
/// <param name="BytesReceived">Bytes on disk for this download, including resumed partial files and files already present.</param>
/// <param name="TotalBytes">Best known total; grows slightly while small files report their size.</param>
/// <param name="CurrentFile">File being fetched, for example <c>ggml-base.bin</c>.</param>
public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes, string CurrentFile)
{
    /// <summary>0...1, or 0 while the total is unknown.</summary>
    public double FractionCompleted =>
        TotalBytes > 0 ? Math.Min(1, (double)BytesReceived / TotalBytes) : 0;
}

/// <summary>
/// Written to <c>manifest.json</c> in a model folder after every file arrived.
/// Port of <c>ModelManifest</c> in ModelDownloader.swift: sorted keys,
/// indented, ISO 8601 date in whole seconds (UTC, <c>Z</c>). <see cref="Repo"/>
/// is the Hugging Face repo, as on the Mac; <see cref="Files"/> names the
/// weights file, which tells the Windows entries of one repo apart.
/// </summary>
public sealed record ModelManifest
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        RespectNullableAnnotations = true,
        Converters = { new Iso8601SecondsConverter() },
    };

    /// <summary><c>x-repo-commit</c> from Hugging Face, when the server sent it.</summary>
    [JsonPropertyName("commit")]
    [JsonPropertyOrder(0)]
    public string? Commit { get; init; }

    [JsonPropertyName("downloadedAt")]
    [JsonPropertyOrder(1)]
    public required DateTimeOffset DownloadedAt { get; init; }

    [JsonPropertyName("files")]
    [JsonPropertyOrder(2)]
    public required IReadOnlyList<ManifestFile> Files { get; init; }

    [JsonPropertyName("repo")]
    [JsonPropertyOrder(3)]
    public required string Repo { get; init; }

    /// <summary>Value equality over the file list too.</summary>
    public bool Equals(ModelManifest? other) =>
        other is not null && Repo == other.Repo && Commit == other.Commit
        && DownloadedAt == other.DownloadedAt && Files.SequenceEqual(other.Files);

    public override int GetHashCode() => HashCode.Combine(Repo, Commit, DownloadedAt, Files.Count);

    public string Encode() => JsonSerializer.Serialize(this, Options);

    /// <exception cref="JsonException">Not a manifest.</exception>
    public static ModelManifest? Decode(string json) => JsonSerializer.Deserialize<ModelManifest>(json, Options);

    /// <summary>One file of a model and its size in bytes.</summary>
    public sealed record ManifestFile(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size);

    private sealed class Iso8601SecondsConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException("Expected an ISO 8601 date.");
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Why a model download failed. Port of <c>ModelDownloadError</c> in
/// ModelDownloader.swift; the English text is the Swift string catalog key so
/// the shared translations can be reused (localization is W7).
/// </summary>
public abstract record ModelDownloadError
{
    private ModelDownloadError()
    {
    }

    public sealed record HttpStatus(string File, int Status) : ModelDownloadError;

    public sealed record SizeMismatch(string File, long Expected, long Actual) : ModelDownloadError;

    public sealed record RangeNotSatisfiable(string File) : ModelDownloadError;

    public sealed record InvalidResponse(string File) : ModelDownloadError;

    /// <summary>The message shown to the user (Swift <c>errorDescription</c>).</summary>
    public string Description => this switch
    {
        HttpStatus e => $"Downloading {e.File} failed with HTTP status {e.Status}.",
        SizeMismatch e => $"{e.File} is {e.Actual} bytes but the server announced {e.Expected}. The file was discarded; try again.",
        RangeNotSatisfiable e => $"The server refused to resume {e.File}.",
        InvalidResponse e => $"The server sent an unexpected response for {e.File}.",
        _ => throw new InvalidOperationException("Unknown ModelDownloadError."),
    };
}

/// <summary>Thrown by <see cref="ModelDownloader"/>; <see cref="Error"/> says what failed.</summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(ModelDownloadError error)
        : base(error?.Description)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public ModelDownloadError Error { get; }
}

/// <summary>
/// Downloads one catalog entry plus the shared tokenizer files from Hugging
/// Face (PLAN.md section 5). Port of mac/HearsayCore/Sources/HearsayCore/ModelStore/ModelDownloader.swift
/// with <see cref="HttpClient"/> in place of <c>URLSession</c>.
/// <para>
/// Layout under <see cref="ModelsRoot"/>:
/// <c>&lt;entry id with "/" → "_"&gt;\&lt;weights file&gt;</c> plus <c>manifest.json</c>,
/// and <c>_tokenizer\&lt;tokenizer repo with "/" → "_"&gt;\&lt;files&gt;</c> when the
/// catalog lists tokenizer files (the Windows catalog lists none, and then the
/// tokenizer folder is not created; the Mac always creates it).
/// </para>
/// <para>
/// Each file is written to <c>&lt;name&gt;.partial</c> first and moved into place
/// once its size matches what the server announced (<c>x-linked-size</c>,
/// else <c>Content-Range</c> total, else <c>Content-Length</c>); a mismatch
/// deletes the partial file. A cancelled or failed transfer keeps the partial
/// file, and the next attempt resumes with an HTTP <c>Range</c> header; a 416
/// reply deletes it and starts over once. No checksum is computed, as on the
/// Mac. Redirects are followed here (at most 10) rather than by the handler,
/// so the <c>x-linked-size</c> and <c>x-repo-commit</c> headers of the
/// Hugging Face redirect are seen, and <c>Range</c> and
/// <c>Accept-Encoding: identity</c> are sent again. A read that stalls for 60 s
/// fails (URLSession's default request timeout). Thread-safe.
/// </para>
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    public const string TokenizerFolderName = "_tokenizer";
    public const string PartialSuffix = ".partial";

    private const int MaxRedirects = 10;
    private static readonly Uri HuggingFace = new("https://huggingface.co/");

    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly Lock gate = new();

    /// <summary>
    /// Transfers in flight by destination, so two model downloads that both
    /// need the same file never write the same partial file.
    /// </summary>
    private readonly Dictionary<string, Task> inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="modelsRoot">Folder the models live in (the app passes <c>%LOCALAPPDATA%\Hearsay\Models</c>).</param>
    /// <param name="tokenizer">The catalog's tokenizer source.</param>
    /// <param name="handler">HTTP handler; tests pass a fake. Null uses a handler that neither redirects nor decompresses. The caller keeps ownership.</param>
    /// <param name="baseUri">Server root; defaults to https://huggingface.co.</param>
    public ModelDownloader(string modelsRoot, TokenizerSource tokenizer, HttpMessageHandler? handler = null, Uri? baseUri = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelsRoot);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ModelsRoot = Path.GetFullPath(modelsRoot);
        Tokenizer = tokenizer;
        this.baseUri = baseUri ?? HuggingFace;
        client = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public string ModelsRoot { get; }

    public TokenizerSource Tokenizer { get; }

    /// <summary>Idle limit between reads of a response (URLSession's default is 60 s).</summary>
    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public void Dispose() => client.Dispose();

    // Layout

    /// <summary>A repo or entry id as one folder name: every "/" becomes "_".</summary>
    public static string FolderName(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Replace('/', '_');
    }

    /// <summary>The folder of the entry with this id.</summary>
    public static string ModelDirectory(string id, string root) => Path.Combine(root, FolderName(id));

    public static string TokenizerDirectory(TokenizerSource tokenizer, string root)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        return Path.Combine(root, TokenizerFolderName, FolderName(tokenizer.Repo));
    }

    /// <summary><c>&lt;base&gt;/&lt;repo&gt;/resolve/main/&lt;file&gt;</c>.</summary>
    internal Uri FileUri(string repo, string file)
    {
        var path = string.Join('/', repo.Split('/').Select(Uri.EscapeDataString))
            + "/resolve/main/" + Uri.EscapeDataString(file);
        var root = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        return new Uri(root, path);
    }

    // Download

    /// <summary>
    /// Downloads <paramref name="entry"/> and the tokenizer files (files
    /// already present are skipped), then writes <c>manifest.json</c>.
    /// Cancel through <paramref name="cancellationToken"/>; partial files are
    /// kept for resume and an <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    /// <exception cref="ModelDownloadException">The server's answer was not usable.</exception>
    public async Task<ModelManifest> DownloadAsync(
        ModelCatalogEntry entry,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var modelDir = ModelDirectory(entry.Id, ModelsRoot);
        var tokenizerDir = TokenizerDirectory(Tokenizer, ModelsRoot);
        Directory.CreateDirectory(modelDir);
        if (Tokenizer.Files.Count > 0)
        {
            Directory.CreateDirectory(tokenizerDir);
        }

        // A stale manifest must not mark a half-replaced folder as installed.
        TryDelete(Path.Combine(modelDir, ModelManifest.FileName));

        var jobs = new List<Job>();
        jobs.AddRange(Tokenizer.Files.Select(name =>
            new Job(name, FileUri(Tokenizer.Repo, name), Path.Combine(tokenizerDir, name), 0, IsModelFile: false)));
        jobs.AddRange(entry.Files.Select(name =>
            new Job(name, FileUri(entry.Repo, name), Path.Combine(modelDir, name),
                name == entry.WeightsFile ? entry.SizeBytes : 0, IsModelFile: true)));

        var tracker = new ProgressTracker(jobs.Select(job => (job.Name, job.Estimate)).ToList(), progress);
        string? commit = null;
        var manifestFiles = new List<ModelManifest.ManifestFile>();

        for (var index = 0; index < jobs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[index];
            tracker.Begin(index);
            var current = index;
            var fileCommit = await FetchAsync(job, (written, expected) => tracker.Update(current, written, expected),
                cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(job.Destination).Length;
            tracker.Finish(index, size);
            if (job.IsModelFile)
            {
                manifestFiles.Add(new ModelManifest.ManifestFile(job.Name, size));
                commit ??= fileCommit;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var manifest = new ModelManifest
        {
            Repo = entry.Repo,
            Files = manifestFiles,
            Commit = commit,
            // Whole seconds, so the value matches what ISO 8601 stores.
            DownloadedAt = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero),
        };
        WriteAtomically(Path.Combine(modelDir, ModelManifest.FileName), manifest.Encode());
        tracker.EmitFinal();
        return manifest;
    }

    private sealed record Job(string Name, Uri Source, string Destination, long Estimate, bool IsModelFile);

    /// <summary>
    /// Fetches one file unless it is already complete. Returns the
    /// <c>x-repo-commit</c> header when a transfer happened and carried one.
    /// </summary>
    private async Task<string?> FetchAsync(Job job, Action<long, long?> onProgress, CancellationToken cancellationToken)
    {
        TaskCompletionSource done;
        while (true)
        {
            Task? other;
            lock (gate)
            {
                if (!inFlight.TryGetValue(job.Destination, out other))
                {
                    if (File.Exists(job.Destination))
                    {
                        return null;
                    }
                    done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    inFlight[job.Destination] = done.Task;
                    break;
                }
            }
            try
            {
                await other.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The other transfer was cancelled; this one goes on.
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            return await TransferWithRetryAsync(job, onProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                inFlight.Remove(job.Destination);
            }
            done.SetResult();
        }
    }

    private async Task<string?> TransferWithRetryAsync(Job job, Action<long, long?> onProgress, CancellationToken cancellationToken)
    {
        var partial = job.Destination + PartialSuffix;
        try
        {
            return await TransferAsync(job, partial, onProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelDownloadException error) when (error.Error is ModelDownloadError.RangeNotSatisfiable)
        {
            // The partial file does not match the remote file; start over once.
            TryDelete(partial);
            return await TransferAsync(job, partial, onProgress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> TransferAsync(Job job, string partial, Action<long, long?> onProgress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        long? linkedSize = null;
        string? commit = null;
        var uri = job.Source;
        HttpResponseMessage response;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Compressed transfer would make Content-Length disagree with the
            // bytes written to disk.
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            linkedSize = HeaderInt64(response, "x-linked-size") ?? linkedSize;
            commit = HeaderString(response, "x-repo-commit") ?? commit;
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location && redirects < MaxRedirects)
            {
                response.Dispose();
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            break;
        }

        using (response)
        {
            bool append;
            long written;
            long? expected;
            var length = response.Content.Headers.ContentLength;
            switch ((int)response.StatusCode)
            {
                case 200:
                    append = false;
                    written = 0;
                    expected = linkedSize ?? length;
                    break;
                case 206:
                    append = true;
                    written = offset;
                    expected = linkedSize ?? response.Content.Headers.ContentRange?.Length
                        ?? (length is { } partLength ? offset + partLength : null);
                    break;
                case 416:
                    throw new ModelDownloadException(new ModelDownloadError.RangeNotSatisfiable(job.Name));
                default:
                    throw new ModelDownloadException(new ModelDownloadError.HttpStatus(job.Name, (int)response.StatusCode));
            }

            if (!append || !File.Exists(partial))
            {
                written = 0;
            }
            var stream = new FileStream(partial, append && File.Exists(partial) ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, bufferSize: 0, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                onProgress(written, expected);
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    while (true)
                    {
                        idle.CancelAfter(ReadTimeout);
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException("The request timed out.");
                        }
                        if (read == 0)
                        {
                            break;
                        }
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        onProgress(written, expected);
                    }
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var actual = new FileInfo(partial).Length;
            if (expected is { } announced && announced != actual)
            {
                TryDelete(partial);
                throw new ModelDownloadException(new ModelDownloadError.SizeMismatch(job.Name, announced, actual));
            }
            File.Move(partial, job.Destination, overwrite: true);
            return commit;
        }
    }

    private static long? HeaderInt64(HttpResponseMessage response, string name) =>
        HeaderString(response, name) is { } text
        && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? HeaderString(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)
            || response.Content.Headers.TryGetValues(name, out values))
        {
            var value = values.FirstOrDefault();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        return null;
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Aggregates per-file byte counts and reports throttled progress (every 0.1 s, plus each file's start and end).</summary>
    private sealed class ProgressTracker
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(0.1);

        private readonly Lock gate = new();
        private readonly FileState[] files;
        private readonly IProgress<DownloadProgress>? progress;
        private int current;
        private long lastEmit = long.MinValue;

        public ProgressTracker(IReadOnlyList<(string Name, long Estimate)> files, IProgress<DownloadProgress>? progress)
        {
            this.files = files.Select(file => new FileState(file.Name, file.Estimate)).ToArray();
            this.progress = progress;
        }

        public void Begin(int index) => Emit(force: true, () => current = index);

        public void Update(int index, long written, long? expected) => Emit(force: false, () =>
        {
            files[index].Received = written;
            if (expected is { } value)
            {
                files[index].Expected = value;
            }
        });

        public void Finish(int index, long size) => Emit(force: true, () =>
        {
            files[index].Received = size;
            files[index].Expected = size;
        });

        public void EmitFinal() => Emit(force: true, () => { });

        private void Emit(bool force, Action mutate)
        {
            DownloadProgress? snapshot = null;
            lock (gate)
            {
                mutate();
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (force || lastEmit == long.MinValue
                    || System.Diagnostics.Stopwatch.GetElapsedTime(lastEmit, now) >= Interval)
                {
                    lastEmit = now;
                    var received = files.Sum(file => file.Received);
                    var total = files.Sum(file => file.Expected ?? Math.Max(file.Estimate, file.Received));
                    snapshot = new DownloadProgress(received, Math.Max(total, received),
                        files.Length > 0 ? files[current].Name : "");
                }
            }
            if (snapshot is { } value)
            {
                progress?.Report(value);
            }
        }

        private sealed class FileState(string name, long estimate)
        {
            public string Name { get; } = name;
            public long Estimate { get; } = estimate;
            public long Received { get; set; }
            public long? Expected { get; set; }
        }
    }
}
