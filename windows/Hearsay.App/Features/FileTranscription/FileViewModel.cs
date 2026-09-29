using System.ComponentModel;
using Hearsay.App.Features.Recording;
using Hearsay.App.Features.Transcription;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Naming;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;

namespace Hearsay.App.Features.FileTranscription;

/// <summary>The phases of the File flow (the Mac's <c>FileViewModel.Phase</c>).</summary>
internal abstract record FilePhase
{
    private FilePhase()
    {
    }

    public static FilePhase IdleState { get; } = new Idle();

    public sealed record Idle : FilePhase;

    public sealed record Loading(string Source) : FilePhase;

    /// <summary>Auto: detecting the language before transcribing.</summary>
    public sealed record Detecting(string Source) : FilePhase;

    public sealed record Transcribing(string Source, double Progress) : FilePhase;

    public sealed record Finished(string Srt, string Source) : FilePhase;

    public sealed record Failed(string Message) : FilePhase;
}

/// <summary>
/// The File flow (PLAN.md 4.2): decode any file Media Foundation reads to
/// 16 kHz mono, transcribe it with the active model, and write
/// <c>&lt;timestamp&gt;.srt</c> into the output folder, where the timestamp
/// follows the Python rule (<see cref="Timestamps.SourceFileTimestamp"/>:
/// embedded in the filename, else creation time, else modification time).
/// The user's source file is never renamed or moved. A finished SRT is
/// offered to the notes flow through <see cref="PendingNotesRequest"/>.
/// Port of mac/Hearsay/Features/FileTranscription/FileViewModel.swift.
/// </summary>
/// <remarks>
/// One exception to "never touch the source": a recording Hearsay itself kept
/// in the output folder (a failed recording or a recovered spool WAV, named
/// <c>&lt;timestamp&gt;.wav</c>) gets its SRT under the same stem, replacing
/// a saved live preview, so the pair is renamed together by the notes flow.
/// Use from the UI thread.
/// </remarks>
internal sealed class FileViewModel : INotifyPropertyChanged
{
    private readonly AppSettings settings;
    private readonly ModelStore modelStore;
    private readonly TranscriptionEngine engine;
    private FilePhase phase = FilePhase.IdleState;
    private int job;
    private CancellationTokenSource? run;
    /// <summary>The decoded samples and model of the last file, held while a language notice offers a re-run.</summary>
    private (float[] Samples, string Location)? rerunInput;

    public FileViewModel(AppSettings settings, ModelStore modelStore, TranscriptionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelStore);
        ArgumentNullException.ThrowIfNull(engine);
        this.settings = settings;
        this.modelStore = modelStore;
        this.engine = engine;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A finished SRT is waiting for the notes flow; see <see cref="TakeNotesRequest"/>.</summary>
    public event EventHandler? NotesRequested;

    public FilePhase Phase
    {
        get => phase;
        private set
        {
            if (phase == value) return;
            phase = value;
            Notify(nameof(Phase));
        }
    }

    /// <summary>The failure was the missing model; the view links to Models.</summary>
    public bool NeedsModel { get; private set; }

    /// <summary>A finished SRT waiting for the notes flow.</summary>
    public NotesRequest? PendingNotesRequest { get; private set; }

    /// <summary>Short note shown in the idle state, e.g. after Cancel.</summary>
    public string? Note { get; private set; }

    /// <summary>The language of the last file and how it was chosen (PLAN.md section 1, "Languages").</summary>
    public SessionLanguageTracker? Tracker { get; private set; }

    /// <summary>The suggestion or fallback banner of the last file.</summary>
    public LanguageNotice? LanguageNotice { get; private set; }

    /// <summary>Why the last "Transcribe again" failed.</summary>
    public string? RerunError { get; private set; }

    /// <summary>The last detection result, for the debug path.</summary>
    public DetectionResult? LastDetection { get; private set; }

    /// <summary>Shared with the Record tab.</summary>
    public LanguageChoice LanguageChoice
    {
        get => settings.LanguageChoice;
        set => settings.LanguageChoice = value;
    }

    /// <summary>The resolved language of the last file: what its SRT, the Chinese conversion, and the notes flow use.</summary>
    public TranscriptLanguage? SessionLanguage => Tracker?.Language;

