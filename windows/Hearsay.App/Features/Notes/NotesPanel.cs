using Hearsay.Core.Notes;
using Hearsay.Core.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hearsay.App.Features.Notes;

/// <summary>
/// A tab's content with the notes flow under it, for the Record and File
/// tabs: the "Meeting notes" section with its <c>NotesFlowView</c> in
/// mac/Hearsay/Features/Recording/RecordView.swift and
/// FileTranscription/FileView.swift, and the Mac's <c>NotesHandoff</c> in
/// mac/Hearsay/Features/Transcription/TranscriptOutput.swift
/// (<see cref="Start"/>, <see cref="Reset"/>). As on the Mac there is no
/// Done button: the section stays until the tab starts its next recording
/// or file (the caller calls <see cref="Reset"/>).
/// </summary>
internal sealed partial class NotesPanel : UserControl
{
    private readonly AIProviderStore store;
    private readonly Border panel;
    private readonly ContentControl host;

    public NotesPanel(AIProviderStore store, UIElement content)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        this.store = store;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(content);
        host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel = new Border
        {
            BorderBrush = NotesFlowView.Brush("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 8, 24, 16),
            Child = host,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(panel, 1);
        grid.Children.Add(panel);
        Content = grid;
    }

    /// <summary>The flow on screen, or null.</summary>
    public NotesFlowViewModel? Model { get; private set; }

    /// <summary>Debug only: replaces the notes pipeline.</summary>
    public NotesGenerator? GeneratorOverride { get; set; }

    /// <summary>
    /// Starts the flow for a finished SRT: the confirm sheet appears unless
    /// "Ask before sending" is off, in which case generation starts at once.
    /// The notes default to <paramref name="language"/>, the session's
    /// resolved language. A flow still waiting at the confirm sheet is
    /// replaced (the transcript was just rewritten in another language); one
    /// that is further along is left alone (returns null).
    /// </summary>
    public NotesFlowViewModel? Start(string srtPath, TranscriptLanguage language)
    {
        ArgumentNullException.ThrowIfNull(srtPath);
        if (Model is { IsRunning: true } running && running.Phase is not NotesPhase.Confirming) return null;
        if (Model is { Phase: NotesPhase.Confirming }) History.Alert.Current?.Hide();
        var model = new NotesFlowViewModel(store, GeneratorOverride);
        Model = model;
        host.Content = new NotesFlowView(model);
        panel.Visibility = Visibility.Visible;
        model.Run(srtPath, language);
        return model;
    }

    /// <summary>Clears a finished flow (a new recording or file starts).</summary>
    public void Reset()
    {
        if (Model is { IsRunning: true }) return;
        Model = null;
        host.Content = null;
        panel.Visibility = Visibility.Collapsed;
    }
}
