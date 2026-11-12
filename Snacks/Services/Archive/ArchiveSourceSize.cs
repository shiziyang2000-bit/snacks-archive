using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    // Fixed admission rule; original job IDs and frozen encoding rules are unchanged.
    internal const long MinimumSourceBytes = 10_000_000;
    internal const string SmallSourceCode = "source_below_10_mb";
    internal const string SmallSourceReason = "原文件小于 10 MB（10,000,000 字节），保持原样并跳过，不编码、不封装、不备份、不替换。";

    private static bool IsSmallSource(string path, string root, bool canSkipMissing, out long bytes)
    {
        // Unknown size is never treated as zero. Only confirmed missing sources
        // get their separate source_missing outcome; access/I/O errors still pause.
        bytes = ReadSourceMetadata(path, root, canSkipMissing, () => new FileInfo(path).Length);
        return bytes < MinimumSourceBytes;
    }

    private async Task<bool> SkipSmallPendingEntryAsync(ArchiveEntry entry, string root)
    {
        // Recovery runs first at admission. A safely restored source with no
        // outstanding original-file journal can skip; unresolved transactions cannot.
        if (entry.Stage is "completed" or "restored" || NeedsTransactionReview(entry)) return false;
        if (!IsSmallSource(entry.InputPath, root, CanSkipMissingSource(entry), out var bytes)) return false;
        entry.SourceBytes = bytes; entry.ReasonCode = SmallSourceCode; entry.Progress = 0;
        await StageAsync(entry, "skipped", SmallSourceReason);
        await SetMediaStatusAsync(entry, MediaFileStatus.Skipped);
        return true;
    }
}
