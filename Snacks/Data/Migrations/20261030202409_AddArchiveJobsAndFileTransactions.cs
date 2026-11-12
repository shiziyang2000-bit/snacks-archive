using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacks.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveJobsAndFileTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchiveEntryId",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArchiveCaches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Start = table.Column<double>(type: "REAL", nullable: false),
                    Duration = table.Column<double>(type: "REAL", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveCaches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveEntries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    InputPath = table.Column<string>(type: "TEXT", nullable: false),
                    OutputPath = table.Column<string>(type: "TEXT", nullable: false),
                    BackupPath = table.Column<string>(type: "TEXT", nullable: true),
                    SourceHash = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationHash = table.Column<string>(type: "TEXT", nullable: false),
                    OutputHash = table.Column<string>(type: "TEXT", nullable: true),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    Codec = table.Column<string>(type: "TEXT", nullable: false),
                    SourceMbps = table.Column<double>(type: "REAL", nullable: false),
                    TargetMbps = table.Column<double>(type: "REAL", nullable: false),
                    ActualMbps = table.Column<double>(type: "REAL", nullable: true),
                    SourceBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    MediaJson = table.Column<string>(type: "TEXT", nullable: false),
                    EncoderMode = table.Column<string>(type: "TEXT", nullable: false),
                    Parameters = table.Column<string>(type: "TEXT", nullable: true),
                    TempOutputPath = table.Column<string>(type: "TEXT", nullable: true),
                    RollbackPath = table.Column<string>(type: "TEXT", nullable: true),
                    RestoreTempPath = table.Column<string>(type: "TEXT", nullable: true),
                    RestoreDisplacedPath = table.Column<string>(type: "TEXT", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: true),
                    ScoreStatus = table.Column<string>(type: "TEXT", nullable: false),
                    SamplesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RestoredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RulesSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRoot = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedNoBackup = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfirmedUseCurrentOutput = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ArchiveEntryId",
                table: "MediaFiles",
                column: "ArchiveEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveCaches_EntryId",
                table: "ArchiveCaches",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveCaches_Path",
                table: "ArchiveCaches",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_JobId",
                table: "ArchiveEntries",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_OutputHash",
                table: "ArchiveEntries",
                column: "OutputHash");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_SourceHash",
                table: "ArchiveEntries",
                column: "SourceHash");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_Stage",
                table: "ArchiveEntries",
                column: "Stage");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveJobs_RequestId",
                table: "ArchiveJobs",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveCaches");

            migrationBuilder.DropTable(
                name: "ArchiveEntries");

            migrationBuilder.DropTable(
                name: "ArchiveJobs");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ArchiveEntryId",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "ArchiveEntryId",
                table: "MediaFiles");
        }
    }
}
