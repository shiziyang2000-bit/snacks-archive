using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Snacks.Data;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Archive;

[Collection("EnvConfigOverrides")]
public sealed partial class ArchiveBoundedDiscoveryTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("snacks-bounded-").FullName;
    private readonly Dictionary<string, string?> _environment = new();
    private IDbContextFactory<SnacksDbContext> _db = null!;
    private ArchiveService _archive = null!;
    private FileService _files = null!;
    private ConfigFileService _config = null!;
    private string Source => Path.Combine(_root, "source");
    private string ProbeLog => Path.Combine(_root, "probe.log");

    public async Task InitializeAsync()
    {
        foreach (var key in new[] { "SNACKS_WORK_DIR", "FFPROBE_PATH", "FFMPEG_PATH" }) _environment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", Path.Combine(_root, "work"));
        EnvConfigOverrides.SetEnvironmentForTesting(new Dictionary<string, string>());
        Directory.CreateDirectory(Source);
        var probe = Path.Combine(_root, "probe.sh");
        await File.WriteAllTextAsync(probe, "#!/bin/sh\nfor last do :; done\nprintf '%s\\n' \"$last\" >> '" + ProbeLog + "'\ncase \"$last\" in *broken*) echo 'synthetic invalid header' >&2; exit 1;; esac\npixel=yuv420p\ncase \"$last\" in *alpha*) pixel=yuva420p;; esac\necho '{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"pix_fmt\":\"'\"$pixel\"'\",\"width\":32,\"height\":32,\"duration\":1,\"avg_frame_rate\":\"25/1\",\"bit_rate\":\"40000000\"}],\"format\":{\"duration\":1}}'\n");
        var ffmpeg = Path.Combine(_root, "ffmpeg.sh");
        await File.WriteAllTextAsync(ffmpeg, "#!/bin/sh\necho libx265\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("FFPROBE_PATH", probe);
        Environment.SetEnvironmentVariable("FFMPEG_PATH", ffmpeg);
        _db = new Factory(Path.Combine(_root, "test.sqlite"));
        await using (var db = await _db.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        _files = new FileService(); _config = new ConfigFileService(_files);
        _config.Save("archive.json", new ArchiveSettings { Concurrency = 3 });
        _archive = NewService();
    }
    private ArchiveService NewService() => new(_db, _config, _files, new ArchiveMediaService(), new NullHubContext());
    public async Task DisposeAsync()
    {
        await _archive.ShutdownAsync();
        foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        EnvConfigOverrides.SetEnvironmentForTesting(null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
    private sealed class Factory(string path) : IDbContextFactory<SnacksDbContext>
    {
        public SnacksDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SnacksDbContext>().UseSqlite("Data Source=" + path).Options);
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, ArchiveService.Json);
    private async Task<JsonElement> Preview(bool recursive = true) => Json(await _archive.PreviewAsync(new() { DirectoryPath = Source, Recursive = recursive }, CancellationToken.None));
    private async Task<string> Start(JsonElement preview, string? requestId = null) => Json(await _archive.StartAsync(new() { PreviewId = preview.GetProperty("previewId").GetString()!, RequestId = requestId ?? Guid.NewGuid().ToString("N") })).GetProperty("jobId").GetString()!;
    private async Task CreateFiles(int count, string folder = "")
    {
        var directory = Path.Combine(Source, folder); Directory.CreateDirectory(directory);
        for (var i = 0; i < count; i++) await WriteMediaAsync(Path.Combine(directory, $"clip-{i:D4}.mkv"), "original");
    }
    private int Probes => File.Exists(ProbeLog) ? File.ReadAllLines(ProbeLog).Length : 0;
    private async Task<List<ArchiveEntry>> Entries(string jobId)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ArchiveEntries.AsNoTracking().Where(e => e.JobId == jobId).ToListAsync();
    }
    private static async Task Until(Func<Task<bool>> test)
    {
        for (var i = 0; i < 200; i++) { if (await test()) return; await Task.Delay(50); }
        Assert.Fail("Condition did not settle in 10 seconds");
    }
    private async Task EncodeQueued(string jobId)
    {
        foreach (var e in (await Entries(jobId)).Where(e => e.Stage == "queued"))
        {
            var item = new WorkItem { Id = e.Id, ArchiveEntryId = e.Id, Path = e.Path };
            await _archive.ExecuteAsync(item, (args, ct) => WriteMediaAsync(args.Arguments.Last(), "encoded", ct), _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(WorkItemStatus.Completed, item.Status);
        }
    }

    [Fact]
    public async Task FirstBatchStartsEarlyThenDrainsBeforeAutomaticRefillWithFrozenRulesAndRootLayout()
    {
        await CreateFiles(70, "one-large-folder");
        var preview = await Preview();
        Assert.Equal(32, preview.GetProperty("rows").GetArrayLength());
        Assert.False(preview.GetProperty("summary").GetProperty("totalKnown").GetBoolean());
        Assert.Equal(32, Probes);
        Assert.Empty(Directory.GetDirectories(Source, ".Snacks-Archive-Originals"));
        var requestId = Guid.NewGuid().ToString("N");
        var job = await Start(preview, requestId);
        Assert.Equal(job, await Start(preview, requestId));
        await Task.Delay(400);
        Assert.Equal(32, (await Entries(job)).Count);
        Assert.Equal(32, Probes); // the producer is backpressured before probing batch 2
        Assert.False(Json(await _archive.JobAsync(job)).GetProperty("summary").GetProperty("totalKnown").GetBoolean());
        var settings = _archive.GetSettings(); settings.Concurrency = 1; settings.TargetMbps = 1;
        _archive.SaveSettings(settings);
        await EncodeQueued(job);
        await Until(async () => (await Entries(job)).Count == 64);
        Assert.NotEqual("completed", Json(await _archive.JobAsync(job)).GetProperty("status").GetString());
        Assert.Equal(32, (await Entries(job)).Count(e => e.Stage == "queued"));
        await EncodeQueued(job);
        await Until(async () => (await Entries(job)).Count == 70);
        await EncodeQueued(job);
        await Until(async () => Json(await _archive.JobAsync(job)).GetProperty("status").GetString() == "completed");
        var entries = await Entries(job);
        Assert.Equal(70, entries.Select(e => e.DiscoveryKey).Distinct().Count());
        Assert.Equal(70, Probes);
        Assert.All(entries, e => { Assert.Equal(20, e.TargetMbps); Assert.Contains("one-large-folder", e.BackupPath); Assert.Equal("original", ReadMedia(e.BackupPath!)); Assert.Equal("encoded", ReadMedia(e.Path)); });
        Assert.Equal(3, Json(await _archive.JobAsync(job)).GetProperty("rules").GetProperty("concurrency").GetInt32());
    }

    [Fact]
    public async Task EmptyFirstPreviewStillStartsAndTraversalExcludesArtifactsAndLinks()
    {
        for (var i = 0; i < 300; i++) await File.WriteAllTextAsync(Path.Combine(Source, $"note-{i:D4}.txt"), "note");
        var preview = await Preview();
        Assert.Equal(256, preview.GetProperty("visited").GetInt32());
        Assert.Equal(0, preview.GetProperty("summary").GetProperty("ready").GetInt32());
        Assert.NotNull(preview.GetProperty("previewId").GetString());
        foreach (var directory in new[] { ".Snacks-Archive-Originals", ".wuxian-staging", ".snacks-archive-temp" }) await CreateFiles(2, directory);
        await WriteMediaAsync(Path.Combine(Source, "alpha.mkv"), "transparent");
        await WriteMediaAsync(Path.Combine(Source, "broken.mkv"), "invalid");
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(Source, "link"), Source);
        var job = await Start(preview);
        await Until(async () => Json(await _archive.JobAsync(job)).GetProperty("status").GetString() == "completed");
        var entries = await Entries(job);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Stage == "skipped" && e.ReasonCode == "alpha_channel");
        Assert.Contains(entries, e => e.Stage == "failed" && e.Error!.Contains("synthetic invalid header"));
        Assert.Equal(2, Probes);
    }

    [Fact]
    public async Task PauseAndRestartRequireManualContinueAndDoNotDuplicatePreviouslyDiscoveredFiles()
    {
        await CreateFiles(40);
        var job = await Start(await Preview());
        await _archive.PauseJobAsync(job, "test pause");
        await Task.Delay(100);
        Assert.Equal(32, (await Entries(job)).Count);
        Assert.Equal(32, Probes);
        Assert.True(Json(await _archive.JobAsync(job)).GetProperty("canContinue").GetBoolean());
        await _archive.ShutdownAsync();
        _archive = NewService();
        await _archive.InitializeAsync();
        await Task.Delay(100);
        Assert.Equal(32, Probes);
        await _archive.ContinueAsync(job);
        await Until(async () => (await Entries(job)).Count(e=>e.Stage=="queued")==32);
        await EncodeQueued(job);
        await Until(async () => (await Entries(job)).Count == 40);
        await EncodeQueued(job);
        await Until(async () => Json(await _archive.JobAsync(job)).GetProperty("status").GetString() == "completed");
        Assert.Equal(40, (await Entries(job)).Select(e => e.DiscoveryKey).Distinct().Count());
        Assert.Equal(40, Probes);
    }

    [Fact]
    public async Task UnavailableRootPausesDiscoveryAndRemainsReachableWithoutFileEntries()
    {
        for (var i = 0; i < 260; i++) await File.WriteAllTextAsync(Path.Combine(Source, $"note-{i}.txt"), "note");
        var preview = await Preview();
        // An explicit paused job with no rows models a scan interrupted between discovery batches.
        var job = await Start(preview);
        await _archive.PauseJobAsync(job, "test pause");
        await _archive.ShutdownAsync();
        Directory.Move(Source, Source + "-offline");
        _archive = NewService(); await _archive.InitializeAsync();
        await _archive.ContinueAsync(job);
        await Until(async () => Json(await _archive.JobAsync(job)).GetProperty("status").GetString() == "paused_storage");
        var detail = Json(await _archive.JobAsync(job));
        Assert.False(detail.GetProperty("summary").GetProperty("totalKnown").GetBoolean());
        Assert.Contains("目录扫描未完成", detail.GetProperty("error").GetString());
        var history = Json(await _archive.HistoryAsync(null, null, 0, 50));
        Assert.Contains(history.GetProperty("directoryJobs").EnumerateArray(), j => j.GetProperty("jobId").GetString() == job);
        Directory.Move(Source + "-offline", Source);
    }

    [Fact]
    public async Task ExplicitSelectionRetryAndRearchiveStayExplicitAndJobDetailsStayBounded()
    {
        await CreateFiles(1);
        var path = Path.Combine(Source, "clip-0000.mkv");
        var preview = Json(await _archive.PreviewAsync(new() { FilePaths = [path, path] }, CancellationToken.None));
        Assert.Single(preview.GetProperty("rows").EnumerateArray());
        var job = await Start(preview);
        var entry = Assert.Single(await Entries(job));
        await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = path }, (_, _) => Task.FromException(new Exception("synthetic encode failed")), _ => Task.CompletedTask, CancellationToken.None);
        var retry = Json(await _archive.PreviewAsync(new() { EntryIds = [entry.Id], Intent = "retryFailed" }, CancellationToken.None));
        var retryJob = await Start(retry); await EncodeQueued(retryJob);
        var completed = Assert.Single(await Entries(retryJob));
        var again = Json(await _archive.PreviewAsync(new() { EntryIds = [completed.Id], Intent = "rearchive" }, CancellationToken.None));
        Assert.Equal(1, again.GetProperty("summary").GetProperty("ready").GetInt32());
        await using (var db = await _db.CreateDbContextAsync())
        {
            for (var i = 0; i < 150; i++) db.ArchiveEntries.Add(new() { JobId = retryJob, Stage = "skipped", Path = Path.Combine(Source, $"history-{i}.mkv") });
            await db.SaveChangesAsync();
        }
        var detail = Json(await _archive.JobAsync(retryJob));
        Assert.Equal(151, detail.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(100, detail.GetProperty("entries").GetArrayLength());
        Assert.True(detail.GetProperty("entriesTruncated").GetBoolean());
        Assert.Equal(50, Json(await _archive.HistoryAsync(retryJob, null, 100, 50)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task NonRecursiveCancellationAndDirectoryScopeAreEnforced()
    {
        await CreateFiles(1, "nested");
        Assert.Empty((await Preview(false)).GetProperty("rows").EnumerateArray());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _archive.PreviewAsync(new() { DirectoryPath = Source }, cts.Token));
        var preview = await Preview();
        var error = await Assert.ThrowsAsync<ArchiveException>(() => _archive.StartAsync(new() { RequestId = Guid.NewGuid().ToString(), PreviewId = preview.GetProperty("previewId").GetString()!, SelectedIds = [] }));
        Assert.Equal("directory_scope_required", error.Code);
        Assert.Equal("original", await ReadMediaAsync(Path.Combine(Source, "nested", "clip-0000.mkv")));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("restored")]
    public async Task RepeatedDirectorySkipsNeverHideAuthoritativeHistory(string stage)
    {
        await CreateFiles(1);
        var path = Path.Combine(Source, "clip-0000.mkv");
        await using (var db = await _db.CreateDbContextAsync())
        {
            var originalJob = new ArchiveJob { RequestId = "original", Status = "completed" };
            db.ArchiveJobs.Add(originalJob);
            db.ArchiveEntries.Add(new ArchiveEntry { JobId = originalJob.Id, Path = path, InputPath = path, OutputPath = path, Stage = stage,
                MediaJson = JsonSerializer.Serialize(new ArchiveMediaInfo { PathIdentity = ArchiveService.FileIdentity(path) }, ArchiveService.Json) });
            await db.SaveChangesAsync();
        }
        for (var run = 0; run < 3; run++)
        {
            var preview = await Preview();
            Assert.Equal("history_" + stage, preview.GetProperty("rows")[0].GetProperty("reasonCode").GetString());
            var job = await Start(preview);
            await Until(async () => Json(await _archive.JobAsync(job)).GetProperty("status").GetString() == "completed");
            Assert.Equal("skipped", Assert.Single(await Entries(job)).Stage);
        }
        Assert.Equal(0, Probes);
        Assert.Equal("original", await ReadMediaAsync(path));
    }

    [Fact]
    public async Task StartupRecoveryPagesThroughLegacyEntriesWithoutStartingMediaWork()
    {
        string jobId;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var job = new ArchiveJob { RequestId = "legacy-many", Status = "queued" };
            jobId = job.Id; db.ArchiveJobs.Add(job);
            for (var i = 0; i < 130; i++)
            {
                var path = Path.Combine(Source, $"not-accessed-{i}.mkv");
                var entry = new ArchiveEntry { JobId = jobId, Path = path, InputPath = path, OutputPath = path, Stage = "queued", MediaJson = "{}" };
                db.ArchiveEntries.Add(entry);
                db.MediaFiles.Add(new MediaFile { FilePath = path, FileName = Path.GetFileName(path), Directory = Source, ArchiveEntryId = entry.Id, Status = MediaFileStatus.Queued });
            }
            await db.SaveChangesAsync();
        }
        await _archive.InitializeAsync();
        Assert.Equal(130, (await Entries(jobId)).Count(e => e.Stage == "interrupted"));
        await using var resultDb = await _db.CreateDbContextAsync();
        Assert.Equal(130, await resultDb.MediaFiles.CountAsync(f => f.Status == MediaFileStatus.ArchivePaused));
        Assert.Equal("interrupted", (await resultDb.ArchiveJobs.SingleAsync()).Status);
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task MigrationAddsOptionalScanStateWithoutChangingLegacyJobs()
    {
        await using var db = new Factory(Path.Combine(_root, "migration.sqlite")).CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261030202409_AddArchiveJobsAndFileTransactions");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO ArchiveJobs (Id,RequestId,Status,RulesSnapshotJson,SourceRoot,CreatedAt,ConfirmedNoBackup,ConfirmedUseCurrentOutput) VALUES ('legacy','legacy','interrupted','{{}}','unused','2026-11-01',0,0)");
        await db.Database.MigrateAsync();
        var job = await db.ArchiveJobs.SingleAsync();
        Assert.Null(job.ScanRecursive); Assert.True(job.ScanComplete); Assert.Equal("interrupted", job.Status);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
