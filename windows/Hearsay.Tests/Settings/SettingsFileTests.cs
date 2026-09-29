using System.Text;
using System.Text.Json.Nodes;
using Hearsay.Core.Settings;

namespace Hearsay.Tests.Settings;

/// <summary>
/// Windows only: settings.json, the stand-in for the Mac's UserDefaults
/// domain (SettingsFile.cs). Every folder is a scratch folder in the temp
/// folder.
/// </summary>
public sealed class SettingsFileTests : IDisposable
{
    private readonly ScratchSettings scratch = new();

    public void Dispose() => scratch.Dispose();

    [Fact]
    public void FirstWriteCreatesTheFolderAndTheFile()
    {
        var folder = scratch.Make();
        var file = new SettingsFile(folder);
        Assert.False(Directory.Exists(folder));
        file.SetBool("keepRecording", false);
        Assert.True(File.Exists(Path.Combine(folder, "settings.json")));
        Assert.Equal(Path.Combine(folder, "settings.json"), file.FilePath);
    }

    [Fact]
    public void WritesLeaveNoTemporaryFiles()
    {
        var folder = scratch.Make();
        var file = new SettingsFile(folder);
        for (var i = 0; i < 20; i++) file.SetString("windowMode", $"mode{i}");
        Assert.Equal(["settings.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void FileIsUtf8JsonWithLfAndNoBom()
    {
        var folder = scratch.Make();
        var file = new SettingsFile(folder);
        file.SetString("outputFolder", @"C:\Users\x\文件\Hearsay");
        file.SetBool("keepRecording", true);
        var bytes = File.ReadAllBytes(file.FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Contains("文件", text, StringComparison.Ordinal);
        Assert.Equal(@"C:\Users\x\文件\Hearsay", JsonNode.Parse(text)?["outputFolder"]?.GetValue<string>());
    }

    [Fact]
    public void UnknownKeysAreKept()
    {
        var folder = scratch.Make();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"),
            """{"aiProviders":{"selected":"copilot"},"futureSetting":[1,2]}""");
        var settings = new AppSettings(folder);
        settings.KeepRecording = false;
        var raw = ScratchSettings.Raw(folder);
        Assert.Equal("copilot", raw.Get("aiProviders")?["selected"]?.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,2]"), raw.Get("futureSetting")));
        Assert.False(raw.GetBool("keepRecording"));
    }

    [Fact]
    public void SharedFileKeepsBothStoresKeys()
    {
        var folder = scratch.Make();
        var file = new SettingsFile(folder);
        var settings = new AppSettings(file);
        file.SetString("otherStore", "x");
        settings.WindowMode = WindowMode.DockOnly;
        var raw = ScratchSettings.Raw(folder);
        Assert.Equal("x", raw.GetString("otherStore"));
        Assert.Equal("dockOnly", raw.GetString("windowMode"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void CorruptFileIsMovedAsideAndSettingsStartEmpty(string contents)
    {
        var folder = scratch.Make();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, contents);
        var file = new SettingsFile(folder);
        Assert.Equal(Path.Combine(folder, "settings.corrupt.json"), file.CorruptFileBackup);
        Assert.Equal(contents, File.ReadAllText(Path.Combine(folder, "settings.corrupt.json")));
        Assert.False(File.Exists(path));
        var settings = new AppSettings(file);
        Assert.True(settings.KeepRecording);
        settings.KeepRecording = false;
        Assert.False(new AppSettings(folder).KeepRecording);
        Assert.Equal(contents, File.ReadAllText(Path.Combine(folder, "settings.corrupt.json")));
    }

    [Fact]
    public void HandEditedFileWithCommentsAndTrailingCommasLoads()
    {
        var folder = scratch.Make();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{\n  // edited\n  \"keepRecording\": false,\n}\n");
        var file = new SettingsFile(folder);
        Assert.Null(file.CorruptFileBackup);
        Assert.False(new AppSettings(file).KeepRecording);
    }

    [Fact]
    public void GetReturnsACopy()
    {
        var file = new SettingsFile(scratch.Make());
        file.Set("startStopHotkey", new JsonObject { ["keyCode"] = 82 });
        var copy = file.Get("startStopHotkey") as JsonObject;
        Assert.NotNull(copy);
        copy["keyCode"] = 1;
        Assert.Equal(82, file.Get("startStopHotkey")?["keyCode"]?.GetValue<int>());
    }

    [Fact]
    public void RemovingAnAbsentKeyWritesNothing()
    {
        var folder = scratch.Make();
        new SettingsFile(folder).Remove("outputFolder");
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void ConcurrentWritesAllLand()
    {
        var folder = scratch.Make();
        var file = new SettingsFile(folder);
        Parallel.For(0, 50, i => file.SetString($"key{i}", $"value{i}"));
        var raw = ScratchSettings.Raw(folder);
        for (var i = 0; i < 50; i++) Assert.Equal($"value{i}", raw.GetString($"key{i}"));
        Assert.Single(Directory.GetFiles(folder));
    }
}
