using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Hearsay.Core;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
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

    [Fact]
    public void DescribeFallsBackToTheEnglish()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German);
        // No key yet (Windows only): the English message.
        var tooLong = new CliProviderException(new CliProviderError.CommandLineTooLong(CliTool.Copilot, 40_000));
        Assert.Equal(tooLong.Message, Strings.Describe(tooLong));
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
