using System.ComponentModel;
using System.Text.Json.Nodes;
using Hearsay.Core.Settings;
using Hearsay.Core.Transcription;

namespace Hearsay.Tests.Settings;

/// <summary>
/// Port of <c>AppSettingsTests</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/AppSettingsTests.swift, one test
/// per Swift test (a Swift parameterized test is a theory here). The Mac's
/// bookmark test becomes <see cref="OutputFolderRoundTripsAndClears"/>; the
/// Windows-only tests (the settings file itself, change notification) are at
/// the end and in <see cref="SettingsFileTests"/>.
/// </summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    private static string? Stored(string folder, string key) => ScratchSettings.Raw(folder).GetString(key);

    [Fact]
    public void DefaultsWhenEmpty()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.Equal(WindowMode.MenuBarAndDock, settings.WindowMode);
        Assert.Null(settings.OutputFolder);
    }

    [Theory]
    [InlineData(WindowMode.MenuBarAndDock)]
    [InlineData(WindowMode.MenuBarOnly)]
    [InlineData(WindowMode.DockOnly)]
    public void WindowModeRoundTrips(WindowMode mode)
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        settings.WindowMode = mode;
        // The default is not written when it is set to itself (no change); it reads back the same.
        if (mode != WindowMode.MenuBarAndDock)
        {
            Assert.Equal(mode.StorageValue(), Stored(folder, AppSettings.Key.WindowMode));
        }
        Assert.Equal(mode, new AppSettings(folder).WindowMode);
    }

    [Fact]
    public void WindowModeStorageValuesAreTheMacRawValues()
    {
        Assert.Equal(["menuBarAndDock", "menuBarOnly", "dockOnly"], WindowModes.All.Select(m => m.StorageValue()));
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        settings.WindowMode = WindowMode.DockOnly;
        settings.WindowMode = WindowMode.MenuBarAndDock;
        Assert.Equal("menuBarAndDock", Stored(folder, AppSettings.Key.WindowMode));
    }

    [Fact]
    public void UnknownWindowModeFallsBackToDefault()
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).SetString(AppSettings.Key.WindowMode, "floating");
        Assert.Equal(WindowMode.MenuBarAndDock, new AppSettings(folder).WindowMode);
    }

    /// <summary>The Mac's <c>bookmarkRoundTripsAndClears</c>: Windows stores the folder path.</summary>
    [Fact]
    public void OutputFolderRoundTripsAndClears()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        const string chosen = @"D:\Meetings\Hearsay";
        settings.OutputFolder = chosen;
        Assert.Equal(chosen, new AppSettings(folder).OutputFolder);
        settings.OutputFolder = null;
        Assert.False(ScratchSettings.Raw(folder).Contains(AppSettings.Key.OutputFolder));
        Assert.Null(new AppSettings(folder).OutputFolder);
    }

    [Fact]
    public void KeepRecordingDefaultsOnAndRoundTrips()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.True(settings.KeepRecording);
        settings.KeepRecording = false;
        Assert.False(new AppSettings(folder).KeepRecording);
    }

    [Fact]
    public void AutoRecordTeamsMeetingsDefaultsOffAndRoundTrips()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.False(settings.AutoRecordTeamsMeetings);
        settings.AutoRecordTeamsMeetings = true;
        Assert.True(ScratchSettings.Raw(folder).GetBool(AppSettings.Key.AutoRecordTeamsMeetings));
        Assert.True(new AppSettings(folder).AutoRecordTeamsMeetings);
        settings.AutoRecordTeamsMeetings = false;
        Assert.False(new AppSettings(folder).AutoRecordTeamsMeetings);
    }

    [Fact]
    public void AutoRecordAsksLanguageDefaultsOffAndRoundTrips()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.False(settings.AutoRecordAsksLanguage);
        settings.AutoRecordAsksLanguage = true;
        Assert.True(ScratchSettings.Raw(folder).GetBool(AppSettings.Key.AutoRecordAsksLanguage));
        Assert.True(new AppSettings(folder).AutoRecordAsksLanguage);
        settings.AutoRecordAsksLanguage = false;
        Assert.False(new AppSettings(folder).AutoRecordAsksLanguage);
    }

    [Fact]
    public void MenuBarShowsStatusDefaultOnAndRoundTrips()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.True(settings.MenuBarShowsStatus);
        settings.MenuBarShowsStatus = false;
        Assert.False(new AppSettings(folder).MenuBarShowsStatus);
        settings.MenuBarShowsStatus = true;
        Assert.True(new AppSettings(folder).MenuBarShowsStatus);
    }

    [Fact]
    public void AutomaticUpdateChecksDefaultOnAndRoundTrips()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.True(settings.AutomaticUpdateChecks);
        settings.AutomaticUpdateChecks = false;
        Assert.False(new AppSettings(folder).AutomaticUpdateChecks);
    }

    [Fact]
    public void LastUpdateCheckRoundTripsAndClears()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.Null(settings.LastUpdateCheck);
        var date = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).AddTicks(1_234_567);
        settings.LastUpdateCheck = date;
        Assert.Equal(date, new AppSettings(folder).LastUpdateCheck);
        settings.LastUpdateCheck = null;
        Assert.False(ScratchSettings.Raw(folder).Contains(AppSettings.Key.LastUpdateCheck));
        Assert.Null(new AppSettings(folder).LastUpdateCheck);
    }

    [Fact]
    public void PermissionCodeHashesRoundTripAndClear()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.Null(settings.ScreenAudioGrantedCodeHash);
        Assert.Null(settings.ScreenAudioResetCodeHash);
        Assert.Null(settings.MicrophoneGrantedCodeHash);
        settings.ScreenAudioGrantedCodeHash = "cdhash H\"a\"";
        settings.ScreenAudioResetCodeHash = "cdhash H\"b\"";
        settings.MicrophoneGrantedCodeHash = "cdhash H\"c\"";
        var reloaded = new AppSettings(folder);
        Assert.Equal("cdhash H\"a\"", reloaded.ScreenAudioGrantedCodeHash);
        Assert.Equal("cdhash H\"b\"", reloaded.ScreenAudioResetCodeHash);
        Assert.Equal("cdhash H\"c\"", reloaded.MicrophoneGrantedCodeHash);
        settings.ScreenAudioGrantedCodeHash = null;
        settings.ScreenAudioResetCodeHash = null;
        settings.MicrophoneGrantedCodeHash = null;
        var raw = ScratchSettings.Raw(folder);
        Assert.False(raw.Contains(AppSettings.Key.ScreenAudioGrantedCodeHash));
        Assert.False(raw.Contains(AppSettings.Key.ScreenAudioResetCodeHash));
        Assert.False(raw.Contains(AppSettings.Key.MicrophoneGrantedCodeHash));
        var cleared = new AppSettings(folder);
        Assert.Null(cleared.ScreenAudioGrantedCodeHash);
        Assert.Null(cleared.ScreenAudioResetCodeHash);
        Assert.Null(cleared.MicrophoneGrantedCodeHash);
    }

    [Fact]
    public void PermissionCodeHashesUseSeparateKeys()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        settings.ScreenAudioGrantedCodeHash = "granted";
        settings.ScreenAudioResetCodeHash = "reset";
        settings.MicrophoneGrantedCodeHash = "mic";
        Assert.Equal("granted", Stored(folder, AppSettings.Key.ScreenAudioGrantedCodeHash));
        Assert.Equal("reset", Stored(folder, AppSettings.Key.ScreenAudioResetCodeHash));
        Assert.Equal("mic", Stored(folder, AppSettings.Key.MicrophoneGrantedCodeHash));
    }

    [Fact]
    public void LanguageDefaultsForFreshInstall()
    {
        var settings = new AppSettings(scratch.Make());
        Assert.Equal(LanguageChoice.Auto, settings.LanguageChoice);
        Assert.Equal(TranscriptLanguage.English, settings.PreferredLanguage);
        Assert.Equal("en", settings.DefaultLanguageCode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("en")]
    [InlineData("zh-TW")]
    [InlineData("zh-CN")]
    [InlineData("de")]
    [InlineData("es")]
    public void LanguageChoiceRoundTrips(string storageValue)
    {
        var choice = LanguageChoice.FromStorageValue(storageValue) ?? throw new InvalidOperationException(storageValue);
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        // Start from a different choice so every case is a change that is written.
        settings.LanguageChoice = choice.IsAuto ? LanguageChoice.Fixed(TranscriptLanguage.German) : LanguageChoice.Auto;
        settings.LanguageChoice = choice;
        Assert.Equal(choice.StorageValue, Stored(folder, AppSettings.Key.LanguageChoice));
        Assert.Equal(choice, new AppSettings(folder).LanguageChoice);
    }

    [Fact]
    public void LanguageChoiceCasesAreTheMacCases()
    {
        Assert.Equal(["auto", "en", "zh-TW", "zh-CN", "de", "es"], LanguageChoice.All.Select(c => c.StorageValue));
    }

    [Theory]
    [InlineData(TranscriptLanguage.English)]
    [InlineData(TranscriptLanguage.ChineseTaiwan)]
    [InlineData(TranscriptLanguage.ChineseMainland)]
    [InlineData(TranscriptLanguage.German)]
    [InlineData(TranscriptLanguage.Spanish)]
    public void PreferredLanguageRoundTrips(TranscriptLanguage language)
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        settings.PreferredLanguage = language == TranscriptLanguage.English ? TranscriptLanguage.Spanish : TranscriptLanguage.English;
        settings.PreferredLanguage = language;
        Assert.Equal(language.Code(), Stored(folder, AppSettings.Key.PreferredLanguage));
        Assert.Equal(language, new AppSettings(folder).PreferredLanguage);
    }

    [Fact]
    public void UnknownStoredLanguageValuesFallBack()
    {
        var folder = scratch.Make();
        var raw = ScratchSettings.Raw(folder);
        raw.SetString(AppSettings.Key.LanguageChoice, "fr");
        raw.SetString(AppSettings.Key.PreferredLanguage, "fr");
        var settings = new AppSettings(folder);
        Assert.Equal(LanguageChoice.Auto, settings.LanguageChoice);
        Assert.Equal(TranscriptLanguage.English, settings.PreferredLanguage);
    }

    [Theory]
    [InlineData("en", TranscriptLanguage.English)]
    [InlineData("zh", TranscriptLanguage.ChineseTaiwan)]
    public void LegacyLanguageCodeMigratesToFixed(string code, TranscriptLanguage language)
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).SetString(AppSettings.Key.DefaultLanguageCode, code);
        var settings = new AppSettings(folder);
        Assert.Equal(LanguageChoice.Fixed(language), settings.LanguageChoice);
        Assert.Equal(TranscriptLanguage.English, settings.PreferredLanguage);
        Assert.Equal(language.Code(), settings.DefaultLanguageCode);
        Assert.Equal(language.Code(), Stored(folder, AppSettings.Key.LanguageChoice));
    }

    [Fact]
    public void StoredChoiceWinsOverLegacyCode()
    {
        var folder = scratch.Make();
        var raw = ScratchSettings.Raw(folder);
        raw.SetString(AppSettings.Key.DefaultLanguageCode, "zh");
        raw.SetString(AppSettings.Key.LanguageChoice, "auto");
        Assert.Equal(LanguageChoice.Auto, new AppSettings(folder).LanguageChoice);
    }

    [Fact]
    public void UnknownLegacyCodeMigratesToAuto()
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).SetString(AppSettings.Key.DefaultLanguageCode, "fr");
        Assert.Equal(LanguageChoice.Auto, new AppSettings(folder).LanguageChoice);
    }

    [Fact]
    public void SettingLanguageChoiceNeverChangesPreferredLanguage()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        foreach (var preferred in TranscriptLanguages.All)
        {
            settings.PreferredLanguage = preferred;
            foreach (var choice in LanguageChoice.All)
            {
                settings.LanguageChoice = choice;
                Assert.Equal(preferred, settings.PreferredLanguage);
                Assert.Equal(preferred, new AppSettings(folder).PreferredLanguage);
            }
            foreach (var code in new[] { "en", "zh", "zh-TW", "zh-CN", "de", "es", "fr" })
            {
                settings.DefaultLanguageCode = code;
                Assert.Equal(preferred, settings.PreferredLanguage);
            }
        }
    }

    [Fact]
    public void DeprecatedLanguageCodeReadsChoiceOrPreferred()
    {
        var settings = new AppSettings(scratch.Make());
        settings.PreferredLanguage = TranscriptLanguage.Spanish;
        settings.LanguageChoice = LanguageChoice.Auto;
        Assert.Equal("es", settings.DefaultLanguageCode);
        settings.LanguageChoice = LanguageChoice.Fixed(TranscriptLanguage.German);
        Assert.Equal("de", settings.DefaultLanguageCode);
        settings.DefaultLanguageCode = "zh";
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseTaiwan), settings.LanguageChoice);
        settings.DefaultLanguageCode = "zh-CN";
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseMainland), settings.LanguageChoice);
        settings.DefaultLanguageCode = "fr";
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseMainland), settings.LanguageChoice);
    }

    /// <summary>The Mac's <c>hotkeysDefaultToControlOptionCommand</c>, with Command as Win.</summary>
    [Fact]
    public void HotkeysDefaultToControlAltWin()
    {
        var settings = new AppSettings(scratch.Make());
        Assert.Equal(HotkeyBinding.DefaultStartStop, settings.StartStopHotkey);
        Assert.Equal(HotkeyBinding.DefaultPause, settings.PauseHotkey);
        Assert.Equal("Ctrl+Alt+Win+R", settings.StartStopHotkey.DisplayString);
        Assert.Equal("Ctrl+Alt+Win+P", settings.PauseHotkey.DisplayString);
    }

    [Fact]
    public void HotkeysRoundTrip()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        var startStop = new HotkeyBinding(0x53, HotkeyModifiers.Win | HotkeyModifiers.Shift);
        var pause = new HotkeyBinding(0x70, HotkeyModifiers.Alt);
        settings.StartStopHotkey = startStop;
        settings.PauseHotkey = pause;
        var reloaded = new AppSettings(folder);
        Assert.Equal(startStop, reloaded.StartStopHotkey);
        Assert.Equal(pause, reloaded.PauseHotkey);
        Assert.Equal("Shift+Win+S", reloaded.StartStopHotkey.DisplayString);
        Assert.Equal("Alt+F1", reloaded.PauseHotkey.DisplayString);
        var stored = ScratchSettings.Raw(folder).Get(AppSettings.Key.StartStopHotkey);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"keyCode":83,"modifiers":12}"""), stored));
    }

    [Theory]
    [InlineData("\"{\"")]
    [InlineData("{}")]
    [InlineData("""{"keyCode":-1,"modifiers":2}""")]
    [InlineData("""{"keyCode":82}""")]
    [InlineData("[82, 2]")]
    public void CorruptHotkeyFallsBackToDefault(string json)
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).Set(AppSettings.Key.StartStopHotkey, JsonNode.Parse(json));
        Assert.Equal(HotkeyBinding.DefaultStartStop, new AppSettings(folder).StartStopHotkey);
    }

    [Fact]
    public void StopStartNextHotkeyDefaultsRoundTripsAndFallsBack()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.Equal("stopStartNextHotkey", AppSettings.Key.StopStartNextHotkey);
        Assert.Equal(HotkeyBinding.DefaultStopStartNext, settings.StopStartNextHotkey);
        Assert.Equal("Ctrl+Alt+Win+N", settings.StopStartNextHotkey.DisplayString);
        Assert.True(HotkeyBinding.DefaultStopStartNext.IsValidGlobalShortcut);
        // Distinct from the other two defaults.
        Assert.Equal(3, new HashSet<HotkeyBinding>
        {
            HotkeyBinding.DefaultStartStop, HotkeyBinding.DefaultPause, HotkeyBinding.DefaultStopStartNext,
        }.Count);

        var custom = new HotkeyBinding(0x7A, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        settings.StopStartNextHotkey = custom;
        Assert.Equal(custom, new AppSettings(folder).StopStartNextHotkey);

        ScratchSettings.Raw(folder).Set(AppSettings.Key.StopStartNextHotkey, JsonNode.Parse("[123]"));
        Assert.Equal(HotkeyBinding.DefaultStopStartNext, new AppSettings(folder).StopStartNextHotkey);
    }

    [Fact]
    public void FinalPassTimingDefaultsToWhenIdleOnWindows()
    {
        // The Mac's default is immediate; Windows differs (PLAN.md 18.10).
        Assert.Equal("finalPassTiming", AppSettings.Key.FinalPassTiming);
        Assert.Equal(FinalPassTiming.WhenIdle, new AppSettings(scratch.Make()).FinalPassTiming);
    }

    [Theory]
    [InlineData(FinalPassTiming.Immediate)]
    [InlineData(FinalPassTiming.WhenIdle)]
    [InlineData(FinalPassTiming.Manual)]
    public void FinalPassTimingRoundTripsAsStoredValue(FinalPassTiming timing)
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        // Setting the default is a no-op, so go through the other value first.
        settings.FinalPassTiming = timing == FinalPassTiming.WhenIdle ? FinalPassTiming.Immediate : FinalPassTiming.WhenIdle;
        settings.FinalPassTiming = timing;
        Assert.Equal(timing.StorageValue(), Stored(folder, AppSettings.Key.FinalPassTiming));
        Assert.Equal(timing, new AppSettings(folder).FinalPassTiming);
    }

    [Fact]
    public void ManualFinalPassTimingIsStoredAndReadAsTheSharedValue()
    {
        // PLAN.md 4.11: the third value is the string "manual", and the default stays whenIdle.
        var folder = scratch.Make();
        new AppSettings(folder).FinalPassTiming = FinalPassTiming.Manual;
        Assert.Equal("manual", Stored(folder, AppSettings.Key.FinalPassTiming));
        Assert.Equal(FinalPassTiming.Manual, new AppSettings(folder).FinalPassTiming);
        var raw = scratch.Make();
        ScratchSettings.Raw(raw).SetString(AppSettings.Key.FinalPassTiming, "manual");
        Assert.Equal(FinalPassTiming.Manual, new AppSettings(raw).FinalPassTiming);
        Assert.Equal(FinalPassTiming.WhenIdle, FinalPassTimings.Default);
    }

    [Fact]
    public void UnknownFinalPassTimingFallsBackToDefault()
    {
        var folder = scratch.Make();
        ScratchSettings.Raw(folder).SetString(AppSettings.Key.FinalPassTiming, "later");
        Assert.Equal(FinalPassTiming.WhenIdle, new AppSettings(folder).FinalPassTiming);
        ScratchSettings.Raw(folder).Set(AppSettings.Key.FinalPassTiming, JsonValue.Create(3));
        Assert.Equal(FinalPassTiming.WhenIdle, new AppSettings(folder).FinalPassTiming);
    }

    /// <summary>The Mac's <c>globalShortcutNeedsControlOptionOrCommand</c>.</summary>
    [Fact]
    public void GlobalShortcutNeedsControlAltOrWin()
    {
        Assert.True(HotkeyBinding.DefaultStartStop.IsValidGlobalShortcut);
        Assert.False(new HotkeyBinding(0x52, HotkeyModifiers.Shift).IsValidGlobalShortcut);
        Assert.False(new HotkeyBinding(0x52, HotkeyModifiers.None).IsValidGlobalShortcut);
        Assert.True(new HotkeyBinding(0x52, HotkeyModifiers.Control).IsValidGlobalShortcut);
        Assert.True(new HotkeyBinding(0x52, HotkeyModifiers.Alt).IsValidGlobalShortcut);
        Assert.True(new HotkeyBinding(0x52, HotkeyModifiers.Win).IsValidGlobalShortcut);
    }

    /// <summary>The Mac's <c>windowModeFlags</c>: menu bar item is the tray icon, Dock icon the taskbar button.</summary>
    [Fact]
    public void WindowModeFlags()
    {
        Assert.True(WindowMode.MenuBarAndDock.ShowsTrayIcon() && WindowMode.MenuBarAndDock.ShowsTaskbarButton());
        Assert.True(WindowMode.MenuBarOnly.ShowsTrayIcon() && !WindowMode.MenuBarOnly.ShowsTaskbarButton());
        Assert.True(!WindowMode.DockOnly.ShowsTrayIcon() && WindowMode.DockOnly.ShowsTaskbarButton());
    }

    // Windows only.

    [Fact]
    public void ReadingEmptySettingsCreatesNothing()
    {
        var folder = scratch.Make();
        _ = new AppSettings(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void CaptureSystemAudioAndActiveModelRoundTrip()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        Assert.True(settings.CaptureSystemAudio);
        Assert.Null(settings.ActiveModelRepo);
        settings.CaptureSystemAudio = false;
        settings.ActiveModelRepo = "ggml-large-v3-turbo-q5_0";
        var reloaded = new AppSettings(folder);
        Assert.False(reloaded.CaptureSystemAudio);
        Assert.Equal("ggml-large-v3-turbo-q5_0", reloaded.ActiveModelRepo);
        settings.ActiveModelRepo = null;
        Assert.Null(new AppSettings(folder).ActiveModelRepo);
    }

    [Fact]
    public void WrongTypedValuesFallBackToDefaults()
    {
        var folder = scratch.Make();
        var raw = ScratchSettings.Raw(folder);
        raw.SetString(AppSettings.Key.KeepRecording, "false");
        raw.Set(AppSettings.Key.WindowMode, JsonValue.Create(1));
        raw.SetBool(AppSettings.Key.LanguageChoice, true);
        raw.SetString(AppSettings.Key.LastUpdateCheck, "yesterday");
        var settings = new AppSettings(folder);
        Assert.True(settings.KeepRecording);
        Assert.Equal(WindowMode.MenuBarAndDock, settings.WindowMode);
        Assert.Equal(LanguageChoice.Auto, settings.LanguageChoice);
        Assert.Null(settings.LastUpdateCheck);
    }

    [Fact]
    public void ChangesRaisePropertyChangedOnce()
    {
        var settings = new AppSettings(scratch.Make());
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        settings.KeepRecording = true; // already the value
        settings.KeepRecording = false;
        settings.MenuBarShowsStatus = false;
        settings.LanguageChoice = LanguageChoice.Fixed(TranscriptLanguage.German);
        settings.PreferredLanguage = TranscriptLanguage.Spanish;
        settings.StartStopHotkey = HotkeyBinding.DefaultPause;
        Assert.Equal(
            [
                nameof(AppSettings.KeepRecording),
                nameof(AppSettings.MenuBarShowsStatus),
                nameof(AppSettings.LanguageChoice),
                nameof(AppSettings.DefaultLanguageCode),
                nameof(AppSettings.PreferredLanguage),
                nameof(AppSettings.DefaultLanguageCode),
                nameof(AppSettings.StartStopHotkey),
            ],
            changed);
    }

    [Fact]
    public void FailedWriteKeepsTheValueAndReportsIt()
    {
        var folder = scratch.Make();
        var settings = new AppSettings(folder);
        // A file where the settings folder should be makes every write fail.
        Directory.CreateDirectory(Path.GetDirectoryName(folder) ?? throw new InvalidOperationException());
        File.WriteAllText(folder, "not a folder");
        var failures = new List<Exception>();
        settings.SaveFailed += (_, e) => failures.Add(e.Error);
        settings.KeepRecording = false;
        Assert.False(settings.KeepRecording);
        Assert.Single(failures);
        Assert.IsAssignableFrom<IOException>(failures[0]);
        File.Delete(folder);
    }

    [Fact]
    public void KeysAreTheMacKeys()
    {
        var keys = typeof(AppSettings.Key).GetFields()
            .Select(field => (string?)field.GetValue(null))
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "activeModelRepo", "autoRecordAsksLanguage", "autoRecordTeamsMeetings", "automaticUpdateChecks", "captureSystemAudio", "chineseScript",
                "defaultLanguageCode", "finalPassTiming", "interfaceLanguage", "keepRecording", "languageChoice",
                "lastUpdateCheck", "menuBarShowsStatus", "microphoneGrantedCodeHash",
                "outputFolder", "pauseHotkey", "preferredLanguage", "screenAudioGrantedCodeHash",
                "screenAudioResetCodeHash", "startStopHotkey", "stopStartNextHotkey", "windowMode",
            ],
            keys);
    }
}

/// <summary>
/// Port of <c>OutputLocationTests</c> in AppSettingsTests.swift. The
/// bookmark cases become chosen-path cases; every folder is under a scratch
/// root in the temp folder, and the real Documents\Hearsay is never created.
/// </summary>
public sealed class OutputLocationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"HearsayOutputLocationTests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string TempDir() => Path.Combine(root, Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultFolderEndsInDocumentsHearsay()
    {
        var folder = OutputLocation.DefaultFolder();
        Assert.Equal("Hearsay", Path.GetFileName(folder));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetDirectoryName(folder));
    }

    [Fact]
    public void NoChosenFolderCreatesFallback()
    {
        var fallback = Path.Combine(TempDir(), "Hearsay");
        var resolved = OutputLocation.Resolve(null, fallback);
        Assert.Equal(fallback, resolved.Path);
        Assert.True(resolved.IsFallback);
        Assert.True(Directory.Exists(fallback));
    }

    /// <summary>The Mac's <c>garbageBookmarkFallsBack</c>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\folder")]
    [InlineData(@"C:relative")]
    [InlineData("C:\\bad\0path")]
    public void GarbagePathFallsBack(string chosen)
    {
        var fallback = TempDir();
        var resolved = OutputLocation.Resolve(chosen, fallback);
        Assert.Equal(fallback, resolved.Path);
        Assert.True(resolved.IsFallback);
    }

    [Fact]
    public void ChosenFileIsNotAFolderAndFallsBack()
    {
        var chosen = TempDir();
        Directory.CreateDirectory(root);
        File.WriteAllText(chosen, "x");
        var fallback = TempDir();
        Assert.Equal(fallback, OutputLocation.Resolve(chosen, fallback).Path);
    }

    /// <summary>The Mac's <c>bookmarkResolvesToChosenFolder</c>.</summary>
    [Fact]
    public void ChosenFolderResolves()
    {
        var chosen = TempDir();
        var fallback = TempDir();
        Directory.CreateDirectory(chosen);
        var stored = OutputLocation.MakeStoredPath(chosen + Path.DirectorySeparatorChar);
        Assert.Equal(chosen, stored);
        var resolved = OutputLocation.Resolve(stored, fallback);
        Assert.Equal(chosen, resolved.Path);
        Assert.False(resolved.IsFallback);
        Assert.False(Directory.Exists(fallback));
    }

    /// <summary>The Mac's <c>deletedFolderBookmarkFallsBack</c>: the chosen folder is not recreated.</summary>
    [Fact]
    public void DeletedChosenFolderFallsBack()
    {
        var chosen = TempDir();
        var fallback = TempDir();
        Directory.CreateDirectory(chosen);
        var stored = OutputLocation.MakeStoredPath(chosen);
        Directory.Delete(chosen);
        var resolved = OutputLocation.Resolve(stored, fallback);
        Assert.Equal(fallback, resolved.Path);
        Assert.False(Directory.Exists(chosen));
    }

    [Fact]
    public void MakeStoredPathNormalizes()
    {
        Assert.Equal(@"C:\Users\x\Meetings", OutputLocation.MakeStoredPath(@"C:\Users\x\Notes\..\Meetings\"));
        Assert.Equal(@"D:\", OutputLocation.MakeStoredPath(@"D:\"));
        Assert.Throws<ArgumentException>(() => OutputLocation.MakeStoredPath(@"relative\folder"));
        Assert.Throws<ArgumentException>(() => OutputLocation.MakeStoredPath(" "));
    }
}

/// <summary>
/// Port of <c>ChineseVariantMigrationTests</c> in AppSettingsTests.swift. A
/// stored "zh" (before ZH-TW and ZH-CN) migrates by the legacy "Chinese
/// output" setting: traditional or missing gives ZH-TW, simplified ZH-CN.
/// Auto and the other languages are unchanged.
/// </summary>
public sealed class ChineseVariantMigrationTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    private string Folder(string? choice, string? preferred, string? script)
    {
        var folder = scratch.Make();
        var raw = ScratchSettings.Raw(folder);
        if (choice is not null) raw.SetString(AppSettings.Key.LanguageChoice, choice);
        if (preferred is not null) raw.SetString(AppSettings.Key.PreferredLanguage, preferred);
        if (script is not null) raw.SetString(AppSettings.Key.ChineseScript, script);
        return folder;
    }

    [Theory]
    [InlineData("traditional", TranscriptLanguage.ChineseTaiwan)]
    [InlineData(null, TranscriptLanguage.ChineseTaiwan)]
    [InlineData("asIs", TranscriptLanguage.ChineseTaiwan)]
    [InlineData("simplified", TranscriptLanguage.ChineseMainland)]
    public void FixedZhChoiceMigrates(string? script, TranscriptLanguage expected)
    {
        var folder = Folder("zh", null, script);
        var settings = new AppSettings(folder);
        Assert.Equal(LanguageChoice.Fixed(expected), settings.LanguageChoice);
        Assert.Equal(TranscriptLanguage.English, settings.PreferredLanguage);
        Assert.Equal(expected.Code(), ScratchSettings.Raw(folder).GetString(AppSettings.Key.LanguageChoice));
        Assert.Equal(LanguageChoice.Fixed(expected), new AppSettings(folder).LanguageChoice);
    }

    [Theory]
    [InlineData("traditional", TranscriptLanguage.ChineseTaiwan)]
    [InlineData(null, TranscriptLanguage.ChineseTaiwan)]
    [InlineData("simplified", TranscriptLanguage.ChineseMainland)]
    public void PreferredZhMigrates(string? script, TranscriptLanguage expected)
    {
        var folder = Folder("auto", "zh", script);
        var settings = new AppSettings(folder);
        Assert.Equal(expected, settings.PreferredLanguage);
        Assert.Equal(LanguageChoice.Auto, settings.LanguageChoice);
        Assert.Equal(expected.Code(), ScratchSettings.Raw(folder).GetString(AppSettings.Key.PreferredLanguage));
        Assert.Equal(expected, new AppSettings(folder).PreferredLanguage);
    }

    [Fact]
    public void LegacyDefaultLanguageCodeZhMigratesByScript()
    {
        var folder = scratch.Make();
        var raw = ScratchSettings.Raw(folder);
        raw.SetString(AppSettings.Key.DefaultLanguageCode, "zh");
        raw.SetString(AppSettings.Key.ChineseScript, "simplified");
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseMainland), new AppSettings(folder).LanguageChoice);
        Assert.Equal("zh-CN", ScratchSettings.Raw(folder).GetString(AppSettings.Key.LanguageChoice));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-TW")]
    [InlineData("zh-CN")]
    public void OtherChoicesUnchanged(string stored)
    {
        foreach (var script in new[] { "traditional", "simplified", null })
        {
            var folder = Folder(stored, null, script);
            Assert.Equal(stored, new AppSettings(folder).LanguageChoice.StorageValue);
            Assert.Equal(stored, ScratchSettings.Raw(folder).GetString(AppSettings.Key.LanguageChoice));
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-TW")]
    [InlineData("zh-CN")]
    public void OtherPreferredUnchanged(string stored)
    {
        foreach (var script in new[] { "traditional", "simplified", null })
        {
            var folder = Folder(null, stored, script);
            Assert.Equal(stored, new AppSettings(folder).PreferredLanguage.Code());
            Assert.Equal(stored, ScratchSettings.Raw(folder).GetString(AppSettings.Key.PreferredLanguage));
        }
    }

    [Fact]
    public void BothZhMigrateTogether()
    {
        var folder = Folder("zh", "zh", "simplified");
        var settings = new AppSettings(folder);
        Assert.Equal(LanguageChoice.Fixed(TranscriptLanguage.ChineseMainland), settings.LanguageChoice);
        Assert.Equal(TranscriptLanguage.ChineseMainland, settings.PreferredLanguage);
    }
}
