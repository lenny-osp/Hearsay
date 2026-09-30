using System.Text.Json.Nodes;
using Hearsay.Core.Settings;

namespace Hearsay.Tests.Settings;

/// <summary>
/// Windows-only checks of HotkeyBinding.cs (the Swift hotkey tests are in
/// <see cref="AppSettingsTests"/>, as in AppSettingsTests.swift): the Win32
/// encoding that goes to <c>RegisterHotKey</c>, the key names, and the
/// stored JSON.
/// </summary>
public sealed class HotkeyBindingTests
{
    [Fact]
    public void ModifiersAreTheWin32ModFlags()
    {
        Assert.Equal(0x0001, (int)HotkeyModifiers.Alt);
        Assert.Equal(0x0002, (int)HotkeyModifiers.Control);
        Assert.Equal(0x0004, (int)HotkeyModifiers.Shift);
        Assert.Equal(0x0008, (int)HotkeyModifiers.Win);
    }

    [Fact]
    public void DefaultsAreCtrlAltWinRPAndN()
    {
        Assert.Equal(0x4Eu, HotkeyBinding.DefaultStopStartNext.VirtualKey);
        Assert.Equal(HotkeyBinding.DefaultStartStop.Modifiers, HotkeyBinding.DefaultStopStartNext.Modifiers);
        Assert.Equal(0x52u, HotkeyBinding.DefaultStartStop.VirtualKey);
        Assert.Equal(0x50u, HotkeyBinding.DefaultPause.VirtualKey);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win, HotkeyBinding.DefaultStartStop.Modifiers);
        Assert.Equal(HotkeyBinding.DefaultStartStop.Modifiers, HotkeyBinding.DefaultPause.Modifiers);
    }

    [Fact]
    public void RegisterHotKeyModifiersAddNoRepeat()
    {
        Assert.Equal(0x0002u | 0x0001u | 0x0008u | 0x4000u, HotkeyBinding.DefaultStartStop.RegisterHotKeyModifiers);
        Assert.Equal(0x4000u, new HotkeyBinding(0x70, HotkeyModifiers.None).RegisterHotKeyModifiers);
    }

    [Fact]
    public void DisplayStringUsesTheMacModifierOrder()
    {
        var all = HotkeyModifiers.Win | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control;
        Assert.Equal("Ctrl+Alt+Shift+Win+A", new HotkeyBinding(0x41, all).DisplayString);
        Assert.Equal("Z", new HotkeyBinding(0x5A, HotkeyModifiers.None).DisplayString);
    }

    [Theory]
    [InlineData(0x41u, "A")]
    [InlineData(0x5Au, "Z")]
    [InlineData(0x30u, "0")]
    [InlineData(0x39u, "9")]
    [InlineData(0x70u, "F1")]
    [InlineData(0x7Bu, "F12")]
    [InlineData(0x87u, "F24")]
    [InlineData(0x20u, "Space")]
    [InlineData(0x0Du, "Enter")]
    [InlineData(0x1Bu, "Esc")]
    [InlineData(0x25u, "←")]
    [InlineData(0xBAu, ";")]
    [InlineData(0xDCu, "\\")]
    [InlineData(0xC0u, "`")]
    [InlineData(0x6Au, "Num *")]
    [InlineData(0xFFu, "Key 255")]
    public void KeyNames(uint keyCode, string name)
    {
        Assert.Equal(name, HotkeyBinding.KeyName(keyCode));
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var binding = new HotkeyBinding(0x7B, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        Assert.Equal("""{"keyCode":123,"modifiers":6}""", binding.ToJson().ToJsonString());
        Assert.Equal(binding, HotkeyBinding.FromJson(JsonNode.Parse(binding.ToJson().ToJsonString())));
    }

    [Fact]
    public void UnknownModifierBitsAreKept()
    {
        var parsed = HotkeyBinding.FromJson(JsonNode.Parse("""{"keyCode":82,"modifiers":4098}"""));
        Assert.Equal((HotkeyModifiers)0x1002, parsed?.Modifiers);
    }

    [Fact]
    public void BadJsonGivesNull()
    {
        Assert.Null(HotkeyBinding.FromJson(null));
        Assert.Null(HotkeyBinding.FromJson(JsonNode.Parse("""{"keyCode":"R","modifiers":2}""")));
        Assert.Null(HotkeyBinding.FromJson(JsonNode.Parse("""{"keyCode":82,"modifiers":1.5}""")));
        Assert.Null(HotkeyBinding.FromJson(JsonNode.Parse("\"Ctrl+R\"")));
    }
}
