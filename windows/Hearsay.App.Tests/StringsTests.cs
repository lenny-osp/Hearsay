using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Hearsay.Core;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Hearsay.Core.Updates;
using Hearsay.Whisper;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="Strings"/> through MRT Core with no package identity, from the
/// test host (PLAN.md 18.3, "Localization"): lookups in every interface
/// language against the shared translations, the resource names
/// import-strings.py writes, and <see cref="Strings.Describe"/> of Core errors
/// that carry their catalog key.
/// </summary>
public sealed class StringsTests
{
    public static TheoryData<InterfaceLanguage> Languages() =>
    [
        InterfaceLanguage.English, InterfaceLanguage.German, InterfaceLanguage.Spanish,
        InterfaceLanguage.TraditionalChinese, InterfaceLanguage.SimplifiedChinese,
    ];

    [Theory]
    [MemberData(nameof(Languages))]
    public void LooksUpEachCatalogInEveryLanguage(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "app", "Record"), Strings.TabRecord);
        Assert.Equal(Translations.Text(language, "app", "Cancel"), Strings.Cancel);
        Assert.Equal(Translations.Text(language, "core", "Auto"), Strings.LanguageAuto);
        Assert.Equal(Translations.Text(language, "windows", "Show in Explorer"), Strings.ShowInExplorer);
        Assert.Equal(Translations.Format(language, "app", "%@ of %@", "1 MB", "2 MB"), Strings.DownloadProgress("1 MB", "2 MB"));
        Assert.Equal(Translations.Format(language, "app", "Transcribing… %lld%%", 42), Strings.StateTranscribing(42));
        Assert.Equal(Translations.Text(language, "core", "No token"), Strings.CoreText("No token"));
        Assert.Equal("not a catalog text", Strings.CoreText("not a catalog text"));
    }

    [Fact]
    public void ResourceNamesAreThoseOfImportStrings()
    {
        Assert.Equal("app_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Record")))[..16],
            Strings.ResourceName("app", "Record"));
        // The generated English .resw holds the key's English under that name.
        var resw = XDocument.Load(Path.Combine(SharedFiles.Directory, "..", "windows", "Hearsay.App", "Strings", "en", "Resources.resw"));
        string? Value(string name) => resw.Root?.Elements("data")
            .FirstOrDefault(data => (string?)data.Attribute("name") == name)?.Element("value")?.Value;
        Assert.Equal("Record", Value(Strings.ResourceName("app", "Record")));
        Assert.Equal("{0} of {1}", Value(Strings.ResourceName("app", "%@ of %@")));
        Assert.Equal("{0} returned empty output.", Value(Strings.ResourceName("core", "%@ returned empty output.")));
    }

    [Fact]
    public void DescribesALocalizedCoreErrorInGerman()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German);
        var codex = new CliProviderException(new CliProviderError.EmptyOutput(CliTool.Codex));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "core", "%@ returned empty output.", "Codex CLI"),
            Strings.Describe(codex));
        Assert.NotEqual(codex.Message, Strings.Describe(codex));

        // Nested key and the CLI's own excerpt after it.
        var loggedOut = new CliProviderException(new CliProviderError.NotLoggedIn(CliTool.Codex, "Not logged in"));
        Assert.Equal(
            Translations.Format(InterfaceLanguage.German, "core",
                "%@ is not logged in. Run `%@` once in Terminal to log in with your ChatGPT account.", "Codex CLI", "codex login")
            + "\nNot logged in",
            Strings.Describe(loggedOut));

        // A Core text whose Mac key is in the app catalog.
        var model = new WhisperEngineException(WhisperEngineError.ModelNotReady, "Large v3 Turbo");
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "app",
            "The model %@ is not fully downloaded. Finish the download in Models.", "Large v3 Turbo"), Strings.Describe(model));
    }

    /// <summary>Core's Windows-only texts (catalog "windows"), with the English values Core inserts translated too.</summary>
    [Fact]
    public void DescribesWindowsOnlyCoreTextsInGerman()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German, "de-DE");
        var tooLong = new CliProviderException(new CliProviderError.CommandLineTooLong(CliTool.Copilot, 40_000));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "windows",
                "%@ cannot take a transcript this long on Windows: the command line would be %@ characters, and Windows allows %@. "
                + "Use Claude Code, Codex, or an HTTP provider for long meetings.", "GitHub Copilot CLI", "40.000", "32.766"),
            Strings.Describe(tooLong));
        Assert.NotEqual(tooLong.Message, Strings.Describe(tooLong));

        var product = new UpdatePackageException(new UpdatePackageError.WrongIdentifier(null));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "windows", "The new app has the product name %@, not Hearsay.",
            Translations.Text(InterfaceLanguage.German, "windows", "none")), Strings.Describe(product));

        // A Mac key (app catalog) with a nested Windows key.
        var version = new UpdatePackageException(new UpdatePackageError.WrongVersion(null, "1.2.0"));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "app", "The new app is version %@, not %@.",
            Translations.Text(InterfaceLanguage.German, "windows", "unknown"), "1.2.0"), Strings.Describe(version));

        var timedOut = new ChatCompletionsException(ChatCompletionsError.Transport.TimedOut());
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "core",
                "API call failed: A network connection or HTTP client error occurred. %@",
                Translations.Text(InterfaceLanguage.German, "windows", "The request timed out.")),
            Strings.Describe(timedOut));

        var load = WhisperEngineException.LoadFailed(new InvalidOperationException("whisper.cpp could not load the model m.bin."));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "windows", "Could not load the speech model: %@",
            "whisper.cpp could not load the model m.bin."), Strings.Describe(load));

        var srt = new NotesPipelineException(new NotesPipelineError.SrtUnreadable(@"C:\x.srt", "gone"));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "app", "Error: SRT file not found or unreadable (%@): %@",
            @"C:\x.srt", "gone"), Strings.Describe(srt));

        Assert.Equal(Translations.Text(InterfaceLanguage.German, "windows", UpdateTexts.PreviousInstallFailed),
            Strings.Localize(UpdateTexts.PreviousInstallFailedMessage));
        var location = new InstallLocationException(new InstallLocationProblem.FolderNotWritable(@"C:\Apps\Hearsay"));
        Assert.Equal(Translations.Format(InterfaceLanguage.German, "windows",
                "Hearsay cannot write to the folder %@. Move the Hearsay folder to a folder you can write to, open it from there, then check for updates again.",
                @"C:\Apps\Hearsay"),
            Strings.Describe(location));
    }

    [Fact]
    public void DescribeFallsBackToTheEnglish()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German);
        // No key (a technical message): the English message.
        var technical = new WhisperEngineException("whisper_full failed (-1).");
        Assert.Equal(technical.Message, Strings.Describe(technical));
        // Plain English a core key matches exactly.
        Assert.Equal(Translations.Text(InterfaceLanguage.German, "core", "No token"),
            Strings.Describe(new InvalidOperationException("No token")));
        Assert.Equal("boom", Strings.Describe(new InvalidOperationException("boom")));
    }

    [Fact]
    public void DescribeInEnglishIsTheMessage()
    {
        using var english = new InterfaceLanguageScope(InterfaceLanguage.English);
        Exception[] errors =
        [
            new CliProviderException(new CliProviderError.Failed(CliTool.Copilot, 1, "not logged in")),
            new ChatCompletionsException(new ChatCompletionsError.HttpStatus(401, """{"error":{"message":"bad key"}}""")),
            new WhisperEngineException(WhisperEngineError.NoActiveModel, null),
            new NotesResponseException(NotesResponseErrorKind.InvalidJson, "x"),
        ];
        foreach (var error in errors)
        {
            Assert.Equal(error.Message, Strings.Describe(error));
        }
        Assert.IsAssignableFrom<ILocalizedError>(errors[0]);
    }
}
