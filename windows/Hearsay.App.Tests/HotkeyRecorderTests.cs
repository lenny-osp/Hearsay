using Hearsay.App.Features.Hotkeys;
using Hearsay.Core.Settings;

namespace Hearsay.App.Tests;

/// <summary>
/// The shortcut recorder's pure rules (PLAN.md 18.9, "Shortcut recorder"):
/// <see cref="HotkeyRecorder"/> and <see cref="HotkeyDisplay"/>. The Mac has no
/// test for <c>HotkeyRecorderView.handle(_:)</c>; these mirror its behavior
/// (Escape alone cancels, a chord without ⌃ ⌥ ⌘ is refused with the hint,
/// otherwise it becomes the binding) plus the Windows additions (Backspace or
/// Delete restores the default, either other action's chord and a chord
/// <c>RegisterHotKey</c> refuses are refused). No test registers a hotkey:
/// the trial registration is a stub.
/// </summary>
public sealed class HotkeyRecorderTests
{
    private const uint VkA = 0x41;
    private const uint VkR = 0x52;
    private const uint VkSpace = 0x20;
    private const HotkeyModifiers CtrlAltWin = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win;

    [Fact]
    public void ModifierFlagsAreTheWin32Ones()
    {
        Assert.Equal(HotkeyModifiers.None, HotkeyRecorder.Modifiers(false, false, false, false));
        Assert.Equal((HotkeyModifiers)0x0002, HotkeyRecorder.Modifiers(control: true, false, false, false));
        Assert.Equal((HotkeyModifiers)0x0001, HotkeyRecorder.Modifiers(false, alt: true, false, false));
        Assert.Equal((HotkeyModifiers)0x0004, HotkeyRecorder.Modifiers(false, false, shift: true, false));
        Assert.Equal((HotkeyModifiers)0x0008, HotkeyRecorder.Modifiers(false, false, false, win: true));
        Assert.Equal((HotkeyModifiers)0x000F, HotkeyRecorder.Modifiers(true, true, true, true));
    }

    [Theory]
    [InlineData(0x10u)] // VK_SHIFT
    [InlineData(0x11u)] // VK_CONTROL
    [InlineData(0x12u)] // VK_MENU
    [InlineData(0x5Bu)] // VK_LWIN
    [InlineData(0x5Cu)] // VK_RWIN
    [InlineData(0xA0u)] // VK_LSHIFT
    [InlineData(0xA5u)] // VK_RMENU
    [InlineData(0x14u)] // VK_CAPITAL
    [InlineData(0x90u)] // VK_NUMLOCK
    public void AModifierAloneKeepsListening(uint key)
    {
        Assert.Equal(RecorderInputKind.Ignored, HotkeyRecorder.Interpret(key, CtrlAltWin, HotkeyBinding.DefaultStartStop).Kind);
    }

    [Fact]
    public void AChordWithCtrlAltOrWinBecomesTheBinding()
    {
        var input = HotkeyRecorder.Interpret(VkA, HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyBinding.DefaultStartStop);
        Assert.Equal(RecorderInputKind.Chord, input.Kind);
        Assert.Equal(new HotkeyBinding(VkA, HotkeyModifiers.Control | HotkeyModifiers.Shift), input.Binding);
        Assert.Equal(RecorderInputKind.Chord, HotkeyRecorder.Interpret(VkA, HotkeyModifiers.Alt, HotkeyBinding.DefaultStartStop).Kind);
        Assert.Equal(RecorderInputKind.Chord, HotkeyRecorder.Interpret(VkA, HotkeyModifiers.Win, HotkeyBinding.DefaultStartStop).Kind);
        // The Mac's default, ⌃⌥⌘R, as Windows records it.
        Assert.Equal(HotkeyBinding.DefaultStartStop, HotkeyRecorder.Interpret(VkR, CtrlAltWin, HotkeyBinding.DefaultPause).Binding);
    }

