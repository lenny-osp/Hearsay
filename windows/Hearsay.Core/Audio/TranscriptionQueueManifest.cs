using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearsay.Core.Naming;
using Hearsay.Core.Transcription;

namespace Hearsay.Core.Audio;

/// <summary>
/// The persisted background transcription queue, <c>&lt;spool&gt;\queue.json</c>
/// (PLAN.md 4.9 "Persistence and recovery" and 18.10). Jobs are in queue
/// order. Port of <c>TranscriptionQueueManifest</c> in
/// mac/HearsayCore/Sources/HearsayCore/Audio/TranscriptionQueueManifest.swift;
/// the JSON is the Mac's layout, so the format is shared.
/// <para>
/// JSON layout (<c>version</c> 1): <c>{"version": 1, "jobs": [{"id",
/// "wavFileName", "stopTime" (ISO 8601, UTC, whole seconds),
/// "languageChoice" ("auto" or a transcript language code), "settledLanguage"
/// (a language code or null), "chineseScript" ("traditional", "simplified",
/// or null), "keepRecording", "state" (a <see cref="TranscriptionJobState"/>
/// stored value), "displayName" (or null)}]}</c>. Every field is always
/// written; a null is written as <c>null</c>.
/// </para>
/// </summary>
public sealed class TranscriptionQueueManifest
{
    public const string FileName = "queue.json";
    public const int CurrentVersion = 1;

    /// <summary>One queued recording.</summary>
    /// <param name="Id">Plain file-name-safe string; also names <c>&lt;id&gt;.live.srt</c>.</param>
    /// <param name="WavFileName">The spool WAV's file name (no folder), for example <c>2026-09-30_10-00-00.wav</c>.</param>
    /// <param name="StopTime">When the recording was stopped (stored to whole seconds).</param>
    /// <param name="LanguageChoice">The language choice the session was recorded with.</param>
    /// <param name="SettledLanguage">The language the session settled on, or null when not settled yet.</param>
    /// <param name="ChineseScript">The Chinese script for the transcript, or null.</param>
    /// <param name="KeepRecording">Keep the WAV in the output folder after success.</param>
    /// <param name="State">Where the job is in the queue.</param>
    /// <param name="DisplayName">The meeting name the user gave the recording, if any.</param>
    public sealed record Job(
        string Id,
        string WavFileName,
        DateTimeOffset StopTime,
        LanguageChoice LanguageChoice,
        TranscriptLanguage? SettledLanguage = null,
        ChineseScript? ChineseScript = null,
        bool KeepRecording = true,
        TranscriptionJobState State = TranscriptionJobState.Waiting,
        string? DisplayName = null);

    public TranscriptionQueueManifest(IEnumerable<Job>? jobs = null)
    {
        Jobs = jobs is null ? [] : [.. jobs];
    }

    public int Version { get; } = CurrentVersion;

    public IReadOnlyList<Job> Jobs { get; }

    /// <summary>The jobs as <see cref="TranscriptionQueuePolicy"/> reads them.</summary>
    public IReadOnlyList<TranscriptionQueuePolicy.Job> PolicyJobs =>
        [.. Jobs.Select(job => new TranscriptionQueuePolicy.Job(job.Id, job.State))];

    /// <summary>
    /// True for a plain file name: not empty, not <c>.</c> or <c>..</c>, not
    /// hidden, and none of the characters Windows forbids in a file name
    /// (which includes <c>/</c>, <c>\</c>, <c>:</c> and NUL). The Mac's rule
    /// excludes only <c>/</c> and NUL; Windows also has <c>\</c> as a path
    /// separator. Job ids and WAV names must be one.
    /// </summary>
    public static bool IsPlainFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name != "." && name != ".." && !name.StartsWith('.')
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

/// <summary>Why <see cref="TranscriptionQueueStore.Load"/> returned an empty queue although a file was there.</summary>
public readonly record struct QueueLoadError(QueueLoadErrorKind Kind, string? Reason = null, int? Version = null)
{
    /// <summary>The file could not be read or is not a queue manifest.</summary>
    public static QueueLoadError Unreadable(string reason) => new(QueueLoadErrorKind.Unreadable, reason);

    /// <summary>The manifest's <c>version</c> is not one this build reads.</summary>
    public static QueueLoadError UnsupportedVersion(int version) => new(QueueLoadErrorKind.UnsupportedVersion, null, version);

    public override string ToString() => Kind == QueueLoadErrorKind.Unreadable
        ? $"queue.json is unreadable: {Reason}"
        : $"queue.json has unsupported version {Version}";
}

public enum QueueLoadErrorKind
{
    Unreadable,
    UnsupportedVersion,
}

/// <summary>Why <see cref="TranscriptionQueueStore.Load"/> left a job out.</summary>
public enum DroppedJobReason
{
    /// <summary>Its WAV is no longer in the spool folder.</summary>
    MissingRecording,

