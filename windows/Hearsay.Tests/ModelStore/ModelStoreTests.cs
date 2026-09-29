using System.Text;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Settings;
using Hearsay.Tests.Settings;
using Store = Hearsay.Core.ModelStore.ModelStore;

namespace Hearsay.Tests.ModelStore;

/// <summary>
/// Fixtures shared by the model tests, after the <c>Fixture</c> enum in
/// mac/HearsayCore/Tests/HearsayCoreTests/ModelStoreTests.swift. The entry
/// has one file (Windows: the GGML file is the whole model, no
/// <c>config.json</c>); the tokenizer keeps two files so the shared-tokenizer
/// path is still exercised, and <see cref="WindowsTokenizer"/> is the real
/// catalog's empty list.
/// </summary>
internal static class Fixture
{
    public static readonly TokenizerSource Tokenizer = new() { Repo = "openai/whisper-test", Files = ["tokenizer.json", "vocab.json"] };

    public static readonly TokenizerSource WindowsTokenizer = new() { Repo = "ggerganov/whisper-test", Files = [] };

    public static readonly byte[] Weights = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i % 251))];

    public static readonly ModelCatalogEntry Entry = new()
    {
        Repo = "ggerganov/whisper-test",
        DisplayName = "Test",
        SizeBytes = 4096,
        WeightsFile = "ggml-test.bin",
        Quantization = "f16",
        Family = "tiny",
        Recommended = false,
    };

    public static readonly byte[] TokenizerJson = [.. Enumerable.Repeat((byte)0x41, 300)];

    public static readonly byte[] Vocab = [.. Enumerable.Repeat((byte)0x42, 200)];

    public const string WeightsPath = "/ggerganov/whisper-test/resolve/main/ggml-test.bin";

    public static long TotalBytes => Weights.Length + TokenizerJson.Length + Vocab.Length;

    public static FakeHub Hub(FakeHub.Behavior? weightsBehavior = null)
    {
        var hub = new FakeHub();
        hub.Serve("/openai/whisper-test/resolve/main/tokenizer.json", TokenizerJson);
        hub.Serve("/openai/whisper-test/resolve/main/vocab.json", Vocab);
        hub.Serve(WeightsPath, Weights, weightsBehavior);
        return hub;
    }

    public static long? FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : null;
}

/// <summary>A temporary models folder, deleted on dispose.</summary>
internal sealed class ScratchRoot : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"HearsayModelTests-{Guid.NewGuid():N}");

    /// <summary>Retries for up to 5 s: a cancelled download may still be closing its partial file.</summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Collects every report synchronously (<see cref="Progress{T}"/> would post them later).</summary>
internal sealed class ProgressLog : IProgress<DownloadProgress>
{
    private readonly Lock gate = new();
    private readonly List<DownloadProgress> all = [];

    public IReadOnlyList<DownloadProgress> All
    {
        get
        {
            lock (gate)
            {
                return [.. all];
            }
        }
    }

    public void Report(DownloadProgress value)
    {
        lock (gate)
        {
            all.Add(value);
        }
    }
}

/// <summary>Port of <c>ModelCatalogTests</c> in ModelStoreTests.swift, for the Windows catalog.</summary>
public class ModelCatalogTests
{
    private static readonly string[] Quantizations = ["f16", "q5_0", "q8_0"];

