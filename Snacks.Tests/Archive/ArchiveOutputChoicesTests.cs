using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Controllers;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Archive;

public sealed partial class ArchiveBoundedDiscoveryTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("restored")]
    public async Task SuccessfulFinalStateIgnoresOldConflictCodeButStillProtectsRemainingTransactionPaths(string stage)
    {
        var id = await SeedLegacy(1);
        await using (var db = await _db.CreateDbContextAsync())
        { var e = await db.ArchiveEntries.SingleAsync(); e.Stage = stage; e.ReasonCode = "transaction_conflict"; await db.SaveChangesAsync(); }
        var history = Json(await _archive.HistoryAsync(id, null, 0, 1)).GetProperty("items")[0];
        Assert.False(history.GetProperty("needsFileReview").GetBoolean()); Assert.True(history.GetProperty("canRearchive").GetBoolean());
        await using (var db = await _db.CreateDbContextAsync())
        { var e = await db.ArchiveEntries.SingleAsync(); e.RollbackPath = e.Path + ".original"; await db.SaveChangesAsync(); }
        var remaining = Json(await _archive.HistoryAsync(id, null, 0, 1)).GetProperty("items")[0];
        Assert.True(remaining.GetProperty("needsFileReview").GetBoolean()); Assert.False(remaining.GetProperty("canRearchive").GetBoolean());
    }

    [Fact]
    public async Task UnjournaledBackupAfterRenameProtectsScratchRowWithoutChangingItsPaths()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        {
            entry = await db.ArchiveEntries.SingleAsync(); entry.TempOutputPath = entry.Path + ".output";
            Directory.CreateDirectory(Path.GetDirectoryName(entry.BackupPath!)!);
            await File.WriteAllTextAsync(entry.BackupPath!, "original already copied before journal"); await db.SaveChangesAsync();
        }
        var before = JsonSerializer.Serialize(entry);
        var result = Json(await _archive.ApplyPendingOutputAsync(id, new() { BackupEnabled = false }));
        Assert.Equal(0, result.GetProperty("updated").GetInt32()); Assert.Equal(1, result.GetProperty("protectedTransactions").GetInt32());
        Assert.Equal(before, JsonSerializer.Serialize(await _archive.GetEntryAsync(entry.Id)));
        Assert.Equal("original already copied before journal", await ReadMediaAsync(entry.BackupPath!));
    }

    [Fact]
    public async Task EncodingScratchCanDisableBackupOnlyWhenInPlaceDestinationStaysTheSame()
    {
        var id = await SeedLegacy(1);
        await using (var db = await _db.CreateDbContextAsync())
        {
            var e = await db.ArchiveEntries.SingleAsync(); e.TempOutputPath = e.Path + ".output";
            Directory.CreateDirectory(Path.GetDirectoryName(e.BackupPath!)!); await db.SaveChangesAsync();
        }
        var blocked = Json(await _archive.ApplyPendingOutputAsync(id, new() { OutputMode = "saveAs", OutputDirectory = Path.Combine(_root, "elsewhere") }));
        Assert.Equal(1, blocked.GetProperty("protectedTransactions").GetInt32());
        var allowed = Json(await _archive.ApplyPendingOutputAsync(id, new() { OutputMode = "inPlace", BackupEnabled = false }));
        Assert.Equal(1, allowed.GetProperty("updated").GetInt32()); Assert.Equal(0, allowed.GetProperty("protectedTransactions").GetInt32());
        await _archive.ContinueAsync(id); await FinishOwned(id);
        Assert.Null(Assert.Single(await Entries(id)).BackupPath);
    }

    [Fact]
    public async Task InaccessibleBackupParentProtectsScratchRowsOutputChoice()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        {
            entry = await db.ArchiveEntries.SingleAsync(); entry.TempOutputPath = entry.Path + ".output";
            Assert.False(Directory.Exists(Path.GetDirectoryName(entry.BackupPath))); await db.SaveChangesAsync();
        }
        var before = JsonSerializer.Serialize(entry);
        var result = Json(await _archive.ApplyPendingOutputAsync(id, new() { BackupEnabled = false }));
        Assert.Equal(0, result.GetProperty("updated").GetInt32()); Assert.Equal(1, result.GetProperty("protectedTransactions").GetInt32());
        Assert.Equal(before, JsonSerializer.Serialize(await _archive.GetEntryAsync(entry.Id)));
    }

    [Theory]
    [InlineData("/Volumes/ArchiveFixture/media/project", "/Volumes/ArchiveFixture/#recycle/media/project")]
    [InlineData("/tmp/snacks-fixture/project", null)]
    [InlineData("/Volumes/ArchiveFixture/#recycle/project", null)]
    public void NasRecycleLocationUsesOnlyTheTasksExactMountedRoot(string source, string? expected)
        => Assert.Equal(expected, ArchiveService.NasRecycleFolder(source));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingChoiceSurvivesRestartAndControlsReplacement(bool backup)
    {
        var id = await SeedLegacy(2);
        string snapshot;
        await using (var db = await _db.CreateDbContextAsync()) snapshot = (await db.ArchiveJobs.FindAsync(id))!.RulesSnapshotJson;
        var result = Json(await _archive.ApplyPendingOutputAsync(id, new() { OutputMode = "inPlace", BackupEnabled = backup }));
        Assert.Equal(2, result.GetProperty("updated").GetInt32());
        await _archive.ShutdownAsync(); _archive = NewService(); await _archive.InitializeAsync();
        await _archive.ContinueAsync(id); await FinishOwned(id);
        foreach (var entry in await Entries(id))
        {
            Assert.Equal(id, entry.JobId); Assert.Equal(20, entry.TargetMbps); Assert.Equal("completed", entry.Stage);
            Assert.Equal("encoded", await ReadMediaAsync(entry.Path));
            if (backup) Assert.Equal("original", await ReadMediaAsync(entry.BackupPath!));
            else Assert.Null(entry.BackupPath);
        }
        await using var check = await _db.CreateDbContextAsync();
        Assert.Equal(snapshot, (await check.ArchiveJobs.FindAsync(id))!.RulesSnapshotJson);
        if (!backup) Assert.False(Directory.Exists(Path.Combine(Source, ".Snacks-Archive-Originals")));
    }

    [Fact]
    public async Task PendingChoicePreservesCompletedTransactionsAndOtherOwners()
    {
        var id = await SeedLegacy(4);
        ArchiveEntry completed, transaction, other; string historyJson, transactionJson;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var rows = await db.ArchiveEntries.OrderBy(e => e.Path).ToListAsync();
            completed = rows[0]; completed.Stage = "completed";
            transaction = rows[1]; transaction.RollbackPath = transaction.Path + ".original";
            other = rows[2];
            var otherJob = new ArchiveJob { RequestId = "other", SourceRoot = Source, Status = "interrupted" };
            db.ArchiveJobs.Add(otherJob); other.FolderJobId = otherJob.Id;
            await db.SaveChangesAsync(); historyJson = JsonSerializer.Serialize(completed); transactionJson = JsonSerializer.Serialize(transaction);
        }
        var result = Json(await _archive.ApplyPendingOutputAsync(id, new() { BackupEnabled = false }));
        Assert.Equal(1, result.GetProperty("updated").GetInt32()); Assert.Equal(1, result.GetProperty("protectedTransactions").GetInt32());
        Assert.Equal(historyJson, JsonSerializer.Serialize(await _archive.GetEntryAsync(completed.Id)));
        Assert.Equal(transactionJson, JsonSerializer.Serialize(await _archive.GetEntryAsync(transaction.Id)));
        Assert.Null(JsonSerializer.Deserialize<ArchiveMediaInfo>((await _archive.GetEntryAsync(other.Id))!.MediaJson, ArchiveService.Json)!.OutputPolicy);
    }

    [Fact]
    public async Task LaterAdoptedLegacyRowsUseOwnerOutputChoiceWithoutChangingOriginRules()
    {
        var old = await SeedLegacy(1);
        string owner;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var folder = new ArchiveJob { RequestId = "owner", SourceRoot = Source, Status = "interrupted", ScanRecursive = true, ScanComplete = false,
                RulesSnapshotJson = JsonSerializer.Serialize(_archive.GetSettings(), ArchiveService.Json) };
            owner = folder.Id; db.ArchiveJobs.Add(folder); await db.SaveChangesAsync();
        }
        var result = Json(await _archive.ApplyPendingOutputAsync(owner, new() { BackupEnabled = false }));
        Assert.Equal(0, result.GetProperty("updated").GetInt32());
        await _archive.ContinueAsync(owner); await FinishOwned(owner);
        var entry = Assert.Single(await Entries(old));
        Assert.Equal(old, entry.JobId); Assert.Equal(owner, entry.FolderJobId); Assert.Equal(20, entry.TargetMbps);
        Assert.Equal("completed", entry.Stage); Assert.Null(entry.BackupPath);
        Assert.False(Directory.Exists(Path.Combine(Source, ".Snacks-Archive-Originals")));
    }

    [Fact]
    public async Task SingleTransactionConflictPreservesFilesAndDoesNotBlockOrAutomaticallyRetryBatch()
    {
        var id = await SeedLegacy(2); string conflictId; string original, rollback, candidate, backup;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var e = await db.ArchiveEntries.OrderBy(e => e.Path).FirstAsync(); conflictId = e.Id; original = e.Path;
            rollback = e.RollbackPath = Path.Combine(Source, ".snacks-archive-" + e.Id + ".original.mkv");
            candidate = e.TempOutputPath = Path.Combine(Source, ".snacks-archive-" + e.Id + ".output.mkv");
            backup = e.BackupPath!; Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(rollback, "old-original"); await File.WriteAllTextAsync(candidate, "candidate"); await File.WriteAllTextAsync(backup, "registered-backup");
            var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(e.MediaJson, ArchiveService.Json)!;
            media.Transfer = new() { BackupReadyPath = backup }; e.MediaJson = JsonSerializer.Serialize(media, ArchiveService.Json);
            await db.SaveChangesAsync();
        }
        await _archive.ContinueAsync(id); await FinishOwned(id);
        var conflict = (await _archive.GetEntryAsync(conflictId))!; var failedTime = conflict.UpdatedAt;
        Assert.Equal("failed", conflict.Stage); Assert.Equal("transaction_conflict", conflict.ReasonCode); Assert.Contains(original, conflict.Error);
        Assert.Single(await Entries(id), e => e.Stage == "completed");
        var history = Json(await _archive.HistoryAsync(id, "failed", 0, 10)).GetProperty("items")[0];
        Assert.False(history.GetProperty("canRetry").GetBoolean()); Assert.False(history.GetProperty("canRearchive").GetBoolean());
        Assert.True(history.GetProperty("needsFileReview").GetBoolean()); Assert.Contains("需先核对文件", history.GetProperty("retryReason").GetString());
        var conflictBeforeRetry = JsonSerializer.Serialize(conflict); var probesBeforeRetry = Probes;
        foreach (var intent in new[] { "retryFailed", "rearchive" })
        {
            var preview = Json(await _archive.PreviewAsync(new() { EntryIds = [conflict.Id], Intent = intent }, CancellationToken.None));
            Assert.Equal(0, preview.GetProperty("summary").GetProperty("ready").GetInt32());
            Assert.Equal("transaction_requires_review", preview.GetProperty("rows")[0].GetProperty("reasonCode").GetString());
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("previewId").ValueKind);
            Assert.Equal("preview_expired", (await Assert.ThrowsAsync<ArchiveException>(() => _archive.StartAsync(new() { PreviewId = "no-preview", RequestId = intent }))).Code);
        }
        Assert.Equal(probesBeforeRetry, Probes); Assert.Equal(2, (await Entries(id)).Count);
        Assert.Equal(conflictBeforeRetry, JsonSerializer.Serialize(await _archive.GetEntryAsync(conflict.Id)));
        await Task.Delay(250); Assert.Equal(failedTime, (await _archive.GetEntryAsync(conflictId))!.UpdatedAt);
        Assert.Equal("original", await ReadMediaAsync(original)); Assert.Equal("old-original", await ReadMediaAsync(rollback));
        Assert.Equal("candidate", await ReadMediaAsync(candidate)); Assert.Equal("registered-backup", await ReadMediaAsync(backup));
    }

    [Fact]
    public async Task ConflictDetectedAfterRetryPreviewStillBlocksStartWithoutCreatingRows()
    {
        var id = await SeedLegacy(1); ArchiveEntry entry;
        await using (var db = await _db.CreateDbContextAsync())
        { entry = await db.ArchiveEntries.SingleAsync(); entry.Stage = "failed"; await db.SaveChangesAsync(); }
        var preview = Json(await _archive.PreviewAsync(new() { EntryIds = [entry.Id], Intent = "retryFailed" }, CancellationToken.None));
        Assert.Equal(1, preview.GetProperty("summary").GetProperty("ready").GetInt32());
        await using (var db = await _db.CreateDbContextAsync())
        { var changed = await db.ArchiveEntries.SingleAsync(); changed.ReasonCode = "transaction_conflict"; await db.SaveChangesAsync(); }
        await AddMaskingSkips(id);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => Start(preview));
        Assert.Equal("transaction_requires_review", error.Code);
        await using var check = await _db.CreateDbContextAsync();
        Assert.Equal(2, await check.ArchiveJobs.CountAsync()); Assert.Equal(2, await check.ArchiveEntries.CountAsync());
        Assert.Equal("original", await ReadMediaAsync(entry.Path));
    }

    [Fact]
    public async Task SaveAsPendingChoiceKeepsSourceAndCanResolveCompletedOutputAfterRestart()
    {
        var id = await SeedLegacy(1); var outputDir = Path.Combine(_root, "separate-output");
        await _archive.ApplyPendingOutputAsync(id, new() { OutputMode = "saveAs", OutputDirectory = outputDir, BackupEnabled = false });
        await _archive.ContinueAsync(id); await FinishOwned(id);
        var entry = Assert.Single(await Entries(id));
        Assert.Equal("original", await ReadMediaAsync(entry.Path)); Assert.Equal("encoded", await ReadMediaAsync(entry.OutputPath));
        await using var db = await _db.CreateDbContextAsync();
        Assert.Equal(entry.OutputPath, await DesktopFilesController.ResolvePathAsync(db, null, "work", entry.Id, "output"));
        File.Delete(entry.OutputPath);
        Assert.Equal("file_missing", (await Assert.ThrowsAsync<ArchiveException>(() => DesktopFilesController.ResolvePathAsync(db, null, "archive", entry.Id, "output"))).Code);
    }

    [Fact]
    public async Task ManualBackupDeletionUpdatesHistoryWithoutDeletingOtherFiles()
    {
        var id = await SeedLegacy(1); await _archive.ContinueAsync(id); await FinishOwned(id);
        var entry = Assert.Single(await Entries(id));
        Assert.True(Json(await _archive.HistoryAsync(id, null, 0, 1)).GetProperty("items")[0].GetProperty("backupAvailable").GetBoolean());
        File.Delete(entry.BackupPath!);
        var history = Json(await _archive.HistoryAsync(id, null, 0, 1)).GetProperty("items")[0];
        Assert.False(history.GetProperty("backupAvailable").GetBoolean()); Assert.False(history.GetProperty("canRestore").GetBoolean());
        Assert.Equal("missing", history.GetProperty("backupStatus").GetString());
        Assert.Equal("encoded", await ReadMediaAsync(entry.Path));
        await using var db = await _db.CreateDbContextAsync();
        Assert.Equal(Path.GetDirectoryName(entry.BackupPath), await DesktopFilesController.ResolvePathAsync(db, null, "archive", entry.Id, "backup-folder"));
    }

    [Fact]
    public async Task OrdinaryHistoryResolvesOnlyRecordedFinalOutputNeverItsSource()
    {
        var output = Path.Combine(_root, "kept-final.mp4"); await File.WriteAllTextAsync(output, "kept");
        await using var db = await _db.CreateDbContextAsync();
        db.EncodeHistory.Add(new EncodeHistory { JobId = "ordinary", FilePath = "/not/the/output.mov", OutputPath = output, Outcome = "Completed" });
        db.EncodeHistory.Add(new EncodeHistory { JobId = "legacy", FilePath = output, Outcome = "Completed" }); await db.SaveChangesAsync();
        Assert.Equal(output, await DesktopFilesController.ResolvePathAsync(db, null, "work", "ordinary", "output"));
        Assert.Equal("output_path_unavailable", (await Assert.ThrowsAsync<ArchiveException>(() => DesktopFilesController.ResolvePathAsync(db, null, "work", "legacy", "output"))).Code);
    }
}
