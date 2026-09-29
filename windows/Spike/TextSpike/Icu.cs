using System.Runtime.InteropServices;
using System.Text;

namespace TextSpike;

/// <summary>
/// P/Invoke into the ICU that ships with Windows 10 1903+ as
/// C:\Windows\System32\icu.dll (unversioned exports). Mirrors what
/// mac/HearsayCore/.../Transcription/ChineseScript.swift gets from
/// String.applyingTransform(StringTransform("Hans-Hant"), reverse:).
/// </summary>
internal static unsafe partial class Icu
{
    private const string Lib = "icu.dll";
    public const int Forward = 0; // UTRANS_FORWARD
    public const int Reverse = 1; // UTRANS_REVERSE
    private const int BufferOverflow = 15; // U_BUFFER_OVERFLOW_ERROR

    [StructLayout(LayoutKind.Sequential)]
    private struct UParseError
    {
        public int Line;
        public int Offset;
        public fixed char PreContext[16];
        public fixed char PostContext[16];
    }

    [LibraryImport(Lib, EntryPoint = "u_getVersion")]
    private static partial void u_getVersion(byte* versionArray);

    [LibraryImport(Lib, EntryPoint = "u_errorName")]
    private static partial IntPtr u_errorName(int code);

    [LibraryImport(Lib, EntryPoint = "utrans_openU")]
    private static partial IntPtr utrans_openU(char* id, int idLength, int dir, char* rules, int rulesLength, UParseError* parseError, int* status);

    [LibraryImport(Lib, EntryPoint = "utrans_close")]
    private static partial void utrans_close(IntPtr trans);

    [LibraryImport(Lib, EntryPoint = "utrans_transUChars")]
    private static partial void utrans_transUChars(IntPtr trans, char* text, int* textLength, int textCapacity, int start, int* limit, int* status);

    [LibraryImport(Lib, EntryPoint = "utrans_openIDs")]
    private static partial IntPtr utrans_openIDs(int* status);

    [LibraryImport(Lib, EntryPoint = "uenum_count")]
    private static partial int uenum_count(IntPtr en, int* status);

    [LibraryImport(Lib, EntryPoint = "uenum_unext")]
    private static partial char* uenum_unext(IntPtr en, int* resultLength, int* status);

    [LibraryImport(Lib, EntryPoint = "uenum_close")]
    private static partial void uenum_close(IntPtr en);

    [LibraryImport(Lib, EntryPoint = "utrans_getUnicodeID")]
    private static partial char* utrans_getUnicodeID(IntPtr trans, int* resultLength);

    /// <summary>Which of the named exports icu.dll resolves.</summary>
    public static IReadOnlyList<(string Name, bool Found)> ProbeExports(params string[] names)
    {
        var result = new List<(string, bool)>();
        IntPtr handle = NativeLibrary.Load(Lib);
        try
        {
            foreach (string name in names)
            {
                result.Add((name, NativeLibrary.TryGetExport(handle, name, out _)));
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
        return result;
    }

    public static string LoadedPath()
    {
        foreach (System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            if (string.Equals(module.ModuleName, Lib, StringComparison.OrdinalIgnoreCase))
            {
                return $"{module.FileName} (file version {module.FileVersionInfo.FileVersion})";
            }
        }
        return "(icu.dll not loaded)";
    }

    public static string Version()
    {
        byte* v = stackalloc byte[4];
        u_getVersion(v);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v[0]}.{v[1]}.{v[2]}.{v[3]}");
    }

    public static string ErrorName(int code) => Marshal.PtrToStringAnsi(u_errorName(code)) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static List<string> TransliteratorIds()
    {
        var ids = new List<string>();
        int status = 0;
        IntPtr en = utrans_openIDs(&status);
        if (status > 0) throw new InvalidOperationException($"utrans_openIDs: {ErrorName(status)}");
        try
        {
            while (true)
            {
                int len = 0;
                char* s = uenum_unext(en, &len, &status);
                if (status > 0) throw new InvalidOperationException($"uenum_unext: {ErrorName(status)}");
                if (s == null) break;
                ids.Add(new string(s, 0, len));
            }
        }
        finally
        {
            uenum_close(en);
        }
        return ids;
    }

    /// <summary>An opened ICU transliterator.</summary>
    public sealed class Transliterator : IDisposable
    {
        private IntPtr _handle;
        public string Id { get; }

        public Transliterator(string id, int direction)
        {
            int status = 0;
            UParseError pe = default;
            IntPtr h;
            fixed (char* pid = id)
            {
                h = utrans_openU(pid, id.Length, direction, null, -1, &pe, &status);
            }
            if (status > 0 || h == IntPtr.Zero)
            {
                throw new InvalidOperationException($"utrans_openU(\"{id}\", {(direction == Forward ? "UTRANS_FORWARD" : "UTRANS_REVERSE")}) failed: {ErrorName(status)} ({status})");
            }
            _handle = h;
            int len = 0;
            char* uid = utrans_getUnicodeID(h, &len);
            Id = uid == null ? id : new string(uid, 0, len);
        }

        public string Transliterate(string text)
        {
            if (text.Length == 0) return text;
            int capacity = text.Length * 2 + 16;
            while (true)
            {
                char[] buffer = new char[capacity];
                text.CopyTo(0, buffer, 0, text.Length);
                int length = text.Length;
                int limit = text.Length;
                int status = 0;
                fixed (char* p = buffer)
                {
                    utrans_transUChars(_handle, p, &length, capacity, 0, &limit, &status);
                }
                if (status == BufferOverflow)
                {
                    capacity = length + 16;
                    continue;
                }
                if (status > 0) throw new InvalidOperationException($"utrans_transUChars: {ErrorName(status)} ({status})");
                return new string(buffer, 0, length);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                utrans_close(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
