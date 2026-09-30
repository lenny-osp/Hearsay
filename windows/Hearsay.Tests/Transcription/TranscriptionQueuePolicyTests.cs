using System.Text.Json;
using System.Text.Json.Serialization;
using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Transcription;

/// <summary>
/// Runs every vector in shared/transcription-queue-tests.json and ports
/// <c>TranscriptionQueuePolicyVectorTests</c> and
/// <c>TranscriptionQueuePolicyTests</c> from
/// mac/HearsayCore/Tests/HearsayCoreTests/TranscriptionQueuePolicyTests.swift.
/// The "invalid" vectors are tolerated inputs with defined answers (see the
/// file's "about"), run like any other.
/// </summary>
public sealed class TranscriptionQueuePolicyVectorTests
{
    private sealed record VJob(string Id, string State);

    private sealed record NextExpect(string Action, string? Job);

    private sealed record NextCase(string Note, string Timing, bool SessionActive, int ForegroundWaiting, List<VJob> Jobs, NextExpect Expect);

    private sealed record YieldCase(string Timing, bool SessionActive, int ForegroundWaiting, bool Expect);

    private sealed record NotesCase(string Note, bool SessionActive, bool NotesOnScreen, bool Expect);

    private sealed record CountCase(string Note, List<VJob> Jobs, int Expect);

    private sealed record BlockCase(string Note, List<VJob> Jobs, bool Expect);

    private sealed record Vectors(
        string About, List<NextCase> Next, List<YieldCase> ShouldYield, List<NotesCase> PresentsNotes,
        List<CountCase> QuitNeedsConfirmation, List<BlockCase> BlocksUpdateInstall)
    {
        public IEnumerable<VJob> AllJobs =>
            Next.SelectMany(c => c.Jobs).Concat(QuitNeedsConfirmation.SelectMany(c => c.Jobs)).Concat(BlocksUpdateInstall.SelectMany(c => c.Jobs));

        public IEnumerable<string> AllTimings => Next.Select(c => c.Timing).Concat(ShouldYield.Select(c => c.Timing));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] Actions = ["start", "resume", "suspend", "none"];

    private static Vectors Load()
    {
        // SharedFiles.ReadText keeps the file's newlines untouched.
        var vectors = JsonSerializer.Deserialize<Vectors>(SharedFiles.ReadText("transcription-queue-tests.json"), Options);
        Assert.NotNull(vectors);
        return vectors;
    }

    private static IReadOnlyList<TranscriptionQueuePolicy.Job> PolicyJobs(IEnumerable<VJob> jobs) =>
        [.. jobs.Select(job => new TranscriptionQueuePolicy.Job(
            job.Id,
            TranscriptionJobStates.FromStorageValue(job.State) ?? throw new Xunit.Sdk.XunitException($"unknown state {job.State}")))];

    private static FinalPassTiming Timing(string raw) =>
        FinalPassTimings.FromStorageValue(raw) ?? throw new Xunit.Sdk.XunitException($"unknown timing {raw}");

    // Every vector is a theory row addressed by index, so a failure names it.
    public static IEnumerable<object[]> NextRows() => Enumerable.Range(0, Load().Next.Count).Select(i => new object[] { i });

    public static IEnumerable<object[]> YieldRows() => Enumerable.Range(0, Load().ShouldYield.Count).Select(i => new object[] { i });

    public static IEnumerable<object[]> NotesRows() => Enumerable.Range(0, Load().PresentsNotes.Count).Select(i => new object[] { i });

    public static IEnumerable<object[]> QuitRows() => Enumerable.Range(0, Load().QuitNeedsConfirmation.Count).Select(i => new object[] { i });

    public static IEnumerable<object[]> BlockRows() => Enumerable.Range(0, Load().BlocksUpdateInstall.Count).Select(i => new object[] { i });

