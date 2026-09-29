using System.ComponentModel;
using System.Text.Json;
using Hearsay.Core.Settings;

namespace Hearsay.Core.ModelStore;

/// <summary>
/// Where a catalog entry stands on this PC. Port of <c>DownloadState</c> in
/// mac/HearsayCore/Sources/HearsayCore/ModelStore/ModelStore.swift.
/// </summary>
public abstract record DownloadState
{
    private DownloadState()
    {
    }

    public static DownloadState NotInstalled { get; } = new NotInstalledState();

    public static DownloadState Installed { get; } = new InstalledState();

    public sealed record NotInstalledState : DownloadState;

    public sealed record Downloading(DownloadProgress Progress) : DownloadState;

    public sealed record InstalledState : DownloadState;

    public sealed record Failed(string Message) : DownloadState;
}

/// <summary>
/// A model folder that holds the entry's files and <c>manifest.json</c>.
/// Port of <c>InstalledModel</c> in ModelStore.swift; <see cref="Id"/> is the
/// entry id (<c>repo/weightsFile</c>) where the Mac has the repo.
/// </summary>
public sealed record InstalledModel(string Id, string Folder, long SizeOnDisk, ModelManifest? Manifest);

/// <summary>
/// Installed models, downloads, deletion, and the active model (PLAN.md
/// section 5). Port of mac/HearsayCore/Sources/HearsayCore/ModelStore/ModelStore.swift.
/// The models live under a root the app passes (<see cref="DefaultRootPath"/>,
/// <c>%LOCALAPPDATA%\Hearsay\Models</c>); tests pass a scratch folder. The
/// active model is <see cref="AppSettings.ActiveModelRepo"/>, which holds the
/// entry id.
/// <para>
/// Use it from the UI thread, as the Mac's <c>@MainActor</c> class is: a
/// download started there reports progress and its outcome on that thread's
/// <see cref="SynchronizationContext"/> (without one, on a pool thread; the
/// state is locked either way). <see cref="PropertyChanged"/> fires for
/// <see cref="Installed"/>, <see cref="IsTokenizerInstalled"/> and
/// <see cref="TotalSizeOnDisk"/>; <see cref="StateChanged"/> fires with the
/// entry id when a download state changes.
/// </para>
/// <para>
/// Windows differences: an empty tokenizer file list (the whisper.cpp
/// catalog) counts as installed, where the Mac requires at least one file;
/// and deleting is <see cref="DeleteAsync"/>, which first waits for a
/// cancelled download to close its partial file, because Windows cannot
/// delete a file that is still open.
/// </para>
/// </summary>
public sealed class ModelStore : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings settings;
    private readonly ModelDownloader downloader;
    private readonly Lock gate = new();

    /// <summary>Transient states (downloading, failed) by entry id; everything else is derived from <see cref="Installed"/>.</summary>
    private readonly Dictionary<string, DownloadState> transient = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Running> tasks = new(StringComparer.Ordinal);

    /// <summary>Cancelled downloads that have not finished yet, by entry id.</summary>
    private readonly Dictionary<string, List<Task>> stopping = new(StringComparer.Ordinal);

    private IReadOnlyList<InstalledModel> installed = [];
    private bool isTokenizerInstalled;
    private long totalSizeOnDisk;

    /// <param name="settings">Holds the active model.</param>
    /// <param name="rootPath">Models folder; the app passes <see cref="DefaultRootPath"/>.</param>
    /// <param name="catalog">Null reads the embedded catalog.</param>
    /// <param name="handler">HTTP handler for the downloader; tests pass a fake.</param>
    /// <param name="baseUri">Server root; null means Hugging Face.</param>
    public ModelStore(
        AppSettings settings,
        string rootPath,
        ModelCatalog? catalog = null,
        HttpMessageHandler? handler = null,
        Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        string? loadError = null;
        ModelCatalog resolved;
        if (catalog is not null)
        {
            resolved = catalog;
        }
        else
        {
            try
            {
                resolved = ModelCatalog.Bundled();
            }
            catch (ModelCatalogException error)
            {
                loadError = $"The built-in model list could not be read: {error.Message}";
                resolved = new ModelCatalog([], new TokenizerSource { Repo = "ggerganov/whisper.cpp", Files = [] });
            }
        }
        Catalog = resolved;
        CatalogError = loadError;
        RootPath = Path.GetFullPath(rootPath);
        this.settings = settings;
        downloader = new ModelDownloader(RootPath, resolved.Tokenizer, handler, baseUri);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The download state of the entry with this id changed.</summary>
    public event EventHandler<string>? StateChanged;

    /// <summary><c>%LOCALAPPDATA%\Hearsay\Models</c> (PLAN.md 18.3).</summary>
    public static string DefaultRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearsay", "Models");

    public ModelCatalog Catalog { get; }

    public string RootPath { get; }

    /// <summary>Set when the embedded catalog could not be read.</summary>
    public string? CatalogError { get; }

    public IReadOnlyList<InstalledModel> Installed
    {
        get
        {
            lock (gate)
            {
                return installed;
            }
        }
    }

    public bool IsTokenizerInstalled
    {
        get
        {
            lock (gate)
            {
                return isTokenizerInstalled;
            }
        }
    }

    /// <summary>Size of everything under <see cref="RootPath"/>, including partial downloads.</summary>
    public long TotalSizeOnDisk
    {
        get
        {
            lock (gate)
            {
                return totalSizeOnDisk;
            }
        }
    }

    // Layout

    public string ModelDirectory(string id) => ModelDownloader.ModelDirectory(id, RootPath);

    public string TokenizerDirectory => ModelDownloader.TokenizerDirectory(Catalog.Tokenizer, RootPath);

    // Active model

    /// <summary>Id of the active model, or null (stored as <c>activeModelRepo</c>).</summary>
    public string? ActiveModelId
    {
        get => settings.ActiveModelRepo;
        set => settings.ActiveModelRepo = value;
    }

    public ModelCatalogEntry? ActiveEntry => ActiveModelId is { } id ? Catalog.Entry(id) : null;

    /// <summary>Makes <paramref name="entry"/> the active model when it is ready to load.</summary>
    public void Use(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsReady(entry))
        {
            return;
        }
        ActiveModelId = entry.Id;
    }

    // State

    public bool IsInstalled(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Installed.Any(model => model.Id == entry.Id);
    }

    /// <summary>Model files and the shared tokenizer are both on disk.</summary>
    public bool IsReady(ModelCatalogEntry entry) => IsInstalled(entry) && IsTokenizerInstalled;

    public DownloadState State(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            if (transient.TryGetValue(entry.Id, out var state))
            {
                return state;
            }
        }
        return IsReady(entry) ? DownloadState.Installed : DownloadState.NotInstalled;
    }

    /// <summary>Rescans the models folder.</summary>
    public void Refresh()
    {
        var models = new List<InstalledModel>();
        foreach (var entry in Catalog.Entries)
        {
            var folder = ModelDirectory(entry.Id);
            var required = entry.Files.Append(ModelManifest.FileName);
            if (!required.All(name => File.Exists(Path.Combine(folder, name))))
            {
                continue;
            }
            models.Add(new InstalledModel(entry.Id, folder, DirectorySize(folder), ReadManifest(folder)));
        }
        var tokenizerFiles = Catalog.Tokenizer.Files;
        var tokenizerReady = tokenizerFiles.All(name => File.Exists(Path.Combine(TokenizerDirectory, name)));
        var total = DirectorySize(RootPath);
        lock (gate)
        {
            installed = models;
            isTokenizerInstalled = tokenizerReady;
            totalSizeOnDisk = total;
        }
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsTokenizerInstalled));
        OnPropertyChanged(nameof(TotalSizeOnDisk));
    }

    // Download, cancel, delete

    /// <summary>
    /// Starts downloading <paramref name="entry"/> unless it is already
    /// downloading. The state is <see cref="DownloadState.Downloading"/> until
    /// it ends installed (the first model downloaded becomes active),
    /// <see cref="DownloadState.Failed"/>, or cancelled.
    /// </summary>
    public void Download(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = entry.Id;
        var running = new Running(new CancellationTokenSource());
        lock (gate)
        {
            if (tasks.ContainsKey(id))
            {
                return;
            }
            tasks[id] = running;
            transient[id] = new DownloadState.Downloading(new DownloadProgress(0, entry.SizeBytes, ""));
        }
        StateChanged?.Invoke(this, id);
        var context = SynchronizationContext.Current;
        var progress = new Reporter(context, value =>
        {
            lock (gate)
            {
                if (!tasks.TryGetValue(id, out var current) || current != running)
                {
                    return;
                }
                transient[id] = new DownloadState.Downloading(value);
            }
            StateChanged?.Invoke(this, id);
        });
        running.Task = RunAsync(entry, running, progress);
    }

    private async Task RunAsync(ModelCatalogEntry entry, Running running, IProgress<DownloadProgress> progress)
    {
        // Leave the caller's frame first, so Download returns in the downloading state.
        await Task.Yield();
        var id = entry.Id;
        DownloadState? outcome;
        try
        {
            await downloader.DownloadAsync(entry, progress, running.Cancellation.Token);
            outcome = null;
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            outcome = null;
        }
#pragma warning disable CA1031 // Every failure becomes the row's message, as on the Mac.
        catch (Exception error)
#pragma warning restore CA1031
        {
            outcome = new DownloadState.Failed(error.Message);
        }
        lock (gate)
        {
            if (!tasks.TryGetValue(id, out var current) || current != running)
            {
                return;
            }
            tasks.Remove(id);
            if (outcome is null)
            {
                transient.Remove(id);
            }
            else
            {
                transient[id] = outcome;
            }
        }
        running.Cancellation.Dispose();
        Refresh();
        StateChanged?.Invoke(this, id);
        // The first model the user downloads becomes the active one.
        if (ActiveModelId is null && IsReady(entry))
        {
            ActiveModelId = id;
        }
    }

    /// <summary>
    /// Stops a download. Partial files stay on disk so the next download
    /// resumes where this one stopped.
    /// </summary>
    public void CancelDownload(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = entry.Id;
        Running? running;
        lock (gate)
        {
            if (!tasks.Remove(id, out running))
            {
                return;
            }
            transient.Remove(id);
            if (running.Task is { } task)
            {
                if (!stopping.TryGetValue(id, out var list))
                {
                    stopping[id] = list = [];
                }
                list.Add(task);
                _ = task.ContinueWith(finished =>
                {
                    lock (gate)
                    {
                        if (stopping.TryGetValue(id, out var pending) && pending.Remove(finished) && pending.Count == 0)
                        {
                            stopping.Remove(id);
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        running.Cancellation.Cancel();
        Refresh();
        StateChanged?.Invoke(this, id);
    }

    /// <summary>
    /// Removes the model folder, including partial files. The shared
    /// tokenizer stays. Deleting the active model clears the choice. A
    /// running download is cancelled and awaited first so its partial file
    /// is closed.
    /// </summary>
    /// <exception cref="IOException">The folder could not be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder could not be removed.</exception>
    /// <exception cref="TimeoutException">A cancelled download did not stop within 30 s.</exception>
    public async Task DeleteAsync(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CancelDownload(entry);
        Task[] pending;
        lock (gate)
        {
            transient.Remove(entry.Id);
            pending = stopping.TryGetValue(entry.Id, out var list) ? [.. list] : [];
        }
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        }
        var folder = ModelDirectory(entry.Id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        if (ActiveModelId == entry.Id)
        {
            ActiveModelId = null;
        }
        Refresh();
        StateChanged?.Invoke(this, entry.Id);
    }

    /// <summary>Whether the model folder exists at all, including partial downloads.</summary>
    public bool HasLocalFiles(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Directory.Exists(ModelDirectory(entry.Id));
    }

    /// <summary>Clears a failed state so the row offers Download again.</summary>
    public void DismissFailure(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool changed;
        lock (gate)
        {
            changed = transient.TryGetValue(entry.Id, out var state) && state is DownloadState.Failed
                && transient.Remove(entry.Id);
        }
        if (changed)
        {
            StateChanged?.Invoke(this, entry.Id);
        }
    }

    /// <summary>Cancels every running download.</summary>
    public void Dispose()
    {
        Running[] running;
        lock (gate)
        {
            running = [.. tasks.Values];
            tasks.Clear();
        }
        foreach (var item in running)
        {
            item.Cancellation.Cancel();
        }
        downloader.Dispose();
    }

    // Helpers

    /// <summary>Total size of the regular files under <paramref name="path"/>, hidden ones included; 0 when it does not exist.</summary>
    internal static long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try
                {
                    total += file.Length;
                }
                catch (FileNotFoundException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        return total;
    }

    private static ModelManifest? ReadManifest(string folder)
    {
        try
        {
            return ModelManifest.Decode(File.ReadAllText(Path.Combine(folder, ModelManifest.FileName)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class Running(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? Task { get; set; }
    }

    /// <summary>Reports on the context the download was started from, or inline without one.</summary>
    private sealed class Reporter(SynchronizationContext? context, Action<DownloadProgress> handler) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            if (context is null)
            {
                handler(value);
            }
            else
            {
                context.Post(_ => handler(value), null);
            }
        }
    }
}
