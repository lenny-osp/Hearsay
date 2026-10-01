using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Hearsay.Core.Audio;

/// <summary>
/// The state of a WASAPI audio session (<c>AudioSessionState</c>): Active
/// while its stream is running, Inactive when it is open but stopped,
/// Expired once it has been closed.
/// </summary>
public enum CaptureSessionState
{
    Inactive,
    Active,
    Expired,
}

/// <summary>
/// One app's audio session on a capture endpoint, as
/// <see cref="MeetingAudioProbe.CaptureSessions"/> lists it.
/// <see cref="ProcessName"/> is the image name without <c>.exe</c>
/// (<see cref="Process.ProcessName"/>), null when the process has exited.
/// <see cref="ParentProcessName"/> is resolved only when the own name is not
/// Teams (see <see cref="MeetingDetector"/>), else null.
/// </summary>
public sealed record CaptureSession(
    string EndpointId,
    string EndpointName,
    int ProcessId,
    string? ProcessName,
    string? ParentProcessName,
    CaptureSessionState State);

/// <summary>
/// Lists the audio sessions open on every active capture endpoint, the
/// signal <see cref="MeetingDetector"/> reads to notice a Microsoft Teams
/// meeting. Windows only; there is no Swift counterpart yet (the Mac port
/// will read CoreAudio process objects instead).
/// </summary>
/// <remarks>
/// Every endpoint is enumerated, not only the default one: Teams records from
/// the default communications device, which can differ from the console
/// default Hearsay records from. System-sounds sessions are skipped. Every
/// NAudio COM wrapper is disposed: <see cref="SessionCollection"/>'s indexer
/// creates a new <see cref="AudioSessionControl"/> on each access, and the
/// session manager registers a session notification that its disposal
/// removes. An endpoint that fails (it went away while being read) is skipped,
/// as is a session that fails; the rest of the list is still returned.
/// </remarks>
public static class MeetingAudioProbe
{
    /// <summary>The capture sessions on every active capture endpoint, in enumeration order.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<CaptureSession> CaptureSessions()
    {
        var sessions = new List<CaptureSession>();
        ProcessTree? tree = null;
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                try
                {
                    AddSessions(endpoint, sessions, ref tree);
                }
                catch (COMException)
                {
                    // The endpoint went away while it was being read.
                }
            }
        }
        return sessions;
    }

    [SupportedOSPlatform("windows")]
    private static void AddSessions(MMDevice endpoint, List<CaptureSession> sessions, ref ProcessTree? tree)
    {
        if (AudioDeviceList.Describe(endpoint) is not AudioInputDevice device)
        {
            return;
        }
        // Creating the manager enumerates the sessions; MMDevice.Dispose disposes it too (idempotent).
        using var manager = endpoint.AudioSessionManager;
        var collection = manager.Sessions;
        int count = collection.Count;
        for (int index = 0; index < count; index++)
        {
            try
            {
                using var control = collection[index];
                if (control.IsSystemSoundsSession)
                {
                    continue;
                }
                int processId = unchecked((int)control.GetProcessID);
                var state = Map(control.State);
                string? name = ProcessName(processId);
                string? parent = null;
                if (name is not null && !MeetingDetector.IsTeamsName(name))
                {
                    tree ??= ProcessTree.Snapshot();
                    parent = tree.ParentName(processId);
                }
                sessions.Add(new CaptureSession(device.Uid, device.Name, processId, name, parent, state));
            }
            catch (COMException)
            {
                // The session closed while it was being read.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static CaptureSessionState Map(AudioSessionState state) => state switch
    {
        AudioSessionState.AudioSessionStateActive => CaptureSessionState.Active,
        AudioSessionState.AudioSessionStateExpired => CaptureSessionState.Expired,
        _ => CaptureSessionState.Inactive,
    };

    private static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // No process with this id (it has exited).
            return null;
        }
        catch (InvalidOperationException)
        {
            // It exited between the lookup and the name.
            return null;
        }
    }

    /// <summary>
    /// A toolhelp snapshot of process ids, parent ids and image names, taken
    /// at most once per <see cref="CaptureSessions"/> call and only when a
    /// session's own name is not Teams. A snapshot that cannot be taken
    /// resolves no parent.
    /// </summary>
    internal sealed class ProcessTree
    {
        private readonly Dictionary<int, (int ParentId, string Name)> entries;

        private ProcessTree(Dictionary<int, (int ParentId, string Name)> entries) => this.entries = entries;

        /// <summary>The parent's image name without <c>.exe</c>, or null when unknown.</summary>
        public string? ParentName(int processId) =>
            entries.TryGetValue(processId, out var entry)
                && entry.ParentId != 0
                && entries.TryGetValue(entry.ParentId, out var parent)
                ? StripExe(parent.Name)
                : null;

        public static ProcessTree Snapshot()
        {
            var entries = new Dictionary<int, (int ParentId, string Name)>();
            IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
            if (snapshot == NativeMethods.InvalidHandleValue)
            {
                return new ProcessTree(entries);
            }
            try
            {
                var entry = new NativeMethods.ProcessEntry32W { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32W>() };
                bool more = NativeMethods.Process32FirstW(snapshot, ref entry);
                while (more)
                {
                    entries[unchecked((int)entry.ProcessId)] = (unchecked((int)entry.ParentProcessId), entry.ExeFile ?? "");
                    entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32W>();
                    more = NativeMethods.Process32NextW(snapshot, ref entry);
                }
            }
            finally
            {
                _ = NativeMethods.CloseHandle(snapshot);
            }
            return new ProcessTree(entries);
        }

        private static string StripExe(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static class NativeMethods
    {
        public const uint Th32csSnapProcess = 0x00000002;
        public static readonly IntPtr InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32W
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string? ExeFile;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