    [Theory]
    [InlineData(HotkeyModifiers.None)]
    [InlineData(HotkeyModifiers.Shift)]
    public void AKeyWithoutCtrlAltOrWinNeedsAModifier(HotkeyModifiers modifiers)
    {
        Assert.Equal(RecorderInputKind.NeedsModifier, HotkeyRecorder.Interpret(VkA, modifiers, HotkeyBinding.DefaultStartStop).Kind);
        // Shift+Escape is not Escape: it needs a modifier as any other key.
        if (modifiers != HotkeyModifiers.None)
        {
            Assert.Equal(RecorderInputKind.NeedsModifier,
                HotkeyRecorder.Interpret(HotkeyRecorder.VkEscape, modifiers, HotkeyBinding.DefaultStartStop).Kind);
        }
    }

    [Fact]
    public void EscapeAloneCancels()
    {
        Assert.Equal(RecorderInputKind.Cancel,
            HotkeyRecorder.Interpret(HotkeyRecorder.VkEscape, HotkeyModifiers.None, HotkeyBinding.DefaultStartStop).Kind);
        // With Ctrl it is a chord like any other.
        Assert.Equal(RecorderInputKind.Chord,
            HotkeyRecorder.Interpret(HotkeyRecorder.VkEscape, HotkeyModifiers.Control, HotkeyBinding.DefaultStartStop).Kind);
    }

    [Theory]
    [InlineData(HotkeyRecorder.VkBack)]
    [InlineData(HotkeyRecorder.VkDelete)]
    public void BackspaceOrDeleteAloneRestoresTheDefault(uint key)
    {
        var startStop = HotkeyRecorder.Interpret(key, HotkeyModifiers.None, HotkeyBinding.DefaultStartStop);
        Assert.Equal(RecorderInputKind.ResetToDefault, startStop.Kind);
        Assert.Equal(HotkeyBinding.DefaultStartStop, startStop.Binding);
        Assert.Equal(HotkeyBinding.DefaultPause, HotkeyRecorder.Interpret(key, HotkeyModifiers.None, HotkeyBinding.DefaultPause).Binding);
        // Ctrl+Alt+Delete and friends are chords (Windows keeps that one anyway).
        Assert.Equal(RecorderInputKind.Chord, HotkeyRecorder.Interpret(key, HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyBinding.DefaultPause).Kind);
    }

    private static readonly (HotkeyAction, HotkeyBinding)[] OthersOfStartStop =
    [
        (HotkeyAction.Pause, HotkeyBinding.DefaultPause),
        (HotkeyAction.StopStartNext, HotkeyBinding.DefaultStopStartNext),
    ];

