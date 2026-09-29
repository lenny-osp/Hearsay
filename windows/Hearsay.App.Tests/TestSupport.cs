using System.Globalization;
using System.Text.Json;
using Hearsay.Core;
using Hearsay.Core.Settings;

// Strings keeps one language and one cache for the process, and several tests
// switch it: no test runs in parallel with another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Hearsay.App.Tests;

/// <summary>A folder under the temp directory, removed on dispose (never the owner's settings or output folder).</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HearsayAppTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string name, string text)
    {
        var file = System.IO.Path.Combine(Path, name);
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Secrets in memory, so no test touches Credential Manager.</summary>
internal sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);

    public string? Read(string account) => secrets.GetValueOrDefault(account);

    public void Write(string secret, string account) => secrets[account] = secret;

    public void Delete(string account) => secrets.Remove(account);
}

/// <summary>
/// Applies an interface language (the app's own <see cref="Strings.Apply"/>)
/// and culture for one test, and goes back to English afterwards.
/// </summary>
internal sealed class InterfaceLanguageScope : IDisposable
{
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public InterfaceLanguageScope(InterfaceLanguage language, string? cultureName = null)
    {
        Strings.Apply(language);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName ?? language.Code());
    }

    public void Dispose()
    {
        Strings.Apply(InterfaceLanguage.English);
        CultureInfo.CurrentCulture = culture;
    }
}

/// <summary>The shared translations (shared/localization), read as the tests' expected values.</summary>
internal static class Translations
{
    private static readonly Dictionary<string, Dictionary<(string, string), string>> Cache = new(StringComparer.Ordinal);

    /// <summary>The text of <paramref name="key"/> in <paramref name="language"/> as the catalog has it (Mac placeholders).</summary>
    public static string Text(InterfaceLanguage language, string catalog, string key)
    {
        var file = language == InterfaceLanguage.English ? "strings-en.json" : language.Code() + ".json";
        if (!Cache.TryGetValue(file, out var entries))
        {
            using var document = JsonDocument.Parse(SharedFiles.ReadText("localization", file));
            entries = [];
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var text = entry.GetProperty(language == InterfaceLanguage.English ? "english" : "translation").GetString() ?? "";
                entries[(entry.GetProperty("catalog").GetString() ?? "", entry.GetProperty("key").GetString() ?? "")] = text;
            }
            Cache[file] = entries;
        }
        return entries[(catalog, key)];
    }

    /// <summary><see cref="Text"/> with its placeholders filled, as the app shows it.</summary>
    public static string Format(InterfaceLanguage language, string catalog, string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, LocalizedMessage.ToDotNetFormat(Text(language, catalog, key)), arguments);
}
