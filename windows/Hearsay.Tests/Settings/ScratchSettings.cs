using Hearsay.Core.Settings;

namespace Hearsay.Tests.Settings;

/// <summary>
/// Hands out throwaway settings folders under <see cref="Path.GetTempPath"/>
/// and deletes them all when disposed, so no test ever reads or writes
/// <c>%APPDATA%\Hearsay</c>. The Windows counterpart of
/// mac/HearsayCore/Tests/HearsayCoreTests/ScratchDefaults.swift; hold one
/// per test class (xUnit creates a class instance per test and disposes it).
/// </summary>
internal sealed class ScratchSettings : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"HearsaySettingsTests-{Guid.NewGuid():N}");

    public ScratchSettings() => Directory.CreateDirectory(root);

    /// <summary>A new folder path with nothing in it (not created: the settings create it on first write).</summary>
    public string Make() => Path.Combine(root, Guid.NewGuid().ToString("N"));

    /// <summary>A fresh read of the folder's settings.json, like reading the Mac's UserDefaults suite directly.</summary>
    public static SettingsFile Raw(string folder) => new(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
