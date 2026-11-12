using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<SnacksDbContext> _db;
    private readonly ConfigFileService _config;
    private readonly FileService _files;
    private readonly ArchiveMediaService _media;
    private readonly IHubContext<TranscodingHub> _hub;
    private readonly object _settingsLock;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly SemaphoreSlim _commit = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobTokens = new();
    private readonly ConcurrentDictionary<string, byte> _cacheUsers = new();
    private readonly ConcurrentDictionary<string, Preview> _previews = new();
    private readonly ConcurrentDictionary<string, RestorePreview> _restores = new();
    private ArchiveSettings _settings;
    private volatile bool _stopping;
    public event Action? QueueReady;
    public bool IsCustom => _settings.Mode == "custom";
    public bool IsStopping => _stopping;
    private sealed record Preview(string Id, DateTime ExpiresAt, ArchiveSettings Rules, string Root, List<ArchivePreviewRow> Rows, bool? ScanRecursive = null, bool ScanComplete = true);
    private sealed record RestorePreview(DateTime ExpiresAt, List<RestoreRow> Rows);
    public sealed record RestoreRow(string Id, string Path, string? BackupPath, string Action, string ReasonCode, string Reason);
    public ArchiveService(IDbContextFactory<SnacksDbContext> db, ConfigFileService config, FileService files, ArchiveMediaService media, IHubContext<TranscodingHub> hub)
    {
        _db = db; _config = config; _files = files; _media = media; _hub = hub;
        _settingsLock = config.SyncRoot;
        _settings = config.Load<ArchiveSettings>("archive.json");
    }
    public ArchiveSettings GetSettings() { lock (_settingsLock) return Clone(_settings); }
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    internal static ArchiveSettings Rules(ArchiveJob job) => JsonSerializer.Deserialize<ArchiveSettings>(job.RulesSnapshotJson, Json)!;
    public ArchiveSettings SaveSettings(ArchiveSettings settings)
    {
        lock (_settingsLock)
        {
            var prepared = PrepareSettings(settings);
            _config.Save("archive.json", prepared);
            AcceptPersistedSettings(prepared);
            return Clone(prepared);
        }
    }
    internal ArchiveSettings PrepareSettings(ArchiveSettings settings)
    {
        var prepared = Clone(settings);
        ValidateSettings(prepared);
        prepared.OutputDirectory = NormalizeDirectory(prepared.OutputDirectory);
        prepared.BackupDirectory = NormalizeDirectory(prepared.BackupDirectory);
        prepared.CacheDirectory = NormalizeDirectory(prepared.CacheDirectory);
        prepared.Revision = _settings.Revision + 1; prepared.RuleVersion = 1;
        return prepared;
    }
    internal static void ValidateSettings(ArchiveSettings settings)
    {
        if (settings.Mode is not ("custom" or "original") || settings.OutputMode is not ("inPlace" or "saveAs") || settings.BackupLocation is not ("selectedDirectory" or "sameVolume")) throw new ArchiveException("invalid_settings", "归档模式或备份方式无效。");
        if (!double.IsFinite(settings.TargetMbps) || settings.TargetMbps <= 0 || settings.TargetMbps > 10000 || !double.IsFinite(settings.CacheLimitGb) || settings.CacheLimitGb <= 0 || settings.CacheLimitGb > 100000 || settings.Concurrency is < 1 or > 20 || settings.BackupReminderDays < 1) throw new ArchiveException("invalid_settings", "码率与缓存上限必须为正数；并发范围为 1—20。");
    }
    internal void AcceptPersistedSettings(ArchiveSettings prepared)
    {
        lock (_settingsLock)
        {
            _settings = Clone(prepared);
            _ = Task.Run(async () => { try { await TrimCacheAsync(prepared); } catch (Exception ex) { Log.Warning("Archive cache maintenance: " + ex.Message); } });
        }
    }
    private static string? NormalizeDirectory(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    public string CacheDirectory(ArchiveSettings? settings = null) => (settings ?? GetSettings()).CacheDirectory ?? Path.Combine(_files.GetWorkingDirectory(), "archive-cache");
    public static bool Within(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public async Task<bool> IsExcludedAsync(string path) => await IsExcludedAsync(path, GetSettings());
    private async Task<bool> IsExcludedAsync(string path, ArchiveSettings rules)
    {
        if (IsExcludedPath(path, rules)) return true;
        await using var db = await _db.CreateDbContextAsync();
        return await db.ArchiveEntries.AnyAsync(e => e.BackupPath == path || (e.OutputPath == path && e.OutputPath != e.Path)) || await db.ArchiveCaches.AnyAsync(c => c.Path == path);
    }
    public async Task<object> PreviewAsync(ArchivePreviewRequest request, CancellationToken ct)
    {
        await _previewGate.WaitAsync(ct);
        try { return await PreviewCoreAsync(request, ct); }
        finally { _previewGate.Release(); }
    }
    private async Task<object> PreviewCoreAsync(ArchivePreviewRequest request, CancellationToken ct)
    {
        if (_stopping) throw new ArchiveException("shutting_down", "软件正在安全退出，暂停接收新任务。");
        if (request.Intent is not ("normal" or "retryFailed" or "rearchive")) throw new ArchiveException("invalid_intent", "未知归档操作。");
        var rules = GetSettings(); if (rules.Mode != "custom") throw new ArchiveException("original_mode", "当前为原版模式，请切换到定制归档规则。");
        if (!string.IsNullOrWhiteSpace(request.DirectoryPath))
        {
            if (request.Intent != "normal" || request.FilePaths?.Count > 0 || request.EntryIds?.Count > 0)
                throw new ArchiveException("invalid_selection", "目录分批任务请单独选择根目录；重试和重新归档仍使用历史条目。");
            return await PreviewDirectoryAsync(request, rules, ct);
        }
        if ((request.FilePaths?.Count ?? 0) + (request.EntryIds?.Count ?? 0) > 200)
            throw new ArchiveException("selection_limit", "单次文件预览最多 200 项；大量素材请选择根目录分批处理。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = "";
        if (request.FilePaths != null) foreach (var p in request.FilePaths) paths.Add(Path.GetFullPath(p));
        if (request.EntryIds is { Count: > 0 })
        {
            await using var db = await _db.CreateDbContextAsync(ct);
            foreach (var entry in await db.ArchiveEntries.Where(e => request.EntryIds.Contains(e.Id)).AsNoTracking().ToListAsync(ct))
                paths.Add(File.Exists(entry.Path) ? entry.Path : request.Intent=="rearchive" && File.Exists(entry.OutputPath) ? entry.OutputPath : entry.Path);
        }
        if (paths.Count == 0 && root.Length == 0) throw new ArchiveException("empty_selection", "请先选择素材目录或文件。");
        if (root.Length == 0 && paths.Count > 0) root = Path.GetDirectoryName(paths.First())!;
        var rows = new List<ArchivePreviewRow>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var row = await PrepareOneAsync(path, root, rules, request, ct);
            rows.Add(row);
        }
        foreach (var group in rows.Where(r => r.Action == "archive").GroupBy(r => r.OutputPath,StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) foreach (var row in group) Block(row,"output_conflict", "所选文件映射到同一输出位置。");
        var (id, expires) = CachePreviewIfReady(rules, root, rows);
        var confirmations = new List<string>();
        if (rules.OutputMode == "inPlace" && !rules.BackupEnabled && rows.Any(r => r.Action == "archive")) confirmations.Add("noBackup");
        if (rows.Any(r => r.Action == "archive" && r.RequiresCurrentOutputConfirmation)) confirmations.Add("useCurrentOutput");
        return new { previewId = id, expiresAt = expires, settingsRevision = rules.Revision, summary = new { total = rows.Count, ready = rows.Count(r => r.Action == "archive"), skipped = rows.Count(r => r.Action == "skip"), blocked = rows.Count(r => r.Action == "blocked") }, rows, requiredConfirmations = confirmations };
    }
    internal (string? Id, DateTime? ExpiresAt) CachePreviewIfReady(ArchiveSettings rules, string root, List<ArchivePreviewRow> rows, bool? scanRecursive = null, bool scanComplete = true)
    {
        if (scanRecursive == null && !rows.Any(r => r.Action == "archive")) return (null, null);
        var now = DateTime.UtcNow;
        foreach (var p in _previews.Where(p => p.Value.ExpiresAt < now).ToList()) _previews.TryRemove(p.Key,out _);
        if (_previews.Count >= 32) throw new ArchiveException("preview_limit", "待开始的扫描过多，请先处理已有扫描。");
        var id = Guid.NewGuid().ToString("N"); var expires = now.AddHours(2);
        _previews[id] = new Preview(id, expires, rules, root, rows, scanRecursive, scanComplete);
        return (id, expires);
    }
    private static bool IsVideoExtension(string path) => new[] { ".mkv", ".mov", ".mp4", ".m4v", ".mxf", ".m2v", ".avi", ".ts", ".m2ts", ".mts", ".mpeg", ".mpg", ".wmv", ".webm", ".3gp", ".flv" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    private static void Block(ArchivePreviewRow row, string code, string reason) { row.Action = "blocked"; row.ReasonCode = code; row.Reason = reason; }
    private static void Skip(ArchivePreviewRow row, string code, string reason) { row.Action = "skip"; row.ReasonCode = code; row.Reason = reason; }
    private async Task PrepareRowAsync(ArchivePreviewRow row, string root, ArchiveSettings rules, ArchivePreviewRequest request, CancellationToken ct, string? resumeEntryId = null)
    {
        if (IsMacMetadataPath(row.Path)) { Skip(row,"filesystem_metadata","macOS 系统附属文件不是视频，直接跳过。"); return; }
        var excluded=await IsExcludedAsync(row.Path, rules);
        if (excluded && request.Intent=="normal") { Skip(row,"excluded_directory","备份、缓存、另存输出及工作暂存文件不参与普通扫描。"); return; }
        await using var db = await _db.CreateDbContextAsync(ct);
        var history = await db.ArchiveEntries.Where(e => (e.Path == row.Path || e.OutputPath == row.Path) && (e.DiscoveryKey == null || e.Stage != "skipped")).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);
        if (history != null && history.Id != resumeEntryId && NeedsTransactionReview(history))
        { Block(row,"transaction_requires_review",TransactionReviewReason); return; }
        var canSkipMissing = history == null || (history.Stage is not ("completed" or "restored") && !NeedsTransactionReview(history));
        _ = ReadSourceMetadata(row.Path, root, canSkipMissing, () => File.GetAttributes(row.Path));
        if (excluded && !(request.Intent=="rearchive" && history!=null && request.EntryIds?.Contains(history.Id)==true && row.Path==history.OutputPath && history.OutputPath!=history.Path))
        { Skip(row,"excluded_directory","备份、缓存及工作暂存文件不能作为归档替换目标。"); return; }
        if (history != null)
        {
            row.HistoryState = history.Stage;
            if (history.Id != resumeEntryId && history.Stage is "waiting" or "waiting_retry" or "queued" or "encoding" or "muxing" or "validating" or "backing_up" or "committing" or "paused_storage" or "interrupted" or "restoring") { Skip(row,"existing_task","该素材已属于持久任务；请在历史中继续未完成任务。"); return; }
            if (request.Intent == "normal" && history.Stage is "completed" or "restored" or "failed")
            {
                var sameFile = ReadSourceMetadata(row.Path, root, canSkipMissing, () => MatchesHistoryFile(history, row.Path));
                if (sameFile == null) { Block(row,"history_identity_unknown","旧记录未保存文件时间信息，无法确认当前同名文件；请明确选择历史条目重试或重新归档。"); return; }
                if (sameFile == false) { history = null; row.HistoryState = null; }
            }
        }
        if (history != null)
        {
            if (request.Intent == "normal" && history.Stage is "completed" or "restored" or "failed") { Skip(row,"history_" + history.Stage,history.Stage == "failed" ? "该路径曾失败，请明确选择失败项重试：" + history.Error : history.Stage == "restored" ? "已恢复原片，普通扫描不会重新归档。" : "该路径已有已完成记录，普通扫描跳过。"); return; }
            if (request.Intent == "retryFailed" && ((history.Stage != "failed" && !(history.Id == resumeEntryId && history.Stage == "waiting_retry")) || request.EntryIds == null || !request.EntryIds.Contains(history.Id))) { Skip(row,"explicit_failed_selection_required","仅可显式选择失败项重试。"); return; }
            if (request.Intent == "rearchive")
            {
                if (request.EntryIds == null || !request.EntryIds.Contains(history.Id)) { Block(row,"explicit_rearchive_selection_required","重新归档需要明确选择对应历史条目。"); return; }
                if (history.BackupPath != null && File.Exists(history.BackupPath))
                { row.InputPath = history.BackupPath; row.BackupPath = history.BackupPath; }
                else if (history.Stage == "completed") row.RequiresCurrentOutputConfirmation = true;
            }
        }
        else if (request.Intent != "normal") { Block(row,"history_not_found","所选路径没有对应历史，请按新素材扫描。"); return; }
        if (IsSmallSource(row.InputPath, root, canSkipMissing && row.InputPath == row.Path, out var sourceBytes))
        { row.SourceBytes = sourceBytes; Skip(row, SmallSourceCode, SmallSourceReason); return; }
        CheckSourceFileAccess(row.Path, root, rules.OutputMode == "inPlace", canSkipMissing);
        if (row.InputPath != row.Path) CheckSourceFileAccess(row.InputPath, root, false, false);
        var media = await ProbeSourceAsync(row.InputPath, root, canSkipMissing && row.InputPath == row.Path, ct); media.PathIdentity=ReadSourceMetadata(row.Path, root, canSkipMissing && row.InputPath == row.Path, () => FileIdentity(row.Path)); row.Media = media; row.Codec = media.Codec; row.SourceMbps = media.VideoMbps; row.SourceBytes = media.Bytes;
        if (media.Alpha) { Skip(row,"alpha_channel","带透明通道，保持原片不转码、不封装、不移动。"); return; }
        var (target, skip) = ArchiveMediaService.CalculateTarget(media.Codec,media.Profile,media.VideoMbps,rules.TargetMbps); row.TargetMbps = target;
        if (skip != null) { Skip(row,skip,skip == "codec_unconfigured" ? "该编码尚未配置归档规则。" : skip == "hevc_below_threshold" ? "HEVC 低于目标上限的 1.25 倍，保持原样。" : "计算目标不低于源码率或无法可靠测量，保持原样。"); return; }
        var unsupported = await _media.UnsupportedReasonAsync(media,row.InputPath,ct);
        if (unsupported != null) { Skip(row,unsupported,"无法完整保留此素材的封装、视频规格或元数据（" + unsupported + "），保持原样。"); return; }
        row.EncoderMode = media.Encoder;
        var relative = Path.GetRelativePath(root,row.Path); if (relative.StartsWith(".." + Path.DirectorySeparatorChar)) relative = Path.Combine(ShortHash(Path.GetDirectoryName(row.Path)!),Path.GetFileName(row.Path));
        var project = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "-" + ShortHash(root);
        if (rules.OutputMode == "saveAs")
        {
            if (rules.OutputDirectory == null) throw new ArchiveException("output_directory_required","请先在设置中选择另存输出目录。");
            row.OutputPath = Path.Combine(rules.OutputDirectory,project,relative);
            if (Within(row.Path,rules.OutputDirectory)) throw new ArchiveException("output_source_overlap","来源位于输出目录中，请选择不同目录。");
            if (File.Exists(row.OutputPath) || Directory.Exists(row.OutputPath)) throw new ArchiveException("output_conflict","输出位置已有文件，不能覆盖。",row.OutputPath);
        }
        else row.OutputPath = row.Path;
        if (rules.BackupEnabled && rules.OutputMode == "inPlace")
        {
            if (rules.BackupLocation == "selectedDirectory" && rules.BackupDirectory == null) throw new ArchiveException("backup_directory_required","默认保留原片备份，请先选择备份目录。");
            row.BackupPath ??= Path.Combine(rules.BackupLocation == "sameVolume" ? Path.Combine(root,".Snacks-Archive-Originals") : rules.BackupDirectory!,project,relative);
            if (row.BackupPath == row.Path || row.BackupPath == row.OutputPath) throw new ArchiveException("backup_conflict","备份位置与源文件冲突。");
            if (Directory.Exists(row.BackupPath)) throw new ArchiveException("backup_conflict","备份位置是目录，不能覆盖。",row.BackupPath);
            if (File.Exists(row.BackupPath) && row.InputPath != row.BackupPath)
                row.BackupPath = VersionedBackupPath(row.BackupPath, row.Id);
            if (row.InputPath == row.BackupPath)
                media.Transfer = new ArchiveTransferState { BackupReadyPath = row.BackupPath }; // explicitly selected registered original
            else if (File.Exists(row.BackupPath) || Directory.Exists(row.BackupPath))
                throw new ArchiveException("backup_conflict","新的备份位置已被占用，不能覆盖。",row.BackupPath);
        }
        row.Action = "archive"; row.ReasonCode = "ready"; row.Reason = rules.OutputMode == "saveAs" ? "成片另存，原文件保留。" : rules.BackupEnabled ? "原位替换并额外保留原片备份；备份需手动管理。" : "原位替换，成功后只保留压缩成片；不生成长期备份。";
    }
    private static string VersionedBackupPath(string path, string id) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "." + id + Path.GetExtension(path));
    private static string ShortHash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];
    public async Task<object> StartAsync(ArchiveStartRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128) throw new ArchiveException("request_id_required","缺少有效任务请求标识。");
        await _mutation.WaitAsync(ct);
        try
        {
            if (_stopping) throw new ArchiveException("shutting_down","软件正在安全退出。");
            await using var db = await _db.CreateDbContextAsync(ct);
            var existing = await db.ArchiveJobs.FirstOrDefaultAsync(j => j.RequestId == request.RequestId,ct);
            if (existing != null) return new { jobId=existing.Id, queued=await db.ArchiveEntries.CountAsync(e => e.JobId==existing.Id,ct), skipped=0, failed=0 };
            if (!_previews.TryGetValue(request.PreviewId,out var preview) || preview.ExpiresAt < DateTime.UtcNow) throw new ArchiveException("preview_expired","扫描已过期，请重新扫描。");
            if (preview.Rules.Revision != GetSettings().Revision) throw new ArchiveException("preview_expired","扫描已过期，请重新扫描。");
            if (preview.ScanRecursive != null)
                return await StartDirectoryAsync(db, request, preview, ct);
            var rows = preview.Rows.Where(r => r.Action == "archive" && (request.SelectedIds == null || request.SelectedIds.Contains(r.Id))).ToList();
            if (rows.Count == 0) throw new ArchiveException("empty_selection","没有可开始的归档文件。");
            if (await OutstandingDirectoryEntries(db).CountAsync(ct) + rows.Count > DirectoryBatchSize)
                throw new ArchiveException("directory_queue_full", "任务缓冲区已满（最多 32 个未完成文件），请待空位释放后开始。");
            if (preview.Rules.OutputMode == "inPlace" && !preview.Rules.BackupEnabled && !request.ConfirmedNoBackup) throw new ArchiveException("confirmation_required","本次原地任务不保留原片备份，必须单独确认。",new { confirmation="noBackup" });
            if (rows.Any(r => r.RequiresCurrentOutputConfirmation) && !request.ConfirmedUseCurrentOutput) throw new ArchiveException("confirmation_required","无有效原片备份；只能以当前压缩成片再次处理，必须确认。",new { confirmation="useCurrentOutput" });
            foreach (var row in rows)
            {
                var latest = await db.ArchiveEntries.AsNoTracking().Where(e => (e.Path == row.Path || e.OutputPath == row.Path) && (e.DiscoveryKey == null || e.Stage != "skipped"))
                    .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);
                if (latest != null && NeedsTransactionReview(latest)) throw new ArchiveException("transaction_requires_review",TransactionReviewReason,row.Path);
                RequireNonemptyFile(row.Path); RequireNonemptyFile(row.InputPath);
                if (await db.ArchiveEntries.AnyAsync(e => (e.Path == row.Path || e.InputPath == row.InputPath) && (e.Stage == "queued" || e.Stage == "encoding" || e.Stage == "muxing" || e.Stage == "validating" || e.Stage == "backing_up" || e.Stage == "committing" || e.Stage == "paused_storage" || e.Stage == "interrupted"),ct)) throw new ArchiveException("already_queued","素材已属于其他未完成任务。",row.Path);
            }
            var job = new ArchiveJob { RequestId=request.RequestId,SourceRoot=preview.Root,RulesSnapshotJson=JsonSerializer.Serialize(preview.Rules,Json),ConfirmedNoBackup=request.ConfirmedNoBackup,ConfirmedUseCurrentOutput=request.ConfirmedUseCurrentOutput };
            db.ArchiveJobs.Add(job);
            foreach (var row in rows)
            {
                await AddPreparedRowAsync(db, job, row, ct);
            }
            await db.SaveChangesAsync(ct); _previews.TryRemove(request.PreviewId,out _); QueueReady?.Invoke();
            await ChangedAsync(job.Id);
            return new { jobId=job.Id,queued=rows.Count,skipped=preview.Rows.Count-rows.Count,failed=0 };
        }
        finally { _mutation.Release(); }
    }
    // Only the explicitly enabled AutoScanService calls this. Manual and public legacy
    // entrances get preview-required instead of force bypassing the protected workflow.
    public async Task<string> EnqueueMonitoredAsync(string path, CancellationToken ct)
    {
        if (!IsCustom || _stopping || await IsExcludedAsync(path)) return "";
        var result = await PreviewAsync(new ArchivePreviewRequest { FilePaths=[path] },ct);
        var json=JsonSerializer.SerializeToElement(result,Json); var id=json.GetProperty("previewId").GetString()!;
        if (json.GetProperty("requiredConfirmations").GetArrayLength()>0)
        {
            await _hub.Clients.All.SendAsync("ArchiveConfirmationRequired",result,ct);
            return "";
        }
        if (json.GetProperty("summary").GetProperty("ready").GetInt32()==0) return "";
        var started=JsonSerializer.SerializeToElement(await StartAsync(new ArchiveStartRequest { RequestId=Guid.NewGuid().ToString("N"),PreviewId=id },ct),Json);
        return started.GetProperty("jobId").GetString()!;
    }
    private async Task ChangedAsync(string jobId) { try { await _hub.Clients.All.SendAsync("ArchiveJobUpdated",new {jobId}); await _hub.Clients.All.SendAsync("QueueChanged"); } catch { } }
}
