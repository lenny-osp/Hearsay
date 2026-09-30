using Hearsay.App.Interop;
using Hearsay.Core.Settings;

namespace Hearsay.App.Features.Hotkeys;

/// <summary>What one key press means to the shortcut recorder (<see cref="HotkeyRecorder.Interpret"/>).</summary>
internal enum RecorderInputKind
{
    /// <summary>A modifier or lock key on its own: keep listening.</summary>
    Ignored,
    /// <summary>Escape with no modifier: stop listening, keep the binding.</summary>
    Cancel,
    /// <summary>Backspace or Delete with no modifier: the action's default (Windows only).</summary>
    ResetToDefault,
    /// <summary>A key with no Ctrl, Alt or Win: keep listening and show the hint.</summary>
    NeedsModifier,
    /// <summary>A usable chord, still to be validated (<see cref="HotkeyRecorder.Validate"/>).</summary>
    Chord,
}

/// <summary>A key press as the recorder reads it; <see cref="Binding"/> is set for every kind but <see cref="RecorderInputKind.Ignored"/>.</summary>
internal readonly record struct RecorderInput(RecorderInputKind Kind, HotkeyBinding Binding);

/// <summary>Why a recorded chord is not saved.</summary>
internal enum HotkeyProblem
{
    None,
    /// <summary>No Ctrl, Alt or Win (Shift alone would steal typing).</summary>
    NeedsModifier,
    /// <summary>Another action already has this chord (<see cref="HotkeyCheck.Conflict"/>).</summary>
    SameAsOther,
    /// <summary><c>RegisterHotKey</c> failed with ERROR_HOTKEY_ALREADY_REGISTERED: another app or Windows owns it.</summary>
    UsedElsewhere,
    /// <summary><c>RegisterHotKey</c> failed with another error (<see cref="HotkeyCheck.Error"/>).</summary>
    Rejected,
}

/// <summary>
/// The verdict on a chord; <see cref="Error"/> is the Win32 error of
/// <see cref="HotkeyProblem.Rejected"/>, <see cref="Conflict"/> the action that
/// already has the chord of <see cref="HotkeyProblem.SameAsOther"/>.
/// </summary>
internal readonly record struct HotkeyCheck(HotkeyProblem Problem, int Error = 0, HotkeyAction? Conflict = null)
{
    public bool IsAccepted => Problem == HotkeyProblem.None;
}

/// <summary>
/// The pure part of the shortcut recorder (PLAN.md 18.9, "Shortcut
/// recorder"): a key press to a <see cref="HotkeyBinding"/>, the checks
/// before it is saved, and the inline reason. Port of <c>handle(_:)</c> and
/// <c>carbonModifiers(_:)</c> in mac/Hearsay/Features/Hotkeys/HotkeyRecorderView.swift,
/// with three Windows additions: Backspace or Delete alone restores the
/// action's default, a chord either other action already has is refused, and a
/// chord <c>RegisterHotKey</c> refuses (the shell owns many Win chords) is
/// refused before it is saved. The control is <see cref="HotkeyRecorderView"/>.
/// </summary>
internal static class HotkeyRecorder
{
    public const uint VkBack = 0x08;
    public const uint VkEscape = 0x1B;
    public const uint VkDelete = 0x2E;

