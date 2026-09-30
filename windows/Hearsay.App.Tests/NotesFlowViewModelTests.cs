using Hearsay.App.Features.Notes;
using Hearsay.Core.Naming;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Tests;

/// <summary>
/// The phases of <see cref="NotesFlowViewModel"/> (PLAN.md 4.3, the port of
/// mac/Hearsay/Features/Notes/NotesFlowViewModel.swift) with a stub
/// generator: nothing is sent anywhere, and the SRT lives in a scratch folder.
/// </summary>
public sealed class NotesFlowViewModelTests : IDisposable
{
    private const string SrtText = "1\n00:00:01,000 --> 00:00:02,000\nDiscuss launch\n";
    private static readonly NotesResponse Notes = new("Product Launch Plan", "# Notes\n", "# Transcript\n");

    private readonly ScratchFolder folder = new();
    private readonly ScratchFolder settingsFolder = new();
    private readonly InterfaceLanguageScope english = new(InterfaceLanguage.English);
    private readonly SynchronizationContext? previousContext = SynchronizationContext.Current;
    private readonly AIProviderStore store;

    public NotesFlowViewModelTests()
    {
        // No UI thread: the model applies results where the generation finishes.
        SynchronizationContext.SetSynchronizationContext(null);
        store = new AIProviderStore(new SettingsFile(settingsFolder.Path), new MemorySecrets(), () => CliTool.Copilot);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(previousContext);
        english.Dispose();
        settingsFolder.Dispose();
        folder.Dispose();
    }

    private string WriteSrt() => folder.Write("2026-09-30_10-15-00.srt", SrtText);

    private NotesFlowViewModel Model(NotesGenerator generator, bool askBeforeSending = true)
    {
        store.Configuration = store.Configuration with { AskBeforeSending = askBeforeSending };
        return new NotesFlowViewModel(store, generator);
    }

    private static NotesGenerator Returning(NotesResponse response) =>
        (_, _, _, _, _, _) => Task.FromResult(response);

    /// <summary>Waits until the phase is no longer <see cref="NotesPhase.Generating"/>.</summary>
    private static async Task<NotesPhase> SettledAsync(NotesFlowViewModel model)
    {
        for (var i = 0; i < 500 && model.Phase is NotesPhase.Generating; i++) await Task.Delay(10);
        return model.Phase;
    }

    [Fact]
    public async Task SendThenNameSavesTheNotes()
    {
        string? sentLanguage = null;
        var model = Model((srt, language, _, _, token, _) =>
        {
            Assert.Equal(SrtText, srt);
            Assert.Null(token);
            sentLanguage = language;
            return Task.FromResult(Notes);
        });
        var srt = WriteSrt();
        Assert.IsType<NotesPhase.Idle>(model.Phase);

        model.Run(srt, TranscriptLanguage.German);
        Assert.IsType<NotesPhase.Confirming>(model.Phase);
        Assert.True(model.IsRunning);
        Assert.Equal(TranscriptLanguage.German, model.NotesLanguage);

        model.NotesLanguage = TranscriptLanguage.English;
        model.Send();
        var naming = Assert.IsType<NotesPhase.Naming>(await SettledAsync(model));
        Assert.Equal("en", sentLanguage);
        Assert.Equal(FilenameSanitizer.Sanitize("Product Launch Plan"), naming.Suggestion);

        model.SaveName("Launch Plan");
        var finished = Assert.IsType<NotesPhase.Finished>(model.Phase);
        Assert.Equal(Strings.NotesGenerated, finished.Message);
        Assert.Equal(3, finished.Files.Count);
        Assert.All(finished.Files, file => Assert.True(File.Exists(file), file));
        Assert.Equal(finished.Files[0], model.SrtPath);
        Assert.False(model.IsRunning);

        model.Reset();
        Assert.IsType<NotesPhase.Idle>(model.Phase);
    }

    [Fact]
    public async Task WithoutAskingItGeneratesAtOnce()
    {
        var model = Model(Returning(Notes), askBeforeSending: false);
        model.Run(WriteSrt(), TranscriptLanguage.English);
        Assert.IsType<NotesPhase.Naming>(await SettledAsync(model));
    }

    [Fact]
    public async Task AFailedGenerationKeepsTheSrt()
    {
        var model = Model((_, _, _, _, _, _) => throw new ChatCompletionsException(new ChatCompletionsError.MissingToken()));
        var srt = WriteSrt();
        model.Run(srt, TranscriptLanguage.English);
        model.Send();
        var failed = Assert.IsType<NotesPhase.Failed>(await SettledAsync(model));
        Assert.Equal("No API token is set for this provider. Add one in Settings > AI.\n" + Strings.NotesNoneGenerated, failed.Message);
        Assert.Equal(srt, failed.SrtPath);
        Assert.True(File.Exists(srt));
    }

