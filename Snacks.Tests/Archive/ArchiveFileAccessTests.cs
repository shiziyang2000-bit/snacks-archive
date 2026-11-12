using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Snacks.Models;
using Xunit;
using static Snacks.Tests.Fixtures.ArchiveFixtureMedia;

namespace Snacks.Tests.Archive;

public sealed partial class ArchiveBoundedDiscoveryTests
{
    [Theory]
    [InlineData(5, "Input/output error")]
    [InlineData(5, "device failed")]
    [InlineData(unchecked((int)0x80070005), "Input/output error")]
    [InlineData(unchecked((int)0x80070005), "No space left on device")]
    public async Task RealIoErrorsCannotMasqueradeAsFilePermissionBecauseTheirMessageContainsThePath(int errorCode, string message)
    {
        await CreateFiles(1); var id = await Start(await Preview()); var entry = Assert.Single(await Entries(id));
        await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path },
            (_, _) => Task.FromException(new IOException(entry.Path + ": " + message, errorCode)), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("paused_storage", (await _archive.GetEntryAsync(entry.Id))!.Stage);
        Assert.Equal("paused_storage", Json(await _archive.JobAsync(id)).GetProperty("status").GetString());
        Assert.Equal("original", await ReadMediaAsync(entry.Path));
    }

    [DllImport("libc", EntryPoint = "chflags", SetLastError = true)]
    private static extern int SetFixtureFlags(string path, uint flags);

    private static uint FixtureFlags(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/stat")
        { UseShellExecute = false, RedirectStandardOutput = true, ArgumentList = { "-f", "%f", path } })!;
        var output = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        Assert.Equal(0, process.ExitCode); return uint.Parse(output.Trim());
    }
    private static string FixtureFileState(IEnumerable<string> paths) => JsonSerializer.Serialize(paths.Select(path =>
        new { path, bytes = new FileInfo(path).Length, modified = File.GetLastWriteTimeUtc(path).Ticks, flags = FixtureFlags(path) }));

    [Fact]
    public async Task NewImmutableSourceFailsBeforeProbeAndOtherFilesContinue()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await CreateFiles(2); var path = Path.Combine(Source, "clip-0000.mkv");
        Assert.Equal(0, SetFixtureFlags(path, 2));
        try
        {
            var before = FixtureFileState([path]); var id = await Direct(); await FinishOwned(id);
            var failed = Assert.Single(await Owned(id), e => e.Path == path);
            Assert.Equal("failed", failed.Stage); Assert.Equal("source_locked", failed.ReasonCode);
            Assert.Null(failed.TempOutputPath); Assert.Null(failed.RollbackPath);
            Assert.Single(await Owned(id), e => e.Stage == "completed"); Assert.Equal(1, Probes);
            Assert.Equal(before, FixtureFileState([path]));
        }
        finally { Assert.Equal(0, SetFixtureFlags(path, 0)); }
    }

    [Fact]
    public async Task AReadDeniedSourceIsOneFailureWhileItsReadableSiblingCompletes()
    {
        if (OperatingSystem.IsWindows()) return;
        await CreateFiles(2); var path = Path.Combine(Source, "clip-0000.mkv");
        var mode = File.GetUnixFileMode(path); File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var id = await Direct(); await FinishOwned(id);
            var failed = Assert.Single(await Owned(id), e => e.Path == path);
            Assert.Equal("failed", failed.Stage); Assert.Equal("source_permission_denied", failed.ReasonCode);
            Assert.Single(await Owned(id), e => e.Stage == "completed"); Assert.Equal(1, Probes);
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path));
        }
        finally { File.SetUnixFileMode(path, mode); }
    }

    [Fact]
    public async Task ASourceLockedDuringEncodingKeepsItsOriginalAndCandidateWithoutPausingTheFolder()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await CreateFiles(2); var id = await Start(await Preview()); var entry = (await Entries(id)).OrderBy(e => e.Path).First();
        var item = new WorkItem { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path };
        try
        {
            await _archive.ExecuteAsync(item, async (args, ct) =>
            {
                await WriteMediaAsync(args.Arguments.Last(), "candidate", ct);
                Assert.Equal(0, SetFixtureFlags(entry.Path, 2));
            }, _ => Task.CompletedTask, CancellationToken.None);
            var failed = (await _archive.GetEntryAsync(entry.Id))!;
            Assert.Equal("failed", failed.Stage); Assert.Equal("source_locked", failed.ReasonCode);
            Assert.Equal("original", await ReadMediaAsync(entry.Path)); Assert.Equal(2u, FixtureFlags(entry.Path) & 2u);
            Assert.Equal("candidate", await ReadMediaAsync(failed.TempOutputPath!)); Assert.Null(failed.RollbackPath);
            await FinishOwned(id);
            Assert.Single(await Owned(id), e => e.Stage == "completed");
            Assert.Equal("candidate", await ReadMediaAsync(failed.TempOutputPath!));
        }
        finally { Assert.Equal(0, SetFixtureFlags(entry.Path, 0)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootOrChildDirectoryPermissionFailureStillPauses(bool child)
    {
        if (OperatingSystem.IsWindows()) return;
        await CreateFiles(1, child ? "child" : "");
        var directory = child ? Path.Combine(Source, "child") : Source;
        var mode = File.GetUnixFileMode(directory); File.SetUnixFileMode(directory, UnixFileMode.None);
        try
        {
            var id = await Direct();
            await Until(async () => Json(await _archive.JobAsync(id)).GetProperty("status").GetString() == "paused_storage");
            Assert.DoesNotContain(await Owned(id), e => e.ReasonCode is "source_permission_denied" or "source_missing");
            Assert.Equal(0, Probes);
        }
        finally { File.SetUnixFileMode(directory, mode); }
    }

    [Fact]
    public async Task OutputDirectoryPermissionFailureStillPausesInsteadOfBlamingTheSource()
    {
        if (OperatingSystem.IsWindows()) return;
        await CreateFiles(1);
        var output = Directory.CreateDirectory(Path.Combine(_root, "no-output-permission")).FullName;
        var settings = _archive.GetSettings(); settings.OutputMode = "saveAs"; settings.OutputDirectory = output; _archive.SaveSettings(settings);
        var id = await Direct(); await Until(async () => (await Owned(id)).Any(e => e.Stage == "queued"));
        var entry = Assert.Single(await Owned(id)); var mode = File.GetUnixFileMode(output);
        File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await _archive.ExecuteAsync(new() { Id = entry.Id, ArchiveEntryId = entry.Id, Path = entry.Path },
                (_, _) => throw new InvalidOperationException("must not encode"), _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal("paused_storage", (await _archive.GetEntryAsync(entry.Id))!.Stage);
            Assert.Equal("paused_storage", Json(await _archive.JobAsync(id)).GetProperty("status").GetString());
            Assert.Equal("original", await ReadMediaAsync(entry.Path));
        }
        finally { File.SetUnixFileMode(output, mode); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupAndContinueNeverMoveLockedTransactionFilesAndDoNotReopenHeldConflicts(bool originalMissing)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var id = await SeedLegacy(3); ArchiveEntry locked, oldConflict; string oldMedia;
        await using (var db = await _db.CreateDbContextAsync())
        {
            var entries = await db.ArchiveEntries.OrderBy(e => e.Path).ToListAsync();
            locked = entries[0]; oldConflict = entries[1];
            locked.FolderJobId = id; locked.Stage = "paused_storage"; locked.ReasonCode = "storage_failure"; locked.Error = "Access denied in prior replacement";
            locked.Progress = 99; locked.OutputBytes = 44_251_894;
            locked.RollbackPath = Path.Combine(Source, $".snacks-archive-{locked.Id}.original.mkv");
            locked.TempOutputPath = Path.Combine(Source, $".snacks-archive-{locked.Id}.output.mkv");
            await WriteMediaAsync(locked.RollbackPath, "rollback"); await WriteMediaAsync(locked.TempOutputPath, "candidate");
            if (originalMissing) File.Delete(locked.Path);
            oldConflict.FolderJobId = id; oldConflict.Stage = "failed"; oldConflict.ReasonCode = "transaction_conflict"; oldConflict.Error = "original protected conflict";
            oldConflict.RollbackPath = Path.Combine(Source, $".snacks-archive-{oldConflict.Id}.original.mkv");
            await WriteMediaAsync(oldConflict.RollbackPath, "old rollback");
            var job = await db.ArchiveJobs.SingleAsync(); job.ScanRecursive = true; job.ScanComplete = false; job.Status = "paused_storage";
            (await db.MediaFiles.SingleAsync(f => f.ArchiveEntryId == oldConflict.Id)).Status = MediaFileStatus.Failed;
            await db.SaveChangesAsync(); oldMedia = JsonSerializer.Serialize(await db.MediaFiles.SingleAsync(f => f.ArchiveEntryId == oldConflict.Id));
        }
        var lockedPaths = new[] { originalMissing ? null : locked.Path, locked.RollbackPath }.OfType<string>().ToArray();
        foreach (var path in lockedPaths) Assert.Equal(0, SetFixtureFlags(path, 2));
        var protectedPaths = lockedPaths.Concat([locked.TempOutputPath!, oldConflict.Path, oldConflict.RollbackPath!]).ToArray();
        var beforeFiles = FixtureFileState(protectedPaths); var beforeOld = JsonSerializer.Serialize(oldConflict);
        try
        {
            await _archive.ShutdownAsync(); _archive = NewService(); await _archive.InitializeAsync();
            Assert.Equal(beforeFiles, FixtureFileState(protectedPaths)); Assert.Equal(!originalMissing, File.Exists(locked.Path));
            Assert.Equal("paused_storage", (await _archive.GetEntryAsync(locked.Id))!.Stage);
            await _archive.ContinueAsync(id); await FinishOwned(id);
            var held = (await _archive.GetEntryAsync(locked.Id))!;
            Assert.Equal("failed", held.Stage); Assert.Equal("transaction_permission_denied", held.ReasonCode);
            locked.Stage = held.Stage; locked.ReasonCode = held.ReasonCode; locked.Error = held.Error; locked.UpdatedAt = held.UpdatedAt;
            Assert.Equal(JsonSerializer.Serialize(locked), JsonSerializer.Serialize(held));
            Assert.Equal(beforeFiles, FixtureFileState(protectedPaths)); Assert.Equal(!originalMissing, File.Exists(locked.Path));
            Assert.Equal(beforeOld, JsonSerializer.Serialize(await _archive.GetEntryAsync(oldConflict.Id)));
            await using (var db = await _db.CreateDbContextAsync())
            {
                Assert.Equal(oldMedia, JsonSerializer.Serialize(await db.MediaFiles.SingleAsync(f => f.ArchiveEntryId == oldConflict.Id)));
                Assert.Equal(MediaFileStatus.Failed, (await db.MediaFiles.SingleAsync(f => f.ArchiveEntryId == locked.Id)).Status);
            }
            var saved = JsonSerializer.Serialize(held);
            await _archive.ContinueAsync(id); await FinishOwned(id);
            Assert.Equal(saved, JsonSerializer.Serialize(await _archive.GetEntryAsync(locked.Id)));
            Assert.Equal(beforeOld, JsonSerializer.Serialize(await _archive.GetEntryAsync(oldConflict.Id)));
            Assert.Equal(beforeFiles, FixtureFileState(protectedPaths));
            Assert.Single(await Owned(id), e => e.Stage == "completed");
        }
        finally { foreach (var path in lockedPaths) Assert.Equal(0, SetFixtureFlags(path, 0)); }
    }
}
