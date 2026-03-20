using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Options;
using Recon.Core;
using Recon.Domain;

namespace Recon.Api;

public sealed record CreateProjectRequest(
    string Name,
    string? Description,
    string? ExternalReference,
    string? OwnerReference,
    string? SiteReference,
    string? SourceType,
    JsonElement? Config);

public sealed record ProjectResponse(
    Guid Id,
    string Name,
    string? Description,
    ProjectStatus Status,
    string? ExternalReference,
    string? OwnerReference,
    string? SiteReference,
    string? SourceType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ProjectRunSummaryResponse? LatestRun,
    int TotalImageCount,
    int ValidImageCount,
    int ArtifactCount);

public sealed record ProjectRunSummaryResponse(Guid Id, PipelineRunStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? FinishedAtUtc);
public sealed record ProjectListItemResponse(Guid Id, string Name, ProjectStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record PagedResponse<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int TotalCount);
public sealed record UploadImagesResponse(IReadOnlyCollection<ProjectImageResponse> Images);
public sealed record CreateImportBatchRequest(IReadOnlyCollection<string> Urls);
public sealed record ImportBatchItemResponse(Guid Id, string SourceUrl, ImportItemStatus Status, string? ErrorMessage, Guid? ProjectImageId);
public sealed record ImportBatchResponse(Guid Id, ImportBatchStatus Status, int RequestedCount, int SucceededCount, int FailedCount, IReadOnlyCollection<ImportBatchItemResponse> Items);
public sealed record ProjectImageResponse(Guid Id, string OriginalFileName, string SourceType, string? SourceUrl, string MimeType, long FileSizeBytes, string ValidationStatus, bool IsValidImage, int? Width, int? Height);
public sealed record CreatePipelineRunRequest(IReadOnlyCollection<PipelineStage>? Stages, bool ForceRebuild);
public sealed record PipelineRunResponse(Guid Id, PipelineRunStatus Status, string PipelineVersion, DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc);
public sealed record PipelineRunDetailsResponse(PipelineRunResponse Run, IReadOnlyCollection<StageReportResponse> StageReports, IReadOnlyCollection<JobResponse> Jobs, IReadOnlyCollection<ArtifactResponse> Artifacts);
public sealed record StageReportResponse(PipelineStage Stage, bool Success, DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc, double DurationSeconds);
public sealed record ArtifactResponse(Guid Id, ArtifactType Type, ArtifactStatus Status, string FileName, string MimeType, long? FileSizeBytes, Guid? PipelineRunId, DateTimeOffset CreatedAtUtc);
public sealed record JobResponse(Guid Id, JobType Type, JobStatus Status, Guid ProjectId, Guid? PipelineRunId, decimal? ProgressPercent, string? ProgressMessage, DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc);
public sealed record JobDetailsResponse(Guid Id, JobType Type, JobStatus Status, Guid ProjectId, Guid? PipelineRunId, int AttemptCount, int MaxAttempts, decimal? ProgressPercent, string? ProgressMessage, string InputJson, string? OutputJson, string? ErrorJson, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc);
public sealed record ProcessNextJobResponse(bool Handled, Guid? JobId, JobDetailsResponse? Job);
public sealed record ProcessSelectedJobResponse(bool Handled, JobDetailsResponse Job);

public sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateImportBatchRequestValidator : AbstractValidator<CreateImportBatchRequest>
{
    public CreateImportBatchRequestValidator(IOptions<ReconOptions> options)
    {
        RuleFor(x => x.Urls)
            .NotNull()
            .Must(x => x.Count > 0).WithMessage("At least one URL is required.")
            .Must(x => x.Count <= options.Value.MaxImportUrlCount).WithMessage($"A maximum of {options.Value.MaxImportUrlCount} URLs is allowed.");

        RuleForEach(x => x.Urls).Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .WithMessage("Each URL must be an absolute http or https URL.");
    }
}

public sealed class CreatePipelineRunRequestValidator : AbstractValidator<CreatePipelineRunRequest>
{
    public CreatePipelineRunRequestValidator()
    {
        RuleForEach(x => x.Stages).IsInEnum();
    }
}
