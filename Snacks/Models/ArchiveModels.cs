using System.Text.Json;

namespace Snacks.Models;

public sealed class ArchiveSettings
{
    public string Mode { get; set; } = "custom";
    public double TargetMbps { get; set; } = 20;
    public string OutputMode { get; set; } = "inPlace";
    public string? OutputDirectory { get; set; }
    public bool BackupEnabled { get; set; } = true;
    public string BackupLocation { get; set; } = "sameVolume";
    public string? BackupDirectory { get; set; }
    public int BackupReminderDays { get; set; } = 3;
    public string? CacheDirectory { get; set; }
    public double CacheLimitGb { get; set; } = 5;
    public int Concurrency { get; set; } = 10;
    public int Revision { get; set; } = 1;
    public int RuleVersion { get; set; } = 1;
}

// Durable metadata attached to MediaFiles, never a second runnable queue.
public sealed class ArchiveJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestId { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string RulesSnapshotJson { get; set; } = "{}";
    // A scoped choice for future discoveries; the original frozen rules stay intact.
    public string? PendingOutputPolicyJson { get; set; }
    public string SourceRoot { get; set; } = "";
    // null preserves the existing explicitly selected-file task contract.
    public bool? ScanRecursive { get; set; }
    public bool ScanComplete { get; set; } = true;
    public int? FolderConcurrency { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
    public bool ConfirmedNoBackup { get; set; }
    public bool ConfirmedUseCurrentOutput { get; set; }
}

public sealed class ArchiveEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    // Normalized path, not a media/content hash; unique only within a directory job.
    public string? DiscoveryKey { get; set; }
    // Scheduler ownership only. JobId still owns the original frozen rules/transaction.
    public string? FolderJobId { get; set; }
    public string Path { get; set; } = "";
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string? BackupPath { get; set; }
    public string SourceHash { get; set; } = "";
    public string DestinationHash { get; set; } = "";
    public string? OutputHash { get; set; }
    public string Stage { get; set; } = "queued";
    public string? Error { get; set; }
    public string? ReasonCode { get; set; }
    public string Codec { get; set; } = "";
    public double SourceMbps { get; set; }
    public double TargetMbps { get; set; }
    public double? ActualMbps { get; set; }
    public long SourceBytes { get; set; }
    public long? OutputBytes { get; set; }
    public string MediaJson { get; set; } = "{}";
    public string EncoderMode { get; set; } = "";
    public string? Parameters { get; set; }
    public string? TempOutputPath { get; set; }
    public string? RollbackPath { get; set; }
    public string? RestoreTempPath { get; set; }
    public string? RestoreDisplacedPath { get; set; }
    public int Progress { get; set; }
    public double? Score { get; set; }
    public string ScoreStatus { get; set; } = "pending";
    public string SamplesJson { get; set; } = "[]";
    public string? ScoreError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? RestoredAt { get; set; }
}

public sealed class ArchiveCacheRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EntryId { get; set; } = "";
    public string Path { get; set; } = "";
    public double Start { get; set; }
    public double Duration { get; set; }
    public long Bytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ArchiveFolderStartRequest
{
    public string DirectoryPath { get; set; } = "";
}

public sealed class ArchivePreviewRequest
{
    public string? DirectoryPath { get; set; }
    public List<string>? FilePaths { get; set; }
    public List<string>? EntryIds { get; set; }
    public bool Recursive { get; set; } = true;
    public string Intent { get; set; } = "normal";
}
public sealed class ArchiveStartRequest
{
    public string RequestId { get; set; } = "";
    public string PreviewId { get; set; } = "";
    public List<string>? SelectedIds { get; set; }
    public bool ConfirmedNoBackup { get; set; }
    public bool ConfirmedUseCurrentOutput { get; set; }
}
public sealed class ArchiveSelectionRequest
{
    public List<string>? EntryIds { get; set; }
    public string? JobId { get; set; }
}
public sealed class ArchiveRestoreRequest
{
    public string RequestId { get; set; } = "";
    public string RestorePreviewId { get; set; } = "";
    public bool Confirmed { get; set; }
}
public sealed class ArchivePreviewRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Codec { get; set; } = "";
    public double SourceMbps { get; set; }
    public double TargetMbps { get; set; }
    public long SourceBytes { get; set; }
    public string Action { get; set; } = "skip";
    public string ReasonCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? HistoryState { get; set; }
    public string? BackupPath { get; set; }
    public string? OutputPath { get; set; }
    public string? EncoderMode { get; set; }
    public bool RequiresCurrentOutputConfirmation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string InputPath { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string SourceHash { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DestinationHash { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public ArchiveMediaInfo? Media { get; set; }
}
public sealed class ArchiveMediaInfo
{
    public string Codec { get; set; } = "";
    public string PixelFormat { get; set; } = "";
    public string Profile { get; set; } = "";
    public int VideoIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public double Fps { get; set; }
    public double VideoMbps { get; set; }
    public long VideoPackets { get; set; }
    public bool VideoBitrateEstimated { get; set; }
    public long Bytes { get; set; }
    public bool Alpha { get; set; }
    public string ProbeJson { get; set; } = "{}";
    public string Encoder { get; set; } = "libx265";
    // Cheap filesystem identity for history matching; never reads media contents.
    public ArchiveFileIdentity? PathIdentity { get; set; }
    // Optional journal fields in the existing JSON column keep old jobs readable.
    // These record completed filesystem operations, never content fingerprints.
    public ArchiveTransferState? Transfer { get; set; }
    // Explicitly adopted only by pending entries without an existing file transaction.
    public ArchiveOutputPolicy? OutputPolicy { get; set; }
}
public sealed class ArchiveOutputPolicy
{
    public string OutputMode { get; set; } = "inPlace";
    public string? OutputDirectory { get; set; }
    public bool BackupEnabled { get; set; }
    public string BackupLocation { get; set; } = "sameVolume";
    public string? BackupDirectory { get; set; }
}
public sealed record ArchiveFileIdentity(long Bytes, long LastWriteUtcTicks);
public sealed class ArchiveTransferState
{
    public string? BackupReadyPath { get; set; }
    public bool Published { get; set; }
    public bool RestorePublished { get; set; }
}
public sealed record ArchiveSample(double Start, double Duration, double? Score = null);
public sealed class ArchiveException(string code, string message, object? details = null, bool storage = false) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
    public bool Storage { get; } = storage;
}
