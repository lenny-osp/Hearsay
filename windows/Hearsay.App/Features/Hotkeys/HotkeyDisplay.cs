using System.Globalization;
using Hearsay.Core.Settings;

namespace Hearsay.App.Features.Hotkeys;

/// <summary>
/// A shortcut as the interface shows it, for example "Ctrl+Alt+Win+R", or
/// "Strg+Alt+Umschalt+R" in German: <see cref="HotkeyBinding.DisplayString"/>
/// (Core, English, used in log lines) with the names translated where
/// Windows translates them in its own menus (PLAN.md 18.3, "Localization"):
/// the modifiers (German Strg and Umschalt, Spanish Mayús), the space bar and
/// "Key n" (the Mac's core catalog keys "Space" and "Key %u"). Letters,
/// digits, F-keys, punctuation and the other named keys keep
/// <see cref="HotkeyBinding.KeyName"/> (PLAN.md 18.9). The Mac shows symbols
/// (⌃⌥⇧⌘R, <c>displayString</c> in
/// mac/HearsayCore/Sources/HearsayCore/Settings/HotkeyBinding.swift).
/// </summary>
internal static class HotkeyDisplay
{
    public static string Text(HotkeyBinding binding)
    {
        var parts = new List<string>(5);
        // The Mac's order ⌃⌥⇧⌘, as in HotkeyBinding.DisplayString.
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add(Strings.ModifierCtrl);
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add(Strings.ModifierAlt);
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add(Strings.ModifierShift);
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add(Strings.ModifierWin);
        parts.Add(KeyName(binding.KeyCode));
        return string.Join('+', parts);
    }

    /// <summary><see cref="HotkeyBinding.KeyName"/>, with "Space" and "Key n" translated.</summary>
    public static string KeyName(uint keyCode)
    {
        if (keyCode == 0x20) return Strings.KeySpace;
        var english = HotkeyBinding.KeyName(keyCode);
        // Core writes "Key n" for a key with no name.
        return english == string.Create(CultureInfo.InvariantCulture, $"Key {keyCode}") ? Strings.KeyNumber(keyCode) : english;
    }
}
