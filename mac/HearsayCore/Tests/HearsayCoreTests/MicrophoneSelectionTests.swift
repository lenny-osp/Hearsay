import Testing
@testable import HearsayCore

/// PLAN.md 4.13: the Microphone picker's "No microphone (system audio only)"
/// row, the automatic choice with no input device, and the effective
/// system-audio rule.
@Suite struct MicrophoneSelectionTests {
    @Test func firstUpdatePicksTheDefaultDevice() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: ["built-in", "usb"], defaultUID: "usb")
        #expect(selection.choice == .device(uid: "usb"))
        #expect(!selection.isAutomatic)
        #expect(selection.recordsMicrophone)
    }

    @Test func firstUpdateWithoutDefaultPicksTheFirstDevice() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: ["built-in", "usb"], defaultUID: nil)
        #expect(selection.choice == .device(uid: "built-in"))
        var stale = MicrophoneSelection()
        stale.update(deviceUIDs: ["built-in"], defaultUID: "gone")
        #expect(stale.choice == .device(uid: "built-in"))
    }

    @Test func connectedDeviceStaysChosen() {
        var selection = MicrophoneSelection()
        selection.select(.device(uid: "built-in"))
        selection.update(deviceUIDs: ["built-in", "usb"], defaultUID: "usb")
        #expect(selection.choice == .device(uid: "built-in"))
    }

    @Test func removedDeviceFallsBackToTheDefault() {
        var selection = MicrophoneSelection()
        selection.select(.device(uid: "usb"))
        selection.update(deviceUIDs: ["built-in"], defaultUID: "built-in")
        #expect(selection.choice == .device(uid: "built-in"))
    }

    @Test func noDeviceSelectsNoMicrophoneAutomatically() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: [], defaultUID: nil)
        #expect(selection.choice == .noMicrophone)
        #expect(selection.isAutomatic)
        #expect(!selection.recordsMicrophone)
        #expect(selection.deviceUID == nil)

        var unplugged = MicrophoneSelection()
        unplugged.select(.device(uid: "usb"))
        unplugged.update(deviceUIDs: [], defaultUID: nil)
        #expect(unplugged.choice == .noMicrophone)
        #expect(unplugged.isAutomatic)
    }

    @Test func automaticNoMicrophoneSwitchesBackWhenADeviceAppears() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: [], defaultUID: nil)
        selection.update(deviceUIDs: ["usb"], defaultUID: "usb")
        #expect(selection.choice == .device(uid: "usb"))
        #expect(!selection.isAutomatic)
    }

    @Test func userPickedNoMicrophoneStays() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: ["built-in"], defaultUID: "built-in")
        selection.select(.noMicrophone)
        #expect(!selection.isAutomatic)
        selection.update(deviceUIDs: ["built-in", "usb"], defaultUID: "usb")
        #expect(selection.choice == .noMicrophone)
        selection.update(deviceUIDs: [], defaultUID: nil)
        selection.update(deviceUIDs: ["usb"], defaultUID: "usb")
        #expect(selection.choice == .noMicrophone)
        #expect(!selection.isAutomatic)
    }

    @Test func pickingTheAutomaticRowMakesItTheUsersChoice() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: [], defaultUID: nil)
        selection.select(.noMicrophone)
        selection.update(deviceUIDs: ["usb"], defaultUID: "usb")
        #expect(selection.choice == .noMicrophone)
    }

    @Test func stopAndStartNextKeepsAnAutomaticNoMicrophone() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: [], defaultUID: nil)
        selection.update(deviceUIDs: ["usb"], defaultUID: "usb", keepingNoMicrophone: true)
        #expect(selection.choice == .noMicrophone)
        #expect(selection.isAutomatic)
        // Back to idle, the next device update switches back.
        selection.update(deviceUIDs: ["usb"], defaultUID: "usb")
        #expect(selection.choice == .device(uid: "usb"))
    }

    @Test func keepingNoMicrophoneStillFollowsAMissingDevice() {
        var selection = MicrophoneSelection()
        selection.select(.device(uid: "usb"))
        selection.update(deviceUIDs: ["built-in"], defaultUID: "built-in", keepingNoMicrophone: true)
        #expect(selection.choice == .device(uid: "built-in"))
    }

    @Test func effectiveSystemAudio() {
        var selection = MicrophoneSelection()
        selection.update(deviceUIDs: ["built-in"], defaultUID: "built-in")
        #expect(selection.capturesSystemAudio(stored: true))
        #expect(!selection.capturesSystemAudio(stored: false))
        selection.select(.noMicrophone)
        #expect(selection.capturesSystemAudio(stored: true))
        #expect(selection.capturesSystemAudio(stored: false))
        selection.select(.device(uid: "built-in"))
        #expect(!selection.capturesSystemAudio(stored: false))
    }
}