    [Fact]
    public void BundledCatalogHasEightEntriesAndOneRecommended()
    {
        var catalog = ModelCatalog.Bundled();
        Assert.Equal(8, catalog.Entries.Count);
        Assert.Equal(8, catalog.Entries.Select(entry => entry.Id).Distinct().Count());
        var recommended = catalog.Entries.Where(entry => entry.Recommended).Select(entry => entry.Id).ToList();
        Assert.Equal(["ggerganov/whisper.cpp/ggml-large-v3-turbo-q5_0.bin"], recommended);
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", catalog.Recommended?.WeightsFile);
        Assert.Equal(
            [
                "ggml-tiny.bin", "ggml-base.bin", "ggml-small.bin", "ggml-medium-q5_0.bin",
                "ggml-large-v3-turbo-q5_0.bin", "ggml-large-v3-turbo.bin", "ggml-large-v3-q5_0.bin", "ggml-large-v3.bin",
            ],
            catalog.Entries.Select(entry => entry.WeightsFile));
        foreach (var entry in catalog.Entries)
        {
            Assert.True(entry.SizeBytes > 0, entry.Id);
            Assert.Contains(entry.Quantization, Quantizations);
            Assert.Contains(entry.Family, ModelCatalog.FamilyOrder);
            Assert.Equal("ggerganov/whisper.cpp", entry.Repo);
            Assert.Equal(entry.Repo + "/" + entry.WeightsFile, entry.Id);
            Assert.Equal([entry.WeightsFile], entry.Files);
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.Same(entry, catalog.Entry(entry.Id));
        }
        Assert.Equal("ggerganov/whisper.cpp", catalog.Tokenizer.Repo);
        Assert.Empty(catalog.Tokenizer.Files);
    }

    [Fact]
    public void BundledFamiliesFollowTheReadmeOrder()
    {
        var catalog = ModelCatalog.Bundled();
        Assert.Equal(["tiny", "base", "small", "medium", "large-v3-turbo", "large-v3"], ModelCatalog.FamilyOrder);
        var indexes = catalog.Entries.Select(entry => ModelCatalog.FamilyOrder.ToList().IndexOf(entry.Family)).ToList();
        Assert.Equal(indexes.Order(), indexes);
        Assert.Equal(ModelCatalog.FamilyOrder, catalog.Entries.Select(entry => entry.Family).Distinct());
    }

    [Fact]
    public void DecodeIgnoresUnknownKeysAndRejectsMissingOnes()
    {
        const string valid = """
            {"about": "note", "tokenizer": {"repo": "r", "files": []},
             "entries": [{"repo": "a/b", "displayName": "D", "sizeBytes": 5, "weightsFile": "w.bin",
                          "quantization": "q5_0", "family": "tiny", "recommended": true, "extra": 1}]}
            """;
        var catalog = ModelCatalog.Decode(Encoding.UTF8.GetBytes(valid));
        Assert.Equal("a/b/w.bin", Assert.Single(catalog.Entries).Id);

        const string missing = """
            {"tokenizer": {"repo": "r", "files": []},
             "entries": [{"repo": "a/b", "displayName": "D", "weightsFile": "w.bin",
                          "quantization": "q5_0", "family": "tiny", "recommended": true}]}
            """;
        Assert.Throws<ModelCatalogException>(() => ModelCatalog.Decode(Encoding.UTF8.GetBytes(missing)));
        Assert.Throws<ModelCatalogException>(() => ModelCatalog.Decode("[]"u8));
    }
}

/// <summary>Port of <c>ModelDownloaderTests</c> in ModelStoreTests.swift.</summary>
public sealed class ModelDownloaderTests : IDisposable
{
    private static readonly string[] ManifestKeys = ["\"commit\"", "\"downloadedAt\"", "\"files\"", "\"repo\""];

    private readonly ScratchRoot root = new();

    public void Dispose() => root.Dispose();

    private ModelDownloader Downloader(FakeHub hub, TokenizerSource? tokenizer = null) =>
        new(root.Path, tokenizer ?? Fixture.Tokenizer, hub, hub.BaseUri);

    [Fact]
    public void LayoutReplacesSlashes()
    {
        var models = Path.Combine(Path.GetTempPath(), "models");
        Assert.Equal(Path.Combine(models, "ggerganov_whisper.cpp_ggml-tiny.bin"),
            ModelDownloader.ModelDirectory("ggerganov/whisper.cpp/ggml-tiny.bin", models));
        Assert.Equal(Path.Combine(models, "_tokenizer", "openai_whisper-large-v3"),
            ModelDownloader.TokenizerDirectory(new TokenizerSource { Repo = "openai/whisper-large-v3", Files = [] }, models));
    }

