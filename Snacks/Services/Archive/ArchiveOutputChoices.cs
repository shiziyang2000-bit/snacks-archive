using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    internal static ArchiveOutputPolicy OutputChoice(ArchiveSettings s) => new()
    {
        OutputMode = s.OutputMode, OutputDirectory = s.OutputDirectory, BackupEnabled = s.BackupEnabled,
        BackupLocation = s.BackupLocation, BackupDirectory = s.BackupDirectory
    };
    private static ArchiveSettings WithOutputChoice(ArchiveSettings rules, ArchiveOutputPolicy? choice)
    {
        if (choice == null) return rules;
        rules.OutputMode = choice.OutputMode; rules.OutputDirectory = choice.OutputDirectory;
        rules.BackupEnabled = choice.BackupEnabled; rules.BackupLocation = choice.BackupLocation;
        rules.BackupDirectory = choice.BackupDirectory;
        return rules;
    }
    private static ArchiveOutputPolicy? PendingChoice(ArchiveJob job) => job.PendingOutputPolicyJson == null
        ? null : JsonSerializer.Deserialize<ArchiveOutputPolicy>(job.PendingOutputPolicyJson, Json);
    private static ArchiveSettings DiscoveryRules(ArchiveJob job) => WithOutputChoice(Rules(job), PendingChoice(job));
    private static ArchiveSettings EntryRules(ArchiveJob job, ArchiveEntry entry) =>
        WithOutputChoice(Rules(job), JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json)?.OutputPolicy);

    private static bool CanApplyOutputChoice(ArchiveEntry entry, ArchiveMediaInfo media, ArchiveOutputPolicy choice)
    {
        if (entry.RollbackPath != null || entry.RestoreTempPath != null || entry.RestoreDisplacedPath != null ||
            media.Transfer is { BackupReadyPath: not null } or { Published: true } or { RestorePublished: true }) return false;
        // Encoding scratch alone has not touched the original. Disabling future
        // backups is safe when the in-place destination remains exactly the same.
        if (entry.TempOutputPath == null) return true;
        if (choice.OutputMode != "inPlace" || choice.BackupEnabled || entry.OutputPath != entry.Path) return false;
        // A crash can occur after the backup rename but before its journal write.
        // Inspect only this exceptional scratch row's registered backup path, not
        // the ordinary backlog. Access/I/O uncertainty must never mean "missing".
        if (entry.BackupPath == null) return true;
        try { _ = File.GetAttributes(entry.BackupPath); return false; }
        catch (FileNotFoundException)
        {
            try
            {
                var parent = File.GetAttributes(Path.GetDirectoryName(entry.BackupPath)!);
                return (parent & FileAttributes.Directory) != 0 && (parent & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // No filesystem traversal, probing or content reads. Only this paused owner's
    // pending rows are changed, in bounded pages and one database transaction.
    public async Task<object> ApplyPendingOutputAsync(string id, ArchiveOutputPolicy requested)
    {
        var settings = WithOutputChoice(GetSettings(), Clone(requested));
        settings = PrepareSettings(settings);
        if (settings.OutputMode == "saveAs" && settings.OutputDirectory == null)
            throw new ArchiveException("output_directory_required", "请先选择另存输出目录。");
        if (settings.OutputMode == "inPlace" && settings.BackupEnabled && settings.BackupLocation == "selectedDirectory" && settings.BackupDirectory == null)
            throw new ArchiveException("backup_directory_required", "请先选择原片备份目录。");
        var choice = OutputChoice(settings);
        await _mutation.WaitAsync();
        try
        {
            if (_stopping) throw new ArchiveException("shutting_down", "软件正在退出。");
            await using var db = await _db.CreateDbContextAsync();
            var job = await db.ArchiveJobs.SingleOrDefaultAsync(j => j.Id == id) ?? throw new ArchiveException("not_found", "任务不存在。");
            var active = _running.Keys.ToArray();
            if (job.Status is not ("paused_storage" or "interrupted") || _scanTasks.ContainsKey(id) || _folderRestarts.ContainsKey(id) ||
                await db.ArchiveEntries.AnyAsync(e => active.Contains(e.Id) && (e.FolderJobId == id || e.FolderJobId == null && e.JobId == id)))
                throw new ArchiveException("job_busy", "请先暂停此任务并等待当前文件收尾，再应用输出选择。");
            await using var transaction = await db.Database.BeginTransactionAsync();
            var updated = 0; var protectedTransactions = 0; string after = "";
            while (true)
            {
                var rows = await db.ArchiveEntries.Where(e => (e.FolderJobId == id || e.FolderJobId == null && e.JobId == id) &&
                    (e.Stage == "waiting" || e.Stage == "waiting_retry" || e.Stage == "queued" || e.Stage == "interrupted" || e.Stage == "paused_storage" || e.Stage == "failed") &&
                    string.Compare(e.Id, after) > 0).OrderBy(e => e.Id).Take(32).ToListAsync();
                if (rows.Count == 0) break;
                after = rows[^1].Id;
                foreach (var entry in rows)
                {
                    var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json) ?? new();
                    if (!CanApplyOutputChoice(entry, media, choice)) { protectedTransactions++; continue; }
                    media.OutputPolicy = Clone(choice);
                    if (choice.OutputMode == "inPlace" && !choice.BackupEnabled) entry.BackupPath = null;
                    entry.MediaJson = JsonSerializer.Serialize(media, Json);
                    entry.UpdatedAt = DateTime.UtcNow; updated++;
                }
                await db.SaveChangesAsync();
                foreach (var entry in rows) db.Entry(entry).State = EntityState.Detached;
            }
            job.PendingOutputPolicyJson = JsonSerializer.Serialize(choice, Json);
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            await ChangedAsync(id);
            return new { jobId = id, updated, protectedTransactions, choice, historyUnchanged = true };
        }
        finally { _mutation.Release(); }
    }

    private static void ApplyEntryOutputPaths(ArchiveEntry entry, ArchiveJob origin, ArchiveSettings rules)
    {
        var root = string.IsNullOrWhiteSpace(origin.SourceRoot) ? Path.GetDirectoryName(entry.Path)! : origin.SourceRoot;
        var relative = Path.GetRelativePath(root, entry.Path);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar)) relative = Path.Combine(ShortHash(Path.GetDirectoryName(entry.Path)!), Path.GetFileName(entry.Path));
        var project = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "-" + ShortHash(root);
        entry.OutputPath = rules.OutputMode == "inPlace" ? entry.Path :
            Path.Combine(rules.OutputDirectory ?? throw new ArchiveException("output_directory_required", "请先选择另存输出目录。"), project, relative);
        if (rules.OutputMode == "saveAs" && Within(entry.Path, rules.OutputDirectory!))
            throw new ArchiveException("output_source_overlap", "来源位于输出目录中，请选择不同目录。");
        entry.BackupPath = rules.BackupEnabled && rules.OutputMode == "inPlace"
            ? Path.Combine(rules.BackupLocation == "sameVolume" ? Path.Combine(root, ".Snacks-Archive-Originals") :
                rules.BackupDirectory ?? throw new ArchiveException("backup_directory_required", "请先选择原片备份目录。"), project, relative)
            : null;
    }
}