    public bool IsBusy => phase is FilePhase.Loading or FilePhase.Detecting or FilePhase.Transcribing;

    /// <summary>The language notice's buttons apply: the file is finished and its samples are still held.</summary>
    public bool CanRerun => phase is FilePhase.Finished && rerunInput is not null;

    public string? ErrorMessage => phase is FilePhase.Failed failed ? failed.Message : null;

    /// <summary>Starts transcribing <paramref name="source"/> unless a file is already running.</summary>
    public void Transcribe(string source)
    {
        if (IsBusy) return;
        _ = RunAsync(source);
    }

    /// <summary>Stops the running file at the next 30 s window. Nothing is written.</summary>
    public void Cancel()
    {
        if (!IsBusy) return;
        run?.Cancel();
    }

    public NotesRequest? TakeNotesRequest()
    {
        var request = PendingNotesRequest;
        PendingNotesRequest = null;
        return request;
    }

    /// <summary>"Dismiss" on the suggestion banner.</summary>
    public void DismissLanguageNotice()
    {
        LanguageNotice = null;
        rerunInput = null;
        Notify();
    }

    /// <summary>
    /// A language notice button: transcribes the last file again in
    /// <paramref name="language"/> and rewrites the same SRT. Never changes
    /// the language choice or the preferred language.
    /// </summary>
    public void TranscribeAgain(TranscriptLanguage language)
    {
        if (!CanRerun || IsBusy) return;
        _ = RerunAsync(language);
    }

