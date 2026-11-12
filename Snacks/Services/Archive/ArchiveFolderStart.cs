using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    private readonly ConcurrentDictionary<string, Task> _folderRestarts = new();
    private static string Owner(ArchiveEntry entry) => entry.FolderJobId ?? entry.JobId;
    private static readonly string[] LiveStages = ["queued", "encoding", "muxing", "validating", "backing_up", "committing", "restoring"];
    private static readonly string[] ResumeStages = ["waiting", "waiting_retry", "queued", "interrupted", "paused_storage"];

    private static string FolderPrefix(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    private static IQueryable<ArchiveEntry> InFolder(IQueryable<ArchiveEntry> query, string root, bool recursive = true)
    {
        var prefix = FolderPrefix(root);
        query = query.Where(e => EF.Functions.Collate(e.Path, "NOCASE").StartsWith(prefix));
        if (!recursive) query = query.Where(e => !e.Path.Substring(prefix.Length).Contains(Path.DirectorySeparatorChar.ToString()));
        return query;
    }
    private static IQueryable<ArchiveEntry> FolderEntries(SnacksDbContext db, ArchiveJob job)
    {
        if (job.ScanRecursive != null) return Authoritative(InFolder(db.ArchiveEntries, job.SourceRoot, job.ScanRecursive.Value), db);
        return db.ArchiveEntries.Where(e => e.FolderJobId == job.Id || (e.FolderJobId == null && e.JobId == job.Id));
    }
    private static IQueryable<ArchiveEntry> Authoritative(IQueryable<ArchiveEntry> query, SnacksDbContext db) => query.Where(e =>
        !(e.Stage == "skipped" && e.DiscoveryKey != null && (e.ReasonCode == "existing_task" || (e.ReasonCode != null && e.ReasonCode.StartsWith("history_")))) &&
        !db.ArchiveEntries.Any(newer => EF.Functions.Collate(newer.Path, "NOCASE") == e.Path &&
            !(newer.Stage == "skipped" && newer.DiscoveryKey != null && (newer.ReasonCode == "existing_task" || (newer.ReasonCode != null && newer.ReasonCode.StartsWith("history_")))) &&
            (newer.CreatedAt > e.CreatedAt || (newer.CreatedAt == e.CreatedAt && string.Compare(newer.Id, e.Id) > 0))));

    // Selection is the start action. No traversal, probing or bulk materialization
    // happens on this request; the response opens the same durable progress view.
    public async Task<object> StartFolderAsync(ArchiveFolderStartRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DirectoryPath)) throw new ArchiveException("empty_selection", "请选择要处理的文件夹。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DirectoryPath));
        await _mutation.WaitAsync(ct);
        try
        {
            if (_stopping) throw new ArchiveException("shutting_down", "软件正在退出。");
            var rules = GetSettings();
            if (rules.Mode != "custom") throw new ArchiveException("original_mode", "请先选择归档模式。");
            await using var db = await _db.CreateDbContextAsync(ct);
            var jobs = db.ArchiveJobs.Where(j => EF.Functions.Collate(j.SourceRoot, "NOCASE") == root || EF.Functions.Collate(j.SourceRoot, "NOCASE") == root + Path.DirectorySeparatorChar);
            var liveIds = _scanTasks.Keys.Concat(_folderRestarts.Keys).ToArray();
            var job = await jobs.Where(j => liveIds.Contains(j.Id)).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            if (job != null && (!_jobTokens.TryGetValue(job.Id, out var liveToken) || !liveToken.IsCancellationRequested))
                return new { jobId = job.Id, scope = "directory", resumed = true };
            job ??= await jobs
                .OrderByDescending(j => db.ArchiveEntries.Count(e => e.JobId == j.Id && (ResumeStages.Contains(e.Stage) || e.Stage == "failed")))
                .ThenByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            // A legacy explicit-file job may also contain files outside its first
            // file's parent. Keep those independently owned and paused.
            if (job != null && await db.ArchiveEntries.AnyAsync(e => e.JobId == job.Id && !EF.Functions.Collate(e.Path, "NOCASE").StartsWith(FolderPrefix(root)), ct)) job = null;
            if (job == null)
            {
                job = new ArchiveJob { RequestId = Guid.NewGuid().ToString("N"), SourceRoot = root, RulesSnapshotJson = JsonSerializer.Serialize(rules, Json), ConfirmedNoBackup = !rules.BackupEnabled };
                db.ArchiveJobs.Add(job);
            }
            if (liveIds.Distinct().Count() >= 4 && !liveIds.Contains(job.Id))
                throw new ArchiveException("folder_busy", "请先暂停一个正在处理的文件夹，再开始此文件夹。");
            var id = job.Id;
            job.ScanRecursive = true; job.ScanComplete = false; job.FolderConcurrency = rules.Concurrency;
            job.Status = "preparing"; job.Error = null;
            await db.SaveChangesAsync(ct);
            ScheduleFolderRun(id, retryFailed: true);
            return new { jobId = id, scope = "directory", resumed = true };
        }
        finally { _mutation.Release(); }
    }

    // A rapid pause/reselect waits for the old transaction to settle in the
    // background. A repeated selection coalesces into this one restart.
    private void ScheduleFolderRun(string id, bool retryFailed)
    {
        if (_folderRestarts.ContainsKey(id)) return;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                while (!_stopping)
                {
                    var oldScan = _scanTasks.TryGetValue(id, out var scan) ? scan : null;
                    if (oldScan != null) { await oldScan; continue; }
                    await using var check = await _db.CreateDbContextAsync();
                    var runningIds = _running.Keys.ToArray();
                    if (runningIds.Length > 0 && await check.ArchiveEntries.AnyAsync(e => runningIds.Contains(e.Id) && (e.FolderJobId == id || (e.FolderJobId == null && e.JobId == id))))
                    { await Task.Delay(50); continue; }
                    await _mutation.WaitAsync();
                    try
                    {
                        if (_stopping) return;
                        await using var db = await _db.CreateDbContextAsync();
                        var job = await db.ArchiveJobs.SingleAsync(j => j.Id == id);
                        if (job.Status != "preparing") return; // paused again before start
                        await ClaimFolderBacklogAsync(db, job, retryFailed);
                        if (_jobTokens.TryRemove(id, out var old)) old.Dispose();
                        job.Status = "running"; job.Error = null;
                        await db.SaveChangesAsync();
                        StartDirectoryProducer(id);
                    }
                    finally { _mutation.Release(); }
                    await ChangedAsync(id);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Folder start failed: " + ex.Message);
                try { await PauseJobAsync(id, "文件夹暂时无法继续：" + ex.Message); } catch (Exception persist) { Log.Error(persist.Message); }
            }
            finally { _folderRestarts.TryRemove(id, out _); }
        });
        _folderRestarts[id] = task; start.SetResult();
    }

    private async Task ClaimFolderBacklogAsync(SnacksDbContext db, ArchiveJob job, bool retryFailed, bool takePausedOwners = true)
    {
        var candidates = job.ScanRecursive == null ? db.ArchiveEntries.Where(e => e.JobId == job.Id) : InFolder(db.ArchiveEntries, job.SourceRoot, job.ScanRecursive.Value);
        candidates = Authoritative(candidates, db).Where(e => ResumeStages.Contains(e.Stage) ||
            (retryFailed && e.Stage == "failed" && !HeldTransactionCodes.Contains(e.ReasonCode)));
        // The scan can finish while the rolling window still contains queued
        // work. Its final backlog sweep must not demote those runnable rows.
        if (!takePausedOwners) candidates = candidates.Where(e => e.Stage != "queued");
        var runningIds = _running.Keys.ToArray();
        candidates = candidates.Where(e => !runningIds.Contains(e.Id) &&
            (e.FolderJobId == null || e.FolderJobId == job.Id || takePausedOwners && !db.ArchiveJobs.Any(j => j.Id == e.FolderJobId && (j.Status == "running" || j.Status == "queued" || j.Status == "preparing"))));
        // SQL updates only: 13k legacy entries remain in SQLite, never in a List.
        await candidates.ExecuteUpdateAsync(set => set.SetProperty(e => e.FolderJobId, job.Id)
            .SetProperty(e => e.Stage, e => e.Stage == "failed" || e.Stage == "waiting_retry" ? "waiting_retry" : "waiting"));
        var held = db.ArchiveEntries.Where(e => e.FolderJobId == job.Id && (e.Stage == "waiting" || e.Stage == "waiting_retry")).Select(e => e.Id);
        await db.MediaFiles.Where(f => f.ArchiveEntryId != null && held.Contains(f.ArchiveEntryId)).ExecuteUpdateAsync(set => set.SetProperty(f => f.Status, MediaFileStatus.ArchivePaused));
    }
}
