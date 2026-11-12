using System.Text;

namespace Snacks.Tests.Fixtures;

// Synthetic marker files use sparse capacity at the real admission boundary.
// This keeps prior encoding/transaction tests meaningful without bypassing the rule.
internal static class ArchiveFixtureMedia
{
    public static async Task WriteMediaAsync(string path, string marker, CancellationToken ct = default, long bytes = 10_000_000)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        await file.WriteAsync(Encoding.UTF8.GetBytes(marker), ct);
        file.SetLength(bytes);
    }
    public static string ReadMedia(string path)
    {
        using var file = File.OpenRead(path);
        var prefix = new byte[Math.Min(1024, file.Length)];
        file.ReadExactly(prefix);
        return Encoding.UTF8.GetString(prefix).TrimEnd('\0');
    }
    public static Task<string> ReadMediaAsync(string path) => Task.FromResult(ReadMedia(path));
}
