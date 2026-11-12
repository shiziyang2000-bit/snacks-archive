using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Archive;

public sealed partial class ArchiveBoundedDiscoveryTests
{
    private async Task<string> SeedLegacy(int count, string? jobRoot = null)
    {
        var job = new ArchiveJob { RequestId = Guid.NewGuid().ToString(), SourceRoot = jobRoot ?? Source, Status = "interrupted", RulesSnapshotJson = JsonSerializer.Serialize(_archive.GetSettings(), ArchiveService.Json) };
        await using var db = await _db.CreateDbContextAsync(); db.ArchiveJobs.Add(job);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(Source, $"old-{i:D4}.mkv"); await WriteMediaAsync(path, "original");
            var media = new ArchiveMediaInfo { Codec = "h264", PixelFormat = "yuv420p", Encoder = "libx265", Width = 32, Height = 32, Fps = 25, Duration = 1, VideoMbps = 40, PathIdentity = ArchiveService.FileIdentity(path), ProbeJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p"}]}""" };
            var entry = new ArchiveEntry { JobId = job.Id, Path = path, InputPath = path, OutputPath = path, BackupPath = Path.Combine(_root, "legacy-backups", $"old-{i:D4}.mkv"), Stage = "interrupted", TargetMbps = 20, SourceMbps = 40, SourceBytes = 10_000_000, Codec = "h264", EncoderMode = "libx265", MediaJson = JsonSerializer.Serialize(media, ArchiveService.Json) };
            db.ArchiveEntries.Add(entry);
            db.MediaFiles.Add(new MediaFile { FilePath = path, FileName = Path.GetFileName(path), Directory = Source, ArchiveEntryId = entry.Id, Status = MediaFileStatus.ArchivePaused });
        }
        await db.SaveChangesAsync(); return job.Id;
    }
    private async Task AddMaskingSkips(string originalJob)
    {
        await using var db = await _db.CreateDbContextAsync();
        var auxiliary = new ArchiveJob { SourceRoot = Source, RequestId = Guid.NewGuid().ToString(), Status = "completed", ScanRecursive = true, ScanComplete = true };
        db.ArchiveJobs.Add(auxiliary);
        foreach (var old in await db.ArchiveEntries.AsNoTracking().Where(e=>e.JobId==originalJob).ToListAsync())
            db.ArchiveEntries.Add(new ArchiveEntry { JobId=auxiliary.Id, Path=old.Path, InputPath=old.Path, OutputPath=old.Path, Stage="skipped", ReasonCode="existing_task", DiscoveryKey=old.Path.ToUpperInvariant(), CreatedAt=DateTime.UtcNow.AddSeconds(1) });
        await db.SaveChangesAsync();
    }
    private async Task<string> Direct(string? root = null) => Json(await _archive.StartFolderAsync(new() { DirectoryPath = root ?? Source })).GetProperty("jobId").GetString()!;
    private async Task<List<ArchiveEntry>> Owned(string id)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ArchiveEntries.AsNoTracking().Where(e => e.FolderJobId == id).ToListAsync();
    }
    private async Task EncodeOwned(string id)
    {
        foreach (var entry in (await Owned(id)).Where(e => e.Stage == "queued"))
        {
            var item = new WorkItem { Id=entry.Id, ArchiveEntryId=entry.Id, Path=entry.Path };
            await _archive.ExecuteAsync(item,(args,ct)=>WriteMediaAsync(args.Arguments.Last(),"encoded",ct),_=>Task.CompletedTask,CancellationToken.None);
            Assert.Equal(WorkItemStatus.Completed,item.Status);
        }
    }
    private async Task FinishOwned(string id)
    {
        for (var batch = 0; batch < 200; batch++)
        {
            await Until(async () => (await Owned(id)).Any(e=>e.Stage=="queued") || Json(await _archive.JobAsync(id)).GetProperty("status").GetString()=="completed");
            await using (var db = await _db.CreateDbContextAsync()) Assert.InRange(await db.MediaFiles.CountAsync(f=>f.Status==MediaFileStatus.Queued || f.Status==MediaFileStatus.Processing),0,32);
            if (Json(await _archive.JobAsync(id)).GetProperty("status").GetString()=="completed") return;
            await EncodeOwned(id);
        }
        Assert.Fail("More batches than this small fixture should need");
    }

    [Fact]
    public async Task DirectFolderAdopts130OldInterruptedRowsDespiteNewerSkipsAndAddsNewFiles()
    {
        var old = await SeedLegacy(130);
        var originals = (await Entries(old)).Select(e => (e.Id,e.BackupPath,e.TargetMbps)).ToList();
        await AddMaskingSkips(old); await CreateFiles(4);
        var id = await Direct(); Assert.Equal(old,id); Assert.Equal(0,Probes);
        await Until(async () => (await Owned(id)).Count(e=>e.Stage=="queued")==32);
        Assert.Equal(98,(await Owned(id)).Count(e=>e.Stage=="waiting"));
        Assert.Equal(id,await Direct()); Assert.Equal(130,(await Owned(id)).Count);
        await FinishOwned(id);
        var entries = await Entries(old);
        foreach (var original in originals)
        {
            var actual = Assert.Single(entries,e=>e.Id==original.Id);
            Assert.Equal(old,actual.JobId); Assert.Equal(original.BackupPath,actual.BackupPath); Assert.Equal(original.TargetMbps,actual.TargetMbps); Assert.Equal("completed",actual.Stage);
        }
        Assert.Equal(134,Json(await _archive.JobAsync(id)).GetProperty("summary").GetProperty("completed").GetInt32());
        Assert.Equal(4,Probes); // none of the 130 known files were re-probed
    }

    [Fact]
    public async Task LegacyContinueAlsoReleasesOnly32AndDoesNotDiscoverUnselectedFiles()
    {
        var old=await SeedLegacy(130); await CreateFiles(3);
        await _archive.ContinueAsync(old);
        await Until(async ()=>(await Owned(old)).Count(e=>e.Stage=="queued")==32);
        Assert.Equal(98,(await Owned(old)).Count(e=>e.Stage=="waiting"));
        await FinishOwned(old);
        Assert.Equal(130,(await Entries(old)).Count); Assert.Equal(0,Probes);
        Assert.Equal("original",await ReadMediaAsync(Path.Combine(Source,"clip-0000.mkv")));
    }

    [Fact]
    public async Task FailedEntriesRetryOncePerExplicitFolderStartAndNeverSpinWithinTheRun()
    {
        var old=await SeedLegacy(2);
        await using(var db=await _db.CreateDbContextAsync())
        {
            var entries=await db.ArchiveEntries.Where(e=>e.JobId==old).OrderBy(e=>e.Path).ToListAsync();
            foreach(var e in entries){e.Stage="failed";e.TargetMbps=0;e.EncoderMode="";e.MediaJson="{}";}
            var bad=entries[1];var oldPath=bad.Path;bad.Path=bad.InputPath=bad.OutputPath=Path.Combine(Source,"broken.mkv");File.Move(oldPath,bad.Path);
            var f=await db.MediaFiles.SingleAsync(f=>f.ArchiveEntryId==bad.Id);f.FilePath=bad.Path;
            await db.SaveChangesAsync();
        }
        await AddMaskingSkips(old);await CreateFiles(2);
        var id=await Direct();
        await Until(async ()=>(await Owned(id)).Any(e=>e.Stage=="queued"));
        Assert.Equal(id,await Direct());
        await FinishOwned(id);
        var first=Probes;Assert.Equal(4,first);
        Assert.Equal(1,Json(await _archive.JobAsync(id)).GetProperty("summary").GetProperty("failed").GetInt32());
        Assert.Equal(id,await Direct());await FinishOwned(id);
        Assert.Equal(first+1,Probes);
    }

    [Fact]
    public async Task SelectingAndPausingAChildNeverStartsOrCancelsItsSiblingFromTheOriginalJob()
    {
        var old=await SeedLegacy(4,_root);
        ArchiveEntry sibling;
        await using(var db=await _db.CreateDbContextAsync())
        {
            var model=await db.ArchiveEntries.FirstAsync(e=>e.JobId==old);
            var path=Path.Combine(_root,"outside.mkv");await WriteMediaAsync(path,"original");
            sibling=new ArchiveEntry {JobId=old,Path=path,InputPath=path,OutputPath=path,BackupPath=Path.Combine(_root,"outside-backup"),Stage="queued",TargetMbps=20,SourceMbps=40,SourceBytes=10_000_000,Codec="h264",EncoderMode="libx265",MediaJson=model.MediaJson};
            db.ArchiveEntries.Add(sibling);(await db.ArchiveJobs.FindAsync(old))!.Status="queued";await db.SaveChangesAsync();
        }
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingWork=_archive.ExecuteAsync(new(){Id=sibling.Id,ArchiveEntryId=sibling.Id,Path=sibling.Path},async(args,ct)=>{entered.SetResult();await release.Task.WaitAsync(ct);await WriteMediaAsync(args.Arguments.Last(),"encoded",ct);},_=>Task.CompletedTask,CancellationToken.None);
        await entered.Task;
        string child;
        try
        {
            child=await Direct();Assert.NotEqual(old,child);
            await Until(async ()=>(await Owned(child)).Count==4);
            await _archive.PauseJobAsync(child,"用户暂停目录任务，请手动继续。");
            Assert.False(siblingWork.IsCompleted);Assert.Equal("encoding",(await _archive.GetEntryAsync(sibling.Id))!.Stage);
        }
        finally {release.TrySetResult();await siblingWork;}
        await _archive.ContinueAsync(child);await FinishOwned(child);
        Assert.All(await Owned(child),e=>{Assert.Equal(old,e.JobId);Assert.StartsWith(Source+Path.DirectorySeparatorChar,e.Path);Assert.Equal("completed",e.Stage);});
        Assert.Null((await _archive.GetEntryAsync(sibling.Id))!.FolderJobId);
        Assert.Equal("completed",(await _archive.GetEntryAsync(sibling.Id))!.Stage);
    }

    [Fact]
    public async Task CompletedAndRestoredRecordsAreProtectedOnDirectStart()
    {
        var old=await SeedLegacy(2);
        await using(var db=await _db.CreateDbContextAsync())
        {
            var rows=await db.ArchiveEntries.Where(e=>e.JobId==old).OrderBy(e=>e.Path).ToListAsync();rows[0].Stage="completed";rows[1].Stage="restored";(await db.ArchiveJobs.FindAsync(old))!.Status="completed";await db.SaveChangesAsync();
        }
        await AddMaskingSkips(old);var id=await Direct();
        await Until(async()=>Json(await _archive.JobAsync(id)).GetProperty("status").GetString()=="completed");
        Assert.Equal(0,Probes);Assert.Equal("original",await ReadMediaAsync(Path.Combine(Source,"old-0000.mkv")));
        var stages=(await Entries(old)).Select(e=>e.Stage).ToList();Assert.Contains("completed",stages);Assert.Contains("restored",stages);
    }
}
