using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hearsay.App.Features.History;

/// <summary>
/// File Explorer and the shell for the History and Models tabs: the
/// counterparts of <c>NSWorkspace.activateFileViewerSelecting</c> and
/// <c>NSWorkspace.open</c> in mac/Hearsay/Features/History/HistoryViewModel.swift.
/// </summary>
internal static class ExplorerShell
{
    private const int ErrorNoAssociation = 1155;

    /// <summary>
    /// Opens one File Explorer window on the folder of <paramref name="paths"/>
    /// with all of them selected (<c>SHOpenFolderAndSelectItems</c>; they
    /// must share one folder). Returns null, or the reason it failed.
    /// </summary>
    public static string? Reveal(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(paths[0]));
        if (folder is null) return null;
        var allocated = new List<IntPtr>();
        try
        {
            var hr = NativeMethods.SHParseDisplayName(folder, IntPtr.Zero, out var folderId, 0, out _);
            if (hr != 0) return Marshal.GetExceptionForHR(hr)?.Message;
            allocated.Add(folderId);
            var children = new List<IntPtr>();
            foreach (var path in paths)
            {
                hr = NativeMethods.SHParseDisplayName(Path.GetFullPath(path), IntPtr.Zero, out var itemId, 0, out _);
                if (hr != 0) continue;
                allocated.Add(itemId);
                children.Add(NativeMethods.ILFindLastID(itemId));
            }
            hr = NativeMethods.SHOpenFolderAndSelectItems(folderId, (uint)children.Count, [.. children], 0);
            return hr == 0 ? null : Marshal.GetExceptionForHR(hr)?.Message;
        }
        finally
        {
            foreach (var id in allocated) Marshal.FreeCoTaskMem(id);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> in its default app. A file type no app
    /// claims (common for <c>.md</c> and <c>.srt</c>) opens in Notepad, as
    /// the Mac falls back to the default plain-text editor. Returns null, or
    /// the message to show.
    /// </summary>
    public static string? Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ErrorNoAssociation)
        {
            try
            {
                var notepad = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                notepad.ArgumentList.Add(path);
                using var process = Process.Start(notepad);
                return null;
            }
            catch (Win32Exception)
            {
                return Strings.NoAppToOpen(Path.GetFileName(path));
            }
        }
        catch (Win32Exception error)
        {
            return error.Message;
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", EntryPoint = "SHParseDisplayName", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr idList, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll", EntryPoint = "SHOpenFolderAndSelectItems")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] children, uint flags);

        [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr ILFindLastID(IntPtr idList);
    }
}
