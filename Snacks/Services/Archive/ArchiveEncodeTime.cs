using Snacks.Data;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    // Telemetry must never change encoder success/failure or transaction behavior.
    public async Task RecordEncodeTimeAsync(string entryId, string segmentId, double seconds, bool finished)
    {
        try { await new ArchiveEncodeTimingStore(_db).CheckpointAsync(entryId, segmentId, seconds, finished); }
        catch (Exception ex) { Log.Warning(ex, "Could not persist archive encode timing for {EntryId}", entryId); }
    }

    private async Task PreserveUnknownPriorEncodeTimeAsync(ArchiveEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Parameters)) return;
        try { await new ArchiveEncodeTimingStore(_db).MarkLegacyAttemptIfUnrecordedAsync(entry.Id); }
        catch (Exception ex) { Log.Warning(ex, "Could not preserve unknown prior archive timing for {EntryId}", entry.Id); }
    }
}
