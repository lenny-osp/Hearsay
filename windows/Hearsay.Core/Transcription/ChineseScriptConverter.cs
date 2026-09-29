using System.Globalization;
using System.Runtime.InteropServices;

namespace Hearsay.Core.Transcription;

/// <summary>
/// Converts Chinese transcript text to the script its language uses:
/// Traditional for ZH-TW, Simplified for ZH-CN
/// (<see cref="TranscriptLanguages.ChineseScript(TranscriptLanguage)"/>).
/// Whisper often writes Simplified characters even for Taiwanese speech, so
/// the cue text is converted after transcription, character by character
/// (软件 becomes 軟件, not the Taiwan word 軟體). The Whisper call itself is
/// the same "zh" either way (PLAN.md section 6).
/// Port of <c>ChineseScript.convert(_:to:)</c> in
/// mac/HearsayCore/Sources/HearsayCore/Transcription/ChineseScript.swift.
/// </summary>
/// <remarks>
/// <para>The Mac uses ICU's <c>Hans-Hant</c> transform through
/// <c>String.applyingTransform</c>; Windows 10 1903 and later ship the same
/// ICU as <c>C:\Windows\System32\icu.dll</c> (PLAN.md 18.3, "Chinese script";
/// windows/Spike/TextSpike/REPORT.md). Traditional is <c>Hans-Hant</c>
/// forward, Simplified the same id reversed, which is what Swift's
/// <c>reverse: true</c> does.</para>
/// <para>Where the Mac returns the text unchanged when ICU fails, this port
/// throws: a missing icu.dll or export is a
/// <see cref="PlatformNotSupportedException"/> naming it, and a failed
/// conversion an <see cref="InvalidOperationException"/> with ICU's error
/// name. A transcript is never written in the wrong script silently.</para>
/// <para>Each direction's transliterator is opened on first use (70 to
/// 106 ms in the spike) and kept for the process lifetime. ICU
/// transliterators are not thread-safe, so each one is used under its own lock.</para>
/// </remarks>
public static class ChineseScriptConverter
{
    private static readonly Transliterator ToTraditional = new(NativeMethods.UtransForward);
    private static readonly Transliterator ToSimplified = new(NativeMethods.UtransReverse);

    /// <summary>
    /// <paramref name="text"/> in <paramref name="script"/>: Hans-Hant for
    /// Traditional, the same transform reversed for Simplified, unchanged for null.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">icu.dll or one of its exports is missing.</exception>
    /// <exception cref="InvalidOperationException">ICU reported an error.</exception>
    public static string Convert(string text, ChineseScript? script)
    {
        ArgumentNullException.ThrowIfNull(text);
        return script switch
        {
            null => text,
            ChineseScript.Traditional => ToTraditional.Transliterate(text),
            ChineseScript.Simplified => ToSimplified.Transliterate(text),
            _ => throw new ArgumentOutOfRangeException(nameof(script), script, null),
        };
    }