    [Fact]
    public async Task CancellingTheGeneration()
    {
        var model = Model(async (_, _, _, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Notes;
        });
        model.Run(WriteSrt(), TranscriptLanguage.English);
        model.Send();
        Assert.IsType<NotesPhase.Generating>(model.Phase);
        model.CancelGeneration();
        var failed = Assert.IsType<NotesPhase.Failed>(await SettledAsync(model));
        Assert.Equal(Strings.NotesCancelled, failed.Message);
    }

    [Fact]
    public void KeepLocalThenDeclineNaming()
    {
        var model = Model((_, _, _, _, _, _) => throw new InvalidOperationException("must not be sent"));
        var srt = WriteSrt();
        model.Run(srt, TranscriptLanguage.English);
        model.KeepLocal();
        Assert.IsType<NotesPhase.AskingManualNaming>(model.Phase);
        model.DeclineManualNaming();
        var finished = Assert.IsType<NotesPhase.Finished>(model.Phase);
        Assert.Equal(Strings.NotesSkipped, finished.Message);
        Assert.Equal([srt], finished.Files);
    }

    [Fact]
    public void KeepLocalThenNameItRenamesTheSrt()
    {
        var model = Model((_, _, _, _, _, _) => throw new InvalidOperationException("must not be sent"));
        model.Run(WriteSrt(), TranscriptLanguage.English);
        model.KeepLocal();
        model.AcceptManualNaming();
        Assert.Null(Assert.IsType<NotesPhase.Naming>(model.Phase).Suggestion);
        model.SaveName("Standup");
        var finished = Assert.IsType<NotesPhase.Finished>(model.Phase);
        Assert.Equal(Strings.NotesSkippedRenamed, finished.Message);
        Assert.EndsWith("_standup.srt", finished.Files[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheNamingSheetAfterGenerating()
    {
        var model = Model(Returning(Notes));
        var srt = WriteSrt();
        model.Run(srt, TranscriptLanguage.English);
        model.Send();
        Assert.IsType<NotesPhase.Naming>(await SettledAsync(model));
        model.SheetDismissed();
        var failed = Assert.IsType<NotesPhase.Failed>(model.Phase);
        Assert.Equal(Strings.NotesNoNameRetained, failed.Message);
        Assert.True(File.Exists(srt));
    }

    [Fact]
    public void DismissingTheConfirmSheetKeepsTheTimestampNames()
    {
        var model = Model((_, _, _, _, _, _) => throw new InvalidOperationException("must not be sent"));
        var srt = WriteSrt();
        model.Run(srt, TranscriptLanguage.English);
        model.SheetDismissed();
        var finished = Assert.IsType<NotesPhase.Finished>(model.Phase);
        Assert.Equal(Strings.NotesSkipped, finished.Message);
        Assert.Equal([srt], finished.Files);
    }

    [Fact]
    public void AnUnreadableSrtFails()
    {
        var model = Model(Returning(Notes));
        var missing = Path.Combine(folder.Path, "2026-09-30_10-15-00.srt");
        model.Run(missing, TranscriptLanguage.English);
        var failed = Assert.IsType<NotesPhase.Failed>(model.Phase);
        Assert.StartsWith("Error: SRT file not found or unreadable (" + missing + "): ", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutOfOrderCommandsAreIgnored()
    {
        var model = Model(Returning(Notes));
        model.Send();
        model.KeepLocal();
        model.AcceptManualNaming();
        model.SaveName("x");
        model.CancelNaming();
        Assert.IsType<NotesPhase.Idle>(model.Phase);
    }

    [Fact]
    public void AnyRunningFlowIsTrackedForTheQueuesNotesRule()
    {
        // The Mac's isAnyRunning (PLAN.md 4.9 item 4): a flow on screen stops a finished
        // recording from opening another one.
        // Flows of earlier tests are unreachable by now; the tracker holds them weakly.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var model = Model(Returning(Notes));
        Assert.False(NotesFlowViewModel.IsAnyRunning);
        model.Run(WriteSrt(), TranscriptLanguage.English);
        Assert.IsType<NotesPhase.Confirming>(model.Phase);
        Assert.True(NotesFlowViewModel.IsAnyRunning);
        model.SheetDismissed();
        model.Reset();
        Assert.False(model.IsRunning);
        Assert.False(NotesFlowViewModel.IsAnyRunning);
    }
}
