using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Snacks.Data.Migrations;

[DbContext(typeof(SnacksDbContext))]
[Migration("20261104120000_AddArchiveEncodeTiming")]
public sealed class AddArchiveEncodeTiming : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The audited offline timing import may create this additive table first.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS ArchiveEncodeTimings (
                Id TEXT NOT NULL PRIMARY KEY, EntryId TEXT NOT NULL,
                EncodeSeconds REAL NOT NULL, Finished INTEGER NOT NULL, Source TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ArchiveEncodeTimings_EntryId ON ArchiveEncodeTimings (EntryId);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ArchiveEncodeTimings");
}
