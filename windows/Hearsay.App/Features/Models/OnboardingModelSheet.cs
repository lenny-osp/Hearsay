using Hearsay.App.Features.History;
using Hearsay.Core.ModelStore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Models;

/// <summary>
/// First-run prompt when no model is installed: one click downloads the
/// recommended model; "Choose another…" closes it and leaves the Models tab
/// showing. Mirrors mac/Hearsay/Features/Models/OnboardingModelSheet.swift
/// as a <see cref="ContentDialog"/> (the text says "this PC").
/// </summary>
internal sealed partial class OnboardingModelSheet : ContentDialog
{
    public OnboardingModelSheet(XamlRoot root, ModelCatalogEntry recommended)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(recommended);
        Alert.Prepare(this, root);
        Title = Strings.OnboardingTitle;
        // Wider than the default 548 so the long button label fits in its half.
        Resources["ContentDialogMaxWidth"] = 640.0;
        Content = new TextBlock { Text = Strings.OnboardingText, TextWrapping = TextWrapping.Wrap, Width = 560 };
        PrimaryButtonText = Strings.DownloadRecommended(ByteSize.Short(recommended.SizeBytes));
        CloseButtonText = Strings.ChooseAnother;
        DefaultButton = ContentDialogButton.Primary;
    }

    /// <summary>True when the user chose to download the recommended model.</summary>
    public async Task<bool> AskAsync() => await Alert.PresentAsync(this).ConfigureAwait(true) == ContentDialogResult.Primary;
}
