using Hearsay.Core.Updates;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// Debug only: <c>HEARSAY_INSTALL_UPDATE=&lt;zip&gt; HEARSAY_INSTALL_TARGET=&lt;folder&gt;
/// [HEARSAY_INSTALL_VERSION=&lt;v&gt;]</c>, the port of
/// mac/Hearsay/Features/Debug/UpdateInstallDebug.swift (AGENTS.md's debug
/// table, PLAN.md 4.6 "Debug" and 18.4). <see cref="Program"/> runs it before
/// any window opens or settings folder is made: the install steps of
/// <see cref="UpdateInstallDebug.RunAsync"/> against the scratch target
/// (location, checksum against a <c>SHA256SUMS.txt</c> next to the zip,
/// extraction, identity and signer against this Hearsay.exe, staging, and
/// the swap through the real helper with no process to wait for and no
/// relaunch), each step printed as <c>hearsay install update: …</c>; exits 0,
/// or 1 at the first failure. The running app's folder is refused. Point it
/// only at a scratch copy, never at an installed Hearsay.
/// <code>
/// $env:HEARSAY_INSTALL_UPDATE="C:\scratch\Hearsay-0.9.9-win-x64.zip"; $env:HEARSAY_INSTALL_TARGET="C:\scratch\target\Hearsay"
/// windows\Hearsay.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Hearsay.exe | Out-Host
/// </code>
/// </summary>
internal static class UpdateInstallDebugEntry
{
    public const string Variable = UpdateInstallDebug.Variable;

    /// <summary>Runs the entry and returns the exit status.</summary>
    public static int Run()
    {
        var output = DebugOutput.Out;
        if (UpdateInstallDebug.FromEnvironment(Environment.ProcessPath) is not { } options)
        {
            output.WriteLine($"hearsay install update: {Variable} is not set");
            return 1;
        }
        try
        {
            // No synchronization context yet: the core's awaits run on the
            // thread pool and this STA thread only waits.
            return Task.Run(() => UpdateInstallDebug.RunAsync(options, output)).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or ArgumentException)
        {
            output.WriteLine($"hearsay install update: failed: {error.Message}");
            return 1;
        }
    }
}