    [Fact]
    public async Task FullDownloadWritesFilesManifestAndReachesTotal()
    {
        using var hub = Fixture.Hub();
        using var downloader = Downloader(hub);
        var log = new ProgressLog();
        var manifest = await downloader.DownloadAsync(Fixture.Entry, log);

        var modelDir = ModelDownloader.ModelDirectory(Fixture.Entry.Id, root.Path);
        var tokenizerDir = ModelDownloader.TokenizerDirectory(Fixture.Tokenizer, root.Path);
        Assert.Equal(Fixture.Weights, File.ReadAllBytes(Path.Combine(modelDir, "ggml-test.bin")));
        Assert.Equal(Fixture.TokenizerJson, File.ReadAllBytes(Path.Combine(tokenizerDir, "tokenizer.json")));
        Assert.Equal(Fixture.Vocab, File.ReadAllBytes(Path.Combine(tokenizerDir, "vocab.json")));
        Assert.False(File.Exists(Path.Combine(modelDir, "tokenizer.json")));
        Assert.False(File.Exists(Path.Combine(modelDir, "ggml-test.bin.partial")));

        var stored = ModelManifest.Decode(File.ReadAllText(Path.Combine(modelDir, "manifest.json")));
        Assert.Equal(manifest, stored);
        Assert.Equal(Fixture.Entry.Repo, manifest.Repo);
        Assert.Equal(FakeHub.Commit, manifest.Commit);
        Assert.Equal([new ModelManifest.ManifestFile("ggml-test.bin", Fixture.Weights.Length)], manifest.Files);

        var updates = log.All;
        var last = updates[^1];
        Assert.Equal(Fixture.TotalBytes, last.BytesReceived);
        Assert.Equal(Fixture.TotalBytes, last.TotalBytes);
        Assert.Equal(1, last.FractionCompleted);
        Assert.Contains(updates, update => update.CurrentFile == "ggml-test.bin");
        var requests = hub.RecordedRequests();
        Assert.All(requests, request => Assert.Null(request.Range));
        Assert.All(requests, request => Assert.Equal("identity", request.AcceptEncoding));
        Assert.Equal(3, requests.Count);
    }