    [Fact]
    public void VectorsLoadAndCoverEveryFunction()
    {
        var vectors = Load();
        Assert.False(string.IsNullOrEmpty(vectors.About));
        Assert.Equal(41, vectors.Next.Count);
        Assert.Equal(12, vectors.ShouldYield.Count);
        Assert.Equal(4, vectors.PresentsNotes.Count);
        Assert.NotEmpty(vectors.QuitNeedsConfirmation);
        Assert.NotEmpty(vectors.BlocksUpdateInstall);
        Assert.Equal(
            new HashSet<string> { "start", "resume", "suspend", "none" },
            vectors.Next.Select(c => c.Expect.Action).ToHashSet());
        Assert.Contains(vectors.Next, c => c.Note.StartsWith("invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRawValueInTheVectorsIsKnownAndExercised()
    {
        var vectors = Load();
        foreach (var job in vectors.AllJobs)
        {
            Assert.NotNull(TranscriptionJobStates.FromStorageValue(job.State));
        }
        foreach (var raw in vectors.AllTimings)
        {
            Assert.NotNull(FinalPassTimings.FromStorageValue(raw));
        }
        foreach (var vector in vectors.Next)
        {
            Assert.Contains(vector.Expect.Action, Actions);
        }
        Assert.Equal(
            TranscriptionJobStates.All.Select(s => s.StorageValue()).Order(StringComparer.Ordinal),
            vectors.AllJobs.Select(j => j.State).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            FinalPassTimings.All.Select(t => t.StorageValue()).Order(StringComparer.Ordinal),
            vectors.AllTimings.Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NextRows))]
    public void Next(int index)
    {
        var vector = Load().Next[index];
        var expected = vector.Expect.Action switch
        {
            "start" => TranscriptionQueuePolicy.Decision.Start(vector.Expect.Job ?? throw new Xunit.Sdk.XunitException(vector.Note)),
            "resume" => TranscriptionQueuePolicy.Decision.Resume(vector.Expect.Job ?? throw new Xunit.Sdk.XunitException(vector.Note)),
            "suspend" => TranscriptionQueuePolicy.Decision.Suspend(vector.Expect.Job ?? throw new Xunit.Sdk.XunitException(vector.Note)),
            "none" => TranscriptionQueuePolicy.Decision.None,
            var other => throw new Xunit.Sdk.XunitException($"unknown action {other}"),
        };
        if (vector.Expect.Action == "none")
        {
            Assert.Null(vector.Expect.Job);
        }
        var decision = TranscriptionQueuePolicy.Next(
            Timing(vector.Timing), vector.SessionActive, vector.ForegroundWaiting, PolicyJobs(vector.Jobs));
        Assert.True(expected == decision, $"{vector.Note}: expected {expected}, got {decision}");
    }

    [Theory]
    [MemberData(nameof(YieldRows))]
    public void ShouldYield(int index)
    {
        var vector = Load().ShouldYield[index];
        var timing = Timing(vector.Timing);
        var yields = TranscriptionQueuePolicy.ShouldYield(timing, vector.SessionActive, vector.ForegroundWaiting);
        Assert.Equal(vector.Expect, yields);
        // True exactly when Next suspends a running job.
        var decision = TranscriptionQueuePolicy.Next(timing, vector.SessionActive, vector.ForegroundWaiting,
        [
            new("r", TranscriptionJobState.Running), new("w", TranscriptionJobState.Waiting),
        ]);
        Assert.Equal(yields, decision == TranscriptionQueuePolicy.Decision.Suspend("r"));
    }

    [Theory]
    [MemberData(nameof(NotesRows))]
    public void PresentsNotes(int index)
    {
        var vector = Load().PresentsNotes[index];
        Assert.Equal(vector.Expect, TranscriptionQueuePolicy.PresentsNotes(vector.SessionActive, vector.NotesOnScreen));
    }

    [Theory]
    [MemberData(nameof(QuitRows))]
    public void QuitNeedsConfirmation(int index)
    {
        var vector = Load().QuitNeedsConfirmation[index];
        Assert.Equal(vector.Expect, TranscriptionQueuePolicy.QuitNeedsConfirmation(PolicyJobs(vector.Jobs)));
    }

    [Theory]
    [MemberData(nameof(BlockRows))]
    public void BlocksUpdateInstall(int index)
    {
        var vector = Load().BlocksUpdateInstall[index];
        Assert.Equal(vector.Expect, TranscriptionQueuePolicy.BlocksUpdateInstall(PolicyJobs(vector.Jobs)));
    }
}

public sealed class TranscriptionQueuePolicyTests
{
    [Fact]
    public void RawValuesAreTheSharedStrings()
    {
        Assert.Equal(["immediate", "whenIdle"], FinalPassTimings.All.Select(t => t.StorageValue()));
        Assert.Equal(
            ["waiting", "running", "suspended", "done", "failed"],
            TranscriptionJobStates.All.Select(s => s.StorageValue()));
        Assert.Equal(
            [TranscriptionJobState.Waiting, TranscriptionJobState.Running, TranscriptionJobState.Suspended],
            TranscriptionJobStates.All.Where(s => s.IsPending()));
        foreach (var timing in FinalPassTimings.All)
        {
            Assert.Equal(timing, FinalPassTimings.FromStorageValue(timing.StorageValue()));
        }
        foreach (var state in TranscriptionJobStates.All)
        {
            Assert.Equal(state, TranscriptionJobStates.FromStorageValue(state.StorageValue()));
        }
        Assert.Null(FinalPassTimings.FromStorageValue("later"));
        Assert.Null(FinalPassTimings.FromStorageValue(null));
        Assert.Null(TranscriptionJobStates.FromStorageValue("paused"));
    }

    [Fact]
    public void WindowsDefaultTimingIsWhenIdle()
    {
        Assert.Equal(FinalPassTiming.WhenIdle, FinalPassTimings.Default);
    }
}