    /// <summary>
    /// Runs the whole flow and returns the SRT, or null after a failure (the
    /// message is in <see cref="Phase"/>). <paramref name="locationOverride"/>
    /// replaces the active model and <paramref name="choice"/> the Language
    /// picker (debug entry point only).
    /// </summary>
    public async Task<string?> RunAsync(string source, string? locationOverride = null, LanguageChoice? choice = null)
    {
        job += 1;
        var id = job;
        NeedsModel = false;
        PendingNotesRequest = null;
        Note = null;
        LanguageNotice = null;
        RerunError = null;
        rerunInput = null;
        LastDetection = null;
        Tracker = null;

        string location;
        if (locationOverride is not null)
        {
            location = locationOverride;
        }
        else
        {
            try
            {
                location = WhisperModelLocation.Active(modelStore);
            }
            catch (WhisperEngineException error)
            {
                NeedsModel = true;
                Phase = new FilePhase.Failed(error.Message);
                return null;
            }
        }

        Phase = new FilePhase.Loading(source);
        string folder;
        try
        {
            folder = TranscriptOutput.ResolveFolder(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Phase = new FilePhase.Failed(Strings.OutputFolderOpenFailed(error.Message));
            return null;
        }

        var cancel = new CancellationTokenSource();
        run = cancel;
        var token = cancel.Token;
        var tracker = new SessionLanguageTracker(choice ?? LanguageChoice, settings.PreferredLanguage);
        try
        {
            var samples = await Task.Run(() => AudioFileLoader.LoadMono16k(source, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (tracker.IsUndecided)
            {
                // Auto: detect first; unsure or no speech falls back to the preferred language.
                Phase = new FilePhase.Detecting(source);
                var detection = await TryDetectAsync(location, samples, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                LastDetection = detection;
                tracker.Finish(detection?.Detection);
            }
            if (tracker.Language is not { } language) throw new OperationCanceledException();
            Tracker = tracker;
            Phase = new FilePhase.Transcribing(source, 0);
            var progress = new Progress<double>(value => UpdateProgress(value, id));
            var result = await engine.TranscribeAsync(location, samples, language, progress, token).ConfigureAwait(true);
            var srt = WriteSrt(result.Cues(0, language.ChineseScript()), source, folder);
            Phase = new FilePhase.Finished(srt, source);
            RequestNotes(srt);
            if (!tracker.IsSettled)
            {
                // Fixed language: one background check for a mismatch.
                var detection = await TryDetectAsync(location, samples, CancellationToken.None).ConfigureAwait(true);
                if (id != job) return srt;
                LastDetection = detection;
                tracker.Finish(detection?.Detection);
                Tracker = tracker;
            }
            LanguageNotice = LanguageNotice.From(tracker.Decision);
            if (LanguageNotice is not null) rerunInput = (samples, location);
            Notify();
            return srt;
        }
        catch (OperationCanceledException)
        {
            Phase = FilePhase.IdleState;
            Note = Strings.Cancelled;
            Notify();
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Phase = new FilePhase.Failed(Strings.CouldNotTranscribeFile(Path.GetFileName(source), TranscriptionEngine.Describe(error)));
            return null;
        }
        finally
        {
            if (ReferenceEquals(run, cancel)) run = null;
            cancel.Dispose();
        }
    }

    /// <summary>One full pass over the held samples in <paramref name="language"/>, written over the same SRT. On failure or Cancel the previous SRT stays.</summary>
    public async Task<string?> RerunAsync(TranscriptLanguage language)
    {
        if (phase is not FilePhase.Finished finished || rerunInput is not { } input || Tracker is not { } tracker) return null;
        job += 1;
        var id = job;
        RerunError = null;
        var cancel = new CancellationTokenSource();
        run = cancel;
        Phase = new FilePhase.Transcribing(finished.Source, 0);
        try
        {
            var progress = new Progress<double>(value => UpdateProgress(value, id));
            var result = await engine.TranscribeAsync(input.Location, input.Samples, language, progress, cancel.Token).ConfigureAwait(true);
            if (!File.Exists(finished.Srt))
            {
                Phase = finished;
                RerunError = Strings.CouldNotTranscribeAgainMoved(Path.GetFileName(finished.Srt));
                Notify();
                return null;
            }
            TranscriptOutput.WriteSrt(result.Cues(0, language.ChineseScript()), finished.Srt);
            tracker.Choose(language);
            LanguageNotice = null;
            rerunInput = null;
            Phase = finished;
            RequestNotes(finished.Srt);
            Notify();
            return finished.Srt;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Phase = finished;
            if (!TranscriptionEngine.IsCancellation(error)) RerunError = Strings.CouldNotTranscribeAgain(TranscriptionEngine.Describe(error));
            Notify();
            return null;
        }
        finally
        {
            if (ReferenceEquals(run, cancel)) run = null;
            cancel.Dispose();
        }
    }

    /// <summary>
    /// <c>&lt;folder&gt;\&lt;timestamp&gt;.srt</c> with a numeric suffix when
    /// that name or its WAV is taken, except for Hearsay's own
    /// <c>&lt;timestamp&gt;.wav</c> in the output folder, whose SRT uses the same stem.
    /// </summary>
    public static string WriteSrt(IReadOnlyList<TranscriptSegment> cues, string source, string folder)
    {
        var baseStem = Timestamps.SourceFileTimestamp(source) ?? Timestamps.Now();
        var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? "";
        var isOwnRecording = TranscriptOutput.SameFolder(sourceFolder, folder)
            && string.Equals(Path.GetExtension(source), ".wav", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(source) == baseStem;
        var stem = isOwnRecording ? baseStem : TranscriptOutput.FreeStem(baseStem, folder, [".srt", ".wav"]);
        var srt = Path.Combine(folder, stem + ".srt");
        TranscriptOutput.WriteSrt(cues, srt);
        return srt;
    }

    /// <summary>Debug only (UI snapshots): shows a stubbed phase.</summary>
    internal void ShowSample(FilePhase sample, SessionLanguageTracker? tracker = null, LanguageNotice? notice = null)
    {
        Tracker = tracker;
        LanguageNotice = notice;
        rerunInput = notice is null ? null : (Array.Empty<float>(), "");
        phase = sample;
        Notify();
    }

    private async Task<DetectionResult?> TryDetectAsync(string location, float[] samples, CancellationToken token)
    {
        try
        {
            return await engine.DetectLanguageAsync(location, samples, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"file: detection failed: {error.Message}");
            return null;
        }
    }

    private void UpdateProgress(double value, int id)
    {
        if (id != job || phase is not FilePhase.Transcribing transcribing) return;
        Phase = transcribing with { Progress = Math.Clamp(value, 0, 1) };
    }

    private void RequestNotes(string srt)
    {
        if (SessionLanguage is not { } language) return;
        PendingNotesRequest = new NotesRequest(srt, language);
        NotesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Notify(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
