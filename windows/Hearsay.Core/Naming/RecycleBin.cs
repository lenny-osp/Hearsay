using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hearsay.Core.Naming;

/// <summary>
/// The Recycle Bin through the shell's <c>IFileOperation</c>, the Windows
/// side of Swift's <c>FileManager.trashItem(at:resultingItemURL:)</c> in
/// mac/HearsayCore/Sources/HearsayCore/Naming/OutputWriter.swift
/// (<c>defaultTrash</c>), with the move back that <c>restoreFromTrash</c>
/// does on the Mac (PLAN.md 4.3, 18.4 "Recycle Bin").
///
/// <see cref="Recycle"/> deletes with <c>FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE</c>
/// and a progress sink whose <c>PostDeleteItem</c> hands over the item as it
/// now sits in the bin; its parsing name is the handle <see cref="Restore"/>
/// takes to move the file back to its folder and name (<c>MoveItem</c> on the
/// item as a child of the Recycle Bin, then the bin's <c>$I</c> record of it
/// is removed, see <see cref="RemoveRecord"/>). A file the bin cannot
/// take (network share, larger than the bin) asks before it is deleted for
/// good (<c>FOF_WANTNUKEWARNING</c>) and gives no handle.
///
/// Plain <c>[ComImport]</c> interop with shell32 (no package). Each operation
/// runs on a single-threaded apartment: the calling thread when it is one,
/// else a short-lived thread of its own.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RecycleBin
{
    /// <summary>
    /// Sends <paramref name="path"/> to the Recycle Bin and returns the parsing
    /// name of the recycled item, or null when the file was deleted instead
    /// (after the user agreed to the warning). Throws <see cref="IOException"/>
    /// when the operation fails or the user declines.
    /// </summary>
    public static string? Recycle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OnSta(() => RecycleNow(fullPath));
    }

    /// <summary>
    /// Moves the recycled item <paramref name="location"/> (a handle from
    /// <see cref="Recycle"/>) back to <paramref name="originalPath"/>. Never
    /// replaces an existing file; throws <see cref="IOException"/> on failure.
    /// </summary>
    public static void Restore(string location, string originalPath)
    {
        var original = Path.GetFullPath(originalPath);
        OnSta(() =>
        {
            RestoreNow(location, original);
            return true;
        });
    }

    /// <summary>
    /// Removes for good the item <see cref="Recycle"/> put in the bin (the debug
    /// replay's clean-up of a file its own run recycled). <paramref name="location"/>
    /// must be a <c>$R</c> file directly inside a user's folder of a
    /// <c>$Recycle.Bin</c>, and the <c>$I</c> record beside it must name
    /// <paramref name="originalPath"/>; anything else throws
    /// <see cref="IOException"/> and nothing is deleted. Other items are never touched.
    /// </summary>
    public static void Purge(string location, string originalPath)
    {
        var original = Path.GetFullPath(originalPath);
        var file = Path.GetFullPath(location);
        var name = Path.GetFileName(file);
        var userFolder = Path.GetDirectoryName(file);
        var binFolder = userFolder is null ? null : Path.GetDirectoryName(userFolder);
        if (!name.StartsWith("$R", StringComparison.Ordinal) || binFolder is null
            || !string.Equals(Path.GetFileName(binFolder), "$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"{file} is not an item of the Recycle Bin");
        }
        var record = Path.Combine(userFolder ?? "", "$I" + name[2..]);
        var info = new FileInfo(record);
        if (!info.Exists || info.Length > 64 * 1024 || !RecordNames(File.ReadAllBytes(record), original))
        {
            throw new IOException($"The Recycle Bin record of {file} does not name {original}");
        }
        File.Delete(file);
        File.Delete(record);
    }

    private static string? RecycleNow(string fullPath)
    {
        var item = ShellItem(fullPath);
        var operation = CreateOperation();
        try
        {
            var sink = new DeleteSink();
            Check(operation.SetOperationFlags(Native.FofAllowUndo | Native.FofNoConfirmation | Native.FofSilent
                | Native.FofNoErrorUi | Native.FofWantNukeWarning | Native.FofxRecycleOnDelete
                | Native.FofxEarlyFailure), "The file could not be moved to the Recycle Bin");
            Check(operation.DeleteItem(item, sink), "The file could not be moved to the Recycle Bin");
            var result = operation.PerformOperations();
            if (Aborted(operation))
            {
                throw new IOException("Moving the file to the Recycle Bin was cancelled");
            }
            Check(result, "The file could not be moved to the Recycle Bin");
            Check(sink.Result, "The file could not be moved to the Recycle Bin");
            return sink.Location;
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(operation);
            _ = Marshal.FinalReleaseComObject(item);
        }
    }

    private static void RestoreNow(string location, string original)
    {
        if (File.Exists(original) || Directory.Exists(original))
        {
            throw new IOException($"The file {original} already exists");
        }
        var folderPath = Path.GetDirectoryName(original)
            ?? throw new IOException($"The file {original} has no folder to go back to");
        // The item as a child of the Recycle Bin folder (the path alone is a
        // plain file item in C:\$Recycle.Bin\<SID>).
        var item = TryShellItem(Native.RecycleBinFolder + "\\" + location) ?? ShellItem(location);
        var folder = ShellItem(folderPath);
        var operation = CreateOperation();
        try
        {
            // No FOF_ALLOWUNDO: putting the file back is not a new step for
            // Explorer's Undo. No collision flags: the check above keeps an
            // existing file from being replaced.
            Check(operation.SetOperationFlags(Native.FofNoConfirmation | Native.FofSilent | Native.FofNoErrorUi
                | Native.FofxEarlyFailure), "The file could not be restored from the Recycle Bin");
            Check(operation.MoveItem(item, folder, Path.GetFileName(original), null),
                "The file could not be restored from the Recycle Bin");
            var result = operation.PerformOperations();
            if (Aborted(operation))
            {
                throw new IOException("Restoring the file from the Recycle Bin was cancelled");
            }
            Check(result, "The file could not be restored from the Recycle Bin");
            if (!File.Exists(original))
            {
                throw new IOException("The file could not be restored from the Recycle Bin");
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(operation);
            _ = Marshal.FinalReleaseComObject(folder);
            _ = Marshal.FinalReleaseComObject(item);
        }
        RemoveRecord(location, original);
    }

    /// <summary>
    /// Removes the bin's record of a file just moved back. The bin keeps each
    /// recycled file as <c>$R&lt;id&gt;.ext</c> with its original path and deletion
    /// time in <c>$I&lt;id&gt;.ext</c> beside it; the shell's own Restore deletes
    /// both, but <c>MoveItem</c> (tested on Windows 11 26200) moves the
    /// <c>$R</c> file and leaves the <c>$I</c> record behind. The record is
    /// deleted only when it names <paramref name="original"/>; any failure
    /// here is ignored, since the file itself is back (an orphaned record is
    /// not shown in the bin).
    /// </summary>
    private static void RemoveRecord(string location, string original)
    {
        try
        {
            var name = Path.GetFileName(location);
            var binFolder = Path.GetDirectoryName(location);
            if (binFolder is null || !name.StartsWith("$R", StringComparison.Ordinal))
            {
                return;
            }
            var record = Path.Combine(binFolder, "$I" + name[2..]);
            var info = new FileInfo(record);
            if (!info.Exists || info.Length > 64 * 1024 || !RecordNames(File.ReadAllBytes(record), original))
            {
                return;
            }
            File.Delete(record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort, see above.
        }
    }

    /// <summary>
    /// Whether a <c>$I</c> record holds <paramref name="original"/>. Version 2
    /// (Windows 10 and later): version (8 bytes), size (8), deletion time
    /// (8), path length in characters with the terminator (4), UTF-16 path.
    /// Version 1 (Vista to 8.1): the path at offset 24, 260 characters.
    /// </summary>
    internal static bool RecordNames(byte[] record, string original)
    {
        if (record.Length < 24)
        {
            return false;
        }
        var version = BitConverter.ToInt64(record, 0);
        string path;
        if (version == 2 && record.Length >= 28)
        {
            var characters = BitConverter.ToInt32(record, 24);
            if (characters <= 0 || 28 + (characters * 2) > record.Length)
            {
                return false;
            }
            path = System.Text.Encoding.Unicode.GetString(record, 28, characters * 2);
        }
        else if (version == 1)
        {
            path = System.Text.Encoding.Unicode.GetString(record, 24, record.Length - 24);
        }
        else
        {
            return false;
        }
        var end = path.IndexOf('\0', StringComparison.Ordinal);
        if (end >= 0)
        {
            path = path[..end];
        }
        return string.Equals(path, original, StringComparison.OrdinalIgnoreCase);
    }

    private static IFileOperation CreateOperation()
    {
        var type = Type.GetTypeFromCLSID(Native.ClsidFileOperation, throwOnError: true)
            ?? throw new IOException("The Windows file operation service is not available");
        return Activator.CreateInstance(type) as IFileOperation
            ?? throw new IOException("The Windows file operation service is not available");
    }

    private static IShellItem? TryShellItem(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        return Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) < 0 ? null : item;
    }

    private static IShellItem ShellItem(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        var result = Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item);
        if (result < 0 || item is null)
        {
            throw new IOException($"{parsingName} could not be found (error 0x{result:X8})",
                Marshal.GetExceptionForHR(result));
        }
        return item;
    }

    private static bool Aborted(IFileOperation operation) =>
        operation.GetAnyOperationsAborted(out var aborted) >= 0 && aborted;

    private static void Check(int result, string message)
    {
        if (result < 0)
        {
            throw new IOException($"{message} (error 0x{result:X8})", Marshal.GetExceptionForHR(result));
        }
    }

    /// <summary>Runs <paramref name="body"/> on a single-threaded apartment, as the shell requires.</summary>
    private static T OnSta<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return body();
        }
        T? value = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = body();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        })
        {
            IsBackground = true,
            Name = "Hearsay Recycle Bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return value ?? throw new InvalidOperationException("The Recycle Bin operation returned no value");
    }

    // MARK: - Progress sink

    /// <summary>
    /// Keeps what <c>PostDeleteItem</c> reports for the one item deleted: the
    /// result and, when the file went to the bin, the recycled item's parsing
    /// name. Every other notification is accepted and ignored.
    /// </summary>
    [ComVisible(true)]
    private sealed class DeleteSink : IFileOperationProgressSink
    {
        public int Result { get; private set; }

        public string? Location { get; private set; }

        public void StartOperations() { }

        public void FinishOperations(int result) { }

        public void PreRenameItem(uint flags, IShellItem item, string? newName) { }

        public void PostRenameItem(uint flags, IShellItem item, string? newName, int result, IShellItem? created) { }

        public void PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) { }

        public void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName,
            int result, IShellItem? created) { }

        public void PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) { }

        public void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName,
            int result, IShellItem? created) { }

        public void PreDeleteItem(uint flags, IShellItem item) { }

        public void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated)
        {
            Result = result;
            if (result < 0 || newlyCreated is null)
            {
                return;
            }
            if (newlyCreated.GetDisplayName(Native.SigdnDesktopAbsoluteParsing, out var name) >= 0
                && !string.IsNullOrEmpty(name))
            {
                Location = name;
            }
        }

        public void PreNewItem(uint flags, IShellItem destinationFolder, string? newName) { }

        public void PostNewItem(uint flags, IShellItem destinationFolder, string? newName, string? templateName,
            uint fileAttributes, int result, IShellItem? newItem) { }

        public void UpdateProgress(uint workTotal, uint workSoFar) { }

        public void ResetTimer() { }

        public void PauseTimer() { }

        public void ResumeTimer() { }
    }

    // MARK: - Interop

    private static class Native
    {
        public static readonly Guid ClsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

        /// <summary>The Recycle Bin's parsing name (CLSID_RecycleBin).</summary>
        public const string RecycleBinFolder = "::{645FF040-5081-101B-9F08-00AA002F954E}";

        // FILEOP_FLAGS (shellapi.h) and the IFileOperation extensions (shobjidl_core.h).
        public const uint FofSilent = 0x0004;
        public const uint FofNoConfirmation = 0x0010;
        public const uint FofAllowUndo = 0x0040;
        public const uint FofNoErrorUi = 0x0400;
        public const uint FofWantNukeWarning = 0x4000;
        public const uint FofxRecycleOnDelete = 0x0008_0000;
        public const uint FofxEarlyFailure = 0x0010_0000;

        /// <summary>SIGDN_DESKTOPABSOLUTEPARSING: a name <c>SHCreateItemFromParsingName</c> accepts back.</summary>
        public const uint SigdnDesktopAbsoluteParsing = 0x8002_8000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);
    }
}

