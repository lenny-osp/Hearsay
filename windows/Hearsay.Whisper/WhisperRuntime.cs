using System.Diagnostics;
using System.Runtime.InteropServices;
using Hearsay.Whisper.Native;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Hearsay.Whisper;

/// <summary>
/// The whisper.cpp native library for this process, chosen and loaded by
/// Whisper.net's own loader (PLAN.md 18.3, Whisper row: default order
/// Vulkan then CPU, <c>RuntimeOptions.LoadedLibrary</c> logged). Hearsay then
/// binds the C API of that same <c>whisper.dll</c> itself
/// (<see cref="WhisperNative"/>); see <see cref="WhisperEngine"/> for why.
/// No Swift counterpart (the Mac links MLX statically).
/// </summary>
/// <remarks>
/// Whisper.net loads the library on its first native call; Hearsay makes that
/// call with <see cref="WhisperFactory.GetRuntimeInfo"/>, reads which runtime
/// won from <see cref="RuntimeOptions.LoadedLibrary"/>, finds the loaded
/// <c>whisper.dll</c> among the process's modules (so the path is the one the
/// loader really used, not a guess at its search order), and takes its own
/// reference to it with <see cref="NativeLibrary.Load(string)"/>. The library
/// stays loaded for the life of the process, as Whisper.net's does.
/// </remarks>
public static class WhisperRuntime
{
    private static readonly Lock Gate = new();
    private static LoadedRuntime? loaded;

    /// <summary>
    /// The order Whisper.net tries the runtimes in, set before the first
    /// engine loads (later changes have no effect: a process loads one
    /// whisper.dll). Null keeps Whisper.net's default, which with the two
    /// runtime packages Hearsay ships is Vulkan, then CPU.
    /// </summary>
    public static IReadOnlyList<RuntimeLibrary>? PreferredOrder { get; set; }

    /// <summary>The runtime in use, once loaded; null before the first engine loads.</summary>
    public static RuntimeLibrary? Library => loaded?.Library;

    /// <summary>Full path of the loaded whisper.dll; null before the first engine loads.</summary>
    public static string? LibraryPath => loaded?.Path;

    /// <summary>whisper.cpp's system info line (CPU features, backends), for the log.</summary>
    public static string? SystemInfo => loaded?.SystemInfo;

    /// <summary>A short name for the log and the Record tab: "Vulkan", "Cpu", "Cuda".</summary>
    public static string? LibraryName => loaded?.Library.ToString();

    /// <summary>True when the loaded runtime computes on the CPU only.</summary>
    public static bool IsCpu => loaded?.Library is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx;

    /// <summary>Loads the library once per process and returns the bound API.</summary>
    /// <exception cref="PlatformNotSupportedException">No runtime could be loaded, or its whisper.dll lacks an export.</exception>
    internal static LoadedRuntime Load()
    {
        lock (Gate)
        {
            if (loaded is { } existing)
            {
                return existing;
            }
            if (PreferredOrder is { } order)
            {
                RuntimeOptions.RuntimeLibraryOrder = [.. order];
            }
            string? systemInfo;
            try
            {
                systemInfo = WhisperFactory.GetRuntimeInfo();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new PlatformNotSupportedException(
                    $"No whisper.cpp runtime could be loaded: {error.Message}", error);
            }
            if (RuntimeOptions.LoadedLibrary is not { } library)
            {
                throw new PlatformNotSupportedException("No whisper.cpp runtime could be loaded.");
            }
            var path = LoadedModulePath("whisper.dll")
                ?? throw new PlatformNotSupportedException(
                    $"Whisper.net reports the {library} runtime loaded, but no whisper.dll is loaded in this process.");
            var handle = NativeLibrary.Load(path);
            var api = new WhisperNative(handle, path);
            loaded = new LoadedRuntime(library, path, (systemInfo ?? "").Trim(), api);
            return loaded;
        }
    }

    /// <summary>The full path of a module loaded in this process, matched by file name ignoring case.</summary>
    private static string? LoadedModulePath(string fileName)
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            using (module)
            {
                if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return module.FileName;
                }
            }
        }
        return null;
    }

    internal sealed record LoadedRuntime(RuntimeLibrary Library, string Path, string SystemInfo, WhisperNative Api);
}
