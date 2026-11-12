namespace Snacks.Models;

/// <summary>One actual encoder invocation. Absolute checkpoints make retries idempotent.</summary>
public sealed class ArchiveEncodeTiming
{
    public string Id { get; set; } = "";
    public string EntryId { get; set; } = "";
    public double EncodeSeconds { get; set; }
    public bool Finished { get; set; }
    public string Source { get; set; } = "measured";
}
