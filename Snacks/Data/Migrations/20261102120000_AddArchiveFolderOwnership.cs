using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Snacks.Data.Migrations;

[DbContext(typeof(SnacksDbContext))]
[Migration("20261102120000_AddArchiveFolderOwnership")]
public sealed class AddArchiveFolderOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("FolderJobId", "ArchiveEntries", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("FolderConcurrency", "ArchiveJobs", type: "INTEGER", nullable: true);
        migrationBuilder.Sql("CREATE INDEX IX_ArchiveEntries_PathNoCase_CreatedAt_Id ON ArchiveEntries (Path COLLATE NOCASE, CreatedAt, Id)");
        migrationBuilder.CreateIndex("IX_ArchiveEntries_FolderJobId_Stage", "ArchiveEntries", new[] { "FolderJobId", "Stage" });
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IX_ArchiveEntries_PathNoCase_CreatedAt_Id");
        migrationBuilder.DropIndex("IX_ArchiveEntries_FolderJobId_Stage", "ArchiveEntries");
        migrationBuilder.DropColumn("FolderJobId", "ArchiveEntries");
        migrationBuilder.DropColumn("FolderConcurrency", "ArchiveJobs");
    }
}
