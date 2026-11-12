using System.Text.Json;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Archive;

[Collection("EnvConfigOverrides")]
public sealed class ArchivePreviewCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("snacks-preview-cache-").FullName;
    private readonly string? _priorWorkDir;
    private readonly InMemoryDb _db = new();
    private readonly FileService _files = new();
    private readonly ConfigFileService _config;
    private readonly ArchiveService _archive;

    public ArchivePreviewCacheTests()
    {
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _root);
        EnvConfigOverrides.SetEnvironmentForTesting(new Dictionary<string, string>());
        _config = new ConfigFileService(_files);
        _archive = new ArchiveService(_db.Factory, _config, _files, new ArchiveMediaService(), new NullHubContext());
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        EnvConfigOverrides.SetEnvironmentForTesting(null);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SkipAndBlockedPreviewsDoNotConsumePendingSlotsButReadyLimitRemains32()
    {
        var skippedPath = Path.Combine(_root, "excluded.mp4");
        var blockedPath = Path.Combine(_root + "-unavailable", "missing.mp4");
        await File.WriteAllTextAsync(skippedPath, "placeholder; excluded before media probing");

        for (var i = 0; i < 40; i++)
        {
            var result = await _archive.PreviewAsync(new ArchivePreviewRequest { FilePaths = [skippedPath, blockedPath] }, CancellationToken.None);
            var json = JsonSerializer.SerializeToElement(result, ArchiveService.Json);
            Assert.Null(json.GetProperty("previewId").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("ready").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("blocked").GetInt32());
            Assert.Equal(2, json.GetProperty("rows").GetArrayLength());
        }

        var rules = _archive.GetSettings();
        var readyRow = new ArchivePreviewRow { Action = "archive" };
        for (var i = 0; i < 32; i++)
        {
            var cached = _archive.CachePreviewIfReady(rules, _root, [readyRow]);
            Assert.NotNull(cached.Id);
            Assert.InRange(cached.ExpiresAt!.Value - DateTime.UtcNow, TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(59)), TimeSpan.FromHours(2));
        }

        var error = Assert.Throws<ArchiveException>(() => _archive.CachePreviewIfReady(rules, _root, [readyRow]));
        Assert.Equal("preview_limit", error.Code);
    }
}
