using System.ComponentModel;
using System.Runtime.InteropServices;
using Hearsay.App.Interop;

namespace Hearsay.App.Features.Hotkeys;

/// <summary>
/// A hidden message-only window (<c>HWND_MESSAGE</c>) on the UI thread, the
/// target <c>RegisterHotKey</c> posts <c>WM_HOTKEY</c> to. The Mac needs none:
/// Carbon delivers hotkey events to the application event target
/// (mac/Hearsay/Features/Hotkeys/HotkeyManager.swift). It never shows and is
/// not in the taskbar or Alt+Tab, so it works in every window mode.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const string ClassName = "Hearsay.MessageWindow";

    // The class procedure routes to the instance by handle. One delegate for
    // the class, kept alive for the process so Windows can always call it.
    private static readonly Dictionary<IntPtr, MessageWindow> Windows = [];
    private static readonly NativeMethods.WndProc StaticProcedure = Route;
    private static int classUsers;

    private readonly IntPtr instance;
    private IntPtr handle;

    /// <exception cref="Win32Exception">The window could not be created.</exception>
    public MessageWindow()
    {
        instance = NativeMethods.GetModuleHandle(null);
        if (++classUsers == 1)
        {
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProcedure),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                classUsers--;
                throw new Win32Exception(error);
            }
        }
        handle = NativeMethods.CreateWindowEx(0, ClassName, "Hearsay", 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            ReleaseClass();
            throw new Win32Exception(error);
        }
        Windows[handle] = this;
    }

    /// <summary>A message arrived: (message, wParam, lParam). Return true when handled.</summary>
    public event Func<uint, IntPtr, IntPtr, bool>? MessageReceived;

    public IntPtr Handle => handle;

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Windows.Remove(handle);
        NativeMethods.DestroyWindow(handle);
        handle = IntPtr.Zero;
        ReleaseClass();
    }

    private static IntPtr Route(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hwnd, out var window) && window.MessageReceived?.Invoke(message, wParam, lParam) == true)
        {
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ReleaseClass()
    {
        if (--classUsers == 0)
        {
            NativeMethods.UnregisterClass(ClassName, instance);
        }
    }
}
