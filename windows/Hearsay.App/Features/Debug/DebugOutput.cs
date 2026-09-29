using System.Text;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// stdout and stderr for the debug entry points. The Mac writes UTF-8 with
/// <c>FileHandle.standardOutput</c>; .NET's <see cref="Console"/> encodes
/// redirected output in the OEM code page, which turns Chinese into "?".
/// Redirected streams get UTF-8 (no BOM) here; a console keeps
/// <see cref="Console"/>, so the parent console's code page is never changed.
/// </summary>
internal static class DebugOutput
{
    private static readonly Lazy<TextWriter> OutWriter = new(() => Open(Console.IsOutputRedirected, Console.OpenStandardOutput, Console.Out));
    private static readonly Lazy<TextWriter> ErrorWriter = new(() => Open(Console.IsErrorRedirected, Console.OpenStandardError, Console.Error));

    public static TextWriter Out => OutWriter.Value;

    public static TextWriter Error => ErrorWriter.Value;

    private static TextWriter Open(bool redirected, Func<Stream> open, TextWriter console)
    {
        if (!redirected) return console;
        return TextWriter.Synchronized(new StreamWriter(open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" });
    }
}
