using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    internal const int DirectoryBatchSize = 32;
    internal const int DirectoryRefillSize = 8;
    internal const int DirectoryVisitLimit = 256;
    internal const int JobDetailLimit = 100;
    private const int MaxDirectoryDepth = 64;
    private readonly SemaphoreSlim _directoryProducer = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _scanTasks = new();

    // AppleDouble names can be returned by a NAS directory listing and disappear
    // when the corresponding video is replaced. Reject them without a stat call.
    private static bool IsMacMetadataPath(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(part =>
            part.StartsWith("._", StringComparison.Ordinal) ||
            part.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".AppleDouble", StringComparison.OrdinalIgnoreCase));

    private bool IsExcludedPath(string path, ArchiveSettings rules) =>
        IsMacMetadataPath(path) || Within(path, _files.GetWorkingDirectory()) || Within(path, CacheDirectory(rules)) ||
        (rules.BackupDirectory != null && Within(path, rules.BackupDirectory)) ||
        (rules.OutputDirectory != null && Within(path, rules.OutputDirectory)) ||
        path.Split(Path.DirectorySeparatorChar).Any(part =>
            part.Equals(".Snacks-Archive-Originals", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".wuxian-staging", StringComparison.OrdinalIgnoreCase) ||
            part.Contains(".snacks-archive-", StringComparison.OrdinalIgnoreCase));

    // One lazy OS enumerator per depth, never a queue/list of all child directories.
    // null represents a visited non-media item, so preview work is bounded even in
    // a huge directory containing no videos. Storage/access errors still escape.
    internal IEnumerable<string?> WalkDirectory(string root, bool recursive, ArchiveSettings rules, CancellationToken ct)
    {
        RequireSourceDirectory(root);
        if (IsExcludedPath(root, rules)) yield break;
        foreach (var item in Walk(root, 0)) yield return item;

        IEnumerable<string?> Walk(string directory, int depth)
        {
            if (depth >= MaxDirectoryDepth)
                throw new ArchiveException("directory_depth_limit", "目录层级超过 64，扫描已暂停；请核对目录结构后继续。", directory, true);
            using var paths = OpenSourceDirectory(directory, root);
            while (paths != null && MoveNextSourcePath(paths, directory, root))
            {
                ct.ThrowIfCancellationRequested();
                var path = paths.Current;
                if (IsExcludedPath(path, rules)) { yield return null; continue; }
                var attributes = DiscoveryAttributes(path, root);
                // Pass a disappeared video to preparation so its skipped reason is
                // persisted. Non-media and vanished child directories need no probe.
                if (attributes == null) { yield return IsVideoExtension(path) ? Path.GetFullPath(path) : null; continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { yield return null; continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    yield return null;
                    if (recursive) foreach (var child in Walk(path, depth + 1)) yield return child;
                }
                else yield return IsVideoExtension(path) ? Path.GetFullPath(path) : null;
            }
        }
    }

    private static void RequireSourceDirectory(string root)
    {
        if (!Directory.Exists(root)) throw new ArchiveException("source_unavailable", "来源目录不可访问，扫描已暂停；恢复存储后请手动继续。", root, true);
        // A directly selected linked root must not bypass the traversal link policy.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ArchiveException("linked_source", "来源根目录不能是符号链接。", root);
    }

    private async Task<ArchivePreviewRow> PrepareOneAsync(string path, string root, ArchiveSettings rules, ArchivePreviewRequest request, CancellationToken ct, bool stopOnStorage = false)
    {
        var row = new ArchivePreviewRow { Path = path, InputPath = path };
        try { await PrepareRowAsync(row, root, rules, request, ct); }
        catch (OperationCanceledException) { throw; }
        catch (ArchiveException ex) when (ex.Code == MissingSourceCode) { Skip(row, ex.Code, ex.Message); }
        catch (ArchiveException ex) when (ex.Code is SourceLockedCode or SourcePermissionCode) { Block(row, ex.Code, ex.Message); }
        catch (Exception ex) when (stopOnStorage && IsStorageFailure(ex)) { throw; }
        catch (ArchiveException ex) { Block(row, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Block(row, "storage_unavailable", "文件或存储不可访问：" + ex.Message); }
        catch (Exception ex) { Block(row, "analysis_failed", "分析失败：" + ex.Message); }
        return row;
    }

    private async Task<object> PreviewDirectoryAsync(ArchivePreviewRequest request, ArchiveSettings rules, CancellationToken ct)
    {
        var root = Path.GetFullPath(request.DirectoryPath!);
        var rows = new List<ArchivePreviewRow>(DirectoryBatchSize);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = false;
        var visited = 0;
        using (var walker = WalkDirectory(root, request.Recursive, rules, ct).GetEnumerator())
        {
            while (rows.Count < DirectoryBatchSize && visited < DirectoryVisitLimit)
            {
                ct.ThrowIfCancellationRequested();
                if (!walker.MoveNext()) { complete = true; break; }
                visited++;
                if (walker.Current is { } path && seen.Add(path))
                    rows.Add(await PrepareOneAsync(path, root, rules, request, ct, stopOnStorage: true));
            }
        }
        var (id, expires) = CachePreviewIfReady(rules, root, rows, request.Recursive, complete);
        return new
        {
            previewId = id, expiresAt = expires, settingsRevision = rules.Revision,
            scope = "directory", sourceRoot = root, recursive = request.Recursive, scanComplete = complete,
            batchSize = DirectoryBatchSize, visited,
            summary = new { total = rows.Count, ready = rows.Count(r => r.Action == "archive"), skipped = rows.Count(r => r.Action == "skip"), blocked = rows.Count(r => r.Action == "blocked"), totalKnown = complete },
            rows, requiredConfirmations = rules.OutputMode == "inPlace" && !rules.BackupEnabled ? new[] { "noBackup" } : Array.Empty<string>()
        };
    }

    private static string DiscoveryKey(string path) => Path.GetFullPath(path).ToUpperInvariant();
    private static IQueryable<ArchiveEntry> OutstandingDirectoryEntries(SnacksDbContext db) =>
        db.ArchiveEntries.Where(e => LiveStages.Contains(e.Stage));

    private async Task<object> StartDirectoryAsync(SnacksDbContext db, ArchiveStartRequest request, Preview preview, CancellationToken ct)
    {
        if (request.SelectedIds != null) throw new ArchiveException("directory_scope_required", "目录任务会自动处理整个根目录，请使用“开始整个目录”确认，不能仅提交首批勾选项。");
        if (preview.Rules.OutputMode == "inPlace" && !preview.Rules.BackupEnabled && !request.ConfirmedNoBackup)
            throw new ArchiveException("confirmation_required", "整个目录任务不保留原片备份，必须单独确认。", new { confirmation = "noBackup" });
        if (await db.ArchiveJobs.AnyAsync(j => j.ScanRecursive != null && j.SourceRoot == preview.Root && (!j.ScanComplete || j.Status != "completed"), ct))
            throw new ArchiveException("existing_directory_task", "该根目录已有未完成任务，请在历史中继续，不能重复开始。", preview.Root);
        if (await db.ArchiveJobs.CountAsync(j => j.ScanRecursive != null && !j.ScanComplete, ct) >= 4)
            throw new ArchiveException("directory_task_limit", "最多保留 4 个未完成目录扫描，请先完成已有任务。");
        if (await OutstandingDirectoryEntries(db).CountAsync(ct) + preview.Rows.Count(r => r.Action == "archive") > DirectoryBatchSize)
            throw new ArchiveException("directory_queue_full", "目录任务缓冲区已满（最多 32 个未完成文件），请待当前批次完成后开始。");
        RequireSourceDirectory(preview.Root);
        var job = new ArchiveJob
        {
            RequestId = request.RequestId, SourceRoot = preview.Root,
            RulesSnapshotJson = JsonSerializer.Serialize(preview.Rules, Json),
            ConfirmedNoBackup = request.ConfirmedNoBackup, ScanRecursive = preview.ScanRecursive,
            // A fresh bounded walk after confirmation also observes changes since preview.
            ScanComplete = false
        };
        db.ArchiveJobs.Add(job);
        foreach (var row in preview.Rows)
            await AddDirectoryRowAsync(db, job, row, ct);
        await db.SaveChangesAsync(ct);
        _previews.TryRemove(request.PreviewId, out _);
        StartDirectoryProducer(job.Id);
        QueueReady?.Invoke();
        await ChangedAsync(job.Id);
        return new { jobId = job.Id, queued = preview.Rows.Count(r => r.Action == "archive"), skipped = preview.Rows.Count(r => r.Action == "skip"), failed = preview.Rows.Count(r => r.Action == "blocked"), scope = "directory" };
    }

    private async Task AddDirectoryRowAsync(SnacksDbContext db, ArchiveJob job, ArchivePreviewRow row, CancellationToken ct)
    {
        var key = DiscoveryKey(row.Path);
        if (db.ArchiveEntries.Local.Any(e => e.JobId == job.Id && e.DiscoveryKey == key) ||
            await db.ArchiveEntries.AnyAsync(e => e.JobId == job.Id && e.DiscoveryKey == key, ct)) return;
        if (row.Action == "archive")
        {
            try
            {
            var canSkipMissing = row.InputPath == row.Path && row.Media?.Transfer == null;
            RequireNonemptySourceFile(row.Path, job.SourceRoot, canSkipMissing);
            if (row.InputPath != row.Path) RequireNonemptyFile(row.InputPath);
            if (row.Media?.PathIdentity != ReadSourceMetadata(row.Path, job.SourceRoot, canSkipMissing, () => FileIdentity(row.Path)))
                Block(row, "source_changed", "预览后文件已变化，请在历史中明确重试。");
            else if (await db.ArchiveEntries.AnyAsync(e =>
                (EF.Functions.Collate(e.Path, "NOCASE") == row.Path || EF.Functions.Collate(e.InputPath, "NOCASE") == row.InputPath || EF.Functions.Collate(e.OutputPath, "NOCASE") == row.OutputPath) &&
                e.Stage != "completed" && e.Stage != "failed" && e.Stage != "restored" && e.Stage != "skipped", ct))
                Skip(row, "existing_task", "素材或输出位置已属于其他未完成任务。");
            }
            catch (ArchiveException ex) when (ex.Code == MissingSourceCode) { Skip(row, ex.Code, ex.Message); }
        }
        await AddPreparedRowAsync(db, job, row, ct, key);
    }

    private static async Task AddPreparedRowAsync(SnacksDbContext db, ArchiveJob job, ArchivePreviewRow row, CancellationToken ct, string? discoveryKey = null)
    {
        var media = row.Media ?? new ArchiveMediaInfo { PathIdentity = row.ReasonCode != MissingSourceCode && File.Exists(row.Path) ? FileIdentity(row.Path) : null };
        media.OutputPolicy ??= PendingChoice(job);
        var entry = new ArchiveEntry
        {
            JobId = job.Id, DiscoveryKey = discoveryKey, Path = row.Path, InputPath = row.InputPath,
            OutputPath = row.OutputPath ?? row.Path, BackupPath = row.BackupPath,
            SourceHash = row.SourceHash, DestinationHash = row.DestinationHash, Codec = row.Codec,
            SourceMbps = row.SourceMbps, TargetMbps = row.TargetMbps, SourceBytes = row.SourceBytes,
            MediaJson = JsonSerializer.Serialize(media, Json), EncoderMode = row.EncoderMode ?? "",
            FolderJobId = job.ScanRecursive != null || job.FolderConcurrency != null ? job.Id : null,
            Stage = row.Action == "archive" ? "queued" : row.Action == "blocked" ? "failed" : "skipped",
            ReasonCode = row.ReasonCode, Error = row.Action == "archive" ? null : row.Reason,
            ScoreStatus = "not_scored"
        };
        db.ArchiveEntries.Add(entry);
        if (entry.Stage != "queued") return;
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == row.Path, ct);
        if (file == null)
        {
            file = new MediaFile { FilePath = row.Path, Directory = Path.GetDirectoryName(row.Path)!, FileName = Path.GetFileName(row.Path), BaseName = Path.GetFileNameWithoutExtension(row.Path) };
            db.MediaFiles.Add(file);
        }
        file.ArchiveEntryId = entry.Id; file.Status = MediaFileStatus.Queued; file.ForceMux = false;
        file.CreatedAt = DateTime.UtcNow; file.FileSize = row.SourceBytes; file.Bitrate = (long)(row.SourceMbps * 1000);
        file.Codec = row.Codec; file.Width = media.Width; file.Height = media.Height; file.PixelFormat = media.PixelFormat;
        file.Duration = media.Duration; file.Kind = MediaKind.Video; file.IsHevc = row.Codec == "hevc";
    }

    // Created only under _mutation; pause/continue waits for the old worker to settle.
    private void StartDirectoryProducer(string jobId)
    {
        var token = _jobTokens.GetOrAdd(jobId, _ => new CancellationTokenSource()).Token;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await start.Task;
            try { await ProduceDirectoryAsync(jobId, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Warning("Archive directory discovery paused: " + ex.Message);
                try { await PauseJobAsync(jobId, "目录扫描未完成，已停止投放；请核对存储或目录权限后手动继续：" + ex.Message); }
                catch (Exception pauseError) { Log.Error("Cannot persist archive discovery pause: " + pauseError.Message); }
            }
            finally { _scanTasks.TryRemove(jobId, out _); }
        });
        _scanTasks[jobId] = task;
        start.SetResult();
    }

    private async Task ProduceDirectoryAsync(string jobId, CancellationToken ct)
    {
        {
            ArchiveJob job;
            await using (var db = await _db.CreateDbContextAsync(ct)) job = await db.ArchiveJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
            var rules = DiscoveryRules(job);
            if (job.ScanRecursive != null) RequireSourceDirectory(job.SourceRoot);
            using var walker = job.ScanRecursive != null && !job.ScanComplete
                ? WalkDirectory(job.SourceRoot, job.ScanRecursive.Value, rules, ct).GetEnumerator() : null;
            var scanned = walker == null;
            ArchivePreviewRow? pendingRow = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Only the native runnable window consumes capacity. Thousands of
                // paused/waiting legacy records stay solely in the database.
                while (true)
                {
                    await using var waitingDb = await _db.CreateDbContextAsync(ct);
                    if (await OutstandingDirectoryEntries(waitingDb).CountAsync(ct) < DirectoryBatchSize) break;
                    await Task.Delay(100, ct);
                }
                await _directoryProducer.WaitAsync(ct);
                try
                {
                int capacity;
                await using (var capacityDb = await _db.CreateDbContextAsync(ct))
                    capacity = DirectoryBatchSize - await OutstandingDirectoryEntries(capacityDb).CountAsync(ct);
                if (capacity <= 0) continue;
                var budget = Math.Min(DirectoryRefillSize, capacity);
                var admitted = await AdmitWaitingBatchAsync(job, budget, ct);
                if (admitted > 0) { QueueReady?.Invoke(); await RefreshJobAsync(jobId); continue; }
                if (scanned)
                {
                    await _mutation.WaitAsync(ct);
                    try
                    {
                        await using var doneDb = await _db.CreateDbContextAsync(ct);
                        if (job.ScanRecursive != null)
                        {
                            await ClaimFolderBacklogAsync(doneDb, job, retryFailed: false, takePausedOwners: false);
                            if (await doneDb.ArchiveEntries.AnyAsync(e=>e.FolderJobId==jobId && (e.Stage=="waiting" || e.Stage=="waiting_retry"),ct)) continue;
                            var scope = Authoritative(InFolder(doneDb.ArchiveEntries,job.SourceRoot,job.ScanRecursive.Value),doneDb);
                            if (await scope.AnyAsync(e=>e.FolderJobId!=jobId && (ResumeStages.Contains(e.Stage)||LiveStages.Contains(e.Stage)),ct))
                            { await Task.Delay(50,ct); continue; }
                        }
                        await doneDb.ArchiveJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.ScanComplete, true), ct);
                    }
                    finally { _mutation.Release(); }
                    await RefreshJobAsync(jobId);
                    break;
                }
                RequireSourceDirectory(job.SourceRoot);
                var visited = 0;
                var found = 0;
                while (visited < DirectoryVisitLimit && found < budget)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pendingRow == null)
                    {
                    if (!walker!.MoveNext()) { scanned = true; break; }
                    visited++;
                    if (walker.Current is not { } path) continue;
                    await using var readDb = await _db.CreateDbContextAsync(ct);
                    // A path already owned by this run includes failed attempts;
                    // never rediscover/retry it again in this automatic loop.
                    if (await readDb.ArchiveEntries.AnyAsync(e => e.FolderJobId == jobId && e.Path == path, ct)) continue;
                    var history = await Authoritative(readDb.ArchiveEntries.Where(e => EF.Functions.Collate(e.Path, "NOCASE") == path), readDb).FirstOrDefaultAsync(ct);
                    if (history != null && (history.Stage is "completed" or "restored" || LiveStages.Contains(history.Stage))) continue;
                    if (history != null && ResumeStages.Contains(history.Stage))
                    {
                        // A legacy row found after the initial SQL claim keeps its
                        // original transaction/rules, rather than an existing_task skip.
                        await _mutation.WaitAsync(ct);
                        try
                        {
                            await using var claimDb = await _db.CreateDbContextAsync(ct);
                            var activeIds = _running.Keys.ToArray();
                            await claimDb.ArchiveEntries.Where(e => e.Id == history.Id && !activeIds.Contains(e.Id) && ResumeStages.Contains(e.Stage) && (e.FolderJobId == null || e.FolderJobId == jobId))
                                .ExecuteUpdateAsync(s => s.SetProperty(e => e.FolderJobId, jobId).SetProperty(e => e.Stage, "waiting"), ct);
                        }
                        finally { _mutation.Release(); }
                        found++; continue;
                    }
                    // Failures that occurred after this run's initial claim remain
                    // visible, and will be retried only on a later explicit start.
                    if (history?.Stage == "failed") continue;
                    pendingRow = await PrepareOneAsync(path, job.SourceRoot, rules, new ArchivePreviewRequest(), ct, stopOnStorage: true);
                    }
                    await _mutation.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        await using var writeDb = await _db.CreateDbContextAsync(ct);
                        // Preview/start can admit while this producer probes. Check
                        // under the same mutation gate before publishing this row.
                        // Keep one prepared row when full; never lose/re-probe it.
                        if (pendingRow.Action == "archive" && await OutstandingDirectoryEntries(writeDb).CountAsync(ct) >= DirectoryBatchSize) break;
                        await AddDirectoryRowAsync(writeDb, job, pendingRow, ct);
                        await writeDb.SaveChangesAsync(ct);
                        pendingRow = null;
                    }
                    finally { _mutation.Release(); }
                    found++;
                }
                if (found > 0) { QueueReady?.Invoke(); await ChangedAsync(jobId); }
                // Do not mark total known until discovery and the held backlog
                // have both been consumed. A paused scan remains resumable.
                if (!scanned) await Task.Delay(20, ct);
                }
                finally { _directoryProducer.Release(); }
            }
        }
    }

    private async Task<int> AdmitWaitingBatchAsync(ArchiveJob folder, int budget, CancellationToken ct)
    {
        List<ArchiveEntry> entries;
        await using (var db = await _db.CreateDbContextAsync(ct))
            entries = await db.ArchiveEntries.AsNoTracking().Where(e => e.FolderJobId == folder.Id && (e.Stage == "waiting" || e.Stage == "waiting_retry"))
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Take(budget).ToListAsync(ct);
        var processed = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await _mutation.WaitAsync(ct);
            ArchiveSettings? admissionRules = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                await using var db = await _db.CreateDbContextAsync(ct);
                if (await OutstandingDirectoryEntries(db).CountAsync(ct) >= DirectoryBatchSize) break;
                processed++;
                var origin = await db.ArchiveJobs.AsNoTracking().SingleAsync(j => j.Id == entry.JobId, ct);
                var rules = admissionRules = EntryRules(origin, entry);
                var metadata = IsMacMetadataPath(entry.Path);
                if (await HoldKnownTransactionConflictAsync(entry)) continue;
                if (metadata && !NeedsTransactionReview(entry))
                { entry.Stage = "skipped"; entry.ReasonCode = "filesystem_metadata"; entry.Error = "macOS 系统附属文件不是视频，直接跳过。"; await SaveEntryAsync(entry); await SetMediaStatusAsync(entry, MediaFileStatus.Skipped); continue; }
                try { await RecoverEntryAsync(entry, folder.SourceRoot); }
                catch (ArchiveException ex) when (ex.Code is "transaction_conflict" or "transaction_path_conflict" or "rollback_conflict" or "restore_interrupted")
                {
                    // An ambiguous existing transaction blocks only this file. Preserve
                    // every transaction path and journal; never infer success or retry it
                    // in the automatic producer loop. Storage/traversal errors still pause.
                    entry.ReasonCode = ex.Code;
                    await StageAsync(entry, "failed", "需先核对文件。" + ex.Message + " 文件：" + entry.Path);
                    await SetMediaStatusAsync(entry, MediaFileStatus.Failed);
                    continue;
                }
                if (entry.Stage is "completed" or "restored") { await SetMediaStatusAsync(entry, entry.Stage == "completed" ? MediaFileStatus.Completed : MediaFileStatus.Skipped); continue; }
                if (metadata)
                {
                    if (NeedsTransactionReview(entry))
                    {
                        entry.ReasonCode ??= "transaction_requires_review";
                        await StageAsync(entry, "failed", TransactionReviewReason);
                        await SetMediaStatusAsync(entry, MediaFileStatus.Failed);
                    }
                    else
                    {
                        entry.Stage = "skipped"; entry.ReasonCode = "filesystem_metadata"; entry.Error = "macOS 系统附属文件不是视频，直接跳过。";
                        await SaveEntryAsync(entry); await SetMediaStatusAsync(entry, MediaFileStatus.Skipped);
                    }
                    continue;
                }
                if (!NeedsTransactionReview(entry) && (await IsExcludedAsync(entry.Path, rules) ||
                    ReadSourceMetadata(entry.Path, folder.SourceRoot, CanSkipMissingSource(entry), () => IsLinkedInsideRoot(entry.Path, folder.SourceRoot))))
                { entry.Stage = "skipped"; entry.ReasonCode = "excluded_directory"; entry.Error = "备份、缓存、暂存目录和符号链接不处理。"; await SaveEntryAsync(entry); await SetMediaStatusAsync(entry, MediaFileStatus.Skipped); continue; }
                if (await SkipSmallPendingEntryAsync(entry, folder.SourceRoot)) continue;
                CheckEntrySourceAccess(entry, rules, folder.SourceRoot);
                var savedMedia = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json) ?? new();
                var selectedChoice = PendingChoice(folder) ?? savedMedia.OutputPolicy;
                if (selectedChoice != null && CanApplyOutputChoice(entry, savedMedia, selectedChoice))
                {
                    // New ownership changes only output choices, never the origin's
                    // encoding rules or an already started original-file transaction.
                    savedMedia.OutputPolicy = selectedChoice; entry.MediaJson = JsonSerializer.Serialize(savedMedia, Json);
                    rules = EntryRules(origin, entry); ApplyEntryOutputPaths(entry, origin, rules);
                }
                RequirePendingSource(entry, folder.SourceRoot);
                if (entry.TargetMbps <= 0 || string.IsNullOrEmpty(entry.EncoderMode))
                {
                    var row = new ArchivePreviewRow { Path = entry.Path, InputPath = entry.InputPath };
                    try { await PrepareRowAsync(row, string.IsNullOrWhiteSpace(origin.SourceRoot) ? Path.GetDirectoryName(entry.Path)! : origin.SourceRoot, rules, new ArchivePreviewRequest { Intent = "retryFailed", EntryIds = [entry.Id] }, ct, entry.Id); }
                    catch (OperationCanceledException) { throw; }
                    catch (ArchiveException ex) when (ex.Code == MissingSourceCode) { throw; }
                    catch (ArchiveException ex) when (ex.Code is SourceLockedCode or SourcePermissionCode) { throw; }
                    catch (Exception ex) when (IsStorageFailure(ex)) { throw; }
                    catch (Exception ex) { Block(row, ex is ArchiveException ae ? ae.Code : "analysis_failed", ex.Message); }
                    if (row.Action != "archive")
                    { entry.Stage = row.Action == "skip" ? "skipped" : "failed"; entry.Error = row.Reason; entry.ReasonCode = row.ReasonCode; await SaveEntryAsync(entry); await SetMediaStatusAsync(entry, entry.Stage == "failed" ? MediaFileStatus.Failed : MediaFileStatus.Skipped); continue; }
                    entry.InputPath = row.InputPath; entry.OutputPath = row.OutputPath!; entry.BackupPath = row.BackupPath;
                    entry.MediaJson = JsonSerializer.Serialize(row.Media, Json); entry.Codec = row.Codec; entry.TargetMbps = row.TargetMbps;
                    if (savedMedia.OutputPolicy != null && row.Media != null)
                    { row.Media.OutputPolicy = savedMedia.OutputPolicy; entry.MediaJson = JsonSerializer.Serialize(row.Media, Json); }
                    entry.SourceMbps = row.SourceMbps; entry.SourceBytes = row.SourceBytes; entry.EncoderMode = row.EncoderMode!;
                }
                var otherActive = await db.ArchiveEntries.AnyAsync(e => e.Id != entry.Id && EF.Functions.Collate(e.Path, "NOCASE") == entry.Path && LiveStages.Contains(e.Stage), ct);
                if (otherActive) { entry.Stage = "interrupted"; entry.Error = "同一文件正在处理中，保留当前记录。"; await SaveEntryAsync(entry); continue; }
                entry.Stage = "queued"; entry.Error = null; entry.ReasonCode = "ready"; entry.Progress = 0;
                db.ArchiveEntries.Update(entry);
                var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json)!;
                var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == entry.Path, ct);
                if (file == null)
                {
                    file = new MediaFile { FilePath = entry.Path, Directory = Path.GetDirectoryName(entry.Path)!, FileName = Path.GetFileName(entry.Path), BaseName = Path.GetFileNameWithoutExtension(entry.Path) };
                    db.MediaFiles.Add(file);
                }
                file.ArchiveEntryId = entry.Id; file.Status = MediaFileStatus.Queued; file.ForceMux = false;
                file.FileSize = entry.SourceBytes; file.Bitrate = (long)(entry.SourceMbps * 1000); file.Codec = entry.Codec;
                file.Width = media.Width; file.Height = media.Height; file.PixelFormat = media.PixelFormat; file.Duration = media.Duration; file.Kind = MediaKind.Video; file.IsHevc = entry.Codec == "hevc";
                await db.SaveChangesAsync(ct);
            }
            catch (ArchiveException ex) when (ex.Code == MissingSourceCode && CanSkipMissingSource(entry))
            { await MarkSourceMissingAsync(entry); }
            catch (Exception ex) when (CanIsolateFileAccess(entry, folder.SourceRoot, admissionRules, ex))
            { await FailFileAccessAsync(entry, ex); }
            finally { _mutation.Release(); }
        }
        return processed;
    }

    private static bool IsLinkedInsideRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        var current = path;
        while (Within(current, root))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
            if (string.IsNullOrEmpty(current)) break;
        }
        return false;
    }
}
