using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _running = new();
    public async Task<ArchiveEntry?> GetEntryAsync(string id)
    { await using var db=await _db.CreateDbContextAsync(); return await db.ArchiveEntries.AsNoTracking().FirstOrDefaultAsync(e=>e.Id==id); }
    public async Task<bool> CanDispatchAsync(string id)
    {
        if (_stopping) return false;
        await using var db=await _db.CreateDbContextAsync();
        return await (from e in db.ArchiveEntries join j in db.ArchiveJobs on (e.FolderJobId ?? e.JobId) equals j.Id where e.Id==id && e.Stage=="queued" && (j.Status=="queued" || j.Status=="running") select e.Id).AnyAsync();
    }
    public async Task HydrateAsync(WorkItem item, string entryId)
    {
        await using var db=await _db.CreateDbContextAsync();
        var e=await db.ArchiveEntries.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==entryId);
        if (e==null) throw new ArchiveException("history_missing","归档任务记录缺失，拒绝派发。");
        var j=await db.ArchiveJobs.AsNoTracking().SingleAsync(x=>x.Id==e.JobId);
        var owner = e.FolderJobId == null ? j : await db.ArchiveJobs.AsNoTracking().SingleAsync(x=>x.Id==e.FolderJobId);
        item.Id=e.Id; item.ArchiveEntryId=e.Id; item.ArchiveJobId=Owner(e); item.ArchiveConcurrency=owner.FolderConcurrency ?? Rules(j).Concurrency; item.ArchiveEncoder=e.EncoderMode;
        item.VideoEncoderName=e.EncoderMode; item.WaitReason=null;
    }
    private async Task SaveEntryAsync(ArchiveEntry entry)
    {
        entry.UpdatedAt=DateTime.UtcNow;
        await using var db=await _db.CreateDbContextAsync();
        db.ArchiveEntries.Update(entry); await db.SaveChangesAsync();
        await ChangedAsync(Owner(entry));
        if (Owner(entry) != entry.JobId) await ChangedAsync(entry.JobId);
    }
    private async Task StageAsync(ArchiveEntry e, string stage, string? error=null)
    { e.Stage=stage; e.Error=error; await SaveEntryAsync(e); }
    private async Task SetMediaStatusAsync(ArchiveEntry entry, MediaFileStatus status)
    {
        await using var db=await _db.CreateDbContextAsync();
        await db.MediaFiles.Where(f=>f.ArchiveEntryId==entry.Id).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,status));
    }
    private static void RequireReachable(string path)
    {
        if (!File.Exists(path)) throw new ArchiveException("storage_unavailable","素材/存储不可访问，任务已暂停，恢复后请手动继续。",path,true);
    }
    private static void EnsureWritableParent(string file)
    {
        var full=Path.GetFullPath(file);
        if (OperatingSystem.IsMacOS() && full.StartsWith("/Volumes/",StringComparison.Ordinal))
        {
            var components=full.Split('/',StringSplitOptions.RemoveEmptyEntries);
            if (components.Length<3 || !Directory.Exists("/Volumes/"+components[1])) throw new ArchiveException("storage_unavailable","目标硬盘或共享不在线。",file,true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    }
    public async Task ExecuteAsync(WorkItem item, Func<FfmpegArgumentList,CancellationToken,Task> runFfmpeg, Func<string,Task> log, CancellationToken cancellationToken)
    {
        ArchiveEntry entry;
        CancellationTokenSource tokenSource;
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            entry = await GetEntryAsync(item.ArchiveEntryId!) ?? throw new ArchiveException("history_missing","归档记录缺失。");
            if (!await CanDispatchAsync(entry.Id)) { item.Status=WorkItemStatus.Stopped; return; }
            await PreserveUnknownPriorEncodeTimeAsync(entry);
            _running[entry.Id]=0;
            item.ArchiveJobId = Owner(entry);
            tokenSource = _jobTokens.GetOrAdd(Owner(entry), _=>new CancellationTokenSource());
        }
        finally { _mutation.Release(); }
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,tokenSource.Token);
        var ct=linked.Token;
        string? sonyScratch=null;
        string? sourceRoot=null;
        ArchiveSettings? executionRules=null;
        try
        {
            if (_stopping) throw new OperationCanceledException();
            await using var db=await _db.CreateDbContextAsync();
            var job=await db.ArchiveJobs.SingleAsync(j=>j.Id==entry.JobId);
            var rules=EntryRules(job,entry); ct.ThrowIfCancellationRequested();
            var owner = entry.FolderJobId == null || entry.FolderJobId == job.Id ? job : await db.ArchiveJobs.SingleAsync(j => j.Id == entry.FolderJobId);
            sourceRoot=owner.SourceRoot; executionRules=rules;
            if (await HoldKnownTransactionConflictAsync(entry))
            { item.Status=WorkItemStatus.Failed; item.ArchiveStage="failed"; item.ErrorMessage=entry.Error; return; }
            if (NeedsTransactionReview(entry)) await RecoverEntryAsync(entry, sourceRoot);
            if (entry.Stage is "completed" or "restored")
            {
                item.Status=entry.Stage=="completed"?WorkItemStatus.Completed:WorkItemStatus.Stopped;
                item.ArchiveStage=entry.Stage;
                await SetMediaStatusAsync(entry,entry.Stage=="completed"?MediaFileStatus.Completed:MediaFileStatus.Skipped);
                return;
            }
            if (await SkipSmallPendingEntryAsync(entry, owner.SourceRoot))
            {
                item.Status=WorkItemStatus.Stopped; item.ArchiveStage="skipped";
                item.WaitReason=SmallSourceReason; item.Progress=0; item.Size=entry.SourceBytes;
                item.OutputPath=null; item.OutputSize=null; item.ErrorMessage=null;
                item.CompletedAt=DateTime.UtcNow;
                await log(SmallSourceReason); return;
            }
            CheckEntrySourceAccess(entry, rules, sourceRoot);
            await db.ArchiveJobs.Where(j=>j.Id==item.ArchiveJobId && (j.Status=="queued" || j.Status=="running")).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.Status,"running"),ct);
            var media=JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson,Json)!;
            item.Status=WorkItemStatus.Processing; item.StartedAt=DateTime.UtcNow; item.Progress=0;
            await SetMediaStatusAsync(entry,MediaFileStatus.Processing);
            RequirePendingSource(entry, owner.SourceRoot);
            media.PathIdentity ??= ReadSourceMetadata(entry.Path, owner.SourceRoot, CanSkipMissingSource(entry), () => FileIdentity(entry.Path));
            entry.MediaJson=JsonSerializer.Serialize(media,Json);
            if (rules.OutputMode=="saveAs" && (File.Exists(entry.OutputPath)||Directory.Exists(entry.OutputPath))) throw new ArchiveException("output_conflict","输出位置出现同名文件，拒绝覆盖。",entry.OutputPath);
            EnsureWritableParent(entry.OutputPath);
            if (rules.BackupEnabled && rules.OutputMode=="inPlace") EnsureWritableParent(entry.BackupPath!);
            var temp=Path.Combine(Path.GetDirectoryName(entry.OutputPath)!,".snacks-archive-"+entry.Id+".output"+Path.GetExtension(entry.OutputPath));
            if (File.Exists(temp))
            {
                if (entry.TempOutputPath!=temp) throw new ArchiveException("temporary_conflict","工作暂存路径已有未知文件。",temp);
                File.Delete(temp);
            }
            entry.TempOutputPath=temp;
            item.ArchiveStage="encoding"; item.WaitReason=null;
            await StageAsync(entry,"encoding");
            if (SonyRtmdMp4Compatibility.RequiresTrackPreservation(media))
            {
                sonyScratch=SonyRtmdMp4Compatibility.ScratchPath(temp,entry.Id);
                if (Directory.Exists(sonyScratch))
                {
                    if ((File.GetAttributes(sonyScratch)&FileAttributes.ReparsePoint)!=0) throw new ArchiveException("temporary_conflict","Sony MP4 暂存目录是符号链接，拒绝覆盖。",sonyScratch);
                    Directory.Delete(sonyScratch,true);
                }
                else if (File.Exists(sonyScratch)) throw new ArchiveException("temporary_conflict","Sony MP4 暂存路径被同名文件占用。",sonyScratch);
                Directory.CreateDirectory(sonyScratch);
                var firstPass=Path.Combine(sonyScratch,"pass1.mp4");
                var videoCandidate=Path.Combine(sonyScratch,"video.mp4");
                var stats=Path.Combine(sonyScratch,"x265-two-pass.stats");
                if (media.Encoder=="hevc_videotoolbox")
                {
                    var hardwareArgs=_media.BuildArguments(entry,media,videoCandidate,sonyVideoOnly:true);
                    entry.Parameters="hardware: "+hardwareArgs; await SaveEntryAsync(entry);
                    var preservedDataTrack=SonyRtmdMp4Compatibility.HasTmcd(media)?"tmcd":"rtmd";
                    await log($"MP4 {media.PixelFormat} 原采样 VideoToolbox 硬件编码；保留源音轨与 {preservedDataTrack}，目标参数不变。"+hardwareArgs);
                    try { await runFfmpeg(hardwareArgs,ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException && IsHardwareSetupFailure(ex.Message) && !IsStorageFailure(ex))
                    {
                        if (File.Exists(videoCandidate)) File.Delete(videoCandidate);
                        media.Encoder="libx265"; entry.EncoderMode="libx265"; item.ArchiveEncoder="libx265"; item.VideoEncoderName="libx265";
                        await log("MP4 VideoToolbox 设置失败，按软件两遍编码回退；保持输入采样与目标不变。"+ex.Message);
                        await EncodeSonySoftwareTwoPassAsync(item,entry,media,firstPass,videoCandidate,stats,runFfmpeg,log,ct);
                    }
                }
                else
                    await EncodeSonySoftwareTwoPassAsync(item,entry,media,firstPass,videoCandidate,stats,runFfmpeg,log,ct);
                if (!File.Exists(videoCandidate) || new FileInfo(videoCandidate).Length==0) throw new ArchiveException("sony_video_candidate_missing","HEVC 第二遍没有生成视频候选。");
                item.ArchiveStage="muxing"; item.WaitReason="正在封装原音轨与数据轨"; item.Progress=0;
                await StageAsync(entry,"muxing");
                await _hub.Clients.All.SendAsync("WorkItemUpdated",item,ct);
                await SonyRtmdMp4Compatibility.PreserveAsync(_media.Mp4BoxPath,_media.FfmpegPath,entry.InputPath,videoCandidate,temp,sonyScratch,log,ct);
            }
            else
            {
                var args=_media.BuildArguments(entry,media,temp); entry.Parameters=args.ToString(); await SaveEntryAsync(entry); await log("归档参数："+entry.Parameters);
                try { await runFfmpeg(args,ct); }
                catch (Exception ex) when (ex is not OperationCanceledException && media.Encoder=="hevc_videotoolbox" && IsHardwareSetupFailure(ex.Message) && !IsStorageFailure(ex))
                {
                    // One specification-preserving fallback, never a bitrate/size retry.
                    if (File.Exists(temp)) File.Delete(temp);
                    media.Encoder="libx265"; entry.EncoderMode="libx265"; item.ArchiveEncoder="libx265"; item.VideoEncoderName="libx265";
                    args=_media.BuildArguments(entry,media,temp); entry.Parameters=args.ToString(); await SaveEntryAsync(entry);
                    await log("硬件无法满足原规格，切换软件 HEVC；不改变目标、像素格式或色彩。"+entry.Parameters);
                    await runFfmpeg(args,ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            RequireNonemptyFile(temp);
            entry.OutputBytes = new FileInfo(temp).Length;
            entry.OutputHash = null; // Legacy column is retained, but new jobs do not calculate content hashes.
            entry.ActualMbps = null;
            entry.ScoreStatus = "disabled"; entry.ScoreError = "内容校验与抽样评分已按要求移除。";
            await SaveEntryAsync(entry);
            if (sonyScratch != null)
            {
                Directory.Delete(sonyScratch, true);
                sonyScratch = null;
                await log("封装完成；已清理本任务中间文件，不进行额外内容检查。");
            }
            item.ArchiveStage="backing_up"; item.WaitReason=rules.BackupEnabled&&rules.OutputMode=="inPlace"?"正在备份并替换":"正在保存成片"; item.Progress=0;
            await _hub.Clients.All.SendAsync("WorkItemUpdated",item,ct);
            ct.ThrowIfCancellationRequested();
            CheckEntrySourceAccess(entry, rules, sourceRoot, allowMissingSkip: false);
            await CommitOutputAsync(entry,rules,ct);
            item.OutputPath=entry.OutputPath;
            item.OutputSize=entry.OutputBytes; item.OutputUsedVideoCopy=false; item.Status=WorkItemStatus.Completed; item.ArchiveStage="completed"; item.WaitReason=null; item.Progress=100; item.CompletedAt=DateTime.UtcNow;
            await SetMediaStatusAsync(entry,MediaFileStatus.Completed);
            await log(rules.OutputMode=="saveAs"?"归档完成；成片已另存，原文件保留。":rules.BackupEnabled?"归档完成；原位已替换，额外原片备份需手动管理。":"归档完成；原位已替换，未生成长期原片备份。");
        }
        catch (OperationCanceledException)
        {
            if (entry.Stage!="completed")
            {
                await StageAsync(entry,tokenSource.IsCancellationRequested&&!_stopping?"paused_storage":"interrupted", "任务已停止，保留完成进度；请手动继续。");
                await SetMediaStatusAsync(entry,MediaFileStatus.ArchivePaused);
                CleanupOwnOutput(entry); item.Status=WorkItemStatus.Stopped;
            }
        }
        catch (ArchiveException ex) when (ex.Code == MissingSourceCode && CanSkipMissingSource(entry))
        {
            await MarkSourceMissingAsync(entry);
            item.Status=WorkItemStatus.Stopped; item.ArchiveStage="skipped"; item.WaitReason=MissingSourceReason;
            item.Progress=0; item.OutputPath=null; item.OutputSize=null; item.ErrorMessage=null; item.CompletedAt=DateTime.UtcNow;
            await log(MissingSourceReason + " " + entry.Path);
        }
        catch (Exception ex)
        {
            if (entry.Stage=="completed") { item.Status=WorkItemStatus.Completed; item.ErrorMessage="已安全归档，后续状态刷新失败："+ex.Message; }
            else if (CanIsolateFileAccess(entry, sourceRoot, executionRules, ex))
            {
                await FailFileAccessAsync(entry, ex);
                item.Status=WorkItemStatus.Failed; item.ArchiveStage="failed"; item.ErrorMessage=entry.Error; item.CompletedAt=DateTime.UtcNow;
            }
            else if (IsStorageFailure(ex) || entry.Stage is "backing_up" or "committing")
            {
                entry.ReasonCode=ex is ArchiveException ae?ae.Code:"storage_failure";
                await StageAsync(entry,"paused_storage",ex.Message); await PauseJobAsync(Owner(entry),ex.Message);
                item.Status=WorkItemStatus.Stopped; item.ErrorMessage=ex.Message;
            }
            else
            {
                entry.ReasonCode=ex is ArchiveException ae?ae.Code:"encode_failed";
                entry.ScoreStatus="not_scored"; entry.ScoreError="归档未完成；内容校验与评分已停用。";
                await StageAsync(entry,"failed",ex.Message); await SetMediaStatusAsync(entry,MediaFileStatus.Failed);
                CleanupOwnOutput(entry); item.Status=WorkItemStatus.Failed; item.ErrorMessage=ex.Message; item.CompletedAt=DateTime.UtcNow;
            }
            await log("归档未完成："+ex.Message);
        }
        finally
        {
            _running.TryRemove(entry.Id,out _);
            await RefreshJobAsync(Owner(entry));
            if (Owner(entry) != entry.JobId) await RefreshJobAsync(entry.JobId);
            try { await _hub.Clients.All.SendAsync("WorkItemUpdated",item); } catch { }
        }
    }
    private static bool IsHardwareSetupFailure(string error) => new[] { "videotoolbox", "VideoToolbox", "cannot create", "not supported", "Error while opening encoder" }.Any(error.Contains);
    private static bool IsStorageFailure(Exception e) => e is IOException or UnauthorizedAccessException || e is ArchiveException { Storage:true } || new[] { "No space left", "Input/output error", "Read-only file system", "Stale file handle", "Disk quota", "Permission denied", "No such file or directory" }.Any(s=>e.Message.Contains(s,StringComparison.OrdinalIgnoreCase));
    private async Task EncodeSonySoftwareTwoPassAsync(WorkItem item,ArchiveEntry entry,ArchiveMediaInfo media,string firstPass,string videoCandidate,string stats,Func<FfmpegArgumentList,CancellationToken,Task> runFfmpeg,Func<string,Task> log,CancellationToken ct)
    {
        media.Encoder="libx265"; entry.EncoderMode="libx265"; item.ArchiveEncoder="libx265"; item.VideoEncoderName="libx265";
        var pass1=_media.BuildArguments(entry,media,firstPass,sonyVideoOnly:true,x265Pass:1,x265StatsPath:stats);
        var pass2=_media.BuildArguments(entry,media,videoCandidate,sonyVideoOnly:true,x265Pass:2,x265StatsPath:stats);
        entry.Parameters="pass1: "+pass1+Environment.NewLine+"pass2: "+pass2;
        await SaveEntryAsync(entry);
        var preservedDataTrack=SonyRtmdMp4Compatibility.HasTmcd(media)?"tmcd":"rtmd";
        await log($"MP4 原采样视频两遍软件 HEVC；保留源音轨与 {preservedDataTrack}，目标参数不变。pass1："+pass1);
        await runFfmpeg(pass1,ct);
        if (!File.Exists(stats) || new FileInfo(stats).Length==0) throw new ArchiveException("sony_two_pass_stats_missing","HEVC 两遍编码统计文件缺失，无法进行第二遍编码。");
        if (File.Exists(firstPass)) File.Delete(firstPass);
        await log("MP4 HEVC pass2："+pass2);
        await runFfmpeg(pass2,ct);
    }
    private static void CleanupOwnOutput(ArchiveEntry e)
    {
        if (e.RollbackPath!=null || e.Stage is "committing" or "backing_up" or "completed") return;
        if (e.TempOutputPath!=null && Path.GetFileName(e.TempOutputPath).StartsWith(".snacks-archive-"+e.Id+".",StringComparison.Ordinal) && File.Exists(e.TempOutputPath)) { try { File.Delete(e.TempOutputPath); } catch { } }
    }
    private async Task CommitOutputAsync(ArchiveEntry e,ArchiveSettings s,CancellationToken ct)
    {
        await _commit.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            RequireNonemptyFile(e.InputPath); RequireNonemptyFile(e.Path); RequireNonemptyFile(e.TempOutputPath!);
            var transfer=Transfer(e);
            if(s.BackupEnabled && s.OutputMode=="inPlace")
            {
                await StageAsync(e,"backing_up");
                if (transfer.BackupReadyPath == e.BackupPath)
                    RequireNonemptyFile(e.BackupPath!); // A registered original or this job's completed copy; never overwrite.
                else
                {
                    if (File.Exists(e.BackupPath) || Directory.Exists(e.BackupPath))
                        throw new ArchiveException("backup_conflict","备份位置已有文件，保留原片与现有备份。",e.BackupPath,true);
                    var staging=e.BackupPath+".snacks-archive-"+e.Id+"."+Guid.NewGuid().ToString("N")+".copy";
                    await CopyFileAsync(e.InputPath,staging,ct);
                    File.Move(staging,e.BackupPath!,false);
                    transfer.BackupReadyPath=e.BackupPath;
                    await SaveTransferAsync(e,transfer);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (s.OutputMode=="inPlace")
            {
                e.RollbackPath=Path.Combine(Path.GetDirectoryName(e.Path)!,".snacks-archive-"+e.Id+".original"+Path.GetExtension(e.Path));
                if(File.Exists(e.RollbackPath)||Directory.Exists(e.RollbackPath))
                    throw new ArchiveException("rollback_conflict","原片暂存位置已被占用，保留所有文件。",e.RollbackPath,true);
            }
            await StageAsync(e,"committing");
            // Atomic moves are a short, non-cancellable transaction; no content reads occur here.
            if(s.OutputMode=="inPlace")File.Move(e.Path,e.RollbackPath!,false);
            try { File.Move(e.TempOutputPath!,e.OutputPath,false); }
            catch
            {
                if(s.OutputMode=="inPlace"&&!File.Exists(e.Path)&&File.Exists(e.RollbackPath))File.Move(e.RollbackPath!,e.Path,false);
                throw;
            }
            transfer.Published=true;
            await SaveTransferAsync(e,transfer);
            e.CompletedAt=DateTime.UtcNow;e.Progress=100;await StageAsync(e,"completed");
            if(e.RollbackPath!=null&&File.Exists(e.RollbackPath))
            {RequireOwnedTransactionPath(e,e.RollbackPath,"original");File.Delete(e.RollbackPath);}
            e.RollbackPath=null;e.TempOutputPath=null;await SaveEntryAsync(e);
        }
        finally{_commit.Release();}
    }
    public async Task PauseJobAsync(string jobId,string reason)
    {
        _jobTokens.GetOrAdd(jobId,_=>new CancellationTokenSource()).Cancel();
        await _mutation.WaitAsync();
        try
        {
            _jobTokens.GetOrAdd(jobId,_=>new CancellationTokenSource()).Cancel();
            await using var db=await _db.CreateDbContextAsync();
            await db.ArchiveJobs.Where(j=>j.Id==jobId).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.Status,"paused_storage").SetProperty(j=>j.Error,reason));
            await db.ArchiveEntries.Where(e=>(e.FolderJobId==jobId || (e.FolderJobId==null && e.JobId==jobId)) && e.Stage=="queued").ExecuteUpdateAsync(s=>s.SetProperty(e=>e.Stage,"paused_storage").SetProperty(e=>e.Error,reason));
            var ids=db.ArchiveEntries.Where(e=>(e.FolderJobId==jobId || (e.FolderJobId==null && e.JobId==jobId)) && (e.Stage=="paused_storage" || e.Stage=="interrupted")).Select(e=>e.Id);
            await db.MediaFiles.Where(f=>f.ArchiveEntryId!=null && ids.Contains(f.ArchiveEntryId)).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,MediaFileStatus.ArchivePaused));
        }
        finally { _mutation.Release(); }
        await ChangedAsync(jobId);
    }

    private async Task RefreshJobAsync(string id)
    {
        await using var db=await _db.CreateDbContextAsync(); var job=await db.ArchiveJobs.FindAsync(id); if(job==null)return;
        var stages=await FolderEntries(db,job).GroupBy(e=>e.Stage).Select(g=>g.Key).ToListAsync();
        var status=job.Status is "paused_storage" or "interrupted" or "preparing" ? job.Status :
            !job.ScanComplete || stages.Any(s=>s is "waiting" or "waiting_retry") ? "running" :
            stages.Contains("paused_storage")?"paused_storage":stages.Contains("interrupted")?"interrupted":
            stages.All(s=>s is "completed" or "failed" or "restored" or "skipped")?"completed":"running";
        // Do not overwrite a pause that arrived while counts were being read.
        await db.ArchiveJobs.Where(j=>j.Id==id && j.Status==job.Status).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.Status,status));
        await ChangedAsync(id);
    }

    public async Task<object> ContinueAsync(string id)
    {
        await _mutation.WaitAsync();
        try
        {
            if (_stopping) throw new ArchiveException("shutting_down","软件正在退出。");
            await using var db=await _db.CreateDbContextAsync();
            var job=await db.ArchiveJobs.FindAsync(id)??throw new ArchiveException("not_found","任务不存在。");
            if ((_scanTasks.ContainsKey(id) || _folderRestarts.ContainsKey(id)) && (!_jobTokens.TryGetValue(id,out var token) || !token.IsCancellationRequested)) return new {jobId=id,continued=0};
            job.Status="preparing"; job.Error=null; job.FolderConcurrency=GetSettings().Concurrency;
            await db.SaveChangesAsync();
            ScheduleFolderRun(id, retryFailed: job.ScanRecursive != null);
            return new {jobId=id,continued=0};
        }
        finally { _mutation.Release(); }
    }
    public async Task InitializeAsync()
    {
        await using var db=await _db.CreateDbContextAsync();
        await db.ArchiveJobs.Where(j=>(j.ScanRecursive!=null && !j.ScanComplete || j.FolderConcurrency!=null && (j.Status=="running" || j.Status=="queued" || j.Status=="preparing")) && j.Status!="paused_storage").ExecuteUpdateAsync(s=>s.SetProperty(j=>j.Status,"interrupted").SetProperty(j=>j.Error,"软件重新启动，目录扫描未完成，请手动继续。"));
        string afterId = "";
        while (true)
        {
            var entries=await db.ArchiveEntries.Where(e=>string.Compare(e.Id,afterId)>0 && (e.Stage=="queued"||e.Stage=="encoding"||e.Stage=="muxing"||e.Stage=="validating"||e.Stage=="backing_up"||e.Stage=="committing"||e.Stage=="restoring"||e.Stage=="paused_storage"||e.Stage=="interrupted")).OrderBy(e=>e.Id).Take(64).ToListAsync();
            if (entries.Count==0) break;
            afterId=entries[^1].Id;
            foreach(var e in entries)
            {
                try { await RecoverEntryAsync(e); }
                catch(Exception ex){e.Stage="paused_storage";e.Error="事务恢复需要核对："+ex.Message;}
                if(e.Stage is not ("completed" or "restored" or "paused_storage")) {e.Stage="interrupted";e.Error="软件重新启动，请手动继续任务。";}
                db.ArchiveEntries.Update(e);
                await db.MediaFiles.Where(f=>f.ArchiveEntryId==e.Id).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,e.Stage=="completed"?MediaFileStatus.Completed:MediaFileStatus.ArchivePaused));
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        // Reconcile the tiny crash window between a durable terminal entry and its
        // MediaFiles projection; never let the legacy startup queue resurrect it.
        var completedIds=db.ArchiveEntries.Where(e=>e.Stage=="completed").Select(e=>e.Id);
        var restoredIds=db.ArchiveEntries.Where(e=>e.Stage=="restored").Select(e=>e.Id);
        var failedIds=db.ArchiveEntries.Where(e=>e.Stage=="failed").Select(e=>e.Id);
        await db.MediaFiles.Where(f=>f.ArchiveEntryId!=null&&completedIds.Contains(f.ArchiveEntryId)).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,MediaFileStatus.Completed));
        await db.MediaFiles.Where(f=>f.ArchiveEntryId!=null&&restoredIds.Contains(f.ArchiveEntryId)).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,MediaFileStatus.Skipped));
        await db.MediaFiles.Where(f=>f.ArchiveEntryId!=null&&failedIds.Contains(f.ArchiveEntryId)).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,MediaFileStatus.Failed));
        afterId="";
        while (true)
        {
            var jobIds=await db.ArchiveJobs.Where(j=>string.Compare(j.Id,afterId)>0 && (j.Status!="completed" || db.ArchiveEntries.Any(e=>e.JobId==j.Id && (e.Stage=="interrupted"||e.Stage=="paused_storage")))).OrderBy(j=>j.Id).Select(j=>j.Id).Take(64).ToListAsync();
            if (jobIds.Count==0) break;
            afterId=jobIds[^1];
            foreach(var id in jobIds) await RefreshJobAsync(id);
        }
        afterId="";
        while (true)
        {
        var cleanup=await db.ArchiveEntries.AsNoTracking().Where(e=>string.Compare(e.Id,afterId)>0 && ((e.Stage=="completed"&&e.RollbackPath!=null)||(e.Stage=="restored"&&e.RestoreDisplacedPath!=null))).OrderBy(e=>e.Id).Take(64).ToListAsync();
        if (cleanup.Count==0) break;
        afterId=cleanup[^1].Id;
        foreach(var e in cleanup)
        {
            try
            {
                if(e.Stage=="completed")
                {
                    RequireNonemptyFile(e.OutputPath);
                    if(File.Exists(e.RollbackPath)){RequireOwnedTransactionPath(e,e.RollbackPath!,"original");File.Delete(e.RollbackPath!);}
                    e.RollbackPath=null;e.TempOutputPath=null;
                }
                else
                {
                    RequireNonemptyFile(e.Path);
                    if(File.Exists(e.RestoreDisplacedPath)){RequireOwnedTransactionPath(e,e.RestoreDisplacedPath!,"compressed");File.Delete(e.RestoreDisplacedPath!);}
                    e.RestoreDisplacedPath=null;e.RestoreTempPath=null;
                }
                await SaveEntryAsync(e);
            }
            catch(Exception ex){e.Error="已完成文件保留；事务暂存需要核对："+ex.Message;await SaveEntryAsync(e);}
        }
        }
    }

    private async Task RecoverEntryAsync(ArchiveEntry e, string? sourceRoot = null)
    {
        if (e.Stage is not ("completed" or "restored") && HeldTransactionCodes.Contains(e.ReasonCode))
            throw new ArchiveException(e.ReasonCode!, e.Error ?? TransactionReviewReason, e.Path, true);
        if (NeedsTransactionReview(e))
        {
            if (sourceRoot == null)
            {
                await using var db = await _db.CreateDbContextAsync();
                sourceRoot = await db.ArchiveJobs.Where(j => j.Id == Owner(e)).Select(j => j.SourceRoot).FirstOrDefaultAsync() ?? Path.GetDirectoryName(e.Path)!;
            }
            CheckPendingTransactionAccess(e, sourceRoot);
        }
        var transfer=Transfer(e);
        if(e.RestoreTempPath!=null||e.RestoreDisplacedPath!=null)
        {
            if(transfer.RestorePublished)
            {RequireNonemptyFile(e.Path);e.Stage="restored";e.RestoredAt??=DateTime.UtcNow;await SaveEntryAsync(e);return;}
            if(!File.Exists(e.Path)&&e.RestoreDisplacedPath!=null&&File.Exists(e.RestoreDisplacedPath))
            {RequireOwnedTransactionPath(e,e.RestoreDisplacedPath,"compressed");File.Move(e.RestoreDisplacedPath,e.Path,false);}
            throw new ArchiveException("restore_interrupted","恢复事务未完成，所有文件均保留；请核对路径后重新恢复。",e.Path,true);
        }
        if(transfer.Published)
        {RequireNonemptyFile(e.OutputPath);e.Stage="completed";e.CompletedAt??=DateTime.UtcNow;e.Progress=100;await SaveEntryAsync(e);return;}
        // Legacy hash values are ignored. Ambiguous old transactions are never inferred successful.
        if(e.RollbackPath!=null&&File.Exists(e.RollbackPath))
        {
            RequireOwnedTransactionPath(e,e.RollbackPath,"original");
            if(File.Exists(e.Path))throw new ArchiveException("transaction_conflict","原位与原片暂存同时存在，保留全部文件。",e.Path,true);
            File.Move(e.RollbackPath,e.Path,false);e.RollbackPath=null;
        }
    }
    public async Task RecordProgressAsync(string entryId,int progress)
    {
        await using var db=await _db.CreateDbContextAsync();
        await db.ArchiveEntries.Where(e=>e.Id==entryId&&e.Stage=="encoding").ExecuteUpdateAsync(s=>s.SetProperty(e=>e.Progress,Math.Clamp(progress,0,100)).SetProperty(e=>e.UpdatedAt,DateTime.UtcNow));
    }
    public async Task PauseAllAsync()
    {
        await using var db=await _db.CreateDbContextAsync();
        var jobs=await db.ArchiveJobs.Where(j=>j.Status=="queued"||j.Status=="running"||j.Status=="preparing").Select(j=>j.Id).ToListAsync();
        foreach(var id in jobs) await PauseJobAsync(id,"用户停止任务，请手动继续。");
        while(!_running.IsEmpty || !_scanTasks.IsEmpty || !_folderRestarts.IsEmpty) await Task.Delay(50);
    }
    public async Task ShutdownAsync()
    {
        _stopping=true;
        await _mutation.WaitAsync();
        try { foreach(var source in _jobTokens.Values) source.Cancel(); }
        finally { _mutation.Release(); }
        while(!_running.IsEmpty || !_scanTasks.IsEmpty || !_folderRestarts.IsEmpty) await Task.Delay(50);
        await _commit.WaitAsync();
        try
        {
            await using var db=await _db.CreateDbContextAsync();
            await db.ArchiveEntries.Where(e=>e.Stage=="queued").ExecuteUpdateAsync(s=>s.SetProperty(e=>e.Stage,"interrupted").SetProperty(e=>e.Error,"软件已关闭，请手动继续。"));
            var ids=db.ArchiveEntries.Where(e=>e.Stage=="interrupted"||e.Stage=="paused_storage").Select(e=>e.Id);
            await db.MediaFiles.Where(f=>f.ArchiveEntryId!=null && ids.Contains(f.ArchiveEntryId)).ExecuteUpdateAsync(s=>s.SetProperty(f=>f.Status,MediaFileStatus.ArchivePaused));
            await db.ArchiveJobs.Where(j=>(j.ScanRecursive!=null && !j.ScanComplete || j.FolderConcurrency!=null) && (j.Status=="queued"||j.Status=="running"||j.Status=="preparing")).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.Status,"interrupted").SetProperty(j=>j.Error,"软件已关闭，目录扫描未完成，请手动继续。"));
            var jobs=await db.ArchiveJobs.Where(j=>j.Status=="queued"||j.Status=="running"||j.Status=="preparing").Select(j=>j.Id).ToListAsync();
            foreach(var job in jobs) await RefreshJobAsync(job);
        }
        finally{_commit.Release();}
    }
}
