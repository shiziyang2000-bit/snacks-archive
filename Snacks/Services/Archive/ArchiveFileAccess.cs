using System.ComponentModel;
using System.Runtime.InteropServices;
using Snacks.Models;

namespace Snacks.Services;

public sealed partial class ArchiveService
{
    private const string SourceLockedCode = "source_locked";
    private const string SourcePermissionCode = "source_permission_denied";
    private const string TransactionPermissionCode = "transaction_permission_denied";
    private static readonly string[] HeldTransactionCodes = ["transaction_conflict", TransactionPermissionCode];

    [StructLayout(LayoutKind.Sequential)]
    private struct MacAttributeList
    {
        public ushort BitmapCount, Reserved;
        public uint Common, Volume, Directory, File, Fork;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MacFileFlags { public uint Length, Flags; }
    [DllImport("libc", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetMacAttributes(string path, ref MacAttributeList attributes, out MacFileFlags result, nuint size, uint options);
    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int UnixAccess(string path, int mode);

    private static Exception NativeAccessError(string path, int error) => error switch
    {
        2 => new FileNotFoundException("文件不存在。", path),
        20 => new DirectoryNotFoundException("路径中的目录不存在：" + path),
        1 or 13 => new UnauthorizedAccessException("文件访问被拒绝：" + path),
        _ => new IOException(path + ": " + new Win32Exception(error).Message, error)
    };

    private static bool HasFileAccess(string path, int mode)
    {
        if (OperatingSystem.IsWindows()) return true; // read-only flag and actual operation errors are checked separately
        if (UnixAccess(path, mode) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is 1 or 13) return false;
        throw NativeAccessError(path, error);
    }

    private static bool HasImmutableFlags(string path)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var attributes = new MacAttributeList { BitmapCount = 5, Common = 0x00040000 }; // ATTR_CMN_FLAGS
        if (GetMacAttributes(path, ref attributes, out var result, 8, 1) != 0) // FSOPT_NOFOLLOW
            throw NativeAccessError(path, Marshal.GetLastPInvokeError());
        if (result.Length != 8) throw new IOException("无法可靠读取文件锁定标记：" + path);
        return (result.Flags & (0x2u | 0x4u | 0x20000u | 0x40000u)) != 0; // user/system immutable or append-only
    }

    private static void RequireHealthyFileParent(string path, string root, bool writable)
    {
        root = string.IsNullOrWhiteSpace(root) ? Path.GetDirectoryName(path)! : root;
        RequireReadableSourceDirectory(root);
        var parent = Path.GetDirectoryName(path)!;
        RequireReadableSourceDirectory(parent);
        if (!HasFileAccess(root, 5) || !HasFileAccess(parent, writable ? 7 : 5))
            throw new ArchiveException("storage_unavailable", "来源或目标目录无访问权限，任务已暂停。", parent, true);
    }

    private static void CheckSourceFileAccess(string path, string root, bool replacing, bool canSkipMissing)
    {
        ReadSourceMetadata(path, root, canSkipMissing, () =>
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            var locked = replacing && (HasImmutableFlags(path) || OperatingSystem.IsWindows() && (attributes & FileAttributes.ReadOnly) != 0);
            if (!locked && HasFileAccess(path, replacing ? 6 : 4)) return true;
            RequireHealthyFileParent(path, root, replacing);
            throw new ArchiveException(locked ? SourceLockedCode : SourcePermissionCode,
                locked ? "源文件已锁定，已隔离此文件；请手动解除锁定后重试，其他文件继续处理。" :
                    "此源文件无读取或替换权限，已隔离；其他文件继续处理。", path);
        });
    }

    private static void CheckEntrySourceAccess(ArchiveEntry entry, ArchiveSettings rules, string root, bool allowMissingSkip = true)
    {
        CheckSourceFileAccess(entry.Path, root, rules.OutputMode == "inPlace", allowMissingSkip && CanSkipMissingSource(entry));
        if (entry.InputPath != entry.Path) CheckSourceFileAccess(entry.InputPath, root, false, false);
    }