    /// <summary>Windows only: the real catalog's shape, one GGML file and no tokenizer files.</summary>
    [Fact]
    public async Task SingleFileModelNeedsOneRequestAndNoTokenizerFolder()
    {
        using var hub = Fixture.Hub();
        using var downloader = Downloader(hub, Fixture.WindowsTokenizer);
        var manifest = await downloader.DownloadAsync(Fixture.Entry);
        Assert.Single(hub.RecordedRequests());
        Assert.False(Directory.Exists(Path.Combine(root.Path, ModelDownloader.TokenizerFolderName)));
        Assert.Equal(["ggml-test.bin"], manifest.Files.Select(file => file.Name));
        Assert.Equal(["ggerganov_whisper-test_ggml-test.bin"], Directory.GetDirectories(root.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task SharedTokenizerIsDownloadedOnce()
    {
        using var hub = Fixture.Hub();
        using var downloader = Downloader(hub);
        await downloader.DownloadAsync(Fixture.Entry);
        Directory.Delete(ModelDownloader.ModelDirectory(Fixture.Entry.Id, root.Path), recursive: true);
        await downloader.DownloadAsync(Fixture.Entry);
        var tokenizerRequests = hub.RecordedRequests().Where(request => request.Path.StartsWith("/openai/", StringComparison.Ordinal));
        Assert.Equal(2, tokenizerRequests.Count());
    }

    [Fact]
    public async Task SizeMismatchFailsAndKeepsNoFinalFile()
    {
        using var hub = Fixture.Hub(new FakeHub.Behavior { LinkedSizeOverride = Fixture.Weights.Length + 10 });
        using var downloader = Downloader(hub);
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAsync(Fixture.Entry));
        Assert.Equal(new ModelDownloadError.SizeMismatch("ggml-test.bin", Fixture.Weights.Length + 10, Fixture.Weights.Length), error.Error);
        Assert.Equal(
            "ggml-test.bin is 4096 bytes but the server announced 4106. The file was discarded; try again.",
            error.Message);
        var modelDir = ModelDownloader.ModelDirectory(Fixture.Entry.Id, root.Path);
        Assert.False(File.Exists(Path.Combine(modelDir, "ggml-test.bin")));
        Assert.False(File.Exists(Path.Combine(modelDir, "ggml-test.bin.partial")));
        Assert.False(File.Exists(Path.Combine(modelDir, "manifest.json")));
    }

    [Fact]
    public async Task HttpErrorFails()
    {
        using var hub = new FakeHub();
        using var downloader = Downloader(hub);
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAsync(Fixture.Entry));
        Assert.Equal(new ModelDownloadError.HttpStatus("tokenizer.json", 404), error.Error);
        Assert.Equal("Downloading tokenizer.json failed with HTTP status 404.", error.Message);
    }

    [Fact]
    public async Task CancellationKeepsPartialAndSecondDownloadResumesWithRange()
    {
        const int firstChunk = 1000;
        using var hub = Fixture.Hub(new FakeHub.Behavior { HangAfterBytes = firstChunk });
        using var downloader = Downloader(hub);
        var modelDir = ModelDownloader.ModelDirectory(Fixture.Entry.Id, root.Path);
        var partial = Path.Combine(modelDir, "ggml-test.bin.partial");
        var final = Path.Combine(modelDir, "ggml-test.bin");

        using var cancellation = new CancellationTokenSource();
        var task = downloader.DownloadAsync(Fixture.Entry, cancellationToken: cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Fixture.FileSize(partial) != firstChunk && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(firstChunk, Fixture.FileSize(partial));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(firstChunk, Fixture.FileSize(partial));
        Assert.False(File.Exists(final));
        Assert.False(File.Exists(Path.Combine(modelDir, "manifest.json")));

        await downloader.DownloadAsync(Fixture.Entry);
        var weightRequests = hub.RecordedRequests().Where(request => request.Path == Fixture.WeightsPath).ToList();
        Assert.Equal(2, weightRequests.Count);
        Assert.Equal($"bytes={firstChunk}-", weightRequests[^1].Range);
        Assert.Equal(Fixture.Weights, File.ReadAllBytes(final));
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(Path.Combine(modelDir, "manifest.json")));
    }

    /// <summary>Windows only: redirects are followed by the downloader, which keeps the redirect's headers and the Range.</summary>
    [Fact]
    public async Task RedirectKeepsRangeAndTheRedirectsLinkedSize()
    {
        using var hub = new FakeHub();
        hub.Serve(Fixture.WeightsPath, [], new FakeHub.Behavior { RedirectTo = "/cdn/blob", LinkedSizeOverride = Fixture.Weights.Length });
        hub.Serve("/cdn/blob", Fixture.Weights);
        using var downloader = Downloader(hub, Fixture.WindowsTokenizer);
        var modelDir = ModelDownloader.ModelDirectory(Fixture.Entry.Id, root.Path);
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(Path.Combine(modelDir, "ggml-test.bin.partial"), Fixture.Weights[..1500]);

        var manifest = await downloader.DownloadAsync(Fixture.Entry);
        var requests = hub.RecordedRequests();
        Assert.Equal([Fixture.WeightsPath, "/cdn/blob"], requests.Select(request => request.Path));
        Assert.All(requests, request => Assert.Equal("bytes=1500-", request.Range));
        Assert.All(requests, request => Assert.Equal("identity", request.AcceptEncoding));
        Assert.Equal(Fixture.Weights, File.ReadAllBytes(Path.Combine(modelDir, "ggml-test.bin")));
        Assert.Equal(FakeHub.Commit, manifest.Commit);

        // The redirect's x-linked-size is the one checked.
        using var wrongHub = new FakeHub();
        wrongHub.Serve(Fixture.WeightsPath, [], new FakeHub.Behavior { RedirectTo = "/cdn/blob", LinkedSizeOverride = 7 });
        wrongHub.Serve("/cdn/blob", Fixture.Weights);
        using var second = new ModelDownloader(Path.Combine(root.Path, "second"), Fixture.WindowsTokenizer, wrongHub, wrongHub.BaseUri);
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => second.DownloadAsync(Fixture.Entry));
        Assert.Equal(new ModelDownloadError.SizeMismatch("ggml-test.bin", 7, Fixture.Weights.Length), error.Error);
    }

