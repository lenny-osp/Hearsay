using System.Text.Json.Nodes;

namespace Hearsay.Core.Settings;

/// <summary>
/// Modifier keys of a global shortcut. The values are the Win32
/// <c>RegisterHotKey</c> MOD_* flags (WinUser.h), so they pass straight
/// through, as the Mac's Carbon masks do.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    /// <summary>MOD_ALT; the Mac's Option (⌥).</summary>
    Alt = 0x0001,
    /// <summary>MOD_CONTROL; the Mac's Control (⌃).</summary>
    Control = 0x0002,
    /// <summary>MOD_SHIFT; the Mac's Shift (⇧).</summary>
    Shift = 0x0004,
    /// <summary>MOD_WIN; takes the place of the Mac's Command (⌘).</summary>
    Win = 0x0008,
}

/// <summary>
/// A global keyboard shortcut (PLAN.md 4.4): a virtual-key code plus
/// modifiers, stored in <see cref="AppSettings"/> and registered with
/// <c>RegisterHotKey</c> by the app (W4; nothing here registers anything).
/// Port of mac/HearsayCore/Sources/HearsayCore/Settings/HotkeyBinding.swift.
/// <para>
/// Both values use the Win32 encoding: <see cref="KeyCode"/> is a VK_*
/// virtual-key code and <see cref="Modifiers"/> holds MOD_* flags. Stored in
/// settings.json as <c>{"keyCode": 82, "modifiers": 11}</c>, the shape the
/// Mac's <c>JSONEncoder</c> writes (with Carbon values there).
/// </para>
/// </summary>
public readonly record struct HotkeyBinding(uint KeyCode, HotkeyModifiers Modifiers)
{
    /// <summary>MOD_NOREPEAT: holding the keys down does not fire again (Carbon hotkeys fire once per press).</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>VK_R, VK_P, and VK_N.</summary>
    public const uint KeyCodeR = 0x52;
    public const uint KeyCodeP = 0x50;
    public const uint KeyCodeN = 0x4E;

    private const HotkeyModifiers DefaultModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win;

    // The Windows defaults translate the Mac's ⌃⌥⌘ as Ctrl+Alt+Win (Command
    // becomes Win). This is a proposal for the owner to confirm (PLAN.md 18.3,
    // "Hotkeys, login, window modes").

    /// <summary>Ctrl+Alt+Win+R toggles Start / Stop (the Mac's ⌃⌥⌘R).</summary>
    public static HotkeyBinding DefaultStartStop { get; } = new(KeyCodeR, DefaultModifiers);

    /// <summary>Ctrl+Alt+Win+P toggles Pause / Resume (the Mac's ⌃⌥⌘P).</summary>
    public static HotkeyBinding DefaultPause { get; } = new(KeyCodeP, DefaultModifiers);

    /// <summary>Ctrl+Alt+Win+N stops the recording and starts the next one (the Mac's ⌃⌥⌘N; PLAN.md 4.9 and 18.10).</summary>
    public static HotkeyBinding DefaultStopStartNext { get; } = new(KeyCodeN, DefaultModifiers);

    /// <summary>
    /// A global shortcut needs at least one of Ctrl, Alt, Win; Shift alone
    /// would steal ordinary typing.
    /// </summary>
    public bool IsValidGlobalShortcut =>
        (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;

    /// <summary>The <c>fsModifiers</c> argument for <c>RegisterHotKey</c>: the MOD_* flags plus MOD_NOREPEAT.</summary>
    public uint RegisterHotKeyModifiers => (uint)Modifiers | ModNoRepeat;

    /// <summary>The <c>vk</c> argument for <c>RegisterHotKey</c>.</summary>
    public uint VirtualKey => KeyCode;

    /// <summary>
    /// The shortcut as Windows writes it, for example "Ctrl+Alt+Win+R".
    /// Modifiers come in the Mac's order (⌃⌥⇧⌘ becomes Ctrl, Alt, Shift, Win).
    /// </summary>
    public string DisplayString
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyName(KeyCode));
            return string.Join('+', parts);
        }
    }

    /// <summary>
    /// Name of a key on the US layout. <c>RegisterHotKey</c> takes a virtual
    /// key, and for letters and digits that is the printed character; the OEM
    /// punctuation keys are named by their US label, as on the Mac. "Key n"
    /// for a key with no name ("Key \(keyCode)" in the Mac's catalog; English
    /// until W7 wires the translations, as "Space" is).
    /// </summary>
    public static string KeyName(uint keyCode)
    {
        if (keyCode is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)keyCode).ToString();
        if (keyCode is >= 0x70 and <= 0x87) return $"F{keyCode - 0x6F}";
        return KeyNames.TryGetValue(keyCode, out var name) ? name : $"Key {keyCode}";
    }

    private static readonly Dictionary<uint, string> KeyNames = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "Page Up",
        [0x22] = "Page Down",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "←",
        [0x26] = "↑",
        [0x27] = "→",
        [0x28] = "↓",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x60] = "Num 0",
        [0x61] = "Num 1",
        [0x62] = "Num 2",
        [0x63] = "Num 3",
        [0x64] = "Num 4",
        [0x65] = "Num 5",
        [0x66] = "Num 6",
        [0x67] = "Num 7",
        [0x68] = "Num 8",
        [0x69] = "Num 9",
        [0x6A] = "Num *",
        [0x6B] = "Num +",
        [0x6D] = "Num -",
        [0x6E] = "Num .",
        [0x6F] = "Num /",
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    /// <summary>The stored form: <c>{"keyCode": n, "modifiers": n}</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["keyCode"] = KeyCode,
        ["modifiers"] = (uint)Modifiers,
    };

    /// <summary>
    /// Parses the stored form; null when it is not an object with two
    /// unsigned integers (the Mac's failed <c>JSONDecoder</c> decode).
    /// Unknown modifier bits are kept, as the Mac's option set keeps them.
    /// </summary>
    public static HotkeyBinding? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return null;
        if (json["keyCode"] is not JsonValue key || !key.TryGetValue<uint>(out var keyCode)) return null;
        if (json["modifiers"] is not JsonValue mods || !mods.TryGetValue<uint>(out var modifiers)) return null;
        return new HotkeyBinding(keyCode, (HotkeyModifiers)modifiers);
    }
}
