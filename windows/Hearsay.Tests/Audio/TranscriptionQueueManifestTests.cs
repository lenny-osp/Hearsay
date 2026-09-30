using System.Text.Json.Nodes;
using Hearsay.Core.Audio;
using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Port of <c>TranscriptionQueueManifestTests</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/TranscriptionQueueManifestTests.swift,
/// plus the Windows-only file-name rules.
/// </summary>
public sealed class TranscriptionQueueManifestTests : IDisposable
{
    private readonly ScratchDirectory scratch = new();
    private readonly TranscriptionQueueStore store;

    // Whole seconds: ISO 8601 keeps seconds. 1_790_750_000 = 2026-09-30T06:33:20Z.
    private static readonly DateTimeOffset StopTime = DateTimeOffset.FromUnixTimeSeconds(1_790_750_000);

    private static readonly string[] JobKeys =
        ["id", "wavFileName", "stopTime", "languageChoice", "settledLanguage", "chineseScript", "keepRecording", "state", "displayName"];

    public TranscriptionQueueManifestTests() => store = new TranscriptionQueueStore(scratch.Path);

    public void Dispose() => scratch.Dispose();

    private void TouchWav(string name) => File.WriteAllText(scratch.File(name), "RIFF");

    private static TranscriptionQueueManifest.Job Job(string id, TranscriptionJobState state = TranscriptionJobState.Waiting, string? wav = null) =>
        new(id, wav ?? id + ".wav", StopTime, LanguageChoice.Auto, KeepRecording: true, State: state);

    private void WriteManifest(string text) => File.WriteAllText(store.ManifestPath, text);

    private static IReadOnlyList<TranscriptionQueuePolicy.Job> Policy(params (string Id, TranscriptionJobState State)[] jobs) =>
        [.. jobs.Select(job => new TranscriptionQueuePolicy.Job(job.Id, job.State))];