    [Fact]
    public void ValidateAcceptsAFreeChord()
    {
        var probed = new List<HotkeyBinding>();
        var candidate = new HotkeyBinding(VkA, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        var check = HotkeyRecorder.Validate(candidate, OthersOfStartStop, binding =>
        {
            probed.Add(binding);
            return 0;
        });
        Assert.True(check.IsAccepted);
        Assert.Equal([candidate], probed);
    }

    [Fact]
    public void ValidateRefusesAChordWithoutAModifierBeforeTryingIt()
    {
        var check = HotkeyRecorder.Validate(new HotkeyBinding(VkA, HotkeyModifiers.Shift), OthersOfStartStop,
            _ => throw new InvalidOperationException("not probed"));
        Assert.Equal(HotkeyProblem.NeedsModifier, check.Problem);
    }

    [Fact]
    public void ValidateRefusesEitherOtherActionsChordBeforeTryingIt()
    {
        var pause = HotkeyRecorder.Validate(HotkeyBinding.DefaultPause, OthersOfStartStop,
            _ => throw new InvalidOperationException("not probed"));
        Assert.Equal(new HotkeyCheck(HotkeyProblem.SameAsOther, Conflict: HotkeyAction.Pause), pause);
        Assert.False(pause.IsAccepted);
        var next = HotkeyRecorder.Validate(HotkeyBinding.DefaultStopStartNext, OthersOfStartStop,
            _ => throw new InvalidOperationException("not probed"));
        Assert.Equal(new HotkeyCheck(HotkeyProblem.SameAsOther, Conflict: HotkeyAction.StopStartNext), next);
    }

    [Theory]
    [InlineData(nameof(HotkeyAction.StartStop))]
    [InlineData(nameof(HotkeyAction.Pause))]
    [InlineData(nameof(HotkeyAction.StopStartNext))]
    public void EachBindingIsCheckedAgainstBothOthers(string actionName)
    {
        var action = Enum.Parse<HotkeyAction>(actionName);
        using var folder = new ScratchFolder();
        var settings = new AppSettings(folder.Path);
        var others = HotkeyRecorder.OthersOf(settings, action);
        Assert.Equal(2, others.Count);
        Assert.DoesNotContain(others, entry => entry.Action == action);
        foreach (var (other, binding) in others)
        {
            var check = HotkeyRecorder.Validate(binding, others, _ => throw new InvalidOperationException("not probed"));
            Assert.Equal(new HotkeyCheck(HotkeyProblem.SameAsOther, Conflict: other), check);
        }
        // Its own binding is not a conflict.
        var own = action switch
        {
            HotkeyAction.StartStop => settings.StartStopHotkey,
            HotkeyAction.Pause => settings.PauseHotkey,
            _ => settings.StopStartNextHotkey,
        };
        Assert.True(HotkeyRecorder.Validate(own, others, _ => 0).IsAccepted);
    }

    [Fact]
    public void EachActionHasItsOwnDefault()
    {
        Assert.Equal(HotkeyBinding.DefaultStartStop, HotkeyRecorder.DefaultFor(HotkeyAction.StartStop));
        Assert.Equal(HotkeyBinding.DefaultPause, HotkeyRecorder.DefaultFor(HotkeyAction.Pause));
        Assert.Equal(HotkeyBinding.DefaultStopStartNext, HotkeyRecorder.DefaultFor(HotkeyAction.StopStartNext));
        Assert.Equal("Ctrl+Alt+Win+N", HotkeyDisplay.Text(HotkeyBinding.DefaultStopStartNext));
        var reset = HotkeyRecorder.Interpret(HotkeyRecorder.VkBack, HotkeyModifiers.None, HotkeyRecorder.DefaultFor(HotkeyAction.StopStartNext));
        Assert.Equal(RecorderInputKind.ResetToDefault, reset.Kind);
        Assert.Equal(HotkeyBinding.DefaultStopStartNext, reset.Binding);
    }

    [Fact]
    public void ValidateReportsAChordAnotherAppOrWindowsOwns()
    {
        var candidate = new HotkeyBinding(0x45, HotkeyModifiers.Win); // Win+E, the shell's
        Assert.Equal(new HotkeyCheck(HotkeyProblem.UsedElsewhere), HotkeyRecorder.Validate(candidate, OthersOfStartStop, _ => 1409));
        Assert.Equal(new HotkeyCheck(HotkeyProblem.Rejected, 87), HotkeyRecorder.Validate(candidate, OthersOfStartStop, _ => 87));
    }

    [Fact]
    public void ReasonsNameTheProblem()
    {
        var candidate = new HotkeyBinding(0x45, HotkeyModifiers.Win);
        Assert.Null(HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.None), candidate));
        Assert.Equal("Include Ctrl, Alt, or Win.",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.NeedsModifier), candidate));
        Assert.Equal("Already used for Pause / Resume.",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.SameAsOther, Conflict: HotkeyAction.Pause), candidate));
        Assert.Equal("Already used for Start / Stop recording.",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.SameAsOther, Conflict: HotkeyAction.StartStop), candidate));
        Assert.Equal("Already used for Stop & Start Next.",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.SameAsOther, Conflict: HotkeyAction.StopStartNext), candidate));
        Assert.Equal("Win+E is already used by another app or Windows.",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.UsedElsewhere), candidate));
        Assert.Equal("Windows does not accept this shortcut (error 87).",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.Rejected, 87), candidate));
    }

    [Fact]
    public void DisplayIsCoresDisplayStringInEnglish()
    {
        HotkeyBinding[] bindings =
        [
            HotkeyBinding.DefaultStartStop, HotkeyBinding.DefaultPause,
            new(VkA, HotkeyModifiers.Control | HotkeyModifiers.Shift),
            new(0x70, HotkeyModifiers.Alt), new(0xBA, HotkeyModifiers.Win), new(0x21, HotkeyModifiers.Control),
            new(VkSpace, HotkeyModifiers.Control), new(0xE2, HotkeyModifiers.Control),
        ];
        foreach (var binding in bindings) Assert.Equal(binding.DisplayString, HotkeyDisplay.Text(binding));
        Assert.Equal("Ctrl+Alt+Win+R", HotkeyDisplay.Text(HotkeyBinding.DefaultStartStop));
        Assert.Equal("Ctrl+Alt+Shift+Win+P", HotkeyDisplay.Text(new(0x50, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win)));
        Assert.Equal("Ctrl+Space", HotkeyDisplay.Text(new(VkSpace, HotkeyModifiers.Control)));
        Assert.Equal("Ctrl+Key 226", HotkeyDisplay.Text(new(0xE2, HotkeyModifiers.Control)));
    }

    [Fact]
    public void DisplayUsesWindowsNamesInGerman()
    {
        using var german = new InterfaceLanguageScope(InterfaceLanguage.German);
        Assert.Equal("Strg+Alt+Win+R", HotkeyDisplay.Text(HotkeyBinding.DefaultStartStop));
        Assert.Equal("Strg+Umschalt+Leertaste", HotkeyDisplay.Text(new(VkSpace, HotkeyModifiers.Control | HotkeyModifiers.Shift)));
        Assert.Equal("Alt+Taste 226", HotkeyDisplay.Text(new(0xE2, HotkeyModifiers.Alt)));
        Assert.Equal("Strg+Umschalt+A wird bereits von einer anderen App oder von Windows verwendet.",
            HotkeyRecorder.Reason(new HotkeyCheck(HotkeyProblem.UsedElsewhere), new(VkA, HotkeyModifiers.Control | HotkeyModifiers.Shift)));
    }

    [Theory]
    [MemberData(nameof(StringsTests.Languages), MemberType = typeof(StringsTests))]
    public void RecorderTextsComeFromTheSharedTranslations(InterfaceLanguage language)
    {
        using var scope = new InterfaceLanguageScope(language);
        Assert.Equal(Translations.Text(language, "windows", "Ctrl"), Strings.ModifierCtrl);
        Assert.Equal(Translations.Text(language, "windows", "Shift"), Strings.ModifierShift);
        Assert.Equal(Translations.Text(language, "core", "Space"), Strings.KeySpace);
        Assert.Equal(Translations.Text(language, "app", "Type shortcut…"), Strings.RecorderListening);
        Assert.Equal(Translations.Text(language, "windows", "Include Ctrl, Alt, or Win."), Strings.ShortcutNeedsModifier);
        Assert.Equal(Translations.Format(language, "windows", "Windows does not accept this shortcut (error %d).", 5), Strings.ShortcutRejected(5));
        Assert.Equal(Translations.Text(language, "windows", "Already used for Stop & Start Next."), Strings.ShortcutSameAsStopStartNext);
        Assert.Equal(Translations.Text(language, "app", "Stop & Start Next:"), Strings.ShortcutStopStartNext);
        // Every modifier name the recorder's texts mention is the one the display uses.
        foreach (var name in new[] { Strings.ModifierCtrl, Strings.ModifierAlt, Strings.ModifierWin })
        {
            Assert.Contains(name, Strings.ShortcutNeedsModifier, StringComparison.Ordinal);
            Assert.Contains(name, Strings.RecorderListeningTooltip, StringComparison.Ordinal);
        }
    }
}
