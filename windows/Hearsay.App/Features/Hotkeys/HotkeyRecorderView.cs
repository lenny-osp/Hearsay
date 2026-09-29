using Hearsay.Core.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Hearsay.App.Features.Hotkeys;

/// <summary>
/// A small shortcut recorder: click it, press a combination, and it shows
/// it (for example Ctrl+Alt+Win+R). Escape cancels; Backspace or Delete
/// restores the action's default (Windows only). The combination needs at
/// least one of Ctrl, Alt, Win so it cannot steal ordinary typing, must
/// differ from the other action's, and must be one Windows lets Hearsay
/// register (<see cref="HotkeyRecorder.Validate"/>); otherwise the reason
/// shows under the button and it keeps listening. While it listens the
/// global hotkeys are suspended, so the chord reaches it, and they are
/// registered again afterwards. It stops listening when it loses focus or
/// leaves the window.
/// Port of <c>HotkeyRecorderView</c> in mac/Hearsay/Features/Hotkeys/HotkeyRecorderView.swift;
/// the Mac's local key monitor is the button's <c>PreviewKeyDown</c> here.
/// Chords Windows or another app takes before any window sees them (Win+E,
/// Win+L, a chord another app registered) never reach the recorder.
/// </summary>
internal sealed partial class HotkeyRecorderView : UserControl
{
    private readonly HotkeyManager hotkeys;
    private readonly HotkeyAction action;
    private readonly Func<HotkeyBinding> current;
    private readonly Func<HotkeyBinding> other;
    private readonly Action<HotkeyBinding> save;
    private readonly HotkeyBinding defaultBinding;
    private readonly Button button;
    private readonly TextBlock hint;
    private readonly HashSet<VirtualKey> swallowedKeys = [];

    /// <param name="hotkeys">Suspended while listening; also checks a chord (<see cref="HotkeyManager.Probe"/>).</param>
    /// <param name="action">The action this recorder edits.</param>
    /// <param name="current">Reads its binding.</param>
    /// <param name="other">Reads the other action's binding.</param>
    /// <param name="save">Stores a new binding.</param>
    public HotkeyRecorderView(HotkeyManager hotkeys, HotkeyAction action,
        Func<HotkeyBinding> current, Func<HotkeyBinding> other, Action<HotkeyBinding> save)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(save);
        this.hotkeys = hotkeys;
        this.action = action;
        this.current = current;
        this.other = other;
        this.save = save;
        defaultBinding = action == HotkeyAction.StartStop ? HotkeyBinding.DefaultStartStop : HotkeyBinding.DefaultPause;

        button = new Button { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(button, Strings.RecorderAccessibilityName);
        button.Click += (_, _) =>
        {
            if (IsListening) StopListening();
            else StartListening();
        };
        button.PreviewKeyDown += OnPreviewKeyDown;
        button.PreviewKeyUp += OnPreviewKeyUp;
        button.LostFocus += (_, _) => StopListening();
        hint = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            MaxWidth = 320,
            Visibility = Visibility.Collapsed,
        };
        var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        stack.Children.Add(button);
        stack.Children.Add(hint);
        Content = stack;
        Unloaded += (_, _) => StopListening();
        Refresh();
    }

    public bool IsListening { get; private set; }

    /// <summary>The inline reason, empty when there is none (for the UI snapshots).</summary>
    public string Hint => hint.Text;

    /// <summary>Shows the stored binding (call when the settings change).</summary>
    public void Refresh()
    {
        var shown = HotkeyDisplay.Text(current());
        button.Content = IsListening ? Strings.RecorderListening : shown;
        ToolTipService.SetToolTip(button, IsListening ? Strings.RecorderListeningTooltip : Strings.RecorderTooltip);
        AutomationProperties.SetHelpText(button, shown);
    }

    public void StartListening()
    {
        if (IsListening) return;
        SetHint(null);
        IsListening = true;
        hotkeys.Suspend();
        Refresh();
    }

    public void StopListening()
    {
        if (!IsListening) return;
        IsListening = false;
        hotkeys.Resume();
        Refresh();
    }

    /// <summary>
    /// A key press while listening, with the modifiers held (the Mac's
    /// <c>handle(_:)</c>); also what the UI snapshots feed in.
    /// </summary>
    public void Handle(uint virtualKey, HotkeyModifiers modifiers)
    {
        var input = HotkeyRecorder.Interpret(virtualKey, modifiers, defaultBinding);
        switch (input.Kind)
        {
            case RecorderInputKind.Ignored:
                return;
            case RecorderInputKind.Cancel:
                SetHint(null);
                StopListening();
                return;
            default:
                var check = HotkeyRecorder.Validate(input.Binding, other(), hotkeys.Probe);
                if (!check.IsAccepted)
                {
                    SetHint(HotkeyRecorder.Reason(check, input.Binding, action));
                    return;
                }
                SetHint(null);
                // Saved while still suspended: the manager registers the new
                // binding once, when listening stops.
                if (input.Binding != current()) save(input.Binding);
                StopListening();
                return;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsListening) return;
        // Swallow every key press while listening (Tab, Enter and Space too),
        // and its key-up, so Space does not click the button again.
        e.Handled = true;
        swallowedKeys.Add(e.Key);
        if (e.KeyStatus.WasKeyDown) return;
        Handle((uint)e.Key, HotkeyRecorder.Modifiers(
            control: IsDown(VirtualKey.Control),
            alt: IsDown(VirtualKey.Menu),
            shift: IsDown(VirtualKey.Shift),
            win: IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)));
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (swallowedKeys.Remove(e.Key)) e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void SetHint(string? text)
    {
        hint.Text = text ?? "";
        hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
