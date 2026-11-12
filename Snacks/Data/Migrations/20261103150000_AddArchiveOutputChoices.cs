using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Snacks.Data.Migrations;

[DbContext(typeof(SnacksDbContext))]
[Migration("20261103150000_AddArchiveOutputChoices")]
public sealed class AddArchiveOutputChoices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("PendingOutputPolicyJson", "ArchiveJobs", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("OutputPath", "EncodeHistory", type: "TEXT", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("PendingOutputPolicyJson", "ArchiveJobs");
        migrationBuilder.DropColumn("OutputPath", "EncodeHistory");
    }
}
