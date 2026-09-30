using System.Globalization;
using System.Text;
using Hearsay.Core.History;
using Hearsay.Core.Naming;
using Hearsay.Core.Notes;
using Hearsay.Core.Transcription;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// Makes the notes for one SRT: <see cref="NotesPipeline.GenerateAsync"/>, or
/// a stub in the UI snapshots.
/// </summary>
internal delegate Task<NotesResponse> NotesGenerator(string srtText, string languageCode, PromptTemplate template,
    AIProviderConfiguration configuration, string? token, CancellationToken cancellationToken);

/// <summary>
/// Drives the notes flow of PLAN.md 4.3 (Python <c>generate_meeting_notes</c>,
/// API branch, plus <c>name_retained_outputs</c>). Port of
/// mac/Hearsay/Features/Notes/NotesFlowViewModel.swift:
/// <list type="number">
/// <item>Confirm send (skipped when <see cref="AIProviderConfiguration.AskBeforeSending"/> is off).</item>
/// <item>Send: generate notes, then the naming sheet prefilled with the
/// sanitized AI suggestion, then <see cref="OutputWriter.SaveNamed(string, string, string, string, string?)"/>.</item>
/// <item>Keep local: "Name this meeting yourself?" (default no), then the
/// naming sheet with no suggestion and <see cref="OutputWriter.RenameRetained(string, string, string?)"/>.</item>
/// </list>
/// Every failure ends in <see cref="NotesPhase.Failed"/> with a message; the SRT stays where it is.
/// <para>
/// Regenerating (History, an entry that already has notes): the naming sheet
/// starts from the current meeting name and saving goes through
/// <see cref="OutputWriter.ReplaceNamed(string, string, string, string, string, string?)"/>,
/// which moves the current notes to the Recycle Bin only once the new ones
/// are saved. "Keep local" leaves every file as is.
/// </para>
/// <para>
/// Windows: the Swift <c>@Observable</c> becomes <see cref="Changed"/>; the
/// generation runs on the thread pool (the CLI and HTTP clients never touch
/// the UI) and its result is applied back on the UI thread. The pipeline's
/// <see cref="NotesPipeline.GenerateAndSaveAsync"/> is not used because the
/// naming sheet sits between generating and saving; the flow makes the same
/// two calls it makes (<see cref="NotesPipeline.GenerateAsync"/>, then
/// <see cref="OutputWriter"/>). <see cref="PromptTooLong"/> is the Windows
/// command-line refusal (PLAN.md 18.4, "W6 core"), shown in the confirm
/// sheet before anything is sent. Use from the UI thread.
/// </para>
/// </summary>
internal sealed class NotesFlowViewModel
{
    private static readonly Lazy<NotesPipeline> SharedPipeline = new(() => new NotesPipeline());

    private readonly NotesGenerator generate;
    private readonly SynchronizationContext? context;
    private string srtText = "";
    private string? timestamp;
    private NotesResponse? notes;
    private CancellationTokenSource? generation;
    private Guid templateId;
    private TranscriptLanguage notesLanguage = TranscriptLanguage.English;

    /// <param name="store">The app's one provider store.</param>
    /// <param name="generate">Null uses a shared <see cref="NotesPipeline"/>.</param>
    public NotesFlowViewModel(AIProviderStore store, NotesGenerator? generate = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        this.generate = generate ?? DefaultGenerate;
        context = SynchronizationContext.Current;
        templateId = store.Configuration.SelectedTemplateId;
        lock (InstancesGate)
        {
            Instances.RemoveAll(flow => !flow.TryGetTarget(out _));
            Instances.Add(new WeakReference<NotesFlowViewModel>(this));
        }
    }

    private static readonly Lock InstancesGate = new();

    /// <summary>Every flow created in this process (weakly), for <see cref="IsAnyRunning"/> (the Mac's <c>instances</c>).</summary>
    private static readonly List<WeakReference<NotesFlowViewModel>> Instances = [];