    /// <summary>The entry could not be decoded or names an unsafe file.</summary>
    InvalidEntry,
}

/// <summary>A job <see cref="TranscriptionQueueStore.Load"/> left out. Id and WavFileName are set when the entry had a readable one.</summary>
public readonly record struct DroppedJob(string? Id, string? WavFileName, DroppedJobReason Reason);

/// <summary>
/// What <see cref="TranscriptionQueueStore.Load"/> found. <c>Manifest</c> holds
/// the jobs to queue again, in order; a job that was running or suspended is
/// waiting again (it starts over, PLAN.md 4.9). <c>Error</c> is set when the
/// file existed but could not be used; the caller logs it. <c>Dropped</c> are
/// the jobs left out, for the caller to log.
/// </summary>
public sealed record QueueLoadResult(
    TranscriptionQueueManifest Manifest, QueueLoadError? Error, IReadOnlyList<DroppedJob> Dropped);

/// <summary>A job id that is not a plain file name, refused by <see cref="TranscriptionQueueStore"/>.</summary>
public sealed class InvalidJobIdException(string jobId)
    : ArgumentException($"'{jobId}' is not a plain file name and cannot be a job id.", nameof(jobId))
{
    public string JobId { get; } = jobId;
}

/// <summary>
/// Reads and writes the queue manifest and the per-job live segments in the
/// spool folder. Port of <c>TranscriptionQueueStore</c> in
/// TranscriptionQueueManifest.swift. Files are replaced atomically with
/// <see cref="OutputWriter.WriteReplacing"/>, so a reader sees the old or
/// the new file, never a partial one.
/// </summary>
public sealed class TranscriptionQueueStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public TranscriptionQueueStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
    }

    public TranscriptionQueueStore(RecordingSpool spool)
        : this((spool ?? throw new ArgumentNullException(nameof(spool))).Root)
    {
    }

    public string Root { get; }

    public string ManifestPath => Path.Combine(Root, TranscriptionQueueManifest.FileName);

    /// <summary><c>&lt;root&gt;\&lt;id&gt;.live.srt</c>.</summary>
    public string LiveSegmentsPath(string jobId) => Path.Combine(Root, jobId + ".live.srt");

    // Manifest

    /// <summary>
    /// Writes the manifest, replacing the old one atomically. Creates the
    /// folder. Throws <see cref="InvalidJobIdException"/> (before writing
    /// anything) for a job id that is not a plain file name.
    /// </summary>
    public void Save(TranscriptionQueueManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var job in manifest.Jobs)
        {
            if (!TranscriptionQueueManifest.IsPlainFileName(job.Id)) throw new InvalidJobIdException(job.Id);
        }
        var jobs = new JsonArray();
        foreach (var job in manifest.Jobs)
        {
            // Keys in alphabetical order, as the Mac writes them.
            jobs.Add(new JsonObject
            {
                ["chineseScript"] = job.ChineseScript is { } script ? ScriptValue(script) : null,
                ["displayName"] = job.DisplayName,
                ["id"] = job.Id,
                ["keepRecording"] = job.KeepRecording,
                ["languageChoice"] = job.LanguageChoice.StorageValue,
                ["settledLanguage"] = job.SettledLanguage?.Code(),
                ["state"] = job.State.StorageValue(),
                ["stopTime"] = job.StopTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["wavFileName"] = job.WavFileName,
            });
        }
        var root = new JsonObject { ["jobs"] = jobs, ["version"] = manifest.Version };
        Directory.CreateDirectory(Root);
        OutputWriter.WriteReplacing(root.ToJsonString(WriteOptions) + "\n", ManifestPath);
    }

    /// <summary>
    /// Reads the manifest. A missing file is an empty queue with no error.
    /// An unreadable file or an unknown version is an empty queue plus an
    /// error. Entries that cannot be decoded, name an unsafe file, repeat an
    /// id, or whose WAV is missing are dropped and reported; running and
    /// suspended jobs come back as waiting.
    /// </summary>
    public QueueLoadResult Load()
    {
        var empty = new TranscriptionQueueManifest();
        if (!File.Exists(ManifestPath)) return new QueueLoadResult(empty, null, []);

        JsonObject document;
        int version;
        JsonArray entries;
        try
        {
            var text = File.ReadAllText(ManifestPath, Utf8NoBom);
            document = JsonNode.Parse(text) as JsonObject
                ?? throw new JsonException("the top level is not an object");
            version = document["version"] is JsonValue versionValue && versionValue.TryGetValue<int>(out var parsed)
                ? parsed
                : throw new JsonException("\"version\" is missing or not an integer");
            entries = document["jobs"] switch
            {
                null => [],
                JsonArray array => array,
                _ => throw new JsonException("\"jobs\" is not an array"),
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new QueueLoadResult(empty, QueueLoadError.Unreadable(error.Message), []);
        }
        if (version != TranscriptionQueueManifest.CurrentVersion)
        {
            return new QueueLoadResult(empty, QueueLoadError.UnsupportedVersion(version), []);
        }

        var jobs = new List<TranscriptionQueueManifest.Job>();
        var dropped = new List<DroppedJob>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var job = DecodeJob(entry);
            if (job is null
                || !TranscriptionQueueManifest.IsPlainFileName(job.Id)
                || !TranscriptionQueueManifest.IsPlainFileName(job.WavFileName)
                || seenIds.Contains(job.Id))
            {
                dropped.Add(new DroppedJob(ReadString(entry, "id"), ReadString(entry, "wavFileName"), DroppedJobReason.InvalidEntry));
                continue;
            }
            if (!File.Exists(Path.Combine(Root, job.WavFileName)))
            {
                dropped.Add(new DroppedJob(job.Id, job.WavFileName, DroppedJobReason.MissingRecording));
                continue;
            }
            if (job.State is TranscriptionJobState.Running or TranscriptionJobState.Suspended)
            {
                job = job with { State = TranscriptionJobState.Waiting };
            }
            seenIds.Add(job.Id);
            jobs.Add(job);
        }
        return new QueueLoadResult(new TranscriptionQueueManifest(jobs), null, dropped);
    }

    /// <summary>
    /// WAV file names (in the spool folder) of the jobs <see cref="Load"/>
    /// returns that are not transcribed yet (waiting, running, or suspended).
    /// Crash recovery (<see cref="RecordingSpool.UnfinishedRecordings"/>) does
    /// not offer them: the queue continues with them. Empty when the manifest
    /// is missing or unusable, so those WAVs are offered as before. Compared
    /// without regard to case (NTFS).
    /// </summary>
    public IReadOnlySet<string> QueuedWavFileNames() =>
        Load().Manifest.Jobs
            .Where(job => job.State.IsPending())
            .Select(job => job.WavFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Live segments

    /// <summary>Writes a job's live segments as SRT, replacing the file atomically. Creates the folder.</summary>
    public void WriteLiveSegments(IEnumerable<TranscriptSegment> segments, string jobId)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!TranscriptionQueueManifest.IsPlainFileName(jobId)) throw new InvalidJobIdException(jobId);
        Directory.CreateDirectory(Root);
        OutputWriter.WriteReplacing(Srt.Render(segments), LiveSegmentsPath(jobId));
    }

    /// <summary>A job's live segments; empty when the file is missing or unreadable.</summary>
    public IReadOnlyList<TranscriptSegment> ReadLiveSegments(string jobId)
    {
        if (!TranscriptionQueueManifest.IsPlainFileName(jobId)) return [];
        try
        {
            return Srt.Parse(File.ReadAllText(LiveSegmentsPath(jobId), Utf8NoBom));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Removes a job's live-segments file; no error when it is missing.</summary>
    public void RemoveLiveSegments(string jobId)
    {
        if (!TranscriptionQueueManifest.IsPlainFileName(jobId)) return;
        try
        {
            File.Delete(LiveSegmentsPath(jobId));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Coding

    private static string ScriptValue(ChineseScript script) => script switch
    {
        ChineseScript.Traditional => "traditional",
        ChineseScript.Simplified => "simplified",
        _ => throw new ArgumentOutOfRangeException(nameof(script), script, null),
    };

    private static string? ReadString(JsonNode? entry, string key) =>
        entry is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Decodes one entry like the Mac's synthesized decoder: required keys
    /// must have the right type, optional ones may be missing or null, and
    /// an unknown enum value rejects the entry. Returns null when invalid.
    /// </summary>
    private static TranscriptionQueueManifest.Job? DecodeJob(JsonNode? entry)
    {
        if (entry is not JsonObject obj) return null;
        if (ReadString(obj, "id") is not { } id) return null;
        if (ReadString(obj, "wavFileName") is not { } wav) return null;
        if (ReadString(obj, "stopTime") is not { } stopText
            || !DateTimeOffset.TryParse(stopText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stopTime))
        {
            return null;
        }
        if (ReadString(obj, "languageChoice") is not { } choiceText
            || LanguageChoice.FromStorageValue(choiceText) is not { } choice)
        {
            return null;
        }
        TranscriptLanguage? settled = null;
        if (obj["settledLanguage"] is { } settledNode)
        {
            if (settledNode is not JsonValue settledValue || !settledValue.TryGetValue<string>(out var settledText)
                || TranscriptLanguages.FromCode(settledText) is not { } language)
            {
                return null;
            }
            settled = language;
        }
        ChineseScript? script = null;
        if (obj["chineseScript"] is { } scriptNode)
        {
            if (scriptNode is not JsonValue scriptValue || !scriptValue.TryGetValue<string>(out var scriptText))
            {
                return null;
            }
            script = scriptText switch
            {
                "traditional" => ChineseScript.Traditional,
                "simplified" => ChineseScript.Simplified,
                _ => null,
            };
            if (script is null) return null;
        }
        if (obj["keepRecording"] is not JsonValue keepValue || !keepValue.TryGetValue<bool>(out var keep)) return null;
        if (TranscriptionJobStates.FromStorageValue(ReadString(obj, "state")) is not { } state) return null;
        string? displayName = null;
        if (obj["displayName"] is { } nameNode)
        {
            if (nameNode is not JsonValue nameValue || !nameValue.TryGetValue<string>(out displayName)) return null;
        }
        return new TranscriptionQueueManifest.Job(id, wav, stopTime, choice, settled, script, keep, state, displayName);
    }
}
