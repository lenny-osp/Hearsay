using System.Diagnostics;
using System.Globalization;

namespace Hearsay.App;

/// <summary>
/// Log lines for the shell: the debugger's output and, when Hearsay was
/// started from a console or with redirected output (debug entry points),
/// stdout. The Mac logs through <c>os.Logger</c> and prints debug-run
/// results with <c>print</c>. Log lines are never localized.
/// </summary>
internal static class AppLog
{
    public static void Write(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"[hearsay] {message}");
        Debug.WriteLine(line);
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch (IOException)
        {
        }
    }
}
