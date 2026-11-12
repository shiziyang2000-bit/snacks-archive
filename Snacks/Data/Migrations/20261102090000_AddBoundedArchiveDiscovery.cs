using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Snacks.Data.Migrations;

[DbContext(typeof(SnacksDbContext))]
[Migration("20261102090000_AddBoundedArchiveDiscovery")]
public sealed class AddBoundedArchiveDiscovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("ScanRecursive", "ArchiveJobs", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>("ScanComplete", "ArchiveJobs", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>("DiscoveryKey", "ArchiveEntries", type: "TEXT", nullable: true);
        migrationBuilder.CreateIndex("IX_ArchiveEntries_Path_CreatedAt", "ArchiveEntries", new[] { "Path", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_ArchiveEntries_OutputPath", "ArchiveEntries", "OutputPath");
        migrationBuilder.CreateIndex("IX_ArchiveEntries_BackupPath", "ArchiveEntries", "BackupPath");
        migrationBuilder.CreateIndex("IX_ArchiveEntries_JobId_DiscoveryKey", "ArchiveEntries", new[] { "JobId", "DiscoveryKey" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_ArchiveEntries_Path_CreatedAt", "ArchiveEntries");
        migrationBuilder.DropIndex("IX_ArchiveEntries_OutputPath", "ArchiveEntries");
        migrationBuilder.DropIndex("IX_ArchiveEntries_BackupPath", "ArchiveEntries");
        migrationBuilder.DropIndex("IX_ArchiveEntries_JobId_DiscoveryKey", "ArchiveEntries");
        migrationBuilder.DropColumn("DiscoveryKey", "ArchiveEntries");
        migrationBuilder.DropColumn("ScanRecursive", "ArchiveJobs");
        migrationBuilder.DropColumn("ScanComplete", "ArchiveJobs");
    }
}
