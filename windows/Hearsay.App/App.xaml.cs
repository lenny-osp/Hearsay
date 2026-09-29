using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hearsay.App;

/// <summary>
/// The XAML application object, the counterpart of <c>HearsayApp</c> in
/// mac/Hearsay/HearsayApp.swift. Everything the Mac's app delegate owns
/// lives in <see cref="AppShell"/>, created at launch.
/// </summary>
public partial class App : Application
{
    private static App? current;

    private readonly DispatcherQueue dispatcher;
    private AppShell? shell;

    public App()
    {
        InitializeComponent();
        current = this;
        dispatcher = DispatcherQueue.GetForCurrentThread();
        // Hidden windows and the tray keep the app alive; only Quit ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => AppLog.Write($"unhandled: {e.Exception}");
    }

    /// <summary>The process exit code (the UI snapshots report failure through it).</summary>
    internal static int ExitCode { get; set; }

    /// <summary>Restart Now: <see cref="Program"/> starts Hearsay again after the message loop ends.</summary>
    internal static bool RelaunchRequested { get; set; }

    /// <summary>A debug run's throwaway settings folder, removed by <see cref="Program"/> after the message loop ends.</summary>
    internal static string? ScratchFolder { get; set; }

    /// <summary>Another Hearsay.exe was started: bring the main window forward, on the UI thread.</summary>
    internal static void ActivatedByAnotherInstance()
    {
        if (current is not { } app) return;
        app.dispatcher.TryEnqueue(() => app.shell?.ShowMain());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        shell = new AppShell(this, dispatcher);
        shell.Launch();
    }
}
