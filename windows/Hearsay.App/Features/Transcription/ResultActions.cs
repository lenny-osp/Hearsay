using Hearsay.App.Features.History;

namespace Hearsay.App.Features.Transcription;

/// <summary>
/// "Reveal in Explorer" and "Open SRT" for the Record and File tabs' result
/// rows (the Mac's <c>revealInFinder()</c> and <c>reveal()</c> in
/// RecordingController.swift and FileViewModel.swift, through the same
/// <see cref="ExplorerShell"/> History uses). A failure is shown as an alert.
/// </summary>
internal static class ResultActions
{
    public static void Reveal(AppShell shell, IReadOnlyList<string> files)
    {
        var existing = files.Where(File.Exists).ToList();
        if (existing.Count == 0) return;
        if (ExplorerShell.Reveal(existing) is { } error) Report(shell, Strings.RevealFailed(error));
    }

    public static void Open(AppShell shell, string file)
    {
        if (ExplorerShell.Open(file) is { } error) Report(shell, error);
    }

    private static void Report(AppShell shell, string message)
    {
        AppLog.Write($"result: {message}");
        if (shell.MainWindow.RenderRoot.XamlRoot is { } root) _ = Alert.ShowAsync(root, Strings.CouldNotOpen, message);
    }
}
