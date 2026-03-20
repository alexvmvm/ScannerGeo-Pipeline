using Microsoft.EntityFrameworkCore;
using Recon.Core;
using Recon.Domain;

namespace Recon.Infrastructure;

public sealed class ReconDbContext(DbContextOptions<ReconDbContext> options) : DbContext(options), IReconDbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectImage> ProjectImages => Set<ProjectImage>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportBatchItem> ImportBatchItems => Set<ImportBatchItem>();
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<StageReport> StageReports => Set<StageReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ProjectImage>(entity =>
        {
            entity.ToTable("project_images");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalFileName).HasMaxLength(512);
            entity.Property(x => x.StorageKey).HasMaxLength(1024);
            entity.Property(x => x.SourceType).HasMaxLength(100);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.Property(x => x.MimeType).HasMaxLength(200);
            entity.Property(x => x.Sha256).HasMaxLength(128);
            entity.Property(x => x.ValidationStatus).HasMaxLength(100);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => new { x.ProjectId, x.Sha256 });
            entity.HasIndex(x => x.ValidationStatus);
        });

        modelBuilder.Entity<ImportBatch>(entity =>
        {
            entity.ToTable("import_batches");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ImportBatchItem>(entity =>
        {
            entity.ToTable("import_batch_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048);
            entity.HasIndex(x => x.ImportBatchId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<PipelineRun>(entity =>
        {
            entity.ToTable("pipeline_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PipelineVersion).HasMaxLength(100);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.Type);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.PipelineRunId);
            entity.HasIndex(x => new { x.Status, x.Priority, x.CreatedAtUtc });
            entity.Property(x => x.ProgressPercent).HasPrecision(5, 2);
        });

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.ToTable("artifacts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StorageKey).HasMaxLength(1024);
            entity.Property(x => x.FileName).HasMaxLength(512);
            entity.Property(x => x.MimeType).HasMaxLength(200);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.PipelineRunId);
            entity.HasIndex(x => x.Type);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<StageReport>(entity =>
        {
            entity.ToTable("stage_reports");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.PipelineRunId);
            entity.HasIndex(x => x.Stage);
        });
    }
}