    private static void CheckPendingTransactionAccess(ArchiveEntry entry, string root)
    {
        if (!NeedsTransactionReview(entry)) return;
        // This runs only during explicit continuation/admission. Inspect existing
        // transaction files before recovery is allowed to move an original back.
        foreach (var path in new[] { entry.Path, entry.RollbackPath, entry.TempOutputPath, entry.RestoreTempPath, entry.RestoreDisplacedPath }.OfType<string>().Distinct())
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception ex) when (IsMissingPath(ex)) { continue; } // recovery must decide missing transaction files
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
            if (!HasImmutableFlags(path) && (attributes & FileAttributes.ReadOnly) == 0 && HasFileAccess(path, 6)) continue;
            RequireHealthyFileParent(path, root, true);
            throw new ArchiveException(TransactionPermissionCode, "替换事务中的文件已锁定或无权限，需人工检查；已保留全部原片和暂存文件，其他文件继续处理。", path);
        }
    }

    private static bool IsPermissionFailure(Exception ex)
    {
        if (new[] { "Input/output error", "I/O error", "Stale file handle", "No space left", "Read-only file system", "Disk quota" }
            .Any(x => ex.Message.Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        return ex is UnauthorizedAccessException ||
            ex is IOException && (ex.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021) ||
                !OperatingSystem.IsWindows() && ex.HResult is 1 or 13) ||
            ex is ArchiveException { Code: "media_tool_failed" } &&
                new[] { "Permission denied", "Operation not permitted" }.Any(x => ex.Message.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalFilePermission(string path, string root, bool writable, Exception ex)
    {
        if (!IsPermissionFailure(ex)) return false;
        try
        {
            RequireHealthyFileParent(path, root, writable);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            return HasImmutableFlags(path) || !HasFileAccess(path, writable ? 6 : 4) ||
                ex.Message.Contains(path, StringComparison.Ordinal) || ex is IOException && ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
        }
        catch { return false; } // unknown storage/parent state must keep the normal pause behavior
    }

    private static bool CanIsolateFileAccess(ArchiveEntry entry, string? root, ArchiveSettings? rules, Exception ex)
    {
        if (ex is ArchiveException { Code: SourceLockedCode or SourcePermissionCode or TransactionPermissionCode }) return true;
        if (root == null || rules == null || !IsPermissionFailure(ex)) return false;
        // Check all involved parents: a denied output directory is a storage
        // problem, even if the original also happens to have limited permissions.
        try
        {
            foreach (var path in new[] { entry.Path, entry.InputPath, entry.OutputPath, entry.RollbackPath, entry.TempOutputPath }.OfType<string>().Distinct())
                RequireHealthyFileParent(path, root, path != entry.InputPath || rules.OutputMode == "inPlace");
        }
        catch { return false; }
        return new[] { entry.Path, entry.InputPath, entry.OutputPath, entry.RollbackPath, entry.TempOutputPath }.OfType<string>().Distinct()
            .Any(path => IsLocalFilePermission(path, root, rules.OutputMode == "inPlace" || path != entry.Path, ex));
    }

    private async Task FailFileAccessAsync(ArchiveEntry entry, Exception ex)
    {
        var transaction = NeedsTransactionReview(entry) || entry.Stage is "backing_up" or "committing" or "restoring" ||
            ex is ArchiveException { Code: TransactionPermissionCode };
        entry.ReasonCode = transaction ? TransactionPermissionCode : ex is ArchiveException { Code: SourceLockedCode } ? SourceLockedCode : SourcePermissionCode;
        await StageAsync(entry, "failed", transaction
            ? "文件权限阻止了替换事务，需人工检查；全部原片与暂存文件保留，其他文件继续处理。 " + ex.Message
            : "单个文件锁定或无权限，已隔离此项，其他文件继续处理。 " + ex.Message);
        await SetMediaStatusAsync(entry, MediaFileStatus.Failed);
        // No CleanupOwnOutput: every recorded transaction/candidate stays intact.
    }
}