    /// <summary>The Swift <c>rangeNotSatisfiable</c> retry: a partial file longer than the remote one starts over once.</summary>
    [Fact]
    public async Task RangeNotSatisfiableStartsOverOnce()
    {
        using var hub = Fixture.Hub();
        using var downloader = Downloader(hub, Fixture.WindowsTokenizer);
        var modelDir = ModelDownloader.ModelDirectory(Fixture.Entry.Id, root.Path);
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(Path.Combine(modelDir, "ggml-test.bin.partial"), new byte[Fixture.Weights.Length + 5]);

        await downloader.DownloadAsync(Fixture.Entry);
        var requests = hub.RecordedRequests();
        Assert.Equal([$"bytes={Fixture.Weights.Length + 5}-", null], requests.Select(request => request.Range));
        Assert.Equal(Fixture.Weights, File.ReadAllBytes(Path.Combine(modelDir, "ggml-test.bin")));
    }

    [Fact]
    public void ErrorTextsMatchTheSwiftCatalogKeys()
    {
        Assert.Equal("The server refused to resume a.bin.", new ModelDownloadError.RangeNotSatisfiable("a.bin").Description);
        Assert.Equal("The server sent an unexpected response for a.bin.", new ModelDownloadError.InvalidResponse("a.bin").Description);
    }

    [Fact]
    public void ManifestIsSortedIndentedAndWholeSeconds()
    {
        var manifest = new ModelManifest
        {
            Repo = "a/b",
            Files = [new ModelManifest.ManifestFile("w.bin", 12)],
            Commit = null,
            DownloadedAt = new DateTimeOffset(2026, 9, 29, 10, 11, 12, TimeSpan.Zero),
        };
        var json = manifest.Encode();
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.Contains("\"downloadedAt\": \"2026-09-29T10:11:12Z\"", json, StringComparison.Ordinal);
        var keys = ManifestKeys
            .Select(key => json.IndexOf(key, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, keys);
        Assert.Equal(keys.Order(), keys);
        Assert.Equal(manifest, ModelManifest.Decode(json));
    }

    [Fact]
    public void FractionIsZeroWithoutTotalAndCappedAtOne()
    {
        Assert.Equal(0, new DownloadProgress(5, 0, "").FractionCompleted);
        Assert.Equal(1, new DownloadProgress(15, 10, "").FractionCompleted);
        Assert.Equal(0.5, new DownloadProgress(5, 10, "").FractionCompleted);
    }
}

/// <summary>
/// Port of <c>ModelStoreTests</c> in ModelStoreTests.swift, one for one, with
/// scratch settings and models folders.
/// </summary>
public sealed class ModelStoreTests : IDisposable
{
    private static readonly ModelCatalog Catalog = new([Fixture.Entry], Fixture.Tokenizer);

    private readonly ScratchSettings scratch = new();
    private readonly ScratchRoot root = new();

    public void Dispose()
    {
        root.Dispose();
        scratch.Dispose();
    }

    private (AppSettings Settings, string Folder) FreshSettings()
    {
        var folder = scratch.Make();
        return (new AppSettings(folder), folder);
    }

