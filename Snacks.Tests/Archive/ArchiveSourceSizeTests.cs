using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Services;
using Xunit;
using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;

namespace Snacks.Tests.Archive;

public sealed partial class ArchiveBoundedDiscoveryTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(9_999_999, true)]
    [InlineData(10_000_000, false)]
    [InlineData(10_000_001, false)]
    public async Task FixedDecimalSizeBoundarySkipsBeforeProbeAndLeavesSourceUnchanged(long bytes, bool skipped)
    {
        var path = Path.Combine(Source, "boundary.mkv"); await WriteMediaAsync(path, "source", bytes: bytes);
        var modified = File.GetLastWriteTimeUtc(path);
        var preview = Json(await _archive.PreviewAsync(new() { FilePaths = [path] }, CancellationToken.None));
        var row = preview.GetProperty("rows")[0];
        Assert.Equal(skipped ? "skip" : "archive", row.GetProperty("action").GetString());
        Assert.Equal(skipped ? "source_below_10_mb" : "ready", row.GetProperty("reasonCode").GetString());
        Assert.Equal(bytes, row.GetProperty("sourceBytes").GetInt64()); Assert.Equal(skipped ? 0 : 1, Probes);
        Assert.Equal(bytes, new FileInfo(path).Length); Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.EnumerateFiles(Source)); Assert.Empty(Directory.EnumerateDirectories(Source));
    }

    [Fact]
    public async Task AllSmallFirstBatchDrainsAndLaterEligibleFilesStillRun()
    {
        await CreateFiles(37);
        var small = Directory.EnumerateFiles(Source).OrderBy(x => x).Take(35).ToArray();
        foreach (var path in small) await WriteMediaAsync(path, "small", bytes: 100);
        var before = small.ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));
        var id = await Direct(); await FinishOwned(id);
        var entries = await Entries(id);
        Assert.Equal(35, entries.Count(e => e.Stage == "skipped" && e.ReasonCode == "source_below_10_mb"));
        Assert.Equal(2, entries.Count(e => e.Stage == "completed")); Assert.Equal(2, Probes);
        Assert.All(entries.Where(e => e.Stage == "skipped"), e => Assert.Null(e.BackupPath));
        foreach (var path in small)
        { Assert.Equal(100, new FileInfo(path).Length); Assert.Equal("small", ReadMedia(path)); Assert.Equal(before[path], File.GetLastWriteTimeUtc(path)); }
        Assert.True(Json(await _archive.JobAsync(id)).GetProperty("summary").GetProperty("totalKnown").GetBoolean());
    }

    [Fact]
    public async Task OldQueuedAndWaitingRowsUseCurrentSizeOnRestartWithoutRewritingFrozenRulesOrCompletedHistory()
    {
        var id = await SeedLegacy(5); string snapshot, completedId, completedJson;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var rows = await db.ArchiveEntries.OrderBy(e => e.Path).ToListAsync();
            var stages = new[] { "queued", "waiting", "waiting_retry", "interrupted" };
            for (var n = 0; n < stages.Length; n++)
            { rows[n].Stage = stages[n]; rows[n].SourceBytes = 40_000_000; await WriteMediaAsync(rows[n].Path, "small", bytes: 100); }
            rows[4].Stage = "completed"; completedId = rows[4].Id;
            snapshot = (await db.ArchiveJobs.SingleAsync()).RulesSnapshotJson;
            await db.SaveChangesAsync(); completedJson = JsonSerializer.Serialize(rows[4]);
        }
        await _archive.ShutdownAsync(); _archive = NewService(); await _archive.InitializeAsync();
        await _archive.ContinueAsync(id); await FinishOwned(id);
        var entries = await Entries(id);
        Assert.Equal(4, entries.Count(e => e.Stage == "skipped" && e.ReasonCode == "source_below_10_mb")); Assert.Equal(0, Probes);
        Assert.All(entries.Where(e => e.Stage == "skipped"), e => { Assert.Equal(id, e.JobId); Assert.Equal(20, e.TargetMbps); Assert.Equal(100, e.SourceBytes); Assert.False(File.Exists(e.BackupPath)); });
        Assert.Equal(completedJson, JsonSerializer.Serialize(await _archive.GetEntryAsync(completedId)));
        await using var check = await _db.CreateDbContextAsync(); Assert.Equal(snapshot, (await check.ArchiveJobs.SingleAsync()).RulesSnapshotJson);
        Assert.Equal(4, await check.MediaFiles.CountAsync(f => f.Status == MediaFileStatus.Skipped));
    }

    [Fact]
    public async Task FailedRetryOfSmallSourceStopsBeforeProbeOrNewJob()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        { entry = await db.ArchiveEntries.SingleAsync(); entry.Stage = "failed"; await db.SaveChangesAsync(); }
        await WriteMediaAsync(entry.Path, "small", bytes: 100);
        var preview = Json(await _archive.PreviewAsync(new() { EntryIds = [entry.Id], Intent = "retryFailed" }, CancellationToken.None));
        Assert.Equal("source_below_10_mb", preview.GetProperty("rows")[0].GetProperty("reasonCode").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("previewId").ValueKind); Assert.Equal(0, Probes);
        Assert.Single(await Entries(id)); Assert.Equal("failed", (await _archive.GetEntryAsync(entry.Id))!.Stage);
    }

    [Fact]
    public async Task FinalDispatchChecksFreshSizeWithoutEncodingOrCreatingAnyOutput()
    {
        await CreateFiles(1); var id = await Start(await Preview()); var entry = Assert.Single(await Entries(id));
        await WriteMediaAsync(entry.Path, "small", bytes: 100); var modified = File.GetLastWriteTimeUtc(entry.Path);
        var calls = 0; var item = new WorkItem { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path, Size = 40_000_000 };
        await _archive.ExecuteAsync(item, (_, _) => { calls++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
        var saved = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Equal("skipped", saved.Stage); Assert.Equal("source_below_10_mb", saved.ReasonCode);
        Assert.Equal(0, calls); Assert.Equal("skipped", item.ArchiveStage); Assert.Contains("10 MB", item.WaitReason); Assert.Null(item.OutputPath); Assert.Equal(100, item.Size);
        Assert.Null(saved.TempOutputPath); Assert.Null(saved.RollbackPath); Assert.False(File.Exists(saved.BackupPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(entry.Path)); Assert.Equal("small", ReadMedia(entry.Path));
        Assert.Empty(Directory.EnumerateDirectories(Source));
    }

    [Fact]
    public async Task MissingSourceAtDispatchSkipsWithoutEncodingOrPretendingItIsASmallFile()
    {
        await CreateFiles(1); var id = await Start(await Preview()); var entry = Assert.Single(await Entries(id)); File.Delete(entry.Path);
        var calls = 0;
        await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path }, (_, _) => { calls++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
        var saved = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Equal("skipped", saved.Stage); Assert.Equal("source_missing", saved.ReasonCode); Assert.Equal(0, calls);
        Assert.Null(saved.TempOutputPath); Assert.Null(saved.RollbackPath); Assert.Null(saved.CompletedAt);
    }

    [Fact]
    public async Task SmallSourceConflictKeepsAllTransactionFilesAndRemainsAConflict()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        {
            entry = await db.ArchiveEntries.SingleAsync();
            await WriteMediaAsync(entry.Path, "small-original", bytes: 100);
            entry.RollbackPath = Path.Combine(Source, ".snacks-archive-" + entry.Id + ".original.mkv");
            entry.TempOutputPath = Path.Combine(Source, ".snacks-archive-" + entry.Id + ".output.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath!)!);
            await File.WriteAllTextAsync(entry.RollbackPath, "rollback"); await File.WriteAllTextAsync(entry.TempOutputPath, "candidate"); await File.WriteAllTextAsync(entry.BackupPath!, "backup");
            var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, ArchiveService.Json)!;
            media.Transfer = new() { BackupReadyPath = entry.BackupPath }; entry.MediaJson = JsonSerializer.Serialize(media, ArchiveService.Json);
            await db.SaveChangesAsync();
        }
        await _archive.ContinueAsync(id); await FinishOwned(id);
        var saved = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Equal("failed", saved.Stage); Assert.Equal("transaction_conflict", saved.ReasonCode); Assert.Equal(0, Probes);
        Assert.Equal(entry.RollbackPath, saved.RollbackPath); Assert.Equal(entry.TempOutputPath, saved.TempOutputPath); Assert.Equal(entry.MediaJson, saved.MediaJson);
        Assert.Equal("small-original", ReadMedia(entry.Path)); Assert.Equal("rollback", File.ReadAllText(entry.RollbackPath!));
        Assert.Equal("candidate", File.ReadAllText(entry.TempOutputPath!)); Assert.Equal("backup", File.ReadAllText(entry.BackupPath!));
    }

    [Fact]
    public async Task SafelyRecoveredSmallOriginalSkipsWhileKeepingItsRegisteredCandidate()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        {
            entry = await db.ArchiveEntries.SingleAsync(); await WriteMediaAsync(entry.Path, "original", bytes: 100);
            entry.RollbackPath = Path.Combine(Source, ".snacks-archive-" + entry.Id + ".original.mkv");
            entry.TempOutputPath = Path.Combine(Source, ".snacks-archive-" + entry.Id + ".output.mkv");
            File.Move(entry.Path, entry.RollbackPath); await File.WriteAllTextAsync(entry.TempOutputPath, "candidate"); await db.SaveChangesAsync();
        }
        // Normal startup recovers the interrupted atomic move before traversal.
        await _archive.ShutdownAsync(); _archive = NewService(); await _archive.InitializeAsync();
        var recovered = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Null(recovered.RollbackPath); Assert.Equal("original", ReadMedia(entry.Path)); Assert.False(File.Exists(entry.RollbackPath!));
        await _archive.ContinueAsync(id); await FinishOwned(id);
        var saved = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Equal("skipped", saved.Stage); Assert.Equal("source_below_10_mb", saved.ReasonCode); Assert.Equal(0, Probes);
        Assert.Equal(entry.TempOutputPath, saved.TempOutputPath); Assert.Equal("candidate", File.ReadAllText(entry.TempOutputPath!));
        var calls = 0;
        await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path }, (_, _) => { calls++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(0, calls); Assert.Equal(100, new FileInfo(entry.Path).Length); Assert.Equal("original", ReadMedia(entry.Path));
        Assert.Equal("candidate", File.ReadAllText(entry.TempOutputPath!)); Assert.False(File.Exists(entry.BackupPath));
    }

    [Fact]
    public async Task SmallOriginalWithRegisteredBackupContinuesExistingTransactionThroughDispatch()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        {
            entry = await db.ArchiveEntries.SingleAsync(); await WriteMediaAsync(entry.Path, "original", bytes: 100);
            Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath!)!); await WriteMediaAsync(entry.BackupPath!, "original", bytes: 100);
            var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, ArchiveService.Json)!;
            media.Transfer = new() { BackupReadyPath = entry.BackupPath }; entry.MediaJson = JsonSerializer.Serialize(media, ArchiveService.Json); await db.SaveChangesAsync();
        }
        await _archive.ContinueAsync(id); await Until(async () => (await _archive.GetEntryAsync(entry.Id))!.Stage == "queued");
        var calls = 0;
        await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path }, (args, ct) => { calls++; return WriteMediaAsync(args.Arguments.Last(), "encoded", ct); }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, calls); Assert.Equal("completed", (await _archive.GetEntryAsync(entry.Id))!.Stage);
        Assert.Equal(100, new FileInfo(entry.BackupPath!).Length); Assert.Equal("original", ReadMedia(entry.BackupPath!)); Assert.Equal("encoded", ReadMedia(entry.Path));
    }

    [Fact]
    public async Task PublishedSmallOutputRecoversAsCompletedRatherThanSkipped()
    {
        var id = await SeedLegacy(1); string entryId;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entry = await db.ArchiveEntries.SingleAsync(); entryId = entry.Id; await WriteMediaAsync(entry.Path, "published", bytes: 100);
            var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, ArchiveService.Json)!;
            media.Transfer = new() { Published = true }; entry.MediaJson = JsonSerializer.Serialize(media, ArchiveService.Json); await db.SaveChangesAsync();
        }
        await _archive.ContinueAsync(id); await FinishOwned(id);
        Assert.Equal("completed", (await _archive.GetEntryAsync(entryId))!.Stage); Assert.Equal(0, Probes);
        Assert.Equal("published", ReadMedia((await _archive.GetEntryAsync(entryId))!.Path));
    }
}
