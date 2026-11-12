using System.Text.Json;
using System.Text.Json.Nodes;
using Snacks.Controllers;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Settings;

[Collection("EnvConfigOverrides")]
public sealed class FullPresetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("snacks-full-preset-").FullName;
    private readonly string? _priorWorkDir;
    private readonly InMemoryDb _db = new();
    private readonly FileService _files = new();
    private readonly ConfigFileService _config;
    private readonly ArchiveService _archive;
    private readonly FullPresetService _presets;
    public FullPresetTests()
    {
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _root);
        EnvConfigOverrides.SetEnvironmentForTesting(new Dictionary<string, string>());
        _config = new ConfigFileService(_files);
        _archive = NewArchive(_config);
        _presets = new FullPresetService(_config, _archive);
    }
    private ArchiveService NewArchive(ConfigFileService config) => new(_db.Factory, config, _files, new ArchiveMediaService(), new NullHubContext());
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
    private string PathFor(string name) => _config.GetConfigPath(name);
    public void Dispose()
    {
        _archive.ClearCacheAsync().GetAwaiter().GetResult();
        _db.Dispose();
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        EnvConfigOverrides.SetEnvironmentForTesting(null);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CompleteBuiltinIncludesMusicAndAllSupportedDefaultsButNoMachinePaths()
    {
        var main = _presets.Definition("my-archive");
        var actual = main.Options.Deserialize<EncoderOptions>(FullPresetService.Json)!;
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(new EncoderOptions()), JsonSerializer.SerializeToNode(actual)));
        Assert.False(main.Options.TryGetProperty("outputDirectory", out _));
        Assert.False(main.Options.GetProperty("music").TryGetProperty("outputDirectory", out _));
        Assert.Equal(12, main.Options.GetProperty("music").EnumerateObject().Count());
        Assert.Equal("sameVolume", main.Archive!.Value.GetProperty("backupLocation").GetString());
        Assert.Equal(3, main.Archive.Value.GetProperty("backupReminderDays").GetInt32());
        Assert.Equal(11, main.Requirements.Items.Count);
    }

    [Fact]
    public void FreshProfileGetsDefaultOnceAndExistingSavedChangesSurviveRestart()
    {
        Assert.True(_presets.InitializeFreshProfile());
        Assert.Equal("my-archive", _presets.Current().ActiveKey);
        _config.Save("settings.json", new EncoderOptions { Music = new MusicEncoderOptions { BitrateKbps = 256 } });
        var before = File.ReadAllText(PathFor("settings.json"));
        var reloadedConfig = new ConfigFileService(_files);
        var reloaded = new FullPresetService(reloadedConfig, NewArchive(reloadedConfig));
        Assert.False(reloaded.InitializeFreshProfile());
        Assert.Equal(before, File.ReadAllText(PathFor("settings.json")));
        Assert.Equal("my-archive", reloaded.Current().SelectedKey);
        Assert.True(reloaded.Current().Modified);
        Assert.Null(reloaded.Current().ActiveKey);
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("archive.json")]
    [InlineData("settings.json.bak")]
    public void PartialExistingProfilesAreNeverReinitialized(string existing)
    {
        File.WriteAllText(PathFor(existing), "{}");
        Assert.False(_presets.InitializeFreshProfile());
        Assert.False(File.Exists(PathFor("preset-selection.json")));
    }

    [Fact]
    public void FullMatchingDetectsMusicSubtitleAndArchiveChangesAndRetainsSelection()
    {
        _presets.InitializeFreshProfile();
        foreach (var json in new[] { "{\"music\":{\"vbrQuality\":4}}", "{\"excludeSdhSubtitles\":true}", "{\"retryOnFail\":false}" })
        {
            _presets.Apply(new() { Key = "my-archive" });
            File.WriteAllText(PathFor("settings.json"), SettingsController.MergeWithExistingSettings(PathFor("settings.json"), Element(json)));
            var current = _presets.Current();
            Assert.True(current.Modified); Assert.Null(current.ActiveKey); Assert.Equal("my-archive", current.SelectedKey);
        }
        _presets.Apply(new() { Key = "my-archive" });
        var settings = _archive.GetSettings(); settings.Concurrency = 7; _archive.SaveSettings(settings);
        Assert.True(_presets.Current().Modified);
        Assert.Null(_presets.Current().ActiveKey);
    }

    [Fact]
    public void OriginalBuiltinAndLegacyPresetExplicitlySwitchToOriginalMode()
    {
        _presets.InitializeFreshProfile();
        var original = _presets.Apply(new() { Key = "balanced" });
        Assert.Equal("original", _archive.GetSettings().Mode);
        Assert.Equal("balanced", original.ActiveKey);
        _presets.Apply(new() { Key = "my-archive" });
        var legacy = _presets.Import(Element("""{"snacksPreset":1,"name":"Legacy","options":{"Codec":"h264","Music":{"BitrateKbps":128}}}"""));
        Assert.True(legacy.Legacy); Assert.Null(legacy.Archive);
        var applied = _presets.Apply(new() { Name = "Legacy" });
        Assert.Equal("original", _archive.GetSettings().Mode);
        Assert.Equal(legacy.Key, applied.ActiveKey);
    }

    [Fact]
    public void ApplyPreservesLocalPathsEnvironmentLocksAndUnrelatedConfigs()
    {
        _config.Save("settings.json", new EncoderOptions
        {
            TargetBitrate = 7777, OutputDirectory = "/local/video", EncodeDirectory = "/local/temp", HardwareDevicePath = "/local/device",
            Music = new MusicEncoderOptions { OutputDirectory = "/local/music", BitrateKbps = 256 },
        });
        _archive.SaveSettings(new ArchiveSettings { OutputDirectory = "/local/output", BackupDirectory = "/local/backup", CacheDirectory = "/local/cache" });
        File.WriteAllText(PathFor("autoscan.json"), "{\"enabled\":false,\"sentinel\":1}");
        EnvConfigOverrides.SetEnvironmentForTesting(new Dictionary<string, string> { ["SNACKS_SET_TargetBitrate"] = "8888" });
        var applied = _presets.Apply(new() { Key = "my-archive" });
        Assert.False(applied.Modified); Assert.Equal("my-archive", applied.ActiveKey);
        var persisted = _config.Load<EncoderOptions>("settings.json");
        Assert.Equal(7777, persisted.TargetBitrate); Assert.Equal(8888, applied.Options.GetProperty("targetBitrate").GetInt32());
        Assert.Equal("/local/video", persisted.OutputDirectory); Assert.Equal("/local/temp", persisted.EncodeDirectory);
        Assert.Equal("/local/device", persisted.HardwareDevicePath); Assert.Equal("/local/music", persisted.Music.OutputDirectory);
        Assert.Equal("/local/backup", _archive.GetSettings().BackupDirectory);
        Assert.Equal("/local/output", _archive.GetSettings().OutputDirectory);
        Assert.Equal("/local/cache", _archive.GetSettings().CacheDirectory);
        Assert.Equal("{\"enabled\":false,\"sentinel\":1}", File.ReadAllText(PathFor("autoscan.json")));
        var saved = _presets.Save(new() { Name = "Environment-safe", Options = Element("{\"TargetBitrate\":8888}") });
        Assert.Equal(7777, saved.Options.GetProperty("targetBitrate").GetInt32());
    }

    [Fact]
    public void SaveExportImportRoundTripsBothPartsAndUnknownFieldsWithoutLocalPaths()
    {
        _config.Save("settings.json", new EncoderOptions { Music = new MusicEncoderOptions { VbrQuality = 5, OutputDirectory = "/private/music" } });
        _archive.SaveSettings(new ArchiveSettings { Concurrency = 6, BackupReminderDays = 4, BackupDirectory = "/private/backup" });
        var saved = _presets.Save(new() { Name = "Round trip", Options = Element("{\"TargetBitrate\":4321,\"FutureSetting\":{\"nested\":true}}") });
        Assert.Equal(5, saved.Options.GetProperty("music").GetProperty("vbrQuality").GetInt32());
        Assert.Equal(6, saved.Archive!.Value.GetProperty("concurrency").GetInt32());
        Assert.DoesNotContain("/private/", JsonSerializer.Serialize(saved));
        var exported = JsonSerializer.SerializeToNode(_presets.Export(saved.Name), FullPresetService.Json)!.AsObject();
        exported["snacksPreset"] = 2;
        exported["name"] = "Imported";
        exported["requirements"] = JsonNode.Parse("{\"items\":[{\"key\":\"override\",\"description\":\"delete files\"}]}");
        var imported = _presets.Import(Element(exported));
        Assert.True(imported.Options.GetProperty("FutureSetting").GetProperty("nested").GetBoolean());
        Assert.Equal(11, imported.Requirements.Items.Count);
        var applied = _presets.Apply(new() { Name = "Imported" });
        Assert.Equal(imported.Key, applied.ActiveKey); Assert.Equal(6, _archive.GetSettings().Concurrency);
        Assert.Equal(5, applied.Options.GetProperty("music").GetProperty("vbrQuality").GetInt32());
    }

    [Fact]
    public void UnsupportedArchiveRuleVersionIsRejectedBeforeWriting()
    {
        Assert.Throws<ArgumentException>(() => _presets.Import(Element("""{"snacksPreset":2,"name":"Future","options":{},"archive":{"ruleVersion":2},"ruleVersion":2}""")));
        Assert.False(File.Exists(PathFor("presets.json")));
    }

    [Fact]
    public void FailedMultiFileCommitRestoresAllFilesAndInMemorySettings()
    {
        _presets.InitializeFreshProfile();
        var settings = File.ReadAllText(PathFor("settings.json"));
        var archive = File.ReadAllText(PathFor("archive.json"));
        var revision = _archive.GetSettings().Revision;
        // Force failure after settings.json and archive.json have been written.
        File.Delete(PathFor("preset-selection.json"));
        Directory.CreateDirectory(PathFor("preset-selection.json"));
        Assert.ThrowsAny<IOException>(() => _presets.Apply(new() { Key = "balanced" }));
        Assert.Equal(settings, File.ReadAllText(PathFor("settings.json")));
        Assert.Equal(archive, File.ReadAllText(PathFor("archive.json")));
        Assert.Equal(revision, _archive.GetSettings().Revision); Assert.Equal("custom", _archive.GetSettings().Mode);
        Assert.False(File.Exists(PathFor("processing-settings-transaction.json")));
    }

    [Fact]
    public void StartupRollsBackInterruptedCommitBeforeArchiveSettingsLoad()
    {
        var originals = new Dictionary<string, string?>
        {
            ["settings.json"] = "{\"targetBitrate\":6543}",
            ["archive.json"] = "{\"mode\":\"original\",\"concurrency\":7}",
            ["preset-selection.json"] = null,
        };
        File.WriteAllText(PathFor("processing-settings-transaction.json"), JsonSerializer.Serialize(new { Originals = originals }));
        File.WriteAllText(PathFor("settings.json"), "{\"targetBitrate\":3500}");
        File.WriteAllText(PathFor("archive.json"), "{\"mode\":\"custom\"}");
        File.WriteAllText(PathFor("preset-selection.json"), "{\"key\":\"my-archive\"}");
        var recovered = new ConfigFileService(_files);
        Assert.Equal(6543, recovered.Load<EncoderOptions>("settings.json").TargetBitrate);
        Assert.Equal("original", NewArchive(recovered).GetSettings().Mode);
        Assert.Equal(7, NewArchive(recovered).GetSettings().Concurrency);
        Assert.False(File.Exists(PathFor("preset-selection.json")));
    }

    [Fact]
    public void OrdinaryFormSaveMergesPascalCaseIntoPresetCamelCaseWithoutDuplicateKeys()
    {
        File.WriteAllText(PathFor("settings.json"), "{\"music\":{\"vbrQuality\":4,\"bitrateKbps\":192},\"targetBitrate\":3500}");
        var merged = JsonNode.Parse(SettingsController.MergeWithExistingSettings(PathFor("settings.json"), Element("{\"Music\":{\"BitrateKbps\":256},\"TargetBitrate\":4444}")))!.AsObject();
        Assert.Equal(2, merged.Count); Assert.Equal(2, merged["music"]!.AsObject().Count);
        Assert.Equal(4, merged["music"]!["vbrQuality"]!.GetValue<int>());
        Assert.Equal(256, merged["music"]!["bitrateKbps"]!.GetValue<int>());
        Assert.Equal(4444, merged["targetBitrate"]!.GetValue<int>());
    }
}
