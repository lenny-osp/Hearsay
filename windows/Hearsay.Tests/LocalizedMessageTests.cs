using System.Globalization;
using System.Text.Json;
using Hearsay.Core;
using Hearsay.Core.Audio;
using Hearsay.Core.ModelStore;
using Hearsay.Core.Naming;
using Hearsay.Core.Notes;
using Hearsay.Core.Updates;
using Hearsay.Whisper;

namespace Hearsay.Tests;

/// <summary>
/// The catalog keys Core hands the app with its English errors
/// (<see cref="ILocalizedMessage"/>, PLAN.md 18.3 "Localization"). For every
/// type: formatting the key (placeholders as <c>{0}</c>, <c>{1}</c>, …) with
/// the arguments gives exactly the English <c>Message</c>, so the two never
/// drift, and every key, nested ones included, is in
/// shared/localization/strings-en.json, so the app's .resw has a translation.
/// The keys are the Mac's <c>String(localized:)</c> keys in the Swift file each
/// type names.
/// </summary>
public sealed class LocalizedMessageTests
{
    private static readonly Lazy<HashSet<string>> Keys = new(() =>
    {
        using var catalog = JsonDocument.Parse(SharedFiles.ReadText("localization", "strings-en.json"));
        return catalog.RootElement.EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);
    });

    private static string English(ILocalizedMessage message) =>
        LocalizedMessage.Format(message, _ => null, CultureInfo.InvariantCulture);

    /// <summary>The key's English with the arguments is <paramref name="expected"/>, and every key is in the shared catalog.</summary>
    private static void AssertLocalized(ILocalizedMessage? message, string expected)
    {
        Assert.NotNull(message);
        Assert.Equal(expected, English(message));
        foreach (var key in AllKeys(message))
        {
            Assert.True(Keys.Value.Contains(key), $"not in strings-en.json: {key}");
        }
    }

    private static void AssertLocalized(Exception error)
    {
        var localized = Assert.IsAssignableFrom<ILocalizedError>(error);
        AssertLocalized(localized.LocalizedMessage, error.Message);
    }

    private static IEnumerable<string> AllKeys(ILocalizedMessage message)
    {
        yield return message.MessageKey;
        var nested = message.MessageArguments.Concat(message.MessageTail.Select(part => part.Content)).OfType<ILocalizedMessage>();
        foreach (var inner in nested)
        {
            foreach (var key in AllKeys(inner)) yield return key;
        }
    }

    // LocalizedMessage itself

    [Fact]
    public void FormatsTheTranslationWithNestedMessagesAndTail()
    {
        var hint = new LocalizedMessage("Run `%@` once in Terminal to log in.", "codex login");
        var message = new LocalizedMessage("Not logged in. %@", hint).Appending("\n", "raw {output}");
        var translations = new Dictionary<string, string>
        {
            ["Not logged in. %@"] = "Nicht angemeldet. {0}",
            ["Run `%@` once in Terminal to log in."] = "Führe `{0}` einmal aus.",
        };
        Assert.Equal("Nicht angemeldet. Führe `codex login` einmal aus.\nraw {output}",
            LocalizedMessage.Format(message, key => translations.GetValueOrDefault(key), CultureInfo.InvariantCulture));
        Assert.Equal("Not logged in. Run `codex login` once in Terminal to log in.\nraw {output}", message.English);
    }

    [Fact]
    public void ConvertsPlaceholdersAsImportStringsDoes()
    {
        Assert.Equal("{0} is {1} bytes", LocalizedMessage.ToDotNetFormat("%@ is %lld bytes"));
        Assert.Equal("{1} vor {0}", LocalizedMessage.ToDotNetFormat("%2$@ vor %1$@"));
        Assert.Equal("{{x}} {0}%", LocalizedMessage.ToDotNetFormat("{x} %d%%"));
        Assert.True(LocalizedMessage.HasFormat("100%%"));
        Assert.False(LocalizedMessage.HasFormat("No token"));
        // A key without placeholders is used as it is, braces and all.
        Assert.Equal("a {b}", new LocalizedMessage("a {b}").English);
    }

    [Fact]
    public void EqualityIsByValue()
    {
        Assert.Equal(new LocalizedMessage("%@ returned empty output.", "Codex CLI"),
            new LocalizedMessage("%@ returned empty output.", "Codex CLI"));
        Assert.NotEqual(new LocalizedMessage("%@ returned empty output.", "Codex CLI"),
            new LocalizedMessage("%@ returned empty output.", "Codex CLI").Appending("\n", "x"));
    }

    // Notes (CLIProcess.swift, CLIClient.swift)

    [Fact]
    public void CliProviderErrors()
    {
        CliProviderError[] errors =
        [
            new CliProviderError.NotInstalled(CliTool.Codex, ["a", "b"]),
            new CliProviderError.LaunchFailed(CliTool.Copilot, "boom"),
            new CliProviderError.LaunchFailed(CliTool.ClaudeCode, "boom"),
            new CliProviderError.Failed(CliTool.Copilot, 2, ""),
            new CliProviderError.Failed(CliTool.Copilot, 1, "  Error: not logged in \n"),
            new CliProviderError.Failed(CliTool.Copilot, 1, "model not found"),
            new CliProviderError.Failed(CliTool.Codex, 3, "bad effort"),
            new CliProviderError.Failed(CliTool.Antigravity, -1, ""),
            new CliProviderError.NotLoggedIn(CliTool.ClaudeCode, "Please run /login"),
            new CliProviderError.NotLoggedIn(CliTool.Codex, ""),
            new CliProviderError.NotLoggedIn(CliTool.Antigravity, "Authentication required."),
            new CliProviderError.NotLoggedIn(CliTool.Copilot, " "),
            new CliProviderError.TimedOut(CliTool.Antigravity),
            new CliProviderError.EmptyOutput(CliTool.Copilot),
            new CliProviderError.NotACliPreset("Ollama"),
            new CliProviderError.CommandLineTooLong(CliTool.Copilot, 40_000),
            new CliProviderError.CommandLineTooLong(CliTool.Antigravity, 1_234_567),
        ];
        foreach (var error in errors)
        {
            AssertLocalized(new CliProviderException(error));
        }
    }

    [Fact]
    public void CliProviderErrorKeepsTheLocalizedLaunchDetail()
    {
        var detail = new LocalizedMessage("Could not set up the %@ project in %@: %@", CliArguments.AntigravityProjectName,
            @"C:\Users\test\.gemini\config\projects", "Access is denied.");
        var error = new CliProviderError.LaunchFailed(CliTool.Antigravity, detail.English) { LocalizedDetail = detail };
        AssertLocalized(new CliProviderException(error));
        Assert.Same(detail, error.Localized?.MessageArguments[1]);
    }

    /// <summary>Windows only: the counts are grouped in the interface culture, the English in the invariant one.</summary>
    [Fact]
    public void CommandLineTooLongGroupsTheCounts()
    {
        var error = new CliProviderException(new CliProviderError.CommandLineTooLong(CliTool.Copilot, 40_000));
        Assert.Contains("would be 40,000 characters, and Windows allows 32,766.", error.Message, StringComparison.Ordinal);
        var german = LocalizedMessage.Format(error.LocalizedMessage, _ => null, CultureInfo.GetCultureInfo("de-DE"));
        Assert.Contains("would be 40.000 characters, and Windows allows 32.766.", german, StringComparison.Ordinal);
        Assert.Equal(new GroupedNumber(40_000), error.LocalizedMessage.MessageArguments[1]);
    }

    [Fact]
    public void LoginStatusLines()
    {
        CliRunResult[] claude =
        [
            new(0, """{"loggedIn": true, "authMethod": "claude.ai", "subscriptionType": "max"}""", ""),
            new(0, """{"loggedIn": true, "subscriptionType": "pro"}""", ""),
            new(0, """{"loggedIn": true}""", ""),
            new(1, """{"loggedIn": false}""", ""),
            new(0, "", "", TimedOut: true),
        ];
        CliRunResult[] antigravity =
        [
            new(0, "Fetching available models...\na\tA\n", ""),
            new(0, "Fetching available models...\na\tA\nb\tB\n", ""),
            new(1, "", "Please sign in to view available models."),
            new(4, "", "network down"),
        ];
        CliRunResult[] codex = [new(1, "", ""), new(0, "", "")];
        var cases = claude.Select(result => (CliTool.ClaudeCode, result))
            .Concat(antigravity.Select(result => (CliTool.Antigravity, result)))
            .Concat(codex.Select(result => (CliTool.Codex, result)));
        foreach (var (tool, result) in cases)
        {
            var (line, _) = CliClient.LoginStatusMessage(tool, result);
            Assert.Equal(CliClient.LoginStatus(tool, result).Text, line.English);
            AssertLocalized(line.Localized, line.English);
        }
        // The CLI's own words alone have no key; followed by the advice, a Windows key joins the two.
        Assert.Null(CliClient.LoginStatusMessage(CliTool.Codex, new CliRunResult(0, "", "Logged in using ChatGPT")).Text.Localized);
        var (loggedOut, _) = CliClient.LoginStatusMessage(CliTool.Codex, new CliRunResult(1, "", "Not logged in"));
        Assert.Equal("Not logged in. Run `codex login` once in Terminal to log in.", loggedOut.English);
        AssertLocalized(loggedOut.Localized, loggedOut.English);
        Assert.Equal("%@. %@", loggedOut.Localized?.MessageKey);
        Assert.Equal("Not logged in", loggedOut.Localized?.MessageArguments[0]);
    }

    // Notes (ChatCompletionsClient.swift, NotesPipeline.swift, NotesResponse.swift)

    [Fact]
    public void ChatCompletionsErrors()
    {
        ChatCompletionsError[] errors =
        [
            new ChatCompletionsError.MissingToken(),
            new ChatCompletionsError.InvalidUrl(),
            new ChatCompletionsError.Transport("The request timed out."),
            ChatCompletionsError.Transport.TimedOut(),
            new ChatCompletionsError.Unreachable("localhost"),
            new ChatCompletionsError.HttpStatus(500, "  "),
            new ChatCompletionsError.HttpStatus(401, """{"error":{"message":"bad key"}}"""),
            new ChatCompletionsError.HttpStatus(404, "not found {x}"),
            new ChatCompletionsError.InvalidJson("<html>"),
            new ChatCompletionsError.EmptyContent(),
        ];
        foreach (var error in errors)
        {
            AssertLocalized(new ChatCompletionsException(error));
        }
    }

    [Fact]
    public void NotesPipelineErrors()
    {
        AssertLocalized(new NotesPipelineException(new NotesPipelineError.EmptyTranscript()));
        // The key of the Mac's NotesFlowViewModel (app catalog), which reports the same text.
        AssertLocalized(new NotesPipelineException(new NotesPipelineError.SrtUnreadable(@"C:\x.srt", "gone")));
    }

    [Fact]
    public void NotesResponseErrors()
    {
        foreach (var kind in Enum.GetValues<NotesResponseErrorKind>())
        {
            AssertLocalized(new NotesResponseException(kind, "Unexpected character at 0"));
        }
    }

    // Naming (OutputWriter.swift)

    [Fact]
    public void OutputWriterErrors()
    {
        OutputWriterError[] errors =
        [
            new OutputWriterError.UnusableMeetingName(),
            new OutputWriterError.RenameFailed(@"C:\out\a.srt", @"C:\out\b.srt", "denied", []),
            new OutputWriterError.RenameFailed(@"C:\out\a.srt", @"C:\out\b.srt", "denied", ["a.wav", "a.md"]),
            new OutputWriterError.WriteFailed(@"C:\out\n.md", "disk full", []),
            new OutputWriterError.WriteFailed(@"C:\out\n.md", "disk full", ["x.srt"]),
            new OutputWriterError.TrashFailed(@"C:\out\n.md", "in use", []),
            new OutputWriterError.TrashFailed(@"C:\out\n.md", "in use", ["n.md"]),
        ];
        foreach (var error in errors)
        {
            AssertLocalized(new OutputWriterException(error));
        }
    }

    // Audio (MonoResampler.swift, WavWriter.swift)

    [Fact]
    public void AudioErrors()
    {
        AssertLocalized(new AudioConversionException(AudioConversionErrorKind.UnsupportedFormat, "8 channels"));
        AssertLocalized(new AudioConversionException(AudioConversionErrorKind.ConversionFailed, "no output"));
        AssertLocalized(new WavException(WavErrorKind.NotAWavFile, @"C:\x.wav"));
        AssertLocalized(new WavException(WavErrorKind.Closed, null));
        Assert.Null(new WavException("custom").LocalizedMessage);
    }

    // ModelStore (ModelDownloader.swift, ModelStore.swift)

    [Fact]
    public void ModelDownloadErrors()
    {
        ModelDownloadError[] errors =
        [
            new ModelDownloadError.HttpStatus("ggml-base.bin", 503),
            new ModelDownloadError.SizeMismatch("ggml-base.bin", 147_951_465, 1_024),
            new ModelDownloadError.RangeNotSatisfiable("ggml-base.bin"),
            new ModelDownloadError.InvalidResponse("ggml-base.bin"),
        ];
        foreach (var error in errors)
        {
            AssertLocalized(new ModelDownloadException(error));
        }
    }

    [Fact]
    public void CatalogLoadError()
    {
        var message = Hearsay.Core.ModelStore.ModelStore.CatalogLoadError("Embedded resource is missing.");
        AssertLocalized(message, "The built-in model list could not be read: Embedded resource is missing.");
    }

    // Whisper (WhisperEngine.swift, app catalog)

    [Fact]
    public void WhisperEngineErrors()
    {
        AssertLocalized(new WhisperEngineException(WhisperEngineError.NoActiveModel, null));
        AssertLocalized(new WhisperEngineException(WhisperEngineError.ModelNotReady, "Large v3 Turbo (q5_0)"));
        Assert.Null(new WhisperEngineException("whisper_full failed (-1).").LocalizedMessage);
        // Windows only: the technical cause is the argument of a Windows key.
        var cause = new InvalidOperationException(@"whisper.cpp could not load the model C:\m\ggml-base.bin.");
        var loadFailed = WhisperEngineException.LoadFailed(cause);
        AssertLocalized(loadFailed);
        Assert.Equal(@"Could not load the speech model: whisper.cpp could not load the model C:\m\ggml-base.bin.", loadFailed.Message);
        Assert.Equal(WhisperEngineError.LoadFailed, loadFailed.Error);
        Assert.Same(cause, loadFailed.InnerException);
    }

    // Updates (UpdateChecker.swift, UpdatePackage.swift, UpdateService.swift, UpdateInstaller.swift)

    [Fact]
    public void UpdateCheckErrors()
    {
        UpdateCheckError[] errors =
        [
            new UpdateCheckError.Offline(), new UpdateCheckError.HttpStatus(500),
            new UpdateCheckError.NoRelease(), new UpdateCheckError.Parse(),
        ];
        foreach (var error in errors)
        {
            AssertLocalized(new UpdateCheckException(error));
            var outcome = new UpdateCheckOutcome.Failed(error.Description, error.Localized);
            AssertLocalized(((ILocalizedError)outcome).LocalizedMessage, outcome.Message);
        }
    }

    [Fact]
    public void UpdatePackageErrors()
    {
        UpdatePackageError[] errors =
        [
            new UpdatePackageError.ChecksumMissing("Hearsay-1.2.0-win-x64.zip"),
            new UpdatePackageError.ChecksumMismatch("Hearsay-1.2.0-win-x64.zip"),
            new UpdatePackageError.SignatureInvalid("not signed"),
            new UpdatePackageError.WrongVersion("1.1.0", "1.2.0"),
            new UpdatePackageError.WrongVersion(null, "1.2.0"),
            new UpdatePackageError.DownloadFailed("No such host is known."),
            UpdatePackageError.DownloadFailed.HttpStatus(404),
            UpdatePackageError.DownloadFailed.TimedOut(),
            // The Windows wording (zip file, Hearsay.exe, signer, product name), under Windows keys.
            new UpdatePackageError.ExtractFailed("bad zip"), new UpdatePackageError.AppNotFound(2),
            new UpdatePackageError.SignerMismatch("CN=a", "CN=b"), new UpdatePackageError.SignerMismatch(null, null),
            new UpdatePackageError.WrongIdentifier("Notepad"), new UpdatePackageError.WrongIdentifier(null),
            new UpdatePackageError.NoPackage(),
        ];
        foreach (var error in errors)
        {
            AssertLocalized(new UpdatePackageException(error));
        }
        // The English values Core inserts are keys of their own.
        Assert.Equal(CommonMessages.Unknown, new UpdatePackageError.WrongVersion(null, "1.2.0").Localized.MessageArguments[0]);
        Assert.Equal(CommonMessages.Unknown, new UpdatePackageError.SignerMismatch("CN=a", null).Localized.MessageArguments[1]);
        Assert.Equal(CommonMessages.None, new UpdatePackageError.WrongIdentifier(null).Localized.MessageArguments[0]);
        var http = UpdatePackageError.DownloadFailed.HttpStatus(404);
        Assert.Equal("The download failed: HTTP 404", http.Description);
        Assert.Equal(CommonMessages.HttpStatus(404), http.Localized.MessageArguments[0]);
        Assert.Equal("The download failed: The request timed out.", UpdatePackageError.DownloadFailed.TimedOut().Description);
    }

    [Fact]
    public void InstallLocationProblems()
    {
        InstallLocationProblem[] problems =
        [
            new InstallLocationProblem.RunningFromArchive(), new InstallLocationProblem.NotAnInstallFolder(),
            new InstallLocationProblem.FolderNotWritable(@"C:\Program Files\Hearsay"),
        ];
        foreach (var problem in problems)
        {
            AssertLocalized(new InstallLocationException(problem));
        }
    }

    [Fact]
    public void CommonMessagesAreKeys()
    {
        AssertLocalized(CommonMessages.RequestTimedOut, "The request timed out.");
        AssertLocalized(CommonMessages.Unknown, "unknown");
        AssertLocalized(CommonMessages.None, "none");
        AssertLocalized(CommonMessages.HttpStatus(503), "HTTP 503");
        Assert.Equal("1,234,567", new GroupedNumber(1_234_567).ToString());
        Assert.Equal("1.234.567", string.Format(CultureInfo.GetCultureInfo("de-DE"), "{0}", new GroupedNumber(1_234_567)));
    }

    [Fact]
    public void UpdateTextsHaveTheirKeys()
    {
        AssertLocalized(UpdateTexts.VersionLineMessage("1.2.0", "42"), UpdateTexts.VersionLine("1.2.0", "42"));
        AssertLocalized(UpdateTexts.UpToDateMessage("1.2.0"), UpdateTexts.UpToDate("1.2.0"));
        AssertLocalized(UpdateTexts.AvailableMessage("1.3.0"), UpdateTexts.Available("1.3.0"));
        AssertLocalized(UpdateTexts.YouHaveVersionMessage("1.2.0"), UpdateTexts.YouHaveVersion("1.2.0"));
        AssertLocalized(UpdateTexts.LastCheckedMessage("today"), UpdateTexts.LastChecked("today"));
        AssertLocalized(UpdateTexts.DownloadingMessage("1.3.0"), UpdateTexts.Downloading("1.3.0"));
        AssertLocalized(UpdateTexts.VerifyingMessage("1.3.0"), UpdateTexts.Verifying("1.3.0"));
        AssertLocalized(UpdateTexts.ReadyToInstallMessage("1.3.0"), UpdateTexts.ReadyToInstall("1.3.0"));
        AssertLocalized(UpdateTexts.InstallingMessage("1.3.0"), UpdateTexts.Installing("1.3.0"));
        AssertLocalized(UpdateTexts.BytesOfMessage("1 MB", "2 MB"), UpdateTexts.BytesOf("1 MB", "2 MB"));
        string[] plain =
        [
            UpdateTexts.DownloadPageOpens, UpdateTexts.CouldNotCheck, UpdateTexts.NoRepository,
            UpdateTexts.CheckForUpdatesCommand, UpdateTexts.InstallUpdate, UpdateTexts.ViewOnGitHub, UpdateTexts.Download,
            UpdateTexts.Later, UpdateTexts.SoftwareUpdates, UpdateTexts.AutomaticallyCheck,
            UpdateTexts.AutomaticallyCheckCaption, UpdateTexts.CheckNow, UpdateTexts.NotCheckedYet,
            UpdateTexts.CannotInstallHere, UpdateTexts.CouldNotDownload, UpdateTexts.CouldNotVerify,
            UpdateTexts.CouldNotInstall, UpdateTexts.SoftwareUpdateWindowTitle, UpdateTexts.WillQuitAndReopen,
            UpdateTexts.InstallAndRelaunch, UpdateTexts.FinishRecordingFirst,
        ];
        foreach (var text in plain)
        {
            Assert.True(Keys.Value.Contains(text), $"not in strings-en.json: {text}");
        }
        // Windows only, under a Windows key.
        AssertLocalized(UpdateTexts.PreviousInstallFailedMessage, UpdateTexts.PreviousInstallFailed);
    }
}
