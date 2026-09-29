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

    /// <summary>Windows only: no key in shared/localization yet, so the app shows the English.</summary>
    [Fact]
    public void CommandLineTooLongHasNoKey()
    {
        var error = new CliProviderException(new CliProviderError.CommandLineTooLong(CliTool.Copilot, 40_000));
        Assert.Null(error.LocalizedMessage);
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
        // The CLI's own words have no key.
        Assert.Null(CliClient.LoginStatusMessage(CliTool.Codex, new CliRunResult(0, "", "Logged in using ChatGPT")).Text.Localized);
        Assert.Null(CliClient.LoginStatusMessage(CliTool.Codex, new CliRunResult(1, "", "Not logged in")).Text.Localized);
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
        // No Mac key: the Mac's flow reports an unreadable SRT itself.
        Assert.Null(new NotesPipelineException(new NotesPipelineError.SrtUnreadable(@"C:\x.srt", "gone")).LocalizedMessage);
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
        UpdatePackageError[] keyed =
        [
            new UpdatePackageError.ChecksumMissing("Hearsay-1.2.0-win-x64.zip"),
            new UpdatePackageError.ChecksumMismatch("Hearsay-1.2.0-win-x64.zip"),
            new UpdatePackageError.SignatureInvalid("not signed"),
            new UpdatePackageError.WrongVersion("1.1.0", "1.2.0"),
            new UpdatePackageError.WrongVersion(null, "1.2.0"),
            new UpdatePackageError.DownloadFailed("HTTP 404"),
        ];
        foreach (var error in keyed)
        {
            AssertLocalized(new UpdatePackageException(error));
        }
        // The Windows wording (zip file, Hearsay.exe, signer, product name) has no key yet.
        UpdatePackageError[] windowsOnly =
        [
            new UpdatePackageError.ExtractFailed("bad zip"), new UpdatePackageError.AppNotFound(2),
            new UpdatePackageError.SignerMismatch("CN=a", "CN=b"), new UpdatePackageError.WrongIdentifier(null),
            new UpdatePackageError.NoPackage(),
        ];
        foreach (var error in windowsOnly)
        {
            Assert.Null(new UpdatePackageException(error).LocalizedMessage);
            Assert.DoesNotContain(error.Description, Keys.Value);
        }
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
        // Windows only, not in shared/localization yet.
        Assert.DoesNotContain(UpdateTexts.PreviousInstallFailed, Keys.Value);
    }
}
