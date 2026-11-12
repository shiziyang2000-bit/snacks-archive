using Microsoft.EntityFrameworkCore;

namespace Snacks.Data;

public sealed class ArchiveEncodeTimingStore(IDbContextFactory<SnacksDbContext> factory)
{
    public async Task CheckpointAsync(string entryId, string segmentId, double seconds, bool finished)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ArchiveEncodeTimings (Id, EntryId, EncodeSeconds, Finished, Source)
            VALUES ({segmentId}, {entryId}, {seconds}, {finished}, 'measured')
            ON CONFLICT(Id) DO UPDATE SET
                EncodeSeconds = MAX(ArchiveEncodeTimings.EncodeSeconds, excluded.EncodeSeconds),
                Finished = MAX(ArchiveEncodeTimings.Finished, excluded.Finished)
            WHERE ArchiveEncodeTimings.EntryId = excluded.EntryId AND ArchiveEncodeTimings.Source = 'measured'
            """);
    }

    public async Task MarkLegacyAttemptIfUnrecordedAsync(string entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var id = "legacy-unknown:" + entryId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO ArchiveEncodeTimings (Id, EntryId, EncodeSeconds, Finished, Source)
            SELECT {id}, {entryId}, 0, 0, 'legacy-unknown'
            WHERE NOT EXISTS (SELECT 1 FROM ArchiveEncodeTimings WHERE EntryId = {entryId})
            """);
    }
}