    private static void WriteModel(string root, bool manifest = true)
    {
        var dir = ModelDownloader.ModelDirectory(Fixture.Entry.Id, root);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ggml-test.bin"), Fixture.Weights);
        if (manifest)
        {
            var value = new ModelManifest { Repo = Fixture.Entry.Repo, Files = [], Commit = null, DownloadedAt = DateTimeOffset.UtcNow };
            File.WriteAllText(Path.Combine(dir, "manifest.json"), value.Encode());
        }
    }

    private static void WriteTokenizer(string root)
    {
        var dir = ModelDownloader.TokenizerDirectory(Fixture.Tokenizer, root);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "tokenizer.json"), Fixture.TokenizerJson);
        File.WriteAllBytes(Path.Combine(dir, "vocab.json"), Fixture.Vocab);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void EmptyRootHasNothingInstalled()
    {
        var (settings, _) = FreshSettings();
        using var store = new Store(settings, root.Path, Catalog);
        Assert.Empty(store.Installed);
        Assert.False(store.IsTokenizerInstalled);
        Assert.Equal(DownloadState.NotInstalled, store.State(Fixture.Entry));
        Assert.Equal(0, store.TotalSizeOnDisk);
    }

    [Fact]
    public void ScanningRequiresManifestAndReadyRequiresTokenizer()
    {
        var (settings, _) = FreshSettings();
        WriteModel(root.Path, manifest: false);
        using var store = new Store(settings, root.Path, Catalog);
        Assert.Empty(store.Installed);

        WriteModel(root.Path);
        store.Refresh();
        Assert.Equal([Fixture.Entry.Id], store.Installed.Select(model => model.Id));
        Assert.Equal(store.ModelDirectory(Fixture.Entry.Id), store.Installed[0].Folder);
        Assert.True(store.Installed[0].SizeOnDisk >= Fixture.Weights.Length);
        Assert.NotNull(store.Installed[0].Manifest);
        Assert.False(store.IsReady(Fixture.Entry));
        Assert.Equal(DownloadState.NotInstalled, store.State(Fixture.Entry));

        WriteTokenizer(root.Path);
        store.Refresh();
        Assert.True(store.IsTokenizerInstalled);
        Assert.True(store.IsReady(Fixture.Entry));
        Assert.Equal(DownloadState.Installed, store.State(Fixture.Entry));
        Assert.EndsWith(Path.Combine("_tokenizer", "openai_whisper-test"), store.TokenizerDirectory, StringComparison.Ordinal);
        Assert.True(store.TotalSizeOnDisk >= Fixture.TotalBytes);
    }

    /// <summary>Windows only: the whisper.cpp catalog lists no tokenizer files, so a model folder alone is ready.</summary>
    [Fact]
    public void EmptyTokenizerListCountsAsInstalled()
    {
        var (settings, _) = FreshSettings();
        using var store = new Store(settings, root.Path, new ModelCatalog([Fixture.Entry], Fixture.WindowsTokenizer));
        Assert.True(store.IsTokenizerInstalled);
        Assert.False(store.IsReady(Fixture.Entry));
        WriteModel(root.Path);
        store.Refresh();
        Assert.True(store.IsReady(Fixture.Entry));
        Assert.Equal(DownloadState.Installed, store.State(Fixture.Entry));
    }

    [Fact]
    public async Task UseAndDeleteClearsActiveModel()
    {
        var (settings, folder) = FreshSettings();
        WriteModel(root.Path);
        WriteTokenizer(root.Path);
        using var store = new Store(settings, root.Path, Catalog);

        store.Use(Fixture.Entry);
        Assert.Equal(Fixture.Entry.Id, store.ActiveModelId);
        Assert.Equal(Fixture.Entry.Id, ScratchSettings.Raw(folder).GetString(AppSettings.Key.ActiveModelRepo));
        Assert.Equal(Fixture.Entry.Id, new AppSettings(folder).ActiveModelRepo);

        await store.DeleteAsync(Fixture.Entry);
        Assert.Null(store.ActiveModelId);
        Assert.Null(ScratchSettings.Raw(folder).GetString(AppSettings.Key.ActiveModelRepo));
        Assert.Empty(store.Installed);
        Assert.False(Directory.Exists(store.ModelDirectory(Fixture.Entry.Id)));
        Assert.True(store.IsTokenizerInstalled, "the shared tokenizer survives deleting a model");
    }

