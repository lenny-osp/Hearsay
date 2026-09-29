using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.History;

/// <summary>
/// The Mac's <c>.alert</c> and <c>.sheet</c> modifiers as WinUI
/// <see cref="ContentDialog"/>s, used by the History and Models tabs
/// (mac/Hearsay/Features/History/HistoryView.swift,
/// mac/Hearsay/Features/Models/ModelRowView.swift). WinUI allows one open
/// dialog per window, so dialogs wait for each other here in the order they
/// were asked for. <see cref="Current"/> is the open one, for the UI
/// snapshots.
/// </summary>
internal static class Alert
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The dialog on screen, or null.</summary>
    public static ContentDialog? Current { get; private set; }

    /// <summary>A dialog in the app's style over <paramref name="root"/>.</summary>
    public static ContentDialog Make(XamlRoot root, string title, object content)
    {
        ArgumentNullException.ThrowIfNull(root);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
        };
        Prepare(dialog, root);
        return dialog;
    }

    /// <summary>Puts <paramref name="dialog"/> over <paramref name="root"/> in the Fluent dialog style and the window's theme.</summary>
    public static void Prepare(ContentDialog dialog, XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(root);
        dialog.XamlRoot = root;
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style fluent)
        {
            dialog.Style = fluent;
        }
        if (root.Content is FrameworkElement page) dialog.RequestedTheme = page.ActualTheme;
    }

    /// <summary>A text block for a dialog's message: wraps, selectable (errors can be copied).</summary>
    public static TextBlock Message(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    /// <summary>Shows <paramref name="dialog"/> once no other dialog is open.</summary>
    public static async Task<ContentDialogResult> PresentAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            Current = dialog;
            return await dialog.ShowAsync();
        }
        finally
        {
            Current = null;
            Gate.Release();
        }
    }

    /// <summary>An error or notice with one OK button.</summary>
    public static async Task ShowAsync(XamlRoot root, string title, string message)
    {
        var dialog = Make(root, title, Message(message));
        dialog.CloseButtonText = Strings.OK;
        dialog.DefaultButton = ContentDialogButton.Close;
        await PresentAsync(dialog).ConfigureAwait(true);
    }

    /// <summary>
    /// A question with <paramref name="confirm"/> as the default button (the
    /// Mac's alerts make even a destructive action the default so Return
    /// confirms) and Cancel; true when confirmed.
    /// </summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm)
    {
        var dialog = Make(root, title, Message(message));
        dialog.PrimaryButtonText = confirm;
        dialog.CloseButtonText = Strings.Cancel;
        return await PresentAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
    }
}
