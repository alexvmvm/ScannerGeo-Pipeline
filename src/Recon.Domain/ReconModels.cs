using System.Text.Json.Serialization;

namespace Recon.Domain;

public enum ProjectStatus
{
    Draft,
    ReadyForProcessing,
    Processing,
    Succeeded,
    Failed,
    Archived
}

public enum ImportBatchStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    PartiallyCompleted
}

public enum ImportItemStatus
{
    Pending,
    Downloading,
    Succeeded,
    Failed,
    Rejected
}

public enum PipelineRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    RetryScheduled
}

public enum JobType
{
    ValidateUploadedImage,
    ImportImageFromUrl,
    StartPipelineRun,
    InspectProject,
    RunSparseReconstruction,
    RunDenseReconstruction,
    ExportArtifacts,
    PublishArtifacts,
    GenerateProjectSummary
}

public enum ArtifactType
{
    OriginalImage,
    Thumbnail,
    InspectReport,
    SparseModel,
    DensePointCloud,
    DenseReport,
    SparseReport,
    ExportReport,
    OctreePackage,
    PotreePackage,
    LogFile,
    SummaryJson
}

public enum ArtifactStatus
{
    Pending,
    Available,
    Failed,
    Superseded
}

public enum PipelineStage
{
    Inspect,
    Sparse,
    Dense,
    Export,
    Publish
}

public sealed class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; }
    public string? ExternalReference { get; set; }
    public string? OwnerReference { get; set; }
    public string? SiteReference { get; set; }
    public string? SourceType { get; set; }
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ProjectImage
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool IsValidImage { get; set; }
    public string ValidationStatus { get; set; } = string.Empty;
    public string? ValidationError { get; set; }
    public string? ExifJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ImportBatch
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public ImportBatchStatus Status { get; set; }
    public int RequestedCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public string? RequestJson { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ImportBatchItem
{
    public Guid Id { get; set; }
    public Guid ImportBatchId { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public ImportItemStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? ProjectImageId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class PipelineRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public PipelineRunStatus Status { get; set; }
    public string PipelineVersion { get; set; } = string.Empty;
    public string? RequestedStagesJson { get; set; }
    public string? ConfigSnapshotJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}

public sealed class Job
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PipelineRunId { get; set; }
    public JobType Type { get; set; }
    public JobStatus Status { get; set; }
    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public string InputJson { get; set; } = string.Empty;
    public string? OutputJson { get; set; }
    public string? ErrorJson { get; set; }
    public decimal? ProgressPercent { get; set; }
    public string? ProgressMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}

public sealed class Artifact
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PipelineRunId { get; set; }
    public ArtifactType Type { get; set; }
    public ArtifactStatus Status { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long? FileSizeBytes { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class StageReport
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PipelineRunId { get; set; }
    public PipelineStage Stage { get; set; }
    public bool Success { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public string JsonPayload { get; set; } = string.Empty;
}
