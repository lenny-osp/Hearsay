using System.Reflection;

namespace Hearsay.Tests;

/// <summary>
/// Locates the repo's <c>shared/</c> folder (vectors, prompts, fixtures, help)
/// for tests. The path is stamped into the test assembly at build time from
/// <c>Directory.Build.props</c>, so the tests never keep a copy of a shared file.
/// </summary>
public static class SharedFiles
{
    public static string Directory { get; } = Resolve();

    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Directory, .. parts]);

    /// <summary>Reads a shared text file byte-exactly (UTF-8, newlines untouched).</summary>
    public static string ReadText(params string[] parts) =>
        File.ReadAllText(Path(parts), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Resolve()
    {
        var dir = typeof(SharedFiles).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "HearsaySharedDir")?.Value;
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException(
                $"shared/ folder not found (HearsaySharedDir = '{dir}'); build from the repo checkout.");
        }
        return dir;
    }
}
