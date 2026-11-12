using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Snacks.Services;
using Snacks.Services.Slots;
using Xunit;
using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;

namespace Snacks.Tests.Archive;

public sealed partial class ArchiveBoundedDiscoveryTests
{
    private static readonly string[] RunnableArchiveStages = ["queued", "encoding", "muxing", "validating", "backing_up", "committing", "restoring"];
    private async Task<int> RunnableCount()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ArchiveEntries.CountAsync(e => RunnableArchiveStages.Contains(e.Stage));
    }
    private async Task EncodeFixture(ArchiveEntry entry, Func<FfmpegArgumentList, CancellationToken, Task>? encode = null)
    {
        var item = new WorkItem { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path };
        await _archive.ExecuteAsync(item, encode ?? ((args, ct) => WriteMediaAsync(args.Arguments.Last(), "encoded", ct)), _ => Task.CompletedTask, CancellationToken.None);
        Assert.True(item.Status == WorkItemStatus.Completed, $"{entry.Path}: {item.Status}, {item.ErrorMessage}");
    }

    [Fact]
    public async Task SlowEncoderKeepsOneSlotWhileLaterFilesRefillTheOtherTwo()
    {
        await CreateFiles(70); var id = await Direct();
        await Until(async () => await RunnableCount() == 32);
        var slow = (await Owned(id)).First(e => e.Stage == "queued");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new SlotLedger((_, _) => 1);
        var maxActive = 0;
        async Task Run(ArchiveEntry entry)
        {
            var item = new WorkItem { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path };
            await _archive.HydrateAsync(item, entry.Id);
            Assert.Equal(3, item.ArchiveConcurrency);
            Assert.True(ledger.TryReserve("local", entry.Id == slow.Id ? "apple" : "cpu", entry.Id, null, item.ArchiveConcurrency));
            try
            {
                await EncodeFixture(entry, async (args, ct) =>
                {
                    var active = ledger.GetArchiveUsage()!.Used;
                    maxActive = Math.Max(maxActive, active); Assert.InRange(active, 1, 3);
                    if (entry.Id == slow.Id) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
                    else await Task.Delay(15, ct);
                    await WriteMediaAsync(args.Arguments.Last(), "encoded", ct);
                });
            }
            finally { ledger.Release(entry.Id, ReleaseReason.Completed); }
        }
        var longTask = Run(slow);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var turn = 0; turn < 60 && (await Owned(id)).Count(e => e.Stage == "completed") < 40; turn++)
            {
                await Until(async () => (await Owned(id)).Count(e => e.Stage == "queued") >= 2);
                var next = (await Owned(id)).Where(e => e.Stage == "queued").Take(2).ToList();
                await Task.WhenAll(next.Select(Run));
                Assert.InRange(await RunnableCount(), 1, 32);
            }
            Assert.True((await Owned(id)).Count(e => e.Stage == "completed") >= 40);
            Assert.Equal("encoding", (await _archive.GetEntryAsync(slow.Id))!.Stage);
            Assert.Equal("original", await ReadMediaAsync(slow.Path));
            Assert.True(Probes > 32); Assert.Equal(3, maxActive);
        }
        finally { release.TrySetResult(); await longTask; }
        await FinishOwned(id);
        Assert.Equal(70, (await Owned(id)).Count(e => e.Stage == "completed"));
        Assert.Equal(70, Probes);
    }

    [Fact]
    public async Task CompetingRootsShare32SlotsAndParentReselectionDoesNotReencodeCompletedPaths()
    {
        await CreateFiles(48, "first"); await CreateFiles(48, "second");
        var first = await Direct(Path.Combine(Source, "first"));
        var second = await Direct(Path.Combine(Source, "second"));
        await Until(async () => await RunnableCount() == 32);
        Assert.NotEmpty(await Owned(first)); Assert.NotEmpty(await Owned(second));
        var observedPeak = 0;
        using var observe = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            while (!observe.IsCancellationRequested)
            {
                var live = await RunnableCount(); Assert.InRange(live, 0, 32);
                observedPeak = Math.Max(observedPeak, live); await Task.Delay(5);
            }
        });
        try
        {
            for (var turn = 0; turn < 100; turn++)
            {
                await using var db = await _db.CreateDbContextAsync();
                var next = await db.ArchiveEntries.AsNoTracking().Where(e => e.Stage == "queued").Take(3).ToListAsync();
                if (next.Count == 0)
                {
                    if (await db.ArchiveEntries.CountAsync(e => e.Stage == "completed") == 96) break;
                    await Task.Delay(25); continue;
                }
                await Task.WhenAll(next.Select(e => EncodeFixture(e, async (args, ct) =>
                { await Task.Delay(20, ct); await WriteMediaAsync(args.Arguments.Last(), "encoded", ct); })));
            }
        }
        finally { observe.Cancel(); await sampling; }
        Assert.Equal(32, observedPeak);
        await Until(async () => Json(await _archive.JobAsync(first)).GetProperty("status").GetString() == "completed" &&
            Json(await _archive.JobAsync(second)).GetProperty("status").GetString() == "completed");
        var completed = (await Owned(first)).Concat(await Owned(second)).OrderBy(e => e.Id).ToList();
        Assert.Equal(96, completed.Count); Assert.All(completed, e => Assert.Equal("completed", e.Stage));
        var saved = JsonSerializer.Serialize(completed);
        await FinishOwned(await Direct());
        Assert.Equal(saved, JsonSerializer.Serialize((await Owned(first)).Concat(await Owned(second)).OrderBy(e => e.Id)));
        Assert.Equal(96, Probes);
    }

    [Fact]
    public async Task PreviewAdmissionDuringAProducerProbeCannotOverfillTheGlobalWindow()
    {
        await CreateFiles(1, "producer"); await CreateFiles(32, "preview");
        var preview = Json(await _archive.PreviewAsync(new() { DirectoryPath = Path.Combine(Source, "preview") }, CancellationToken.None));
        var marker = Path.Combine(_root, "probe-started"); var release = Path.Combine(_root, "probe-release");
        var probe = Path.Combine(_root, "probe.sh"); var script = await File.ReadAllTextAsync(probe);
        script = script.Replace("case \"$last\" in *broken*)", $"case \"$last\" in */producer/*) touch '{marker}'; while [ ! -f '{release}' ]; do sleep 0.01; done ;; esac\ncase \"$last\" in *broken*)");
        await File.WriteAllTextAsync(probe, script);
        var producer = await Direct(Path.Combine(Source, "producer"));
        await Until(() => Task.FromResult(File.Exists(marker)));
        string previewJob;
        try { previewJob = await Start(preview); }
        finally { await File.WriteAllTextAsync(release, "continue"); }
        await Task.Delay(150);
        Assert.Equal(32, await RunnableCount()); Assert.Empty(await Owned(producer));
        await EncodeFixture((await Owned(previewJob)).First(e => e.Stage == "queued"));
        await Until(async () => (await Owned(producer)).Any(e => e.Stage == "queued"));
        Assert.Equal(32, await RunnableCount()); Assert.Equal(33, Probes);
        await FinishOwned(previewJob); await FinishOwned(producer);
        Assert.Equal(33, Probes);
    }

    [Fact]
    public async Task PausingARollingFolderStopsItsProducerAndActiveEncoderUntilManualContinue()
    {
        await CreateFiles(50); var id = await Direct();
        await Until(async () => await RunnableCount() == 32);
        var entry = (await Owned(id)).First(e => e.Stage == "queued");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path };
        var encoding = _archive.ExecuteAsync(item, async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, _ => Task.CompletedTask, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _archive.PauseJobAsync(id, "用户暂停测试"); await encoding;
        await Task.Delay(150); var probes = Probes; var count = (await Owned(id)).Count;
        await Task.Delay(150);
        Assert.Equal(probes, Probes); Assert.Equal(count, (await Owned(id)).Count);
        Assert.Equal(0, await RunnableCount()); Assert.Equal(WorkItemStatus.Stopped, item.Status);
        Assert.Equal("paused_storage", Json(await _archive.JobAsync(id)).GetProperty("status").GetString());
        await _archive.ContinueAsync(id); await FinishOwned(id);
        Assert.Equal(50, (await Owned(id)).Count(e => e.Stage == "completed"));
    }
}
