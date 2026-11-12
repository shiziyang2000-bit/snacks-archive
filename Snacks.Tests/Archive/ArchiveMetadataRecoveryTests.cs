using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Archive;

public sealed partial class ArchiveBoundedDiscoveryTests
{
    [Fact]
    public async Task MacMetadataIsNeverProbedOrAdmittedAndRealVideosStillFinish()
    {
        await CreateFiles(1);
        await WriteMediaAsync(Path.Combine(Source, "._clip-0000.mkv"), "metadata");
        Directory.CreateDirectory(Path.Combine(Source, ".AppleDouble"));
        await WriteMediaAsync(Path.Combine(Source, ".AppleDouble", "nested.mkv"), "metadata");
        await File.WriteAllTextAsync(Path.Combine(Source, ".DS_Store"), "metadata");
        var job = await Direct();
        await FinishOwned(job);
        var entry = Assert.Single(await Owned(job));
        Assert.Equal("completed", entry.Stage);
        Assert.Equal(Path.Combine(Source, "clip-0000.mkv"), entry.Path);
        Assert.Equal(1, Probes);
        Assert.Equal("metadata", await ReadMediaAsync(Path.Combine(Source, "._clip-0000.mkv")));
    }

    [Fact]
    public async Task MetadataDeletedWhileDirectoryEnumeratorIsOpenDoesNotPauseTheWalk()
    {
        for (var i = 0; i < 100; i++)
            await File.WriteAllTextAsync(Path.Combine(Source, $"._metadata-{i:D3}.mp4"), "metadata");
        using var walker = _archive.WalkDirectory(Source, true, _archive.GetSettings(), CancellationToken.None).GetEnumerator();
        Assert.True(walker.MoveNext());
        Assert.Null(walker.Current);
        foreach (var metadata in Directory.EnumerateFiles(Source, "._*")) File.Delete(metadata);
        while (walker.MoveNext()) Assert.Null(walker.Current);
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task MissingMacMetadataInLegacyBacklogIsSkippedBeforeFilesystemAccess()
    {
        var old = await SeedLegacy(2);
        string metadataId;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entry = await db.ArchiveEntries.OrderBy(e => e.Path).FirstAsync();
            metadataId = entry.Id;
            File.Delete(entry.Path);
            entry.Path = entry.InputPath = entry.OutputPath = Path.Combine(Source, "._missing.mp4");
            var media = await db.MediaFiles.SingleAsync(f => f.ArchiveEntryId == entry.Id);
            media.FilePath = entry.Path;
            await db.SaveChangesAsync();
        }
        Assert.Equal(old, await Direct());
        await FinishOwned(old);
        var rows = await Owned(old);
        var skipped = Assert.Single(rows, e => e.Id == metadataId);
        Assert.Equal("skipped", skipped.Stage);
        Assert.Equal("filesystem_metadata", skipped.ReasonCode);
        Assert.Single(rows, e => e.Stage == "completed");
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task ExplicitMissingMetadataAlsoSkipsWithoutTreatingItAsAnUnavailableVideo()
    {
        var result = Json(await _archive.PreviewAsync(new() { FilePaths = [Path.Combine(Source, "._gone.mp4")] }, CancellationToken.None));
        var row = Assert.Single(result.GetProperty("rows").EnumerateArray());
        Assert.Equal("skip", row.GetProperty("action").GetString());
        Assert.Equal("filesystem_metadata", row.GetProperty("reasonCode").GetString());
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task MetadataWithAnUnresolvedOriginalTransactionStaysFailedAndPreservesBothFiles()
    {
        var old = await SeedLegacy(1);
        string entryId, path, rollback;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entry = await db.ArchiveEntries.SingleAsync();
            entryId = entry.Id;
            path = Path.Combine(Source, "._legacy.mkv");
            File.Move(entry.Path, path);
            rollback = Path.Combine(Source, $".snacks-archive-{entry.Id}.original.mkv");
            await WriteMediaAsync(rollback, "rollback-original");
            entry.Path = entry.InputPath = entry.OutputPath = path;
            entry.RollbackPath = rollback;
            entry.Stage = "failed"; entry.ReasonCode = "transaction_conflict";
            (await db.MediaFiles.SingleAsync()).FilePath = path;
            await db.SaveChangesAsync();
        }
        Assert.Equal(old, await Direct());
        await FinishOwned(old);
        var preserved = (await _archive.GetEntryAsync(entryId))!;
        Assert.Equal("failed", preserved.Stage);
        Assert.Equal("transaction_conflict", preserved.ReasonCode);
        Assert.Equal(rollback, preserved.RollbackPath);
        Assert.Equal("original", await ReadMediaAsync(path));
        Assert.Equal("rollback-original", await ReadMediaAsync(rollback));
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task PublishedMetadataTransactionRecoversAsCompletedInsteadOfBecomingSkipped()
    {
        var old = await SeedLegacy(1);
        string entryId;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entry = await db.ArchiveEntries.SingleAsync();
            entryId = entry.Id;
            var path = Path.Combine(Source, "._published.mkv");
            File.Move(entry.Path, path);
            entry.Path = entry.InputPath = entry.OutputPath = path;
            var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, ArchiveService.Json)!;
            media.Transfer = new ArchiveTransferState { Published = true };
            entry.MediaJson = JsonSerializer.Serialize(media, ArchiveService.Json);
            (await db.MediaFiles.SingleAsync()).FilePath = path;
            await db.SaveChangesAsync();
        }
        Assert.Equal(old, await Direct());
        await FinishOwned(old);
        Assert.Equal("completed", (await _archive.GetEntryAsync(entryId))!.Stage);
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task MainScreenFolderStatusRemainsReadableWithAnUnavailableSource()
    {
        await using (var db = await _db.CreateDbContextAsync())
        {
            db.ArchiveJobs.Add(new ArchiveJob { RequestId = "offline-folder", ScanRecursive = true,
                SourceRoot = Path.Combine(_root, "unavailable-volume"), Status = "paused_storage", Error = "specific storage error" });
            db.ArchiveJobs.Add(new ArchiveJob { RequestId = "old-completed", ScanRecursive = true, Status = "completed" });
            await db.SaveChangesAsync();
        }
        var status = Json(await _archive.FolderStatusAsync());
        var job = Assert.Single(status.GetProperty("jobs").EnumerateArray());
        Assert.Equal("paused_storage", job.GetProperty("status").GetString());
        Assert.Equal("specific storage error", job.GetProperty("error").GetString());
        Assert.Equal(0, Probes);
        await using var after = await _db.CreateDbContextAsync();
        Assert.Equal(0, await after.ArchiveEntries.CountAsync());
    }

    [Fact]
    public async Task MissingRealVideoIsRecordedAsSkippedAndTheNextLegacyFileCompletes()
    {
        var old = await SeedLegacy(2);
        var entry = (await Entries(old)).OrderBy(e => e.Path).First();
        File.Delete(entry.Path);
        Assert.Equal(old, await Direct());
        await FinishOwned(old);
        var skipped = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Equal("skipped", skipped.Stage);
        Assert.Equal("source_missing", skipped.ReasonCode);
        Assert.Contains("源文件已不存在", skipped.Error);
        Assert.Equal(entry.JobId, skipped.JobId); Assert.Equal(entry.MediaJson, skipped.MediaJson);
        Assert.Single(await Entries(old), e => e.Stage == "completed");
        await using var check = await _db.CreateDbContextAsync();
        Assert.Equal(MediaFileStatus.Skipped, (await check.MediaFiles.SingleAsync(f => f.ArchiveEntryId == entry.Id)).Status);
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task CompletedOutputSurvivesRestartReselectionParentSelectionAndChangedTimestamp()
    {
        await CreateFiles(1);
        var first = await Direct();
        await FinishOwned(first);
        var completed = Assert.Single(await Owned(first));
        var savedEntry = JsonSerializer.Serialize(completed, ArchiveService.Json);
        Assert.Equal("encoded", await ReadMediaAsync(completed.Path));
        File.SetLastWriteTimeUtc(completed.Path, DateTime.UtcNow.AddMinutes(-5));
        var touched = File.GetLastWriteTimeUtc(completed.Path);
        await AddMaskingSkips(first);
        await _archive.ShutdownAsync();
        _archive = NewService();
        await _archive.InitializeAsync();
        await CreateFiles(1, "new-files");
        var second = await Direct();
        await FinishOwned(second);
        Assert.Equal(second, await Direct());
        await FinishOwned(second);
        var parent = await Direct(_root);
        await FinishOwned(parent);
        Assert.Equal(2, Probes); // one original plus one newly added video only
        Assert.Equal(touched, File.GetLastWriteTimeUtc(completed.Path));
        Assert.Equal("encoded", await ReadMediaAsync(completed.Path));
        Assert.Equal(savedEntry, JsonSerializer.Serialize(await _archive.GetEntryAsync(completed.Id), ArchiveService.Json));
        await using var verify = await _db.CreateDbContextAsync();
        Assert.Equal(1, await verify.ArchiveEntries.CountAsync(e => e.Path == completed.Path && e.Stage == "completed"));
        Assert.Equal(2, await verify.ArchiveEntries.CountAsync(e => e.Stage == "completed"));
    }
}
