using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Models;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Integration;

public sealed class ArchiveDashboardTests
{
    [Fact]
    public async Task AllDashboardReadsMergeDurableArchiveHistoryWithoutDuplicatesOrInventedTime()
    {
        using var fixture = new InMemoryDb(); var now = DateTime.UtcNow;
        using (var db = fixture.Factory.CreateDbContext())
        {
            db.ArchiveJobs.Add(new() { Id = "folder" });
            db.ArchiveEntries.AddRange(
                Entry("a", "completed", 1000, 400, now, "libx265", "{\"duration\":100,\"width\":3840,\"height\":2160}"),
                Entry("old", "completed", 2000, 1000, now.AddDays(-60), "hevc_videotoolbox"),
                Entry("unknown", "completed", 500, null, now, "", "malformed legacy json"),
                Entry("restored", "restored", 9000, 10, now), Entry("failed", "failed", 9000, 10, now), Entry("small", "skipped", 9000, null, now));
            db.EncodeHistory.AddRange(
                Native("ordinary", 1000, 500, 500, now),
                Native("a", 999999, 1, 999998, now),
                Native("restored", 999999, 1, 999998, now),
                Native("music", 800, 100, 700, now, MediaKind.Music));
            await db.SaveChangesAsync();
        }
        var repo = new EncodeHistoryRepository(fixture.Factory);
        var summary = await repo.GetSummaryAsync();
        Assert.Equal(5, summary.TotalEncodes); Assert.Equal(2800, summary.TotalBytesSaved); Assert.Equal(5300, summary.TotalOriginalBytes);
        Assert.Equal(4, summary.VideoEncodes); Assert.Equal(1, summary.MusicEncodes); Assert.Equal(3, summary.UnknownEncodeTimeEncodes);
        Assert.Equal(20, summary.TotalEncodeSeconds); Assert.Equal(1, summary.FourKEncodes);
        Assert.Equal(700, (await repo.GetSummaryAsync(MediaKind.Music)).TotalBytesSaved);
        var days = await repo.GetSavingsOverTimeAsync(30); Assert.Equal(30, days.Count); Assert.Equal(4, days.Sum(x => x.Encodes)); Assert.Equal(1800, days.Sum(x => x.BytesSaved));
        var devices = await repo.GetDeviceUtilizationAsync(30); Assert.Equal(4, devices.Sum(x => x.Encodes)); Assert.Equal(1800, devices.Sum(x => x.BytesSaved));
        Assert.Equal(2, devices.Sum(x => x.UnknownEncodeTimeEncodes));
        Assert.Equal(1800, (await repo.GetCodecMixAsync(30)).Sum(x => x.BytesSaved));
        Assert.Equal(1800, (await repo.GetNodeThroughputAsync(30)).Sum(x => x.BytesSaved));
        var recent = await repo.GetRecentAsync(25); Assert.Equal(5, recent.Count); Assert.Equal(5, recent.Select(x => x.JobId).Distinct().Count());
        var archived = Assert.Single(recent, x => x.JobId == "a");
        Assert.Equal("same.mkv", archived.FileName); Assert.Equal(0, archived.EncodeSeconds); Assert.Equal(DateTime.MinValue, archived.StartedAt); Assert.Equal("cpu", archived.DeviceId);
        Assert.Equal(4, (await repo.GetTopSavingsAsync(10, 30)).Count); Assert.Single(await repo.GetTopSavingsAsync(10, 30, MediaKind.Music));
        // A fresh repository/context reads the same durable counts after refresh/restart.
        Assert.Equal(summary.TotalBytesSaved, (await new EncodeHistoryRepository(fixture.Factory).GetSummaryAsync()).TotalBytesSaved);
        using var check = fixture.Factory.CreateDbContext(); Assert.Equal(4, await check.EncodeHistory.CountAsync()); Assert.Equal(6, await check.ArchiveEntries.CountAsync());
        Assert.Equal("completed", (await check.ArchiveEntries.FindAsync("a"))!.Stage);
    }

    [Fact]
    public async Task RestoreRemovesCurrentReductionAndNativeDuplicateCannotResurrectIt()
    {
        using var fixture = new InMemoryDb();
        using (var db = fixture.Factory.CreateDbContext())
        {
            db.ArchiveJobs.Add(new() { Id = "folder" }); db.ArchiveEntries.Add(Entry("a", "completed", 1000, 400, DateTime.UtcNow));
            db.EncodeHistory.Add(Native("a", 1000, 400, 600, DateTime.UtcNow)); await db.SaveChangesAsync();
        }
        var repo = new EncodeHistoryRepository(fixture.Factory); Assert.Equal(600, (await repo.GetSummaryAsync()).TotalBytesSaved);
        using (var db = fixture.Factory.CreateDbContext()) { (await db.ArchiveEntries.FindAsync("a"))!.Stage = "restored"; await db.SaveChangesAsync(); }
        Assert.Equal(0, (await repo.GetSummaryAsync()).TotalEncodes); Assert.Empty(await repo.GetRecentAsync(25));
        // Existing native-history clearing never mutates archive/restore records.
        await repo.ClearAllAsync(); using var check = fixture.Factory.CreateDbContext(); Assert.Equal("restored", (await check.ArchiveEntries.SingleAsync()).Stage);
    }

    [Fact]
    public async Task ActivityUsesDatabaseStagesIsBoundedAndMusicDoesNotShowArchiveVideo()
    {
        using var fixture = new InMemoryDb();
        using (var db = fixture.Factory.CreateDbContext())
        {
            db.ArchiveJobs.Add(new() { Id = "folder" });
            for (var i = 0; i < 24; i++) { var e = Entry("active" + i, "encoding", 1000, null, DateTime.UtcNow); e.Progress = 37; db.ArchiveEntries.Add(e); }
            db.ArchiveEntries.Add(Entry("waiting", "waiting", 1000, null, DateTime.UtcNow)); await db.SaveChangesAsync();
        }
        var repo = new EncodeHistoryRepository(fixture.Factory); var result = await repo.GetArchiveActivityAsync();
        Assert.Equal(24, result.ActiveCount); Assert.Equal(20, result.Active.Count); Assert.Equal(1, result.Waiting); Assert.All(result.Active, x => Assert.Equal(37, x.Progress));
        Assert.Empty((await repo.GetArchiveActivityAsync(MediaKind.Music)).Active);
    }

    private static ArchiveEntry Entry(string id, string stage, long source, long? output, DateTime at, string encoder = "", string media = "{}") =>
        new() { Id = id, JobId = "folder", Path = "/not-accessed/same.mkv", InputPath = "/not-accessed/same.mkv", OutputPath = "/not-accessed/final.mkv",
            Stage = stage, SourceBytes = source, OutputBytes = output, CompletedAt = at, CreatedAt = at.AddDays(-2), EncoderMode = encoder, MediaJson = media, Codec = "h264" };
    private static EncodeHistory Native(string id, long source, long output, long saved, DateTime at, MediaKind kind = MediaKind.Video) =>
        new() { JobId = id, FilePath = "/not-accessed/same.mkv", FileName = "same.mkv", OriginalSizeBytes = source, EncodedSizeBytes = output,
            BytesSaved = saved, CompletedAt = at, StartedAt = at.AddSeconds(-10), EncodeSeconds = 10, DurationSeconds = 30,
            DeviceId = "cpu", NodeHostname = "native", EncodedCodec = kind == MediaKind.Music ? "aac" : "hevc", Kind = kind };
}
