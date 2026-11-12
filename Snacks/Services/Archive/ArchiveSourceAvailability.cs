using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    internal const string MissingSourceCode = "source_missing";
    internal const string MissingSourceReason = "源文件已不存在，已跳过；继续处理文件夹中的其他文件。";

    private static bool IsMissingPath(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException;

    // Only source metadata reads may produce source_missing. Output, temporary,
    // backup and transaction operations deliberately never use this wrapper.
    private static T ReadSourceMetadata<T>(string path, string root, bool canSkipMissing, Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (canSkipMissing && IsMissingPath(ex))
        {
            if (ConfirmMissingSource(path, root))
                throw new ArchiveException(MissingSourceCode, MissingSourceReason, path);
            throw;
        }
    }

    private async Task<ArchiveMediaInfo> ProbeSourceAsync(string path, string root, bool canSkipMissing, CancellationToken ct)
    {
        try { return await _media.ProbeAsync(path, ct); }
        catch (Exception ex) when (canSkipMissing && (IsMissingPath(ex) ||
            ex is ArchiveException { Code: "media_tool_failed" } &&
            ex.Message.Contains(path + ": No such file or directory", StringComparison.Ordinal) &&
            !new[] { "Permission denied", "Input/output error", "Stale file handle", "Read-only file system" }
                .Any(error => ex.Message.Contains(error, StringComparison.OrdinalIgnoreCase))))
        {
            // ffprobe can lose the source after admission, or its successful
            // response can race the final source-size read. A missing executable
            // (Win32Exception), unrelated tool error, or I/O error cannot skip.
            if (ConfirmMissingSource(path, root))
                throw new ArchiveException(MissingSourceCode, MissingSourceReason, path);
            throw;
        }
        catch (Exception ex) when (IsLocalFilePermission(path, root, false, ex))
        { throw new ArchiveException(SourcePermissionCode, "此源文件无读取权限，已隔离；其他文件继续处理。 " + ex.Message, path); }
    }

    private static bool ConfirmMissingSource(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Within(path, root) ||
            Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return false;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        // File.Exists suppresses access and I/O errors. Instead, require a real
        // directory metadata read AND a bounded directory enumeration to succeed.
        RequireReadableSourceDirectory(root);
        if (OperatingSystem.IsMacOS() && Path.GetFullPath(path).StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            RequireReadableSourceDirectory(Path.Combine("/Volumes", parts[1]));
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        while (!parent.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            try { RequireReadableSourceDirectory(parent); break; }
            catch (Exception ex) when (IsMissingPath(ex))
            {
                // A stale saved subdirectory is also skippable, but only while
                // an ancestor within the selected root is demonstrably accessible.
                parent = Path.GetDirectoryName(parent)!;
            }
        }
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception ex) when (IsMissingPath(ex)) { return true; }
    }

    private static FileAttributes? DiscoveryAttributes(string path, string root)
    {
        try { return ReadSourceMetadata(path, root, true, () => File.GetAttributes(path)); }
        catch (ArchiveException ex) when (ex.Code == MissingSourceCode) { return null; }
    }

    private static IEnumerator<string>? OpenSourceDirectory(string directory, string root)
    {
        try { return ReadSourceMetadata(directory, root, true, () => Directory.EnumerateFileSystemEntries(directory).GetEnumerator()); }
        catch (ArchiveException ex) when (ex.Code == MissingSourceCode) { return null; }
    }

    private static bool MoveNextSourcePath(IEnumerator<string> paths, string directory, string root)
    {
        try { return ReadSourceMetadata(directory, root, true, paths.MoveNext); }
        catch (ArchiveException ex) when (ex.Code == MissingSourceCode) { return false; }
    }

    private static void RequireReadableSourceDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArchiveException("source_unavailable", "来源目录不可访问或已变为链接，任务已暂停。", path, true);
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        _ = entries.MoveNext(); // One item at most; never materialize the directory.
    }

    private static bool CanSkipMissingSource(ArchiveEntry entry) =>
        entry.Stage is not ("completed" or "restored") && !NeedsTransactionReview(entry) && entry.InputPath == entry.Path;

    private static void RequirePendingSource(ArchiveEntry entry, string root)
    {
        RequireNonemptySourceFile(entry.Path, root, CanSkipMissingSource(entry));
        if (entry.InputPath != entry.Path) RequireNonemptyFile(entry.InputPath);
    }

    private static void RequireNonemptySourceFile(string path, string root, bool canSkipMissing)
    {
        ReadSourceMetadata(path, root, canSkipMissing, () =>
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveException("path_conflict", "文件路径是符号链接，不能用于原位替换。", path, true);
            if ((attributes & FileAttributes.Directory) != 0 || new FileInfo(path).Length <= 0)
                throw new ArchiveException("file_unavailable", "文件不存在或为空。", path, true);
            return true;
        });
    }

    private async Task MarkSourceMissingAsync(ArchiveEntry entry)
    {
        entry.ReasonCode = MissingSourceCode; entry.Progress = 0;
        await StageAsync(entry, "skipped", MissingSourceReason);
        await SetMediaStatusAsync(entry, MediaFileStatus.Skipped);
    }

    private async Task<bool> HoldKnownTransactionConflictAsync(ArchiveEntry entry)
    {
        if (entry.Stage is "completed" or "restored" || !HeldTransactionCodes.Contains(entry.ReasonCode)) return false;
        // A recorded conflict is not permission to retry file recovery. Keep the
        // original error, paths and journal, and stop only this entry's dispatch.
        await StageAsync(entry, "failed", entry.Error);
        await SetMediaStatusAsync(entry, MediaFileStatus.Failed);
        return true;
    }
}