    /// <summary>
    /// A notes flow (Record, File, or History tab) is running: a sheet is up
    /// or notes are being generated. A queue job that finishes now does not
    /// open another one (PLAN.md 4.9 item 4).
    /// </summary>
    public static bool IsAnyRunning
    {
        get
        {
            lock (InstancesGate)
            {
                return Instances.Any(flow => flow.TryGetTarget(out var model) && model.IsRunning);
            }
        }
    }

    /// <summary>The app's one pipeline (Settings > AI uses it for Check and Test connection).</summary>
    public static NotesPipeline Pipeline => SharedPipeline.Value;

    /// <summary>Anything the view shows changed.</summary>
    public event EventHandler? Changed;

    public AIProviderStore Store { get; }

    public NotesPhase Phase { get; private set; } = new NotesPhase.Idle();

    /// <summary>The SRT being processed (updated when it is renamed).</summary>
    public string? SrtPath { get; private set; }

    public int TranscriptCharacterCount { get; private set; }

    /// <summary>Retained recordings beside the SRT, shown when asking about naming.</summary>
    public IReadOnlyList<string> RetainedAudio { get; private set; } = [];

    /// <summary>Template chosen in the confirm sheet for this run.</summary>
    public Guid TemplateId
    {
        get => templateId;
        set
        {
            if (templateId == value) return;
            templateId = value;
            OnChanged();
        }
    }

    /// <summary>The transcript's language: the default notes language.</summary>
    public TranscriptLanguage TranscriptLanguage { get; private set; } = TranscriptLanguage.English;

    /// <summary>The caller's explanation of where <see cref="TranscriptLanguage"/> came from, or null when known.</summary>
    public string? TranscriptLanguageNote { get; private set; }

    /// <summary>The language the notes are written in, chosen in the confirm sheet for this run only (not stored).</summary>
    public TranscriptLanguage NotesLanguage
    {
        get => notesLanguage;
        set
        {
            if (notesLanguage == value) return;
            notesLanguage = value;
            OnChanged();
        }
    }

    /// <summary>True when this run replaces existing notes (History > Regenerate).</summary>
    public bool IsRegenerating { get; private set; }

    /// <summary>The meeting name in the current stem while regenerating; null for a timestamp stem or a new run.</summary>
    public string? CurrentMeetingName { get; private set; }

    public bool IsRunning => Phase is not (NotesPhase.Idle or NotesPhase.Finished or NotesPhase.Failed);

    public string ProviderName => Strings.CoreText(Store.Configuration.Preset.Name);

    /// <summary>"Transcript language: …" under the confirm sheet's picker.</summary>
    public string TranscriptLanguageCaption => Strings.TranscriptLanguageLine(TranscriptLanguage, TranscriptLanguageNote);

    /// <summary>"Notes will be written in …." when the choice differs, else null.</summary>
    public string? NotesLanguageCaptionText => Strings.NotesLanguageLine(TranscriptLanguage, NotesLanguage);

    /// <summary>
    /// Windows only: the refusal the pipeline would give for this transcript
    /// with the current provider, template and notes language (the prompt
    /// does not fit on a Windows command line), or null when it fits.
    /// </summary>
    public string? PromptTooLong =>
        CommandLineCheck.Refusal(srtText, NotesLanguage.Code(), Store.Template(TemplateId), Store.Configuration);

    // Entry point

