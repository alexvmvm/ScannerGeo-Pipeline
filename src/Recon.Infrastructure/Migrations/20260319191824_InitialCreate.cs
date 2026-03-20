using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recon.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_batch_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ProjectImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_batch_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    ErrorJson = table.Column<string>(type: "text", nullable: true),
                    ProgressPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    ProgressMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PipelineVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestedStagesJson = table.Column<string>(type: "text", nullable: true),
                    ConfigSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipeline_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "project_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    MimeType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsValidImage = table.Column<bool>(type: "boolean", nullable: false),
                    ValidationStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ValidationError = table.Column<string>(type: "text", nullable: true),
                    ExifJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_images", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExternalReference = table.Column<string>(type: "text", nullable: true),
                    OwnerReference = table.Column<string>(type: "text", nullable: true),
                    SiteReference = table.Column<string>(type: "text", nullable: true),
                    SourceType = table.Column<string>(type: "text", nullable: true),
                    ConfigJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stage_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    JsonPayload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stage_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_PipelineRunId",
                table: "artifacts",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_ProjectId",
                table: "artifacts",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_Status",
                table: "artifacts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_Type",
                table: "artifacts",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_import_batch_items_ImportBatchId",
                table: "import_batch_items",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_import_batch_items_Status",
                table: "import_batch_items",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_ProjectId",
                table: "import_batches",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_Status",
                table: "import_batches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_PipelineRunId",
                table: "jobs",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_ProjectId",
                table: "jobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status",
                table: "jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_Priority_CreatedAtUtc",
                table: "jobs",
                columns: new[] { "Status", "Priority", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Type",
                table: "jobs",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_runs_CreatedAtUtc",
                table: "pipeline_runs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_runs_ProjectId",
                table: "pipeline_runs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_runs_Status",
                table: "pipeline_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_project_images_ProjectId",
                table: "project_images",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_project_images_ProjectId_Sha256",
                table: "project_images",
                columns: new[] { "ProjectId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_project_images_ValidationStatus",
                table: "project_images",
                column: "ValidationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_projects_CreatedAtUtc",
                table: "projects",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_projects_Status",
                table: "projects",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_stage_reports_PipelineRunId",
                table: "stage_reports",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_stage_reports_ProjectId",
                table: "stage_reports",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_stage_reports_Stage",
                table: "stage_reports",
                column: "Stage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifacts");

            migrationBuilder.DropTable(
                name: "import_batch_items");

            migrationBuilder.DropTable(
                name: "import_batches");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "pipeline_runs");

            migrationBuilder.DropTable(
                name: "project_images");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "stage_reports");
        }
    }
}
