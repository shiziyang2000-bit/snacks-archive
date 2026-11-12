using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    private const string TransactionReviewReason = "需先核对文件；保留全部事务文件，请在原任务中继续并检查恢复状态。";
    private static bool NeedsTransactionReview(ArchiveEntry e)
    {
        if (e.Stage is not ("completed" or "restored") && e.ReasonCode is "transaction_conflict" or "transaction_path_conflict" or "rollback_conflict" or "restore_interrupted" or TransactionPermissionCode ||
            e.RollbackPath != null || e.RestoreTempPath != null || e.RestoreDisplacedPath != null) return true;
        var transfer = JsonSerializer.Deserialize<ArchiveMediaInfo>(e.MediaJson, Json)?.Transfer;
        return e.Stage is not ("completed" or "restored") &&
            transfer is { BackupReadyPath: not null } or { Published: true } or { RestorePublished: true };
    }
    internal static string? NasRecycleFolder(string root)
    {
        if (!root.StartsWith("/Volumes/", StringComparison.Ordinal)) return null;
        var normalized = Path.GetFullPath(root);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "Volumes" || parts[2] == "#recycle") return null;
        return Path.Combine("/Volumes", parts[1], "#recycle", Path.Combine(parts.Skip(2).ToArray()));
    }
    private async Task<object> EntryViewAsync(ArchiveEntry e)
    {
        var hasBackup=e.BackupPath!=null && File.Exists(e.BackupPath);
        var originalAvailable=File.Exists(e.InputPath) && (e.InputPath!=e.OutputPath || e.Stage=="restored");
        await using var db=await _db.CreateDbContextAsync(); var cached=await db.ArchiveCaches.Where(c=>c.EntryId==e.Id).Select(c=>c.Path).ToListAsync();
        var references=originalAvailable||hasBackup||cached.Any(File.Exists);
        var samples=JsonSerializer.Deserialize<List<ArchiveSample>>(e.SamplesJson,Json)??[];
        var job=await db.ArchiveJobs.AsNoTracking().FirstOrDefaultAsync(j=>j.Id==e.JobId);
        var reminderDays=job==null?7:Rules(job).BackupReminderDays;
        var needsReview=NeedsTransactionReview(e);
        return new { e.Id,e.JobId,path=e.Path,e.InputPath,e.OutputPath,e.BackupPath,e.Stage,e.Codec,e.SourceMbps,e.TargetMbps,e.ActualMbps,e.SourceBytes,e.OutputBytes,e.EncoderMode,e.Parameters,e.Progress,e.Score,e.ScoreStatus,scoreMetric="SSIM × 100",samples,e.ScoreError,e.Error,e.ReasonCode,e.CreatedAt,e.UpdatedAt,e.CompletedAt,e.RestoredAt,
            backupAvailable=hasBackup,backupStatus=e.BackupPath==null?"none":hasBackup?"available":"missing",
            canPlay=e.Stage=="completed",
            needsFileReview=needsReview,canRetry=e.Stage=="failed"&&!needsReview,retryReason=needsReview?TransactionReviewReason:e.Stage=="failed"?null:"仅失败项可明确重试。",
            canRestore=HasRestorableBackup(e)&&hasBackup,restoreReason=!HasRestorableBackup(e)?"需要已完成原位替换且备份属于该归档的记录。":!hasBackup?"没有有效原片备份，参考缓存不能恢复整条原片。":null,
            canRescore=false,rescoreReason="内容校验与抽样评分已移除；历史分数仅供查看。",
            canRearchive=!needsReview&&e.Stage is "completed" or "restored",rearchiveReason=needsReview?TransactionReviewReason:e.Stage is "completed" or "restored"?null:"需要已完成或已恢复记录。",
            backupReminderDue=e.CompletedAt.HasValue&&hasBackup&&DateTime.UtcNow>=e.CompletedAt.Value.AddDays(reminderDays) };
    }
    // The main-screen status must remain available even when the NAS is offline.
    // Read job state only; do not probe media or test backup paths here.
    public async Task<object> FolderStatusAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        var jobs = await db.ArchiveJobs.AsNoTracking().Where(j => j.ScanRecursive != null && j.Status != "completed")
            .OrderByDescending(j => j.CreatedAt).Take(8)
            .Select(j => new { jobId = j.Id, j.SourceRoot, j.Status, j.Error,
                pausedByUser = j.Status == "paused_storage" && j.Error != null && j.Error.StartsWith("用户") }).ToListAsync();
        return new { jobs };
    }

    public async Task<object> HistoryAsync(string? jobId,string? status,int skip,int take)
    {
        skip=Math.Max(0,skip);take=Math.Clamp(take,1,200);
        await using var db=await _db.CreateDbContextAsync();var q=db.ArchiveEntries.AsNoTracking().AsQueryable();
        if(!string.IsNullOrEmpty(jobId))
        {
            var owner=await db.ArchiveJobs.AsNoTracking().FirstOrDefaultAsync(j=>j.Id==jobId);
            q=owner==null?q.Where(e=>e.JobId==jobId):FolderEntries(db,owner).AsNoTracking();
        }
        if(!string.IsNullOrEmpty(status))q=q.Where(e=>e.Stage==status);
        var total=await q.CountAsync();var entries=await q.OrderByDescending(e=>e.CreatedAt).Skip(skip).Take(take).ToListAsync();
        var items=new List<object>();foreach(var e in entries)items.Add(await EntryViewAsync(e));
        var directoryJobs=await db.ArchiveJobs.AsNoTracking().Where(j=>j.ScanRecursive!=null && j.Status!="completed").OrderByDescending(j=>j.CreatedAt).Take(8).Select(j=>new {jobId=j.Id,j.SourceRoot,j.Status,j.Error,j.ScanComplete}).ToListAsync();
        return new {items,total,skip,take,directoryJobs};
    }
    public async Task<object> JobAsync(string id)
    {
        await using var db=await _db.CreateDbContextAsync();var job=await db.ArchiveJobs.AsNoTracking().FirstOrDefaultAsync(j=>j.Id==id)??throw new ArchiveException("not_found","任务不存在。");
        var query=FolderEntries(db,job).AsNoTracking();
        var counts=await query.GroupBy(e=>e.Stage).Select(g=>new {stage=g.Key,count=g.Count(),sourceBytes=g.Sum(e=>e.SourceBytes),outputBytes=g.Sum(e=>e.OutputBytes??0)}).ToListAsync();
        int Count(string stage)=>counts.FirstOrDefault(x=>x.stage==stage)?.count??0;
        var total=counts.Sum(x=>x.count);
        var rows=await query.OrderByDescending(e=>e.CreatedAt).ThenByDescending(e=>e.Id).Take(JobDetailLimit).ToListAsync();
        var entries=new List<object>();foreach(var e in rows)entries.Add(await EntryViewAsync(e));
        var recycleFolder=NasRecycleFolder(job.SourceRoot);
        return new { jobId=id,status=job.Status,rules=Rules(job),pendingOutputPolicy=PendingChoice(job),recycleFolderAvailable=recycleFolder!=null&&Directory.Exists(recycleFolder),job.CreatedAt,job.Error,sourceRoot=job.SourceRoot,scope=job.ScanRecursive==null?"selection":"directory",recursive=job.ScanRecursive,scanComplete=job.ScanComplete,
            summary=new {total,totalKnown=job.ScanComplete,completed=Count("completed"),failed=Count("failed"),skipped=Count("skipped"),restored=Count("restored"),pending=Count("queued"),remaining=Count("waiting")+Count("waiting_retry")+Count("queued"),sourceBytes=counts.Sum(x=>x.sourceBytes),outputBytes=counts.Sum(x=>x.outputBytes)},
            entries,entriesTruncated=total>rows.Count,entryLimit=JobDetailLimit,
            pausedByUser=job.Status=="paused_storage" && job.Error!=null && job.Error.StartsWith("用户"),
            canContinue=job.Status is "paused_storage" or "interrupted" || Count("paused_storage")>0 || Count("interrupted")>0 };
    }

    public async Task<object> RestorePreviewAsync(ArchiveSelectionRequest request)
    {
        await using var db=await _db.CreateDbContextAsync();var q=db.ArchiveEntries.AsNoTracking().AsQueryable();
        if(!string.IsNullOrEmpty(request.JobId))q=q.Where(e=>e.JobId==request.JobId);else if(request.EntryIds is {Count:>0})q=q.Where(e=>request.EntryIds.Contains(e.Id));else throw new ArchiveException("empty_selection","请选择恢复条目或任务。");
        var rows=new List<RestoreRow>();
        foreach(var e in await q.ToListAsync())
        {
            try { await ValidateRestoreAsync(e);rows.Add(new(e.Id,e.Path,e.BackupPath,"restore","ready","备份及当前文件可访问；恢复将保留备份，不比较文件内容。")); }
            catch(Exception ex){rows.Add(new(e.Id,e.Path,e.BackupPath,"blocked",ex is ArchiveException ae?ae.Code:"storage_unavailable",ex.Message));}
        }
        var id=Guid.NewGuid().ToString("N");_restores[id]=new RestorePreview(DateTime.UtcNow.AddMinutes(30),rows);
        foreach(var old in _restores.Where(x=>x.Value.ExpiresAt<DateTime.UtcNow).ToList())_restores.TryRemove(old.Key,out _);
        return new {restorePreviewId=id,rows,conflicts=rows.Where(r=>r.Action=="blocked").ToList()};
    }
    private static bool HasRestorableBackup(ArchiveEntry e)
    {
        if (e.BackupPath == null || e.Path != e.OutputPath || e.Stage is not ("completed" or "paused_storage")) return false;
        var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(e.MediaJson, Json);
        // Legacy completed records already registered the successful replacement.
        // New/paused transactions require their durable publish and backup journal.
        return media?.Transfer == null ? e.Stage == "completed"
            : media.Transfer.Published && media.Transfer.BackupReadyPath == e.BackupPath;
    }
    internal static Task ValidateRestoreAsync(ArchiveEntry e)
    {
        if(!HasRestorableBackup(e))throw new ArchiveException("restore_unavailable","没有已完成原位替换且归属明确的原片备份；未发布或备份冲突的任务不能恢复。");
        RequireNonemptyFile(e.BackupPath!); RequireNonemptyFile(e.Path);
        if(Path.GetFullPath(e.BackupPath!)==Path.GetFullPath(e.Path))throw new ArchiveException("restore_conflict","备份与目标不能是同一个路径。");
        return Task.CompletedTask;
    }
    public async Task<object> RestoreAsync(ArchiveRestoreRequest request)
    {
        if(!request.Confirmed || string.IsNullOrWhiteSpace(request.RequestId))throw new ArchiveException("confirmation_required","请确认恢复清单后执行。");
        if(!_restores.TryGetValue(request.RestorePreviewId,out var preview)||preview.ExpiresAt<DateTime.UtcNow)throw new ArchiveException("preview_expired","恢复清单已过期，请重新预览。");
        if(_stopping)throw new ArchiveException("shutting_down","软件正在退出。");
        await _mutation.WaitAsync();
        var results=new List<object>();int restored=0,failed=0;
        try
        {
            foreach(var row in preview.Rows.Where(r=>r.Action=="restore"))
            {
                var e=await GetEntryAsync(row.Id)!;
                if(e==null){failed++;results.Add(new{id=row.Id,success=false,error="记录缺失"});continue;}
                await _commit.WaitAsync();
                try
                {
                    if(_stopping)throw new ArchiveException("shutting_down","软件正在退出。");
                    if(e.Stage=="restored"){restored++;results.Add(new{id=e.Id,success=true});continue;}
                    await ValidateRestoreAsync(e);
                    e.RestoreTempPath=Path.Combine(Path.GetDirectoryName(e.Path)!,".snacks-archive-"+e.Id+".restore"+Path.GetExtension(e.Path));
                    e.RestoreDisplacedPath=Path.Combine(Path.GetDirectoryName(e.Path)!,".snacks-archive-"+e.Id+".compressed"+Path.GetExtension(e.Path));
                    // Refuse unknown remnants; no blind overwrite or deletion.
                    if(File.Exists(e.RestoreTempPath)||File.Exists(e.RestoreDisplacedPath))throw new ArchiveException("restore_conflict","恢复暂存已有文件，保留全部内容，请先核对。",e.Path);
                    await StageAsync(e,"restoring");
                    await CopyFileAsync(e.BackupPath!,e.RestoreTempPath);
                    RequireNonemptyFile(e.Path);
                    File.Move(e.Path,e.RestoreDisplacedPath,false);
                    try{RequireOwnedTransactionPath(e,e.RestoreDisplacedPath,"compressed");File.Move(e.RestoreTempPath,e.Path,false);}
                    catch{if(!File.Exists(e.Path))File.Move(e.RestoreDisplacedPath,e.Path,false);throw;}
                    var transfer=Transfer(e);transfer.RestorePublished=true;await SaveTransferAsync(e,transfer);
                    e.RestoredAt=DateTime.UtcNow;await StageAsync(e,"restored");await SetMediaStatusAsync(e,MediaFileStatus.Skipped);
                    RequireOwnedTransactionPath(e,e.RestoreDisplacedPath,"compressed");File.Delete(e.RestoreDisplacedPath);
                    e.RestoreTempPath=null;e.RestoreDisplacedPath=null;await SaveEntryAsync(e);
                    restored++;results.Add(new{id=e.Id,success=true});
                }
                catch(Exception ex)
                {
                    if(e.Stage=="restored"){restored++;results.Add(new{id=e.Id,success=true,error="原片已恢复，保留压缩暂存供核对："+ex.Message});}
                    else{failed++;if(e.Stage=="restoring")await StageAsync(e,"paused_storage","恢复中断，已保留全部文件："+ex.Message);results.Add(new{id=e.Id,success=false,error=ex.Message});}
                }
                finally{_commit.Release();await RefreshJobAsync(e.JobId);}
            }
            _restores.TryRemove(request.RestorePreviewId,out _);return new{restored,failed,results};
        }
        finally{_mutation.Release();}
    }
    public Task<object> RescoreAsync(ArchiveSelectionRequest request,CancellationToken ct)
        => Task.FromException<object>(new ArchiveException("content_checks_removed","内容校验与抽样评分已移除；历史分数保留，不再计算。"));
    private async Task<long> CacheBytesAsync()
    {await using var db=await _db.CreateDbContextAsync();var files=await db.ArchiveCaches.ToListAsync();long total=0;foreach(var c in files){try{total+=_cacheUsers.ContainsKey(c.EntryId)?Math.Max(c.Bytes,File.Exists(c.Path)?new FileInfo(c.Path).Length:0):File.Exists(c.Path)?new FileInfo(c.Path).Length:0;}catch{}}return total;}
    public async Task<object> CacheInfoAsync()
    {
        await using var db=await _db.CreateDbContextAsync();var records=await db.ArchiveCaches.AsNoTracking().ToListAsync();long used=0,releasable=0;
        foreach(var c in records){try{var length=File.Exists(c.Path)?new FileInfo(c.Path).Length:0;used+=length;if(!_cacheUsers.ContainsKey(c.EntryId))releasable+=length;}catch{}}
        var s=GetSettings();return new{directory=CacheDirectory(s),usedBytes=used,releasableBytes=releasable,limitBytes=(long)(s.CacheLimitGb*1_000_000_000)};
    }
    private readonly SemaphoreSlim _cacheGate=new(1,1);
    private async Task<long> TrimCacheAsync(ArchiveSettings settings,bool all=false)
    {
        await _cacheGate.WaitAsync();
        try
        {
            await using var db=await _db.CreateDbContextAsync();var records=await db.ArchiveCaches.OrderBy(c=>c.CreatedAt).ToListAsync();var used=await CacheBytesAsync();long freed=0;var limit=(long)(settings.CacheLimitGb*1_000_000_000);
            foreach(var c in records)
            {
                if(!all&&used<=limit)break;
                if(_cacheUsers.ContainsKey(c.EntryId))continue;
                // Only individually registered, service-owned paths. No recursive directory deletes.
                if(Path.GetFileName(Path.GetDirectoryName(c.Path))!=c.EntryId)continue;
                try{var bytes=File.Exists(c.Path)?new FileInfo(c.Path).Length:0;if(File.Exists(c.Path))File.Delete(c.Path);db.ArchiveCaches.Remove(c);used-=bytes;freed+=bytes;}catch{ }
            }
            await db.SaveChangesAsync();return freed;
        }
        finally{_cacheGate.Release();}
    }
    public async Task<object> ClearCacheAsync()=>new{freedBytes=await TrimCacheAsync(GetSettings(),true)};
}