    /// <summary>
    /// Starts the flow for a finished SRT. <paramref name="timestamp"/>
    /// overrides the one in the SRT filename, as Python's <c>timestamp=</c>
    /// does. <paramref name="language"/> is the transcript's language and the
    /// default notes language; <paramref name="languageNote"/> replaces its
    /// name in the confirm sheet's caption. <paramref name="replacingNotes"/>
    /// regenerates the notes of an existing meeting.
    /// </summary>
    public void Run(string srtPath, TranscriptLanguage language, string? languageNote = null, string? timestamp = null,
        bool replacingNotes = false)
    {
        ArgumentNullException.ThrowIfNull(srtPath);
        if (IsRunning) return;
        SrtPath = Path.GetFullPath(srtPath);
        IsRegenerating = replacingNotes;
        CurrentMeetingName = replacingNotes ? HistoryIndex.MeetingName(Path.GetFileNameWithoutExtension(SrtPath)) : null;
        TranscriptLanguage = language;
        TranscriptLanguageNote = languageNote;
        notesLanguage = language;
        this.timestamp = timestamp;
        notes = null;
        RetainedAudio = [];
        try
        {
            srtText = File.ReadAllText(SrtPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            srtText = "";
            Fail(Strings.Localize(new NotesPipelineError.SrtUnreadable(SrtPath, error.Message).Localized));
            return;
        }
        // Swift String.count: grapheme clusters.
        TranscriptCharacterCount = new StringInfo(srtText).LengthInTextElements;
        templateId = Store.Configuration.SelectedTemplateId;
        if (Store.Configuration.AskBeforeSending)
        {
            SetPhase(new NotesPhase.Confirming());
        }
        else
        {
            StartGeneration();
        }
    }

    // Confirm send

    /// <summary>"Send" in the confirm sheet (Python: Enter or <c>y</c>).</summary>
    public void Send()
    {
        if (Phase is not NotesPhase.Confirming) return;
        StartGeneration();
    }

    private void StartGeneration()
    {
        SetPhase(new NotesPhase.Generating());
        var template = Store.Template(TemplateId);
        var configuration = Store.Configuration;
        var token = configuration.Auth.NeedsToken() ? Store.CurrentToken : null;
        var text = srtText;
        var language = NotesLanguage.Code();
        var cancellation = new CancellationTokenSource();
        generation = cancellation;
        _ = GenerateAsync(text, language, template, configuration, token, cancellation);
    }

    private async Task GenerateAsync(string text, string language, PromptTemplate template,
        AIProviderConfiguration configuration, string? token, CancellationTokenSource cancellation)
    {
        NotesResponse? response = null;
        string? failure = null;
        try
        {
            response = await Task.Run(
                () => generate(text, language, template, configuration, token, cancellation.Token),
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            failure = Strings.NotesCancelled;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = Describe(error) + "\n" + Strings.NotesNoneGenerated;
        }
        Post(() =>
        {
            if (!ReferenceEquals(generation, cancellation)) return;
            generation = null;
            cancellation.Dispose();
            if (failure is not null)
            {
                if (Phase is NotesPhase.Generating) Fail(failure);
                return;
            }
            if (response is not null) DidGenerate(response);
        });
    }

    /// <summary>"Keep local" in the confirm sheet (Python: <c>n</c>). Not a failure. Regenerating renames nothing.</summary>
    public void KeepLocal()
    {
        if (Phase is not NotesPhase.Confirming || SrtPath is not { } srt) return;
        if (IsRegenerating)
        {
            Finish(Strings.NotesNothingSent, [srt]);
            return;
        }
        RetainedAudio = OutputWriter.RetainedAudioFiles(srt);
        SetPhase(new NotesPhase.AskingManualNaming());
    }

    public void CancelGeneration()
    {
        try
        {
            generation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DidGenerate(NotesResponse response)
    {
        if (Phase is not NotesPhase.Generating) return;
        notes = response;
        SetPhase(new NotesPhase.Naming(FilenameSanitizer.Sanitize(response.Filename)));
    }

    // Manual naming question

    /// <summary>"Name this meeting yourself?" answered yes.</summary>
    public void AcceptManualNaming()
    {
        if (Phase is not NotesPhase.AskingManualNaming) return;
        SetPhase(new NotesPhase.Naming(null));
    }

    /// <summary>Answered no (the default): keep the timestamp names.</summary>
    public void DeclineManualNaming()
    {
        if (Phase is not NotesPhase.AskingManualNaming || SrtPath is not { } srt) return;
        Finish(Strings.NotesSkipped, [srt, .. RetainedAudio]);
    }

    // Naming sheet

    public void SaveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Phase is not NotesPhase.Naming || SrtPath is not { } srt) return;
        if (FilenameSanitizer.Sanitize(name) is null) return;
        if (notes is { } response)
        {
            try
            {
                var outputs = IsRegenerating
                    ? OutputWriter.ReplaceNamed(Path.GetFileNameWithoutExtension(srt), DirectoryOf(srt), name,
                        response.Markdown, response.TranscriptMarkdown, timestamp)
                    : OutputWriter.SaveNamed(srt, name, response.Markdown, response.TranscriptMarkdown, timestamp);
                SrtPath = outputs.Srt;
                Finish(Strings.NotesGenerated, [outputs.Srt, outputs.Markdown, outputs.Transcript, .. outputs.Companions]);
            }
            catch (Exception error) when (error is OutputWriterException or IOException or UnauthorizedAccessException)
            {
                Fail(Describe(error));
            }
        }
        else
        {
            try
            {
                var renamed = OutputWriter.RenameRetained(srt, name, timestamp);
                if (renamed.Count > 0) SrtPath = renamed[0];
                Finish(Strings.NotesSkippedRenamed, renamed);
            }
            catch (Exception error) when (error is OutputWriterException or IOException or UnauthorizedAccessException)
            {
                Fail(Describe(error) + "\n" + Strings.NotesKeepingTimestampNames);
            }
        }
    }

    /// <summary>Cancel in the naming sheet keeps the timestamp names.</summary>
    public void CancelNaming()
    {
        if (Phase is not NotesPhase.Naming || SrtPath is not { } srt) return;
        if (notes is not null)
        {
            // Python: EOF at the name prompt after AI returns 1.
            Fail(IsRegenerating ? Strings.NotesNoNameRegenerate : Strings.NotesNoNameRetained);
        }
        else
        {
            Finish(Strings.NotesSkippedKept, [srt, .. RetainedAudio]);
        }
    }

    // Sheet dismissed without an answer

    /// <summary>
    /// A sheet went away without one of its buttons (Escape, the window
    /// closed, the Cancel button beside the panel). Resolves the step as
    /// Python does when input closes (EOF): the confirm prompt declines, and
    /// the follow-up "Name this meeting yourself?" then also reads EOF and
    /// keeps the timestamp names; the naming prompt cancels. No-op in other phases.
    /// </summary>
    public void SheetDismissed()
    {
        switch (Phase)
        {
            case NotesPhase.Confirming:
                KeepLocal();
                DeclineManualNaming();
                break;
            case NotesPhase.AskingManualNaming:
                DeclineManualNaming();
                break;
            case NotesPhase.Naming:
                CancelNaming();
                break;
        }
    }

    public void Reset()
    {
        if (IsRunning) return;
        SetPhase(new NotesPhase.Idle());
    }

    // Helpers

    private void Finish(string message, IReadOnlyList<string> files)
    {
        notes = null;
        SetPhase(new NotesPhase.Finished(message, files));
    }

    private void Fail(string message)
    {
        notes = null;
        SetPhase(SrtPath is { } srt ? new NotesPhase.Failed(message, srt) : new NotesPhase.Idle());
    }

    private void SetPhase(NotesPhase phase)
    {
        Phase = phase;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Runs <paramref name="action"/> on the UI thread the model was made on.</summary>
    private void Post(Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }

    private static string DirectoryOf(string path) => Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? path;

    private static Task<NotesResponse> DefaultGenerate(string srtText, string languageCode, PromptTemplate template,
        AIProviderConfiguration configuration, string? token, CancellationToken cancellationToken) =>
        SharedPipeline.Value.GenerateAsync(srtText, languageCode, template, configuration, token, cancellationToken);

    /// <summary>The user-facing text of an error (the Swift <c>describe</c>), through <see cref="Strings.Describe"/>.</summary>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Strings.Describe(error);
    }
}

/// <summary>The steps of <see cref="NotesFlowViewModel"/> (the Swift <c>Phase</c> enum).</summary>
internal abstract record NotesPhase
{
    private NotesPhase()
    {
    }

    public sealed record Idle : NotesPhase;

    public sealed record Confirming : NotesPhase;

    public sealed record Generating : NotesPhase;

    public sealed record AskingManualNaming : NotesPhase;

    /// <summary><paramref name="Suggestion"/> is the sanitized AI name, or null for manual naming.</summary>
    public sealed record Naming(string? Suggestion) : NotesPhase;

    /// <summary><paramref name="Files"/> are the final paths, SRT first.</summary>
    public sealed record Finished(string Message, IReadOnlyList<string> Files) : NotesPhase;

    public sealed record Failed(string Message, string SrtPath) : NotesPhase;
}
