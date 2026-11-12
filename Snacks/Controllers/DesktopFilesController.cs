using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

// Only the desktop main process holds this capability. It resolves registered
// identifiers to paths; it never accepts an arbitrary renderer-supplied path.
[ApiController]
[Route("api/desktop/files")]
public sealed class DesktopFilesController(IDbContextFactory<SnacksDbContext> factory, TranscodingService transcoding) : ControllerBase
{
    [HttpGet("{kind}/{id}/{target}")]
    public async Task<IActionResult> Resolve(string kind, string id, string target)
    {
        var secret = Environment.GetEnvironmentVariable("SNACKS_DESKTOP_SHUTDOWN_TOKEN");
        var supplied = Request.Headers["X-Snacks-Desktop-Token"].ToString();
        if (string.IsNullOrEmpty(secret) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(supplied))) return Unauthorized();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var path = await ResolvePathAsync(db, kind == "work" ? transcoding.GetWorkItem(id) : null, kind, id, target);
            return Ok(new { path, directory = target is "backup-folder" or "recycle-folder" });
        }
        catch (ArchiveException ex) { return Conflict(new { code = ex.Code, message = ex.Message }); }
    }

    internal static async Task<string> ResolvePathAsync(SnacksDbContext db, WorkItem? live, string kind, string id, string target)
    {
        if (target is not ("output" or "backup-folder" or "source" or "recycle-folder")) throw new ArchiveException("invalid_action", "文件操作无效。");
        string? path = null;
        if (kind == "job" && target == "recycle-folder")
        {
            var job = await db.ArchiveJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
            path = job == null ? null : ArchiveService.NasRecycleFolder(job.SourceRoot);
        }
        var entryId = kind == "archive" ? id : live?.ArchiveEntryId;
        // Archive work-item IDs equal entry IDs, including after restart.
        var entry = entryId == null && kind == "work"
            ? await db.ArchiveEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id)
            : entryId == null ? null : await db.ArchiveEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId);
        if (entry != null)
        {
            path = target switch
            {
                "output" when entry.Stage == "completed" => entry.OutputPath,
                "backup-folder" when entry.BackupPath != null => Path.GetDirectoryName(entry.BackupPath),
                "source" => entry.Path,
                _ => null
            };
        }
        else if (kind == "work" && target == "output")
        {
            path = live is { Status: WorkItemStatus.Completed } ? live.OutputPath : null;
            path ??= await db.EncodeHistory.AsNoTracking().Where(e => e.JobId == id && e.Outcome == "Completed")
                .OrderByDescending(e => e.CompletedAt).Select(e => e.OutputPath).FirstOrDefaultAsync();
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArchiveException("output_path_unavailable", "该记录没有可用的成片或备份路径；旧普通任务未记录成片路径时不会猜测原文件位置。");
        path = Path.GetFullPath(path);
        if (target is "backup-folder" or "recycle-folder" ? !Directory.Exists(path) : !System.IO.File.Exists(path))
            throw new ArchiveException("file_missing", "文件或文件夹不存在，可能已手动移动、删除或存储未连接：" + path);
        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArchiveException("path_conflict", "登记路径现在是符号链接，请直接在 Finder 中核对：" + path);
        return path;
    }
}
