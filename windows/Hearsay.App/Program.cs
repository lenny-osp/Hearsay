using Hearsay.App.Features.Debug;
using Hearsay.App.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Hearsay.App;

/// <summary>
/// The entry point, the counterpart of <c>HearsayMain</c> in
/// mac/Hearsay/HearsayApp.swift. Before any XAML loads it: connects stdout to
/// the parent console for debug runs, refuses debug entry points this build
/// does not have yet, and makes a normal launch single-instance (macOS gives
/// every app one instance; a second Hearsay.exe hands its activation to the
/// running one and exits, so there is never a second main window, tray icon
/// or set of hotkeys). Debug runs never look for or redirect to the user's
/// running instance.
/// </summary>
public static class Program
{
    /// <summary>The key a normal launch registers to find the running instance.</summary>
    private const string InstanceKey = "tw.og1o.hearsay.main";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var debugVariable = DebugEnvironment.RequestedVariable;
        if (debugVariable is not null)
        {
            AttachParentConsole();
            if (!DebugEnvironment.ImplementedVariables.Contains(debugVariable))
            {
                Console.Error.WriteLine($"Hearsay: {debugVariable} is not available in this build yet (Windows W5/W7).");
                return 2;
            }
        }
        else if (!TakeSingleInstance())
        {
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        if (App.ScratchFolder is { } scratch)
        {
            // WebView2's helper processes release the profile a moment after
            // its window closes; RemoveScratchFolder retries for two seconds.
            DebugEnvironment.RemoveScratchFolder(scratch);
        }
        return App.ExitCode;
    }

    /// <summary>
    /// True when this is the only normal instance. Otherwise hands the
    /// activation to the running one (which brings its main window forward)
    /// and returns false.
    /// </summary>
    private static bool TakeSingleInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, _) => App.ActivatedByAnotherInstance();
            return true;
        }
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // RedirectActivationToAsync must not block the STA thread it runs on.
        var redirect = Task.Run(() => main.RedirectActivationToAsync(activation).AsTask());
        redirect.Wait(TimeSpan.FromSeconds(10));
        return false;
    }

    /// <summary>
    /// A WinExe has no console. When started from one without redirection,
    /// debug output goes to that console; redirected handles are kept.
    /// </summary>
    private static void AttachParentConsole()
    {
        if (!Console.IsOutputRedirected)
        {
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        }
    }
}