    /// <summary>The MOD_* flags of the modifier keys held down (the Mac's <c>carbonModifiers</c>).</summary>
    public static HotkeyModifiers Modifiers(bool control, bool alt, bool shift, bool win)
    {
        var modifiers = HotkeyModifiers.None;
        if (control) modifiers |= HotkeyModifiers.Control;
        if (alt) modifiers |= HotkeyModifiers.Alt;
        if (shift) modifiers |= HotkeyModifiers.Shift;
        if (win) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    /// <summary>
    /// Keys that never end a chord: Shift, Ctrl, Alt (generic, left and
    /// right), the Windows keys, the lock keys, and the IME's VK_PROCESSKEY.
    /// </summary>
    public static bool IsModifierOrLockKey(uint virtualKey) => virtualKey is
        0x00 or 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or 0x90 or 0x91 or (>= 0xA0 and <= 0xA5) or 0xE5 or 0xFF;

    /// <summary>
    /// A key press while listening: <paramref name="virtualKey"/> (VK_*) with
    /// <paramref name="modifiers"/> held. Escape alone cancels, as on the Mac;
    /// Backspace or Delete alone gives <paramref name="defaultBinding"/>.
    /// </summary>
    public static RecorderInput Interpret(uint virtualKey, HotkeyModifiers modifiers, HotkeyBinding defaultBinding)
    {
        if (IsModifierOrLockKey(virtualKey)) return new(RecorderInputKind.Ignored, default);
        var candidate = new HotkeyBinding(virtualKey, modifiers);
        if (modifiers == HotkeyModifiers.None)
        {
            if (virtualKey == VkEscape) return new(RecorderInputKind.Cancel, candidate);
            if (virtualKey is VkBack or VkDelete) return new(RecorderInputKind.ResetToDefault, defaultBinding);
        }
        return candidate.IsValidGlobalShortcut
            ? new(RecorderInputKind.Chord, candidate)
            : new(RecorderInputKind.NeedsModifier, candidate);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> may become the binding: it needs
    /// Ctrl, Alt or Win, must differ from every binding in
    /// <paramref name="others"/> (the other two actions', PLAN.md 18.10), and
    /// <paramref name="probe"/> (a trial <c>RegisterHotKey</c>, 0 when free,
    /// else the Win32 error; called only when the first two pass) must accept
    /// it.
    /// </summary>
    public static HotkeyCheck Validate(HotkeyBinding candidate, IEnumerable<(HotkeyAction Action, HotkeyBinding Binding)> others, Func<HotkeyBinding, int> probe)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(probe);
        if (!candidate.IsValidGlobalShortcut) return new(HotkeyProblem.NeedsModifier);
        foreach (var (action, binding) in others)
        {
            if (candidate == binding) return new(HotkeyProblem.SameAsOther, Conflict: action);
        }
        return probe(candidate) switch
        {
            0 => new(HotkeyProblem.None),
            NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED => new(HotkeyProblem.UsedElsewhere),
            var error => new(HotkeyProblem.Rejected, error),
        };
    }

    /// <summary>The bindings of the two actions other than <paramref name="action"/>.</summary>
    public static IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> OthersOf(AppSettings settings, HotkeyAction action)
    {
        ArgumentNullException.ThrowIfNull(settings);
        (HotkeyAction, HotkeyBinding)[] all =
        [
            (HotkeyAction.StartStop, settings.StartStopHotkey),
            (HotkeyAction.Pause, settings.PauseHotkey),
            (HotkeyAction.StopStartNext, settings.StopStartNextHotkey),
        ];
        return [.. all.Where(entry => entry.Item1 != action)];
    }

    /// <summary>The action's own default (Backspace or Delete restores it; Reset restores all three).</summary>
    public static HotkeyBinding DefaultFor(HotkeyAction action) => action switch
    {
        HotkeyAction.StartStop => HotkeyBinding.DefaultStartStop,
        HotkeyAction.Pause => HotkeyBinding.DefaultPause,
        _ => HotkeyBinding.DefaultStopStartNext,
    };

    /// <summary>The inline reason under the recorder; null when accepted.</summary>
    public static string? Reason(HotkeyCheck check, HotkeyBinding candidate) => check.Problem switch
    {
        HotkeyProblem.None => null,
        HotkeyProblem.NeedsModifier => Strings.ShortcutNeedsModifier,
        HotkeyProblem.SameAsOther => check.Conflict switch
        {
            HotkeyAction.StartStop => Strings.ShortcutSameAsStartStop,
            HotkeyAction.Pause => Strings.ShortcutSameAsPause,
            _ => Strings.ShortcutSameAsStopStartNext,
        },
        HotkeyProblem.UsedElsewhere => Strings.ShortcutUsedElsewhere(HotkeyDisplay.Text(candidate)),
        _ => Strings.ShortcutRejected(check.Error),
    };
}
