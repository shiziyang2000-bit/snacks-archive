using Snacks.Models;
using Snacks.Services;
using Snacks.Services.Slots;
using Xunit;

namespace Snacks.Tests.Archive;

public sealed class ArchiveProtectionTests
{
    [Fact] public void ArchiveUsageReportsTheSameSharedLimitAcrossAppleCpuAndFrozenTasks()
    {
        var ledger = new SlotLedger((_, _) => 1);
        Assert.Null(ledger.GetArchiveUsage());
        Assert.Equal(new SlotLedger.ArchiveUsage(3, 0), ledger.GetArchiveUsage(3));
        Assert.True(ledger.TryReserve("local", "apple", "apple-one", null, 3));
        Assert.True(ledger.TryReserve("local", "cpu", "cpu-one", null, 3));
        Assert.Equal(new SlotLedger.ArchiveUsage(3, 2), ledger.GetArchiveUsage(9));
        ledger.Release("apple-one", ReleaseReason.Completed);
        Assert.Equal(new SlotLedger.ArchiveUsage(3, 1), ledger.GetArchiveUsage(9));
        ledger.Release("cpu-one", ReleaseReason.Completed);
        Assert.Equal(new SlotLedger.ArchiveUsage(9, 0), ledger.GetArchiveUsage(9));
    }
    [Theory]
    [InlineData("hevc", "Main", 25, 20, 20)]
    [InlineData("h264", "High", 30, 20, 15)]
    [InlineData("mpeg2video", "Main", 80, 20, 20)]
    [InlineData("prores", "HQ", 340, 20, 10)]
    [InlineData("dnxhd", "DNXHR SQ", 330, 20, 10)]
    public void PerCodecTargetsUseSpecifiedDivision(string codec,string profile,double source,double ceiling,double expected)
    {var (target,skip)=ArchiveMediaService.CalculateTarget(codec,profile,source,ceiling);Assert.Null(skip);Assert.Equal(expected,target,8);}
    [Fact] public void HevcThresholdFollowsCeilingAndIncludesEquality()
    {Assert.Equal("hevc_below_threshold",ArchiveMediaService.CalculateTarget("hevc","Main",12.499,10).Skip);Assert.Null(ArchiveMediaService.CalculateTarget("hevc","Main",12.5,10).Skip);}
    [Fact] public void GlobalReservationAllowsTenEvenWhenOriginalDeviceDefaultsToOne()
    {
        var ledger=new SlotLedger((_,_)=>1);var accepted=0;
        Parallel.For(0,40,i=>{if(ledger.TryReserve("local",i%2==0?"cpu":"apple",i.ToString(),$"folder-{i}/clip.mov",10))Interlocked.Increment(ref accepted);});
        Assert.Equal(10,accepted);Assert.Equal(10,ledger.EnumerateAll().Count);
        Assert.False(ledger.TryReserve("remote","cpu","extra-original","original.mov"));
    }
    [Fact] public void FrozenLowerLimitDoesNotExpandWhenNewTaskChangesSetting()
    {
        var ledger=new SlotLedger((_,_)=>20);Assert.True(ledger.TryReserve("local","cpu","old-1","a.mov",1));
        Assert.False(ledger.TryReserve("local","cpu","new-1","b.mov",20));ledger.Release("old-1",ReleaseReason.Completed);
        Assert.True(ledger.TryReserve("local","cpu","new-1","b.mov",20));
    }
    [Fact] public async Task CopyRefusesToOverwriteUnknownTarget()
    {
        var root=Directory.CreateTempSubdirectory("snacks-copy-").FullName;
        try
        {
            var source=Path.Combine(root,"source");var target=Path.Combine(root,"target");
            await File.WriteAllTextAsync(source,"original");await File.WriteAllTextAsync(target,"unrelated");
            await Assert.ThrowsAsync<IOException>(()=>ArchiveService.CopyFileAsync(source,target));
            Assert.Equal("original",await File.ReadAllTextAsync(source));Assert.Equal("unrelated",await File.ReadAllTextAsync(target));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task RestoreIgnoresLegacyHashesButStillRequiresFiles()
    {
        var root=Directory.CreateTempSubdirectory("snacks-restore-").FullName;
        try
        {
            var source=Path.Combine(root,"clip.mov");var backup=Path.Combine(root,"original.mov");
            await File.WriteAllTextAsync(source,"encoded");await File.WriteAllTextAsync(backup,"original");
            var entry=new ArchiveEntry {Stage="completed",Path=source,OutputPath=source,BackupPath=backup,SourceHash="legacy-unmatched",OutputHash="legacy-unmatched"};
            await ArchiveService.ValidateRestoreAsync(entry);
            Assert.Equal("encoded",await File.ReadAllTextAsync(source));Assert.Equal("original",await File.ReadAllTextAsync(backup));
            File.Delete(backup);
            var error=await Assert.ThrowsAsync<ArchiveException>(()=>ArchiveService.ValidateRestoreAsync(entry));
            Assert.Equal("file_unavailable",error.Code);Assert.Equal("encoded",await File.ReadAllTextAsync(source));
        }
        finally{Directory.Delete(root,true);}
    }
}
