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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SourceDisappearingDuringProbeSkipsAndContinues(bool legacy, bool probeReturnsMissingError)
    {
        if (legacy)
        {
            await SeedLegacy(2);
            await using var db = await _db.CreateDbContextAsync();
            var entry = await db.ArchiveEntries.OrderBy(e => e.Path).FirstAsync();
            entry.EncoderMode = ""; entry.Stage = "failed"; await db.SaveChangesAsync(); // explicit old retry needs probing
        }
        else await CreateFiles(2);
        var probe = Path.Combine(_root, "probe.sh");
        var script = await File.ReadAllTextAsync(probe);
        var failure = probeReturnsMissingError ? "printf '%s: No such file or directory\\n' \"$last\" >&2; exit 1; " : "";
        script = script.Replace("case \"$last\" in *broken*)", "case \"$last\" in *0000.mkv) rm -f -- \"$last\"; " + failure + ";; esac\ncase \"$last\" in *broken*)");
        await File.WriteAllTextAsync(probe, script);
        var id = await Direct(); await FinishOwned(id);
        var entries = await Owned(id);
        var missing = Assert.Single(entries, e => e.ReasonCode == "source_missing");
        Assert.Equal("skipped", missing.Stage); Assert.False(File.Exists(missing.Path));
        Assert.Single(entries, e => e.Stage == "completed");
        Assert.Equal(legacy ? 1 : 2, Probes);
    }

    [Theory]
    [InlineData("Permission denied", true)]
    [InlineData("Input/output error", true)]
    [InlineData("No such file or directory", false)]
    public async Task ProbeErrorsDoNotSkipUnlessTheSourceIsConfirmedMissing(string error, bool deleteSource)
    {
        var id = await SeedLegacy(1);
        await using (var db = await _db.CreateDbContextAsync())
        { var entry = await db.ArchiveEntries.SingleAsync(); entry.EncoderMode = ""; entry.Stage = "failed"; await db.SaveChangesAsync(); }
        var probe = Path.Combine(_root, "probe.sh");
        var script = "#!/bin/sh\nfor last do :; done\n" + (deleteSource ? "rm -f -- \"$last\"\n" : "") +
            "printf '%s: " + error + "\\n' \"$last\" >&2\nexit 1\n";
        await File.WriteAllTextAsync(probe, script);
        Assert.Equal(id, await Direct());
        await Until(async () => Json(await _archive.JobAsync(id)).GetProperty("status").GetString() == "paused_storage");
        var saved = Assert.Single(await Entries(id));
        Assert.NotEqual("source_missing", saved.ReasonCode); Assert.NotEqual("skipped", saved.Stage);
    }

    [Fact]
    public async Task MissingProbeExecutableIsNotReportedAsAMissingSource()
    {
        await CreateFiles(1);
        Environment.SetEnvironmentVariable("FFPROBE_PATH", Path.Combine(_root, "nonexistent-probe"));
        var id = await Direct();
        await Until(async () => Json(await _archive.JobAsync(id)).GetProperty("status").GetString() is "paused_storage" or "completed");
        Assert.DoesNotContain(await Entries(id), e => e.ReasonCode == "source_missing");
        Assert.Equal("original", await ReadMediaAsync(Path.Combine(Source, "clip-0000.mkv")));
    }

    [Fact]
    public async Task VideoDisappearingBetweenPreviewAndAdmissionGetsAPersistedSkip()
    {
        await CreateFiles(2);
        var preview = await Preview();
        var path = preview.GetProperty("rows")[0].GetProperty("path").GetString()!;
        File.Delete(path);
        var id = await Start(preview);
        await FinishOwned(id);
        var entries = await Entries(id);
        Assert.Contains(entries, e => e.Path == path && e.Stage == "skipped" && e.ReasonCode == "source_missing");
        Assert.Single(entries, e => e.Stage == "completed");
        Assert.Equal(2, Probes); // no re-probe of the disappeared file
    }

    [Fact]
    public async Task DisappearedChildDirectoryDoesNotStopTheLazyWalk()
    {
        await CreateFiles(1, "child");
        using var walker = _archive.WalkDirectory(Source, true, _archive.GetSettings(), CancellationToken.None).GetEnumerator();
        Assert.True(walker.MoveNext()); Assert.Null(walker.Current); // yielded before opening child
        Directory.Delete(Path.Combine(Source, "child"), true);
        Assert.False(walker.MoveNext());
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task VideosRemovedFromAnOpenDirectoryListingRemainExplainableSkips()
    {
        await CreateFiles(40);
        using var walker = _archive.WalkDirectory(Source, true, _archive.GetSettings(), CancellationToken.None).GetEnumerator();
        Assert.True(walker.MoveNext());
        foreach (var path in Directory.EnumerateFiles(Source)) File.Delete(path);
        while (walker.MoveNext())
        {
            if (walker.Current is not { } path) continue;
            var preview = Json(await _archive.PreviewAsync(new() { FilePaths = [path] }, CancellationToken.None));
            var row = Assert.Single(preview.GetProperty("rows").EnumerateArray());
            Assert.Equal("source_missing", row.GetProperty("reasonCode").GetString());
        }
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task LegacyCheckpointInADeletedSubdirectorySkipsAndTheRestContinues()
    {
        var id = await SeedLegacy(2); string missingId;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entry = await db.ArchiveEntries.OrderBy(e => e.Path).FirstAsync(); missingId = entry.Id;
            File.Delete(entry.Path);
            entry.Path = entry.InputPath = entry.OutputPath = Path.Combine(Source, "gone", "SUB", "old.mkv");
            (await db.MediaFiles.SingleAsync(f => f.ArchiveEntryId == entry.Id)).FilePath = entry.Path;
            await db.SaveChangesAsync();
        }
        await _archive.ShutdownAsync(); _archive = NewService(); await _archive.InitializeAsync();
        await _archive.ContinueAsync(id); await FinishOwned(id);
        Assert.Equal("source_missing", (await _archive.GetEntryAsync(missingId))!.ReasonCode);
        Assert.Single(await Entries(id), e => e.Stage == "completed");
        Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task UnavailableRootAtDispatchStillPausesInsteadOfSkippingItsFiles()
    {
        await CreateFiles(1); var id = await Start(await Preview()); var entry = Assert.Single(await Entries(id));
        Directory.Move(Source, Source + "-offline");
        try
        {
            var calls = 0;
            await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path },
                (_, _) => { calls++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
            var saved = (await _archive.GetEntryAsync(entry.Id))!;
            Assert.Equal("paused_storage", saved.Stage); Assert.NotEqual("source_missing", saved.ReasonCode);
            Assert.Equal(0, calls);
        }
        finally { Directory.Move(Source + "-offline", Source); }
    }

    [Fact]
    public async Task PermissionFailureCannotTurnAMissingSourceIntoASkip()
    {
        if (OperatingSystem.IsWindows()) return;
        await CreateFiles(1); var id = await Start(await Preview()); var entry = Assert.Single(await Entries(id));
        File.Delete(entry.Path);
        var mode = File.GetUnixFileMode(Source); File.SetUnixFileMode(Source, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(Source));
            await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path },
                (_, _) => throw new InvalidOperationException("must not encode"), _ => Task.CompletedTask, CancellationToken.None);
            var saved = (await _archive.GetEntryAsync(entry.Id))!;
            Assert.Equal("paused_storage", saved.Stage); Assert.NotEqual("source_missing", saved.ReasonCode);
        }
        finally { File.SetUnixFileMode(Source, mode); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCandidateAndRealIoFailureDoNotBecomeSourceMissing(bool ioFailure)
    {
        await CreateFiles(1); var id = await Start(await Preview()); var entry = Assert.Single(await Entries(id));
        await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path },
            (_, _) => ioFailure ? Task.FromException(new IOException("Input/output error")) : Task.CompletedTask,
            _ => Task.CompletedTask, CancellationToken.None);
        var saved = (await _archive.GetEntryAsync(entry.Id))!;
        Assert.Equal("paused_storage", saved.Stage); Assert.NotEqual("source_missing", saved.ReasonCode);
        Assert.Equal("original", await ReadMediaAsync(entry.Path));
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("published")]
    [InlineData("restore")]
    [InlineData("backup")]
    public async Task MissingSourceWithAnUnresolvedTransactionNeverSkips(string kind)
    {
        var id = await SeedLegacy(1); string entryId; string journal;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entry = await db.ArchiveEntries.SingleAsync(); entryId = entry.Id; File.Delete(entry.Path);
            var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, ArchiveService.Json)!;
            if (kind == "rollback") entry.RollbackPath = Path.Combine(Source, $".snacks-archive-{entry.Id}.original.mkv");
            if (kind == "published") media.Transfer = new() { Published = true };
            if (kind == "restore") entry.RestoreTempPath = Path.Combine(Source, $".snacks-archive-{entry.Id}.restore.mkv");
            if (kind == "backup") media.Transfer = new() { BackupReadyPath = entry.BackupPath };
            entry.MediaJson = journal = JsonSerializer.Serialize(media, ArchiveService.Json); await db.SaveChangesAsync();
        }
        await _archive.ContinueAsync(id);
        await Until(async () => (await _archive.GetEntryAsync(entryId))!.Stage is "failed" or "paused_storage" ||
            Json(await _archive.JobAsync(id)).GetProperty("status").GetString() == "paused_storage");
        var saved = (await _archive.GetEntryAsync(entryId))!;
        Assert.NotEqual("skipped", saved.Stage); Assert.NotEqual("source_missing", saved.ReasonCode);
        Assert.Equal(journal, saved.MediaJson); Assert.Equal(0, Probes);
    }

    [Fact]
    public async Task KnownWaitingConflictIsHeldWithoutRecoveryAndCannotBeAutomaticallyRetried()
    {
        var id = await SeedLegacy(2); ArchiveEntry before;
        await using (var db = await _db.CreateDbContextAsync())
        {
            before = await db.ArchiveEntries.OrderBy(e => e.Path).FirstAsync();
            before.Stage = "waiting_retry"; before.FolderJobId = id; before.ReasonCode = "transaction_conflict"; before.Error = "preserve original conflict explanation";
            before.RollbackPath = Path.Combine(Source, $".snacks-archive-{before.Id}.original.mkv");
            File.Move(before.Path, before.RollbackPath); // recovery would wrongly move this without the conflict guard
            await db.SaveChangesAsync();
        }
        await _archive.ShutdownAsync(); _archive = NewService(); await _archive.InitializeAsync();
        await _archive.ContinueAsync(id); await FinishOwned(id);
        var held = (await _archive.GetEntryAsync(before.Id))!;
        Assert.Equal("failed", held.Stage); Assert.Equal(before.ReasonCode, held.ReasonCode); Assert.Equal(before.Error, held.Error);
        Assert.Equal(before.RollbackPath, held.RollbackPath); Assert.Equal(before.MediaJson, held.MediaJson);
        Assert.False(File.Exists(before.Path)); Assert.Equal("original", await ReadMediaAsync(before.RollbackPath!));
        before.Stage = held.Stage; before.UpdatedAt = held.UpdatedAt;
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(held));
        var saved = JsonSerializer.Serialize(held);
        await _archive.ContinueAsync(id); await FinishOwned(id);
        Assert.Equal(saved, JsonSerializer.Serialize(await _archive.GetEntryAsync(before.Id)));
        Assert.Single(await Entries(id), e => e.Stage == "completed"); Assert.Equal(0, Probes);
    }
}