    [Fact]
    public void UseIgnoresModelThatIsNotReady()
    {
        var (settings, _) = FreshSettings();
        using var store = new Store(settings, root.Path, Catalog);
        store.Use(Fixture.Entry);
        Assert.Null(store.ActiveModelId);
    }

    [Fact]
    public async Task StoreDownloadEndsInstalled()
    {
        var (settings, _) = FreshSettings();
        using var hub = Fixture.Hub();
        using var store = new Store(settings, root.Path, Catalog, hub, hub.BaseUri);
        store.Download(Fixture.Entry);
        Assert.IsType<DownloadState.Downloading>(store.State(Fixture.Entry));
        await WaitUntil(() => store.State(Fixture.Entry) == DownloadState.Installed && store.ActiveModelId is not null);
        Assert.Equal(DownloadState.Installed, store.State(Fixture.Entry));
        Assert.True(store.IsReady(Fixture.Entry));
        Assert.Equal(Fixture.Entry.Id, store.ActiveModelId);
        Assert.True(store.HasLocalFiles(Fixture.Entry));
    }

    [Fact]
    public async Task StoreDownloadFailureIsReported()
    {
        var (settings, _) = FreshSettings();
        using var hub = new FakeHub();
        using var store = new Store(settings, root.Path, Catalog, hub, hub.BaseUri);
        store.Download(Fixture.Entry);
        await WaitUntil(() => store.State(Fixture.Entry) is not DownloadState.Downloading);
        var failed = Assert.IsType<DownloadState.Failed>(store.State(Fixture.Entry));
        Assert.Contains("404", failed.Message, StringComparison.Ordinal);

        store.DismissFailure(Fixture.Entry);
        Assert.Equal(DownloadState.NotInstalled, store.State(Fixture.Entry));
    }

    /// <summary>Windows: deleting during a download waits for the partial file to close, then removes the folder.</summary>
    [Fact]
    public async Task DeleteDuringDownloadCancelsAndRemovesPartialFiles()
    {
        var (settings, _) = FreshSettings();
        using var hub = Fixture.Hub(new FakeHub.Behavior { HangAfterBytes = 1000 });
        using var store = new Store(settings, root.Path, new ModelCatalog([Fixture.Entry], Fixture.WindowsTokenizer), hub, hub.BaseUri);
        var partial = Path.Combine(store.ModelDirectory(Fixture.Entry.Id), "ggml-test.bin.partial");
        store.Download(Fixture.Entry);
        await WaitUntil(() => Fixture.FileSize(partial) == 1000);
        Assert.Equal(1000, Fixture.FileSize(partial));

        await store.DeleteAsync(Fixture.Entry);
        Assert.False(store.HasLocalFiles(Fixture.Entry));
        Assert.Equal(DownloadState.NotInstalled, store.State(Fixture.Entry));
        Assert.Null(store.ActiveModelId);
    }

    [Fact]
    public async Task CancelKeepsPartialFileAndClearsState()
    {
        var (settings, _) = FreshSettings();
        using var hub = Fixture.Hub(new FakeHub.Behavior { HangAfterBytes = 1000 });
        using var store = new Store(settings, root.Path, new ModelCatalog([Fixture.Entry], Fixture.WindowsTokenizer), hub, hub.BaseUri);
        var partial = Path.Combine(store.ModelDirectory(Fixture.Entry.Id), "ggml-test.bin.partial");
        store.Download(Fixture.Entry);
        await WaitUntil(() => Fixture.FileSize(partial) == 1000);

        store.CancelDownload(Fixture.Entry);
        Assert.Equal(DownloadState.NotInstalled, store.State(Fixture.Entry));
        Assert.True(store.HasLocalFiles(Fixture.Entry));
        Assert.Equal(1000, Fixture.FileSize(partial));
    }
}