    /// <summary>
    /// <paramref name="segments"/> with their text converted to
    /// <paramref name="script"/> (the same list for null); timings unchanged.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Convert(IReadOnlyList<TranscriptSegment> segments, ChineseScript? script)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (script is null)
        {
            return segments;
        }
        var converted = new TranscriptSegment[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            converted[i] = segments[i] with { Text = Convert(segments[i].Text, script) };
        }
        return converted;
    }

    /// <summary>The loaded ICU's version from <c>u_getVersion</c>, e.g. "72.1.0.4", for logs and reports.</summary>
    /// <exception cref="PlatformNotSupportedException">icu.dll or one of its exports is missing.</exception>
    public static string IcuVersion()
    {
        NativeMethods.EnsureAvailable();
        var version = new byte[4];
        NativeMethods.u_getVersion(version);
        return string.Join('.', version.Select(part => part.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// One direction of ICU <c>Hans-Hant</c>, opened on first use and never closed.
    /// </summary>
    private sealed class Transliterator(int direction)
    {
        private readonly Lock gate = new();
        private IntPtr handle;

        /// <summary>
        /// <paramref name="text"/> through the transform, in place in a UTF-16
        /// buffer, retried with a larger buffer on U_BUFFER_OVERFLOW_ERROR.
        /// <paramref name="initialCapacity"/> lets tests start with the smallest
        /// buffer ICU accepts.
        /// </summary>
        public string Transliterate(string text, int? initialCapacity = null)
        {
            if (text.Length == 0)
            {
                return text;
            }
            lock (gate)
            {
                if (handle == IntPtr.Zero)
                {
                    handle = NativeMethods.Open(direction);
                }
                int capacity = Math.Max(text.Length, initialCapacity ?? ((text.Length * 2) + 16));
                while (true)
                {
                    var buffer = new char[capacity];
                    text.CopyTo(0, buffer, 0, text.Length);
                    int length = text.Length;
                    int limit = text.Length;
                    int status = 0;
                    NativeMethods.utrans_transUChars(handle, buffer, ref length, capacity, 0, ref limit, ref status);
                    if (status == NativeMethods.UBufferOverflowError)
                    {
                        // ICU reports the length it needed; grow at least that far.
                        capacity = Math.Max(length + 16, capacity * 2);
                        continue;
                    }
                    if (status > 0)
                    {
                        throw new InvalidOperationException(
                            $"ICU utrans_transUChars failed converting Chinese text: {NativeMethods.ErrorName(status)} ({status}).");
                    }
                    return new string(buffer, 0, length);
                }
            }
        }
    }

    /// <summary>Test hook: <see cref="Convert(string, ChineseScript?)"/> with a chosen first buffer size.</summary>
    internal static string Convert(string text, ChineseScript script, int initialCapacity) => script switch
    {
        ChineseScript.Traditional => ToTraditional.Transliterate(text, initialCapacity),
        _ => ToSimplified.Transliterate(text, initialCapacity),
    };

    private static class NativeMethods
    {
        private const string Library = "icu.dll";
        private const string TransformId = "Hans-Hant";

        public const int UtransForward = 0;
        public const int UtransReverse = 1;
        public const int UBufferOverflowError = 15;

        /// <summary>Every export this class calls; checked up front so a missing one is named.</summary>
        private static readonly string[] RequiredExports =
            ["u_getVersion", "u_errorName", "utrans_openU", "utrans_transUChars", "utrans_close"];

        private static readonly Lock LoadGate = new();
        private static bool available;

        /// <summary>Loads System32's icu.dll and checks every export, or throws naming what is missing.</summary>
        public static void EnsureAvailable()
        {
            lock (LoadGate)
            {
                if (available)
                {
                    return;
                }
                if (!NativeLibrary.TryLoad(Library, typeof(NativeMethods).Assembly, DllImportSearchPath.System32, out IntPtr library))
                {
                    throw new PlatformNotSupportedException(
                        "Chinese script conversion needs ICU (C:\\Windows\\System32\\icu.dll, Windows 10 version 1903 or later), which could not be loaded.");
                }
                foreach (string export in RequiredExports)
                {
                    if (!NativeLibrary.TryGetExport(library, export, out _))
                    {
                        throw new PlatformNotSupportedException(
                            $"Chinese script conversion needs the ICU function {export}, which C:\\Windows\\System32\\icu.dll does not export.");
                    }
                }
                // The library stays loaded for the process lifetime; the P/Invokes below bind to it.
                available = true;
            }
        }

        /// <summary><c>utrans_openU("Hans-Hant", direction)</c>, or throws with ICU's error name.</summary>
        public static IntPtr Open(int direction)
        {
            EnsureAvailable();
            int status = 0;
            IntPtr opened = utrans_openU(TransformId, TransformId.Length, direction, null, -1, IntPtr.Zero, ref status);
            if (status > 0 || opened == IntPtr.Zero)
            {
                string name = direction == UtransForward ? "UTRANS_FORWARD" : "UTRANS_REVERSE";
                if (opened != IntPtr.Zero)
                {
                    utrans_close(opened);
                }
                throw new InvalidOperationException(
                    $"ICU utrans_openU(\"{TransformId}\", {name}) failed: {ErrorName(status)} ({status}).");
            }
            return opened;
        }

        public static string ErrorName(int status) =>
            Marshal.PtrToStringAnsi(u_errorName(status)) ?? status.ToString(CultureInfo.InvariantCulture);

        [DllImport(Library, EntryPoint = "u_getVersion")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void u_getVersion([Out] byte[] versionArray);

        [DllImport(Library, EntryPoint = "u_errorName")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr u_errorName(int code);

        [DllImport(Library, EntryPoint = "utrans_openU", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr utrans_openU(
            string id, int idLength, int direction, string? rules, int rulesLength, IntPtr parseError, ref int status);

        [DllImport(Library, EntryPoint = "utrans_close")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void utrans_close(IntPtr transliterator);

        [DllImport(Library, EntryPoint = "utrans_transUChars", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void utrans_transUChars(
            IntPtr transliterator, [In, Out] char[] text, ref int textLength, int textCapacity, int start, ref int limit, ref int status);
    }
}
