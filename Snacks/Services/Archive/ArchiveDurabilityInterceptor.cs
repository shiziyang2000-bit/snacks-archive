using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace Snacks.Services;

// Archive transaction markers must reach stable storage before file renames.
public sealed class ArchiveDurabilityInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    { using var cmd=connection.CreateCommand();cmd.CommandText="PRAGMA synchronous=FULL; PRAGMA busy_timeout=30000;";cmd.ExecuteNonQuery(); }
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,CancellationToken cancellationToken=default)
    { await using var cmd=connection.CreateCommand();cmd.CommandText="PRAGMA synchronous=FULL; PRAGMA busy_timeout=30000;";await cmd.ExecuteNonQueryAsync(cancellationToken); }
}
public sealed class ArchiveShutdownService(TranscodingService transcoding) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken)=>transcoding.SafeShutdownAsync();
}
