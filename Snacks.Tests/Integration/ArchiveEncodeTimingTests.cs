using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Models;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Integration;

public sealed class ArchiveEncodeTimingTests
{
    [Fact]
    public async Task MultiplePassesAndResumedAttemptsAreDurableAndCheckpointsNeverDoubleCount()
    {
        using var fixture = new InMemoryDb();
        var completed = DateTime.UtcNow;
        using (var db = fixture.Factory.CreateDbContext())
        {
            db.ArchiveEntries.Add(new() { Id = "a", JobId = "folder", Stage = "completed", Path = "/metadata-only/a.mp4",
                SourceBytes = 1000, OutputBytes = 400, CompletedAt = completed, MediaJson = "{\"duration\":300}" });
            await db.SaveChangesAsync();
        }
        var store = new ArchiveEncodeTimingStore(fixture.Factory);
        await store.CheckpointAsync("a", "process-did-not-start", 0, false);
        await store.CheckpointAsync("a", "process-did-not-start", 0, true);
        await store.CheckpointAsync("a", "pass1", 10, false);
        await store.CheckpointAsync("a", "pass1", 30, true);
        await store.CheckpointAsync("a", "pass1", 30, true);
        await store.CheckpointAsync("a", "pass1", 20, false); // late checkpoint cannot roll back the terminal record
        await new ArchiveEncodeTimingStore(fixture.Factory).CheckpointAsync("a", "pass2", 70, true);
        var repo = new EncodeHistoryRepository(fixture.Factory);
        var summary = await repo.GetSummaryAsync();
        Assert.Equal(100, summary.TotalEncodeSeconds); Assert.Equal(300, summary.TimedContentSeconds);
        Assert.Equal(1, summary.TotalEncodes); Assert.Equal(600, summary.TotalBytesSaved); Assert.Equal(0, summary.UnknownEncodeTimeEncodes);
        Assert.Equal(100, (await repo.GetRecentAsync(25)).Single().EncodeSeconds);
        Assert.Equal(100, (await repo.GetSavingsOverTimeAsync(7)).Sum(x => x.EncodeSeconds));
        using var check = fixture.Factory.CreateDbContext();
        var entry = await check.ArchiveEntries.SingleAsync();
        Assert.Equal("completed", entry.Stage); Assert.Equal(completed, entry.CompletedAt); Assert.Equal(1000, entry.SourceBytes); Assert.Equal(400, entry.OutputBytes);
        Assert.Equal(3, await check.ArchiveEncodeTimings.CountAsync());
        Assert.False(check.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task CrashOrLegacyUnknownCoverageCannotBecomeKnownAfterAResumedPassAndAverageUsesOnlyPairedFiles()
    {
        using var fixture = new InMemoryDb();
        using (var db = fixture.Factory.CreateDbContext())
        {
            db.ArchiveEntries.AddRange(new ArchiveEntry { Id = "crashed", Stage = "completed", SourceBytes = 1000, OutputBytes = 400,
                CompletedAt = DateTime.UtcNow, MediaJson = "{\"duration\":9000}" },
                new ArchiveEntry { Id = "legacy", Stage = "completed", CompletedAt = DateTime.UtcNow, MediaJson = "{\"duration\":8000}" },
                new ArchiveEntry { Id = "recovered", Stage = "completed", CompletedAt = DateTime.UtcNow, MediaJson = "{\"duration\":60}" },
                new ArchiveEntry { Id = "duration-unknown", Stage = "completed", CompletedAt = DateTime.UtcNow });
            db.EncodeHistory.Add(new() { JobId = "native", EncodeSeconds = 10, DurationSeconds = 30, CompletedAt = DateTime.UtcNow });
            db.ArchiveEncodeTimings.Add(new() { Id = "recovered", EntryId = "recovered", EncodeSeconds = 20, Finished = true, Source = "x265-log" });
            await db.SaveChangesAsync();
        }
        var store = new ArchiveEncodeTimingStore(fixture.Factory);
        await store.CheckpointAsync("crashed", "before-crash", 40, false);
        await store.CheckpointAsync("crashed", "after-restart", 30, true);
        await store.MarkLegacyAttemptIfUnrecordedAsync("legacy");
        await store.CheckpointAsync("legacy", "new-attempt", 20, true);
        await store.CheckpointAsync("duration-unknown", "measured", 60, true);
        var repo = new EncodeHistoryRepository(fixture.Factory); var summary = await repo.GetSummaryAsync();
        Assert.Equal(5, summary.TotalEncodes); Assert.Equal(2, summary.UnknownEncodeTimeEncodes);
        Assert.Equal(90, summary.TotalEncodeSeconds); Assert.Equal(90, summary.TimedContentSeconds); Assert.Equal(1, summary.RecoveredEncodeTimeEncodes);
        Assert.Equal(30, summary.SpeedSampleEncodeSeconds);
        Assert.Equal(90, (await repo.GetDeviceUtilizationAsync(7)).Sum(x => x.ContentSeconds));
        Assert.Equal(30, (await repo.GetDeviceUtilizationAsync(7)).Sum(x => x.SpeedSampleEncodeSeconds));
        Assert.Equal(2, (await repo.GetNodeThroughputAsync(7)).Sum(x => x.UnknownEncodeTimeEncodes));
        Assert.Equal(0, (await repo.GetSummaryAsync(MediaKind.Music)).RecoveredEncodeTimeEncodes);
        await repo.ClearAllAsync(); using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(6, await check.ArchiveEncodeTimings.CountAsync());
    }
}