/// <summary>
/// <c>IShellItem</c> (shobjidl_core.h): one item in the shell namespace, a
/// file, a folder or an item in the Recycle Bin. Only
/// <c>GetDisplayName</c> is called; the other slots keep the vtable order.
/// </summary>
[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);

    [PreserveSig]
    int GetParent(out IShellItem parent);

    /// <summary>The item's name in the form <paramref name="sigdn"/> asks for (a SIGDN value).</summary>
    [PreserveSig]
    int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare(IShellItem other, uint hint, out int order);
}

/// <summary>
/// <c>IFileOperation</c> (shobjidl_core.h): queues shell file operations
/// and runs them with <c>PerformOperations</c>. Hearsay uses
/// <c>SetOperationFlags</c>, <c>DeleteItem</c> (to the Recycle Bin),
/// <c>MoveItem</c> (back out of it), <c>PerformOperations</c> and
/// <c>GetAnyOperationsAborted</c>; the other slots keep the vtable order.
/// </summary>
[ComImport]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IFileOperation
{
    [PreserveSig]
    int Advise(IFileOperationProgressSink sink, out uint cookie);

    [PreserveSig]
    int Unadvise(uint cookie);

    /// <summary>FOF_ and FOFX_ flags for every operation queued afterwards.</summary>
    [PreserveSig]
    int SetOperationFlags(uint flags);

    [PreserveSig]
    int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    [PreserveSig]
    int SetProgressDialog(IntPtr progressDialog);

    [PreserveSig]
    int SetProperties(IntPtr propertyChangeArray);

    [PreserveSig]
    int SetOwnerWindow(IntPtr ownerWindow);

    [PreserveSig]
    int ApplyPropertiesToItem(IShellItem item);

    [PreserveSig]
    int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);

    [PreserveSig]
    int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName,
        IFileOperationProgressSink? sink);

    [PreserveSig]
    int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    /// <summary>Queues a move of <paramref name="item"/> into <paramref name="destinationFolder"/> as <paramref name="newName"/>.</summary>
    [PreserveSig]
    int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        IFileOperationProgressSink? sink);

    [PreserveSig]
    int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destinationFolder);

    [PreserveSig]
    int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName,
        IFileOperationProgressSink? sink);

    [PreserveSig]
    int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destinationFolder);

    /// <summary>Queues a delete of <paramref name="item"/>; <paramref name="sink"/> hears about this item only.</summary>
    [PreserveSig]
    int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);

    [PreserveSig]
    int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);

    [PreserveSig]
    int NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IFileOperationProgressSink? sink);

    /// <summary>Runs the queued operations; a failure HRESULT when one failed.</summary>
    [PreserveSig]
    int PerformOperations();

    /// <summary>Whether an operation was cancelled (by the user, or by a declined warning).</summary>
    [PreserveSig]
    int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

/// <summary>
/// <c>IFileOperationProgressSink</c> (shobjidl_core.h): the callbacks
/// <c>IFileOperation</c> makes around each item. Hearsay implements it
/// for <c>PostDeleteItem</c>, whose last argument is the recycled item in the
/// Recycle Bin (null when the file was deleted for good). Methods return
/// S_OK unless they throw, and a failure would cancel the operation.
/// </summary>
[ComImport]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IFileOperationProgressSink
{
    void StartOperations();

    void FinishOperations(int result);

    void PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    void PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result,
        IShellItem? created);

    void PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    void PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);

    void PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    void PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);

    void PreDeleteItem(uint flags, IShellItem item);

    /// <summary>After a delete: <paramref name="result"/> is its HRESULT, <paramref name="newlyCreated"/> the item in the bin.</summary>
    void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated);

    void PreNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);

    void PostNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int result, IShellItem? newItem);

    void UpdateProgress(uint workTotal, uint workSoFar);

    void ResetTimer();

    void PauseTimer();

    void ResumeTimer();
}
