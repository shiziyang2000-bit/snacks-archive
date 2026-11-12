using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    internal static void RequireNonemptyFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
            throw new ArchiveException("file_unavailable", "文件不存在或为空。", path, true);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArchiveException("path_conflict", "文件路径是符号链接，不能用于原位替换。", path, true);
    }

    internal static async Task CopyFileAsync(string from, string to, CancellationToken ct = default)
    {
        RequireNonemptyFile(from);
        EnsureWritableParent(to);
        // CreateNew protects every existing file. Copy completion/flush errors propagate
        // before the source can be moved. No second read or content comparison follows.
        var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileStream dst;
        try { dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous); }
        catch { await src.DisposeAsync(); throw; }
        await CopyStreamsAndCloseAsync(src, dst, src.Length, () => dst.Flush(true), ct);
    }

    internal static async Task CopyStreamsAndCloseAsync(System.IO.Stream src, System.IO.Stream dst, long expectedBytes,
        Action flushToDisk, CancellationToken ct = default)
    {
        Exception? failure = null;
        var completed = false;
        try
        {
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            int count;
            while ((count = await src.ReadAsync(buffer, ct)) != 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, count), ct);
                copied += count;
            }
            if (copied != expectedBytes) throw new IOException("文件复制在读取完整原片前结束。");
            await dst.FlushAsync(ct);
            flushToDisk();
            completed = true;
        }
        catch (Exception ex) { failure = ex; }
        foreach (var stream in new[] { dst, src })
        {
            try { await stream.DisposeAsync(); }
            catch (IOException ex) when (completed && IsCloseBadDescriptor(ex))
            { /* Some NAS handles report EBADF only at close after successful EOF + flush. */ }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static bool IsCloseBadDescriptor(IOException ex) => ex.HResult == 9
        || ex.HResult == unchecked((int)0x80070006)
        || ex.Message.Contains("Bad file descriptor", StringComparison.OrdinalIgnoreCase);

    private static ArchiveTransferState Transfer(ArchiveEntry entry)
        => JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json)?.Transfer ?? new();

    internal static ArchiveFileIdentity FileIdentity(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static bool? MatchesHistoryFile(ArchiveEntry entry, string path)
    {
        var current = FileIdentity(path);
        var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json);
        if (media?.PathIdentity != null) return media.PathIdentity == current;
        // Legacy records have no filesystem identity. A different length or a later
        // modification is a new file; otherwise report uncertainty, not equality.
        var expectedBytes = entry.Stage == "completed" && path == entry.OutputPath
            ? entry.OutputBytes : entry.SourceBytes;
        if (expectedBytes is > 0 && expectedBytes != current.Bytes) return false;
        if (current.LastWriteUtcTicks > entry.UpdatedAt.Ticks) return false;
        return null;
    }

    private async Task SaveTransferAsync(ArchiveEntry entry, ArchiveTransferState state)
    {
        var media = JsonSerializer.Deserialize<ArchiveMediaInfo>(entry.MediaJson, Json) ?? new();
        media.Transfer = state;
        if ((state.Published || state.RestorePublished) && entry.Path == entry.OutputPath)
            media.PathIdentity = FileIdentity(entry.Path);
        entry.MediaJson = JsonSerializer.Serialize(media, Json);
        await SaveEntryAsync(entry);
    }

    private static void RequireOwnedTransactionPath(ArchiveEntry entry, string path, string kind)
    {
        var expected = Path.Combine(Path.GetDirectoryName(entry.Path)!,
            ".snacks-archive-" + entry.Id + "." + kind + Path.GetExtension(entry.Path));
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.Ordinal))
            throw new ArchiveException("transaction_path_conflict", "事务暂存路径不属于该任务，保留文件。", path, true);
        if (File.Exists(path)) RequireNonemptyFile(path);
    }
}