    [Fact]
    public void MissingFileIsAnEmptyQueueWithoutError()
    {
        var result = store.Load();
        Assert.Empty(result.Manifest.Jobs);
        Assert.Equal(1, result.Manifest.Version);
        Assert.Null(result.Error);
        Assert.Empty(result.Dropped);
        Assert.Empty(store.QueuedWavFileNames());
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var first = new TranscriptionQueueManifest.Job(
            "2026-09-30_10-00-00", "2026-09-30_10-00-00.wav", StopTime,
            LanguageChoice.Fixed(TranscriptLanguage.ChineseTaiwan), TranscriptLanguage.ChineseTaiwan,
            ChineseScript.Traditional, KeepRecording: false, State: TranscriptionJobState.Waiting, DisplayName: "Weekly sync 周會");
        var second = new TranscriptionQueueManifest.Job(
            "2026-09-30_11-00-00", "2026-09-30_11-00-00.wav", StopTime.AddHours(1),
            LanguageChoice.Auto, KeepRecording: true, State: TranscriptionJobState.Done);
        TouchWav(first.WavFileName);
        TouchWav(second.WavFileName);
        store.Save(new TranscriptionQueueManifest([first, second]));

        var result = store.Load();
        Assert.Null(result.Error);
        Assert.Empty(result.Dropped);
        Assert.Equal([first, second], result.Manifest.Jobs);
        Assert.Equal(
            Policy((first.Id, TranscriptionJobState.Waiting), (second.Id, TranscriptionJobState.Done)),
            result.Manifest.PolicyJobs);
        Assert.Equal(["2026-09-30_10-00-00.wav"], store.QueuedWavFileNames());

        // The shared field names and values (the Mac reads the same layout).
        var text = File.ReadAllText(store.ManifestPath);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
        Assert.Contains("周會", text, StringComparison.Ordinal); // not \u escaped
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Equal(1, (int)(root["version"] ?? throw new InvalidOperationException()));
        var jobs = (root["jobs"] ?? throw new InvalidOperationException()).AsArray();
        var jobOne = jobs[0]?.AsObject() ?? throw new InvalidOperationException();
        var jobTwo = jobs[1]?.AsObject() ?? throw new InvalidOperationException();
        Assert.Equal(
            JobKeys.Order(StringComparer.Ordinal),
            jobTwo.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("zh-TW", (string?)jobOne["languageChoice"]);
        Assert.Equal("zh-TW", (string?)jobOne["settledLanguage"]);
        Assert.Equal("traditional", (string?)jobOne["chineseScript"]);
        Assert.Equal("waiting", (string?)jobOne["state"]);
        Assert.Equal("2026-09-30T06:33:20Z", (string?)jobOne["stopTime"]);
        Assert.Equal("auto", (string?)jobTwo["languageChoice"]);
        Assert.True(jobTwo.ContainsKey("settledLanguage") && jobTwo["settledLanguage"] is null);
        Assert.True(jobTwo.ContainsKey("displayName") && jobTwo["displayName"] is null);
    }

    [Fact]
    public void ReadsTheMacsFile()
    {
        TouchWav("a.wav");
        // What the Mac writes: " : " separators, sorted keys, escaped slashes not used.
        WriteManifest("""
        {
          "jobs" : [
            {
              "chineseScript" : "simplified",
              "displayName" : null,
              "id" : "a",
              "keepRecording" : true,
              "languageChoice" : "zh-CN",
              "settledLanguage" : "zh-CN",
              "state" : "running",
              "stopTime" : "2026-09-30T06:33:20Z",
              "wavFileName" : "a.wav"
            }
          ],
          "version" : 1
        }
        """);
        var result = store.Load();
        Assert.Null(result.Error);
        var job = Assert.Single(result.Manifest.Jobs);
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseMainland), job.LanguageChoice);
        Assert.Equal(TranscriptLanguage.ChineseMainland, job.SettledLanguage);
        Assert.Equal(ChineseScript.Simplified, job.ChineseScript);
        Assert.Equal(TranscriptionJobState.Waiting, job.State);
        Assert.Equal(StopTime, job.StopTime);
    }

    [Fact]
    public void RunningAndSuspendedJobsStartOverAsWaiting()
    {
        var jobs = new[] { Job("a", TranscriptionJobState.Running), Job("b", TranscriptionJobState.Suspended), Job("c", TranscriptionJobState.Failed) };
        foreach (var item in jobs) TouchWav(item.WavFileName);
        store.Save(new TranscriptionQueueManifest(jobs));
        Assert.Equal(
            Policy(("a", TranscriptionJobState.Waiting), ("b", TranscriptionJobState.Waiting), ("c", TranscriptionJobState.Failed)),
            store.Load().Manifest.PolicyJobs);
    }

    [Fact]
    public void SaveReplacesAtomicallyAndLeavesNoTemporaryFiles()
    {
        TouchWav("a.wav");
        TouchWav("b.wav");
        store.Save(new TranscriptionQueueManifest([Job("a"), Job("b")]));
        store.Save(new TranscriptionQueueManifest([Job("b", TranscriptionJobState.Running)]));
        Assert.Equal(["b"], store.Load().Manifest.Jobs.Select(job => job.Id));
        var names = Directory.EnumerateFileSystemEntries(scratch.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(["a.wav", "b.wav", "queue.json"], names);
    }

    [Fact]
    public void SaveCreatesTheFolder()
    {
        var nested = new TranscriptionQueueStore(scratch.File("spool"));
        nested.Save(new TranscriptionQueueManifest());
        Assert.True(File.Exists(nested.ManifestPath));
        Assert.Null(nested.Load().Error);
    }

    [Fact]
    public void SaveRefusesUnsafeJobIds()
    {
        var error = Assert.Throws<InvalidJobIdException>(
            () => store.Save(new TranscriptionQueueManifest([Job("../x", wav: "x.wav")])));
        Assert.Equal("../x", error.JobId);
        Assert.False(File.Exists(store.ManifestPath));
    }

    [Fact]
    public void CorruptFileIsAnEmptyQueueWithAnError()
    {
        TouchWav("a.wav");
        WriteManifest("{\"version\": 1, \"jobs\": [");
        var result = store.Load();
        Assert.Empty(result.Manifest.Jobs);
        Assert.Equal(QueueLoadErrorKind.Unreadable, result.Error?.Kind);
        Assert.Empty(store.QueuedWavFileNames());

        WriteManifest("[]"); // a JSON array is not a manifest
        Assert.Equal(QueueLoadErrorKind.Unreadable, store.Load().Error?.Kind);

        WriteManifest("{\"jobs\": []}"); // no version
        Assert.Equal(QueueLoadErrorKind.Unreadable, store.Load().Error?.Kind);
    }

    [Fact]
    public void UnsupportedVersionIsAnEmptyQueueWithAnError()
    {
        TouchWav("a.wav");
        WriteManifest("""
        {"version": 2, "jobs": [{"id": "a", "wavFileName": "a.wav", "stopTime": "2026-09-30T06:33:20Z",
          "languageChoice": "auto", "settledLanguage": null, "chineseScript": null,
          "keepRecording": true, "state": "waiting", "displayName": null}]}
        """);
        var result = store.Load();
        Assert.Empty(result.Manifest.Jobs);
        Assert.Equal(QueueLoadError.UnsupportedVersion(2), result.Error);
        Assert.Empty(store.QueuedWavFileNames());
    }

    [Fact]
    public void JobsWhoseWavIsMissingAreDroppedAndReported()
    {
        TouchWav("a.wav");
        TouchWav("c.wav");
        store.Save(new TranscriptionQueueManifest([Job("a"), Job("b"), Job("c")]));
        var result = store.Load();
        Assert.Null(result.Error);
        Assert.Equal(["a", "c"], result.Manifest.Jobs.Select(job => job.Id));
        Assert.Equal([new DroppedJob("b", "b.wav", DroppedJobReason.MissingRecording)], result.Dropped);
    }

    [Fact]
    public void InvalidEntriesAreDroppedOneByOne()
    {
        foreach (var name in new[] { "a.wav", "b.wav", "c.wav", "d.wav" }) TouchWav(name);
        static string Entry(string id, string wav, string state = "waiting", string language = "auto") => $$"""
            {"id": "{{id}}", "wavFileName": "{{wav}}", "stopTime": "2026-09-30T06:33:20Z",
             "languageChoice": "{{language}}", "settledLanguage": null, "chineseScript": null,
             "keepRecording": true, "state": "{{state}}", "displayName": null}
            """;
        WriteManifest($$"""
        {"version": 1, "jobs": [
          {{Entry("a", "a.wav")}},
          {{Entry("b", "b.wav", state: "paused")}},
          {{Entry("c", "c.wav", language: "fr")}},
          {{Entry("d", "../d.wav")}},
          {{Entry("a", "d.wav")}},
          {"id": 7},
          {{Entry("d", "d.wav", state: "suspended")}}
        ]}
        """);
        var result = store.Load();
        Assert.Null(result.Error);
        Assert.Equal(
            Policy(("a", TranscriptionJobState.Waiting), ("d", TranscriptionJobState.Waiting)),
            result.Manifest.PolicyJobs);
        Assert.Equal(
            [
                new DroppedJob("b", "b.wav", DroppedJobReason.InvalidEntry),
                new DroppedJob("c", "c.wav", DroppedJobReason.InvalidEntry),
                new DroppedJob("d", "../d.wav", DroppedJobReason.InvalidEntry),
                new DroppedJob("a", "d.wav", DroppedJobReason.InvalidEntry),
                new DroppedJob(null, null, DroppedJobReason.InvalidEntry),
            ],
            result.Dropped);
    }

    [Fact]
    public void LiveSegmentsRoundTripThroughSrt()
    {
        Assert.Empty(store.ReadLiveSegments("a"));
        TranscriptSegment[] segments =
        [
            new(0, 2.5, "Hello there."),
            new(2.5, 61.25, "第二行"),
        ];
        store.WriteLiveSegments(segments, "a");
        Assert.Equal("a.live.srt", Path.GetFileName(store.LiveSegmentsPath("a")));
        Assert.Equal(Srt.Render(segments), File.ReadAllText(store.LiveSegmentsPath("a")));
        Assert.Equal(segments, store.ReadLiveSegments("a"));

        // Replaced, not appended.
        store.WriteLiveSegments(segments.Take(1), "a");
        Assert.Equal(segments.Take(1), store.ReadLiveSegments("a"));
        Assert.Equal(["a.live.srt"], Directory.EnumerateFileSystemEntries(scratch.Path).Select(Path.GetFileName));

        store.RemoveLiveSegments("a");
        store.RemoveLiveSegments("a");
        Assert.Empty(store.ReadLiveSegments("a"));
        Assert.Equal("x/y", Assert.Throws<InvalidJobIdException>(() => store.WriteLiveSegments(segments, "x/y")).JobId);
    }

    [Fact]
    public void PlainFileNames()
    {
        Assert.True(TranscriptionQueueManifest.IsPlainFileName("2026-09-30_10-00-00"));
        Assert.True(TranscriptionQueueManifest.IsPlainFileName("2026-09-30_10-00-00-2.wav"));
        foreach (var bad in new[] { "", ".", "..", ".hidden", "a/b", "../a", "a\0b" })
        {
            Assert.False(TranscriptionQueueManifest.IsPlainFileName(bad), bad);
        }
        Assert.False(TranscriptionQueueManifest.IsPlainFileName(null));
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("..\\a")]
    [InlineData("C:a")]
    [InlineData("a*")]
    public void WindowsPathCharactersAreNotPlainFileNames(string name)
    {
        // Windows-only addition: the Mac rejects only "/" and NUL.
        Assert.False(TranscriptionQueueManifest.IsPlainFileName(name));
    }

    [Fact]
    public void StoreCanBeBuiltFromTheSpool()
    {
        var fromSpool = new TranscriptionQueueStore(new RecordingSpool(scratch.Path));
        Assert.Equal(store.ManifestPath, fromSpool.ManifestPath);
    }
}
