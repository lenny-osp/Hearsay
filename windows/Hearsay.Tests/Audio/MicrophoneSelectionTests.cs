using Hearsay.Core.Audio;

namespace Hearsay.Tests.Audio;

/// <summary>
/// Port of mac/HearsayCore/Tests/HearsayCoreTests/MicrophoneSelectionTests.swift
/// (PLAN.md 4.13): the Microphone picker's "No microphone (system audio
/// only)" row, the automatic choice with no input device, and the effective
/// system-audio rule.
/// </summary>
public class MicrophoneSelectionTests
{
    private static MicrophoneChoice Device(string id) => MicrophoneChoice.ForDevice(id);

    [Fact]
    public void FirstUpdatePicksTheDefaultDevice()
    {
        var selection = new MicrophoneSelection();
        selection.Update(["built-in", "usb"], "usb");
        Assert.Equal(Device("usb"), selection.Choice);
        Assert.False(selection.IsAutomatic);
        Assert.True(selection.RecordsMicrophone);
    }

    [Fact]
    public void FirstUpdateWithoutDefaultPicksTheFirstDevice()
    {
        var selection = new MicrophoneSelection();
        selection.Update(["built-in", "usb"], null);
        Assert.Equal(Device("built-in"), selection.Choice);
        var stale = new MicrophoneSelection();
        stale.Update(["built-in"], "gone");
        Assert.Equal(Device("built-in"), stale.Choice);
    }

    [Fact]
    public void ConnectedDeviceStaysChosen()
    {
        var selection = new MicrophoneSelection();
        selection.Select(Device("built-in"));
        selection.Update(["built-in", "usb"], "usb");
        Assert.Equal(Device("built-in"), selection.Choice);
    }

    [Fact]
    public void RemovedDeviceFallsBackToTheDefault()
    {
        var selection = new MicrophoneSelection();
        selection.Select(Device("usb"));
        selection.Update(["built-in"], "built-in");
        Assert.Equal(Device("built-in"), selection.Choice);
    }

    [Fact]
    public void NoDeviceSelectsNoMicrophoneAutomatically()
    {
        var selection = new MicrophoneSelection();
        selection.Update([], null);
        Assert.Equal(MicrophoneChoice.NoMicrophone, selection.Choice);
        Assert.True(selection.IsAutomatic);
        Assert.False(selection.RecordsMicrophone);
        Assert.Null(selection.DeviceId);

        var unplugged = new MicrophoneSelection();
        unplugged.Select(Device("usb"));
        unplugged.Update([], null);
        Assert.Equal(MicrophoneChoice.NoMicrophone, unplugged.Choice);
        Assert.True(unplugged.IsAutomatic);
    }

    [Fact]
    public void AutomaticNoMicrophoneSwitchesBackWhenADeviceAppears()
    {
        var selection = new MicrophoneSelection();
        selection.Update([], null);
        selection.Update(["usb"], "usb");
        Assert.Equal(Device("usb"), selection.Choice);
        Assert.False(selection.IsAutomatic);
    }

    [Fact]
    public void UserPickedNoMicrophoneStays()
    {
        var selection = new MicrophoneSelection();
        selection.Update(["built-in"], "built-in");
        selection.Select(MicrophoneChoice.NoMicrophone);
        Assert.False(selection.IsAutomatic);
        selection.Update(["built-in", "usb"], "usb");
        Assert.Equal(MicrophoneChoice.NoMicrophone, selection.Choice);
        selection.Update([], null);
        selection.Update(["usb"], "usb");
        Assert.Equal(MicrophoneChoice.NoMicrophone, selection.Choice);
        Assert.False(selection.IsAutomatic);
    }

    [Fact]
    public void PickingTheAutomaticRowMakesItTheUsersChoice()
    {
        var selection = new MicrophoneSelection();
        selection.Update([], null);
        selection.Select(MicrophoneChoice.NoMicrophone);
        selection.Update(["usb"], "usb");
        Assert.Equal(MicrophoneChoice.NoMicrophone, selection.Choice);
    }

    [Fact]
    public void StopAndStartNextKeepsAnAutomaticNoMicrophone()
    {
        var selection = new MicrophoneSelection();
        selection.Update([], null);
        selection.Update(["usb"], "usb", keepingNoMicrophone: true);
        Assert.Equal(MicrophoneChoice.NoMicrophone, selection.Choice);
        Assert.True(selection.IsAutomatic);
        // Back to idle, the next device update switches back.
        selection.Update(["usb"], "usb");
        Assert.Equal(Device("usb"), selection.Choice);
    }

    [Fact]
    public void KeepingNoMicrophoneStillFollowsAMissingDevice()
    {
        var selection = new MicrophoneSelection();
        selection.Select(Device("usb"));
        selection.Update(["built-in"], "built-in", keepingNoMicrophone: true);
        Assert.Equal(Device("built-in"), selection.Choice);
    }

    [Fact]
    public void EffectiveSystemAudio()
    {
        var selection = new MicrophoneSelection();
        selection.Update(["built-in"], "built-in");
        Assert.True(selection.CapturesSystemAudio(stored: true));
        Assert.False(selection.CapturesSystemAudio(stored: false));
        selection.Select(MicrophoneChoice.NoMicrophone);
        Assert.True(selection.CapturesSystemAudio(stored: true));
        Assert.True(selection.CapturesSystemAudio(stored: false));
        selection.Select(Device("built-in"));
        Assert.False(selection.CapturesSystemAudio(stored: false));
    }
}
