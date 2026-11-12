using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Archive;

[Collection("EnvConfigOverrides")]
public sealed class ArchiveNoContentChecksTests : IDisposable
{
    private readonly string _root=Directory.CreateTempSubdirectory("snacks-no-content-").FullName;
    private readonly string? _previous;
    private readonly string? _previousProbe=Environment.GetEnvironmentVariable("FFPROBE_PATH");
    private readonly string? _previousFfmpeg=Environment.GetEnvironmentVariable("FFMPEG_PATH");
    private readonly InMemoryDb _db=new();
    private readonly ArchiveService _service;
    public ArchiveNoContentChecksTests()
    {
        _previous=Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");Environment.SetEnvironmentVariable("SNACKS_WORK_DIR",Directory.CreateDirectory(Path.Combine(_root,"work")).FullName);
        EnvConfigOverrides.SetEnvironmentForTesting(new Dictionary<string,string>());
        var files=new FileService();_service=new(_db.Factory,new ConfigFileService(files),files,new ArchiveMediaService(),new NullHubContext());
    }
    public void Dispose(){_db.Dispose();Environment.SetEnvironmentVariable("SNACKS_WORK_DIR",_previous);Environment.SetEnvironmentVariable("FFPROBE_PATH",_previousProbe);Environment.SetEnvironmentVariable("FFMPEG_PATH",_previousFfmpeg);EnvConfigOverrides.SetEnvironmentForTesting(null);Directory.Delete(_root,true);}
    private static ArchiveMediaInfo Media()=>new(){Codec="h264",PixelFormat="yuv420p",Encoder="libx265",Width=32,Height=32,Fps=25,Duration=1,VideoMbps=20,ProbeJson="""{"streams":[{"index":0,"codec_type":"video","pix_fmt":"yuv420p"}]}"""};
    private async Task<(ArchiveEntry Entry,WorkItem Item)> NewEntry(bool saveAs=false)
    {
        var source=Path.Combine(_root,Guid.NewGuid()+".mp4");await WriteMediaAsync(source,"original-bytes");
        var output=saveAs?source+".output.mp4":source;
        var rules=_service.GetSettings();rules.OutputMode=saveAs?"saveAs":"inPlace";
        var job=new ArchiveJob{RulesSnapshotJson=JsonSerializer.Serialize(rules,ArchiveService.Json)};
        var e=new ArchiveEntry{JobId=job.Id,Path=source,InputPath=source,OutputPath=output,BackupPath=source+".backup",SourceBytes=10_000_000,SourceHash="legacy-sha",DestinationHash="legacy-sha",MediaJson=JsonSerializer.Serialize(Media(),ArchiveService.Json),EncoderMode="libx265",TargetMbps=10};
        await using var db=await _db.Factory.CreateDbContextAsync();db.ArchiveJobs.Add(job);db.ArchiveEntries.Add(e);await db.SaveChangesAsync();
        return(e,new WorkItem{Id=e.Id,ArchiveEntryId=e.Id,Path=source});
    }
    [Fact] public async Task ExecutionDoesNotReadSourceForHashesOrRejectLargerOutput()
    {
        var(e,item)=await NewEntry(saveAs:true);
        await using(var locked=new FileStream(e.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            await _service.ExecuteAsync(item,(args,ct)=>WriteMediaAsync(args.Arguments.Last(),"larger-output",ct,bytes:10_000_200),_=>Task.CompletedTask,CancellationToken.None);
        var saved=await _service.GetEntryAsync(e.Id);Assert.Equal("completed",saved!.Stage);Assert.Null(saved.OutputHash);Assert.Null(saved.ActualMbps);Assert.Equal("disabled",saved.ScoreStatus);
        Assert.Equal(10_000_200,new FileInfo(e.OutputPath).Length);Assert.Equal("original-bytes",await ReadMediaAsync(e.Path));
    }
    [Fact] public async Task FailedEncoderKeepsOriginalAndNeverCreatesBackup()
    {
        var(e,item)=await NewEntry();
        await _service.ExecuteAsync(item,(_,_)=>Task.FromException(new Exception("synthetic encoder exit 1")),_=>Task.CompletedTask,CancellationToken.None);
        Assert.Equal("failed",(await _service.GetEntryAsync(e.Id))!.Stage);Assert.Equal("original-bytes",await ReadMediaAsync(e.Path));Assert.False(File.Exists(e.BackupPath));
    }
    [Fact] public async Task UnknownBackupStopsReplacementWithoutOverwritingEitherFile()
    {
        var(e,item)=await NewEntry();await File.WriteAllTextAsync(e.BackupPath!,"unknown-backup");
        await _service.ExecuteAsync(item,(args,ct)=>File.WriteAllTextAsync(args.Arguments.Last(),"encoded",ct),_=>Task.CompletedTask,CancellationToken.None);
        Assert.Equal("paused_storage",(await _service.GetEntryAsync(e.Id))!.Stage);Assert.Equal("original-bytes",await ReadMediaAsync(e.Path));Assert.Equal("unknown-backup",await ReadMediaAsync(e.BackupPath!));
        var preview=JsonSerializer.SerializeToElement(await _service.RestorePreviewAsync(new(){EntryIds=[e.Id]}),ArchiveService.Json);
        Assert.Equal("blocked",preview.GetProperty("rows")[0].GetProperty("action").GetString());
        Assert.Equal("restore_unavailable",preview.GetProperty("rows")[0].GetProperty("reasonCode").GetString());
        var restored=JsonSerializer.SerializeToElement(await _service.RestoreAsync(new(){RequestId=Guid.NewGuid().ToString(),RestorePreviewId=preview.GetProperty("restorePreviewId").GetString()!,Confirmed=true}),ArchiveService.Json);
        Assert.Equal(0,restored.GetProperty("restored").GetInt32());
        Assert.Equal("original-bytes",await ReadMediaAsync(e.Path));Assert.Equal("unknown-backup",await ReadMediaAsync(e.BackupPath!));
    }
    [Fact] public async Task BackupAndRestoreKeepFullOriginalWithoutHashFields()
    {
        var(e,item)=await NewEntry();
        await _service.ExecuteAsync(item,(args,ct)=>File.WriteAllTextAsync(args.Arguments.Last(),"encoded",ct),_=>Task.CompletedTask,CancellationToken.None);
        Assert.Equal("completed",(await _service.GetEntryAsync(e.Id))!.Stage);Assert.Equal(10_000_000,new FileInfo(e.BackupPath!).Length);Assert.Equal("original-bytes",await ReadMediaAsync(e.BackupPath!));Assert.Equal("encoded",await ReadMediaAsync(e.Path));
        var preview=JsonSerializer.SerializeToElement(await _service.RestorePreviewAsync(new(){EntryIds=[e.Id]}),ArchiveService.Json);
        await _service.RestoreAsync(new(){RequestId=Guid.NewGuid().ToString(),RestorePreviewId=preview.GetProperty("restorePreviewId").GetString()!,Confirmed=true});
        Assert.Equal("restored",(await _service.GetEntryAsync(e.Id))!.Stage);Assert.Equal("original-bytes",await ReadMediaAsync(e.Path));Assert.Equal("original-bytes",await ReadMediaAsync(e.BackupPath!));
    }
    [Fact] public async Task OldInterruptedJobKeepsItsIdAndFrozenRulesWhenContinued()
    {
        var(e,_)=await NewEntry();await using(var db=await _db.Factory.CreateDbContextAsync()){var old=await db.ArchiveEntries.FindAsync(e.Id);old!.Stage="interrupted";await db.SaveChangesAsync();}
        await _service.ContinueAsync(e.JobId);
        for(var attempt=0;attempt<200 && (await _service.GetEntryAsync(e.Id))!.Stage!="queued";attempt++) await Task.Delay(25);
        var saved=await _service.GetEntryAsync(e.Id);Assert.Equal("queued",saved!.Stage);Assert.Equal(e.Id,saved.Id);Assert.Equal("legacy-sha",saved.SourceHash);await _service.ShutdownAsync();
    }
    [Fact] public async Task RescoreEndpointCannotRunHiddenComparison()
    {
        var error=await Assert.ThrowsAsync<ArchiveException>(()=>_service.RescoreAsync(new(){EntryIds=["anything"]},CancellationToken.None));Assert.Equal("content_checks_removed",error.Code);
    }
    [Fact] public async Task SamePathNewFileIsNotSkippedAsCompletedHistory()
    {
        if(OperatingSystem.IsWindows())return;
        InstallHeaderProbe();
        var(e,item)=await NewEntry();
        await _service.ExecuteAsync(item,(args,ct)=>File.WriteAllTextAsync(args.Arguments.Last(),"encoded",ct),_=>Task.CompletedTask,CancellationToken.None);
        var unchanged=JsonSerializer.SerializeToElement(await _service.PreviewAsync(new(){FilePaths=[e.Path]},CancellationToken.None),ArchiveService.Json);
        Assert.Equal("history_completed",unchanged.GetProperty("rows")[0].GetProperty("reasonCode").GetString());
        await WriteMediaAsync(e.Path,"new material at the same path");
        var replaced=JsonSerializer.SerializeToElement(await _service.PreviewAsync(new(){FilePaths=[e.Path]},CancellationToken.None),ArchiveService.Json);
        Assert.Equal("archive",replaced.GetProperty("rows")[0].GetProperty("action").GetString());
    }
    [Fact] public async Task HeaderProbeDoesNotAskForPacketTraversal()
    {
        if(OperatingSystem.IsWindows())return;
        var argsPath=InstallHeaderProbe();var source=Path.Combine(_root,"header-only.mp4");await File.WriteAllTextAsync(source,"not read by probe stub");
        var result=await new ArchiveMediaService().ProbeAsync(source);
        Assert.Equal(20,result.VideoMbps);Assert.False(result.VideoBitrateEstimated);
        var args=await File.ReadAllLinesAsync(argsPath);
        Assert.Contains("-show_streams",args);Assert.Contains("-probesize",args);
        Assert.DoesNotContain("-show_packets",args);Assert.DoesNotContain("-count_packets",args);Assert.DoesNotContain("-show_frames",args);
    }
    private string InstallHeaderProbe()
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var argsPath=Path.Combine(_root,"probe-args");var probe=Path.Combine(_root,"probe.sh");var ffmpeg=Path.Combine(_root,"ffmpeg.sh");
        const string json="""{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":32,"height":32,"avg_frame_rate":"25/1","duration":"1","bit_rate":"20000000"}],"format":{"duration":"1"}}""";
        File.WriteAllText(probe,"#!/bin/sh\nprintf '%s\\n' \"$@\" > '"+argsPath+"'\nprintf '%s\\n' '"+json+"'\n");
        File.WriteAllText(ffmpeg,"#!/bin/sh\nprintf '%s\\n' 'libx265'\n");
        File.SetUnixFileMode(probe,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);File.SetUnixFileMode(ffmpeg,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("FFPROBE_PATH",probe);Environment.SetEnvironmentVariable("FFMPEG_PATH",ffmpeg);return argsPath;
    }
    [Fact] public async Task OnlyCloseBadDescriptorAfterEofAndFlushIsIgnored()
    {
        var target=new FaultStream{CloseError=new IOException("Bad file descriptor")};var flushed=false;
        await ArchiveService.CopyStreamsAndCloseAsync(new MemoryStream([1,2,3]),target,3,()=>flushed=true);
        Assert.True(flushed);Assert.Equal(new byte[]{1,2,3},target.ToArray());
        await Assert.ThrowsAsync<IOException>(()=>ArchiveService.CopyStreamsAndCloseAsync(new FaultStream{ReadError=new IOException("Bad file descriptor")},new MemoryStream(),3,()=>{}));
        await Assert.ThrowsAsync<IOException>(()=>ArchiveService.CopyStreamsAndCloseAsync(new MemoryStream([1,2]),new FaultStream{CloseError=new IOException("Bad file descriptor")},3,()=>{}));
        await Assert.ThrowsAsync<IOException>(()=>ArchiveService.CopyStreamsAndCloseAsync(new MemoryStream([1]),new FaultStream{CloseError=new IOException("Bad file descriptor")},1,()=>throw new IOException("flush failed")));
        await Assert.ThrowsAsync<IOException>(()=>ArchiveService.CopyStreamsAndCloseAsync(new MemoryStream([1]),new FaultStream{CloseError=new IOException("disk error")},1,()=>{}));
    }
    private sealed class FaultStream : MemoryStream
    {
        public IOException? ReadError {get;init;}
        public IOException? CloseError {get;init;}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
            =>ReadError!=null?ValueTask.FromException<int>(ReadError):base.ReadAsync(buffer,ct);
        public override ValueTask DisposeAsync()=>CloseError!=null?ValueTask.FromException(CloseError):base.DisposeAsync();
    }
}
