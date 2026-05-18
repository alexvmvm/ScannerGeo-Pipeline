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
public sealed record Point3Request(double X, double Y, double Z);
public sealed record ImagesForPointRequest(Point3Request? Point, int? MaxResults, Guid? RunId, bool? IncludeImageUrls);
public sealed record Point3Response(double X, double Y, double Z);
public sealed record ImagePointMatchResponse(Guid ImageId, string FileName, int Width, int Height, double U, double V, double Score, double CameraDistance, double DistanceToImageCenterPixels, string? ImageUrl, string? ThumbnailUrl);
public sealed record ImagesForPointResponse(Guid ProjectId, Guid RunId, Point3Response QueryPoint, int MatchCount, IReadOnlyCollection<ImagePointMatchResponse> Matches);
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

public sealed class ImagesForPointRequestValidator : AbstractValidator<ImagesForPointRequest>
{
    public ImagesForPointRequestValidator()
    {
        RuleFor(x => x.Point).NotNull().WithMessage("Point is required.");
        When(x => x.Point is not null, () =>
        {
            RuleFor(x => x.Point!.X).Must(double.IsFinite).WithMessage("Point.X must be finite.");
            RuleFor(x => x.Point!.Y).Must(double.IsFinite).WithMessage("Point.Y must be finite.");
            RuleFor(x => x.Point!.Z).Must(double.IsFinite).WithMessage("Point.Z must be finite.");
        });

        RuleFor(x => x.MaxResults)
            .Must(x => x is null || (x >= 1 && x <= 200))
            .WithMessage("MaxResults must be between 1 and 200.");
    }
}
