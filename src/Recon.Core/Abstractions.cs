using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Recon.Domain;

namespace Recon.Core;

public static class ReconJson
{
    public static readonly JsonSerializerOptions Defaults = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

public interface IReconDbContext
{
    DbSet<Project> Projects { get; }
    DbSet<ProjectImage> ProjectImages { get; }
    DbSet<ImportBatch> ImportBatches { get; }
    DbSet<ImportBatchItem> ImportBatchItems { get; }
    DbSet<PipelineRun> PipelineRuns { get; }
    DbSet<Job> Jobs { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<StageReport> StageReports { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IStorageKeyFactory
{
    string GetOriginalImageKey(Guid projectId, Guid imageId, string fileName);
    string GetThumbnailKey(Guid projectId, Guid imageId);
    string GetReportKey(Guid projectId, Guid runId, PipelineStage stage);
    string GetDenseOutputKey(Guid projectId, Guid runId, string fileName);
    string GetSparseOutputKey(Guid projectId, Guid runId, string fileName);
    string GetExportOutputKey(Guid projectId, Guid runId, string fileName);
    string GetLogKey(Guid projectId, Guid runId, Guid jobId);
    string GetSummaryKey(Guid projectId);
}

public sealed record StoredObject(string ContentType, long Length, Stream Stream);

public interface IObjectStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<StoredObject?> OpenReadAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed record ImageInspectionResult(
    string MimeType,
    long FileSizeBytes,
    int Width,
    int Height,
    string Sha256,
    string? ExifJson,
    byte[] ThumbnailBytes,
    string ThumbnailContentType);

public interface IImageInspector
{
    Task<ImageInspectionResult> InspectAsync(byte[] fileBytes, CancellationToken ct);
}

public interface IUrlImportSecurityValidator
{
    Task ValidateAsync(Uri uri, CancellationToken ct);
    bool IsBlocked(IPAddress address);
}

public sealed record UrlImportResult(string FileName, string ContentType, byte[] Bytes);

public interface IUrlImporter
{
    Task<UrlImportResult> DownloadAsync(Uri uri, CancellationToken ct);
}

public sealed record PipelineArtifact(string FileName, string ContentType, byte[] Bytes, ArtifactType ArtifactType, string MetadataJson);
public sealed record PipelineExecutionResult(bool Success, string ReportJson, IReadOnlyCollection<PipelineArtifact> Artifacts);
public sealed record ProjectPipelineContext(Project Project, PipelineRun Run, IReadOnlyCollection<ProjectImage> Images, string WorkingDirectory);
public sealed record ProcessExecutionResult(string FileName, IReadOnlyCollection<string> Arguments, int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct);
}

public interface IProjectPipelineService
{
    Task<PipelineExecutionResult> RunInspectAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunSparseAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunDenseAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunExportAsync(ProjectPipelineContext context, CancellationToken ct);
    Task<PipelineExecutionResult> RunPublishAsync(ProjectPipelineContext context, CancellationToken ct);
}

public interface IJobQueue
{
    Task EnqueueAsync(Job job, CancellationToken ct);
    Task<Job?> DequeueNextAsync(CancellationToken ct);
    Task MarkRunningAsync(Guid jobId, CancellationToken ct);
    Task MarkSucceededAsync(Guid jobId, string? outputJson, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, string errorJson, bool shouldRetry, CancellationToken ct);
    Task ReportProgressAsync(Guid jobId, decimal percent, string? message, CancellationToken ct);
}

public interface ICallerContext
{
    string CallerId { get; }
}

public sealed class ReconOptions
{
    public int MaxUploadFileCount { get; set; } = 100;
    public long MaxUploadFileSizeBytes { get; set; } = 25 * 1024 * 1024;
    public long MaxUploadRequestBodySizeBytes { get; set; } = 500 * 1024 * 1024;
    public int MaxImportUrlCount { get; set; } = 100;
    public int MinimumValidImageCount { get; set; } = 3;
    public long MaxImportDownloadBytes { get; set; } = 50 * 1024 * 1024;
    public int ImportTimeoutSeconds { get; set; } = 15;
    public int MaxRedirects { get; set; } = 3;
    public string[] AllowedExtensions { get; set; } = [".jpg", ".jpeg", ".png", ".tif", ".tiff"];
    public string ObjectStorageProvider { get; set; } = "Minio";
    public string StorageRootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "storage");
    public string ObjectStorageBucket { get; set; } = "recon";
    public string? ObjectStorageEndpoint { get; set; } = "http://localhost:9000";
    public string? ObjectStorageAccessKey { get; set; } = "minioadmin";
    public string? ObjectStorageSecretKey { get; set; } = "minioadmin";
    public bool ObjectStorageUseSsl { get; set; }
    public string PipelineProvider { get; set; } = "Simulated";
    public string ColmapBinaryPath { get; set; } = "colmap";
    public bool ColmapUseGpu { get; set; }
    public string? OctreeCliPath { get; set; }
    public string? OctreeCliProjectPath { get; set; }
    public int WorkerIdlePollSeconds { get; set; } = 5;
    public string ScratchRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "recon");
    public string PipelineVersion { get; set; } = "mvp-simulated";
}

public static class JobPayloads
{
    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, ReconJson.Defaults);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, ReconJson.Defaults)!;
}

public sealed record ValidateUploadedImagePayload(Guid ProjectImageId);
public sealed record ImportImageFromUrlPayload(Guid ImportBatchId, Guid ImportBatchItemId, Guid ProjectId, string Url);
public sealed record StartPipelineRunPayload(Guid RunId, IReadOnlyCollection<PipelineStage> Stages, bool ForceRebuild);
public sealed record PipelineStagePayload(Guid RunId, IReadOnlyCollection<PipelineStage> Stages);

public sealed class NotFoundException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
public sealed class RequestValidationException(string message, IDictionary<string, string[]> errors) : Exception(message)
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}

public static class ValidationExtensions
{
    public static async Task ValidateAndThrowRequestAsync<T>(this IValidator<T> validator, T instance, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(instance, ct);
        if (result.IsValid)
        {
            return;
        }

        var errors = result.Errors
            .GroupBy(x => x.PropertyName)
            .ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray());
        throw new RequestValidationException("The request is invalid.", errors);
    }
}
