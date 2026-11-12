using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Data;

public partial class EncodeHistoryRepository
{
    // A read-only UNION, not a backfill. Filter/aggregate/limit stays in SQLite.
    // Archive IDs are the WorkItem IDs stored in native JobId; never deduplicate
    // by path, since separate encodes of the same path are distinct operations.
    // The native-side exclusion probes ArchiveEntries' primary key.
    internal static IQueryable<EncodeHistory> AnalyticsQuery(SnacksDbContext context) =>
        context.EncodeHistory.FromSqlRaw("""
            SELECT n.Id, n.JobId, n.FilePath, n.FileName, n.OriginalSizeBytes,
                   n.EncodedSizeBytes, n.BytesSaved, n.OriginalCodec, n.EncodedCodec,
                   n.OriginalBitrateKbps, n.EncodedBitrateKbps, n.DurationSeconds,
                   n.EncodeSeconds, n.DeviceId, n.NodeId, n.NodeHostname, n.WasRemote,
                   n.Is4K, n.StartedAt, n.CompletedAt, n.Outcome, n.Kind,
                   n.AdvancedProfileId, n.AdvancedProfileName, n.AdvancedRuleName, n.OutputPath
            FROM EncodeHistory AS n
            WHERE NOT EXISTS (SELECT 1 FROM ArchiveEntries AS a WHERE a.Id = n.JobId)
            UNION ALL
            SELECT 0 AS Id, a.Id AS JobId, a.Path AS FilePath, '' AS FileName,
                   a.SourceBytes AS OriginalSizeBytes, COALESCE(a.OutputBytes, 0) AS EncodedSizeBytes,
                   CASE WHEN a.OutputBytes > 0 AND a.SourceBytes > a.OutputBytes
                        THEN a.SourceBytes - a.OutputBytes ELSE 0 END AS BytesSaved,
                   a.Codec AS OriginalCodec, 'hevc' AS EncodedCodec,
                   CAST(a.SourceMbps * 1000 AS INTEGER) AS OriginalBitrateKbps,
                   0 AS EncodedBitrateKbps,
                   COALESCE(CAST(json_extract(CASE WHEN json_valid(a.MediaJson) THEN a.MediaJson ELSE NULL END, '$.duration') AS REAL), 0) AS DurationSeconds,
                   COALESCE(t.EncodeSeconds, 0.0) AS EncodeSeconds,
                   CASE WHEN a.EncoderMode = 'hevc_videotoolbox' THEN 'apple'
                        WHEN a.EncoderMode = 'libx265' THEN 'cpu' ELSE 'unknown' END AS DeviceId,
                   'archive-local' AS NodeId, 'Local archive' AS NodeHostname, 0 AS WasRemote,
                   CASE WHEN json_extract(CASE WHEN json_valid(a.MediaJson) THEN a.MediaJson ELSE NULL END, '$.width') >= 3840
                          OR json_extract(CASE WHEN json_valid(a.MediaJson) THEN a.MediaJson ELSE NULL END, '$.height') >= 2160
                        THEN 1 ELSE 0 END AS Is4K,
                   '0001-01-01 00:00:00' AS StartedAt,
                   COALESCE(a.CompletedAt, '0001-01-01 00:00:00') AS CompletedAt,
                   'Completed' AS Outcome, 0 AS Kind,
                   NULL AS AdvancedProfileId, NULL AS AdvancedProfileName, NULL AS AdvancedRuleName, a.OutputPath
            FROM ArchiveEntries AS a
            LEFT JOIN (
                SELECT EntryId, CASE WHEN MIN(Finished) = 1 THEN SUM(EncodeSeconds) ELSE 0.0 END AS EncodeSeconds
                FROM ArchiveEncodeTimings GROUP BY EntryId
            ) AS t ON t.EntryId = a.Id
            WHERE a.Stage = 'completed'
            """).AsNoTracking();

    // The projection deliberately does not load ProbeJson or inspect media paths.
    public async Task<ArchiveDashboardActivity> GetArchiveActivityAsync(MediaKind? kind = null)
    {
        if (kind == MediaKind.Music) return new();
        using var db = await _contextFactory.CreateDbContextAsync();
        var stages = await db.ArchiveEntries.AsNoTracking().GroupBy(e => e.Stage)
            .Select(g => new { Stage = g.Key, Count = g.Count() }).ToListAsync();
        int Count(params string[] names) => stages.Where(x => names.Contains(x.Stage)).Sum(x => x.Count);
        var activeStages = new[] { "encoding", "muxing", "backing_up", "committing", "restoring" };
        var active = await db.ArchiveEntries.AsNoTracking().Where(e => activeStages.Contains(e.Stage))
            .OrderBy(e => e.CreatedAt).Take(20)
            .Select(e => new ArchiveDashboardItem { Id = e.Id, Path = e.Path, Stage = e.Stage, Progress = e.Progress,
                SourceBytes = e.SourceBytes, Encoder = e.EncoderMode }).ToListAsync();
        return new() { ActiveCount = Count(activeStages), Queued = Count("queued"), Waiting = Count("waiting", "waiting_retry"),
            Paused = Count("paused_storage", "interrupted"), Active = active };
    }

    private static List<EncodeHistory> DisplayNames(List<EncodeHistory> rows)
    {
        foreach (var row in rows) if (string.IsNullOrEmpty(row.FileName)) row.FileName = Path.GetFileName(row.FilePath);
        return rows;
    }
}

public sealed class ArchiveDashboardActivity
{
    public int ActiveCount { get; set; }
    public int Queued { get; set; }
    public int Waiting { get; set; }
    public int Paused { get; set; }
    public List<ArchiveDashboardItem> Active { get; set; } = [];
}
public sealed class ArchiveDashboardItem
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Stage { get; set; } = "";
    public int Progress { get; set; }
    public long SourceBytes { get; set; }
    public string Encoder { get; set; } = "";
}
