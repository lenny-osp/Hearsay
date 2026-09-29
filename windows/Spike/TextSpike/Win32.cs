using System.Runtime.InteropServices;

namespace TextSpike;

/// <summary>kernel32 LCMapStringEx with LCMAP_SIMPLIFIED_CHINESE /
/// LCMAP_TRADITIONAL_CHINESE: the other in-box character converter.</summary>
internal static unsafe partial class Win32
{
    public const uint LcmapSimplifiedChinese = 0x02000000;
    public const uint LcmapTraditionalChinese = 0x04000000;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int LCMapStringEx(string locale, uint flags, char* src, int srcLen, char* dest, int destLen, IntPtr version, IntPtr reserved, IntPtr sortHandle);

    public static string LcMap(string text, uint flags, string locale)
    {
        if (text.Length == 0) return text;
        fixed (char* s = text)
        {
            int n = LCMapStringEx(locale, flags, s, text.Length, null, 0, 0, 0, 0);
            if (n == 0) throw new InvalidOperationException($"LCMapStringEx failed: Win32 error {Marshal.GetLastPInvokeError()}");
            char[] buffer = new char[n];
            fixed (char* d = buffer)
            {
                n = LCMapStringEx(locale, flags, s, text.Length, d, n, 0, 0, 0);
            }
            if (n == 0) throw new InvalidOperationException($"LCMapStringEx failed: Win32 error {Marshal.GetLastPInvokeError()}");
            return new string(buffer, 0, n);
        }
    }
}
