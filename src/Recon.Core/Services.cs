using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recon.Domain;

namespace Recon.Core;

public sealed record CreateProjectModel(
    string Name,
    string? Description,
    string? ExternalReference,
    string? OwnerReference,
    string? SiteReference,
    string? SourceType,
    JsonElement? Config);

public sealed record ProjectQuery(
    ProjectStatus? Status,
    DateTimeOffset? CreatedAfter,
    DateTimeOffset? CreatedBefore,
    string? Search,
    int Page,
    int PageSize);

public sealed record ProjectImageQuery(string? ValidationStatus, string? SourceType);
public sealed record ArtifactQuery(ArtifactType? Type, Guid? RunId, ArtifactStatus? Status);
public sealed record JobQuery(JobStatus? Status, Guid? ProjectId, Guid? RunId, int Take);
public sealed record RunStartModel(IReadOnlyCollection<PipelineStage> Stages, bool ForceRebuild);
public sealed record PagedResult<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int TotalCount);
public sealed record RunDetails(PipelineRun Run, IReadOnlyCollection<Job> Jobs, IReadOnlyCollection<StageReport> Reports, IReadOnlyCollection<Artifact> Artifacts);
public sealed record UploadImageModel(string SafeFileName, string ContentType, byte[] Bytes, string SourceType, string? SourceUrl);

public sealed class ProjectStatusService
{
    public ProjectStatus DetermineReadyStatus(Project project, int validImageCount, int minimumValidImages)
    {
        if (project.Status is ProjectStatus.Processing or ProjectStatus.Succeeded or ProjectStatus.Archived)
        {
            return project.Status;
        }

        return validImageCount >= minimumValidImages ? ProjectStatus.ReadyForProcessing : ProjectStatus.Draft;
    }

    public void MarkRunStarted(Project project)
    {
        if (project.Status == ProjectStatus.Archived)
        {
            throw new ConflictException("Archived projects cannot be processed.");
        }

        project.Status = ProjectStatus.Processing;
    }

    public void MarkRunSucceeded(Project project) => project.Status = ProjectStatus.Succeeded;
    public void MarkRunFailed(Project project) => project.Status = ProjectStatus.Failed;
}

public sealed class ProjectService(
    IReconDbContext dbContext,
    IClock clock,
    ProjectStatusService statusService,
    IOptions<ReconOptions> options,
    IObjectStorage objectStorage)
{
    private readonly ReconOptions _options = options.Value;

    public async Task<Project> CreateProjectAsync(CreateProjectModel model, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = model.Name.Trim(),
            Description = model.Description?.Trim(),
            ExternalReference = model.ExternalReference?.Trim(),
            OwnerReference = model.OwnerReference?.Trim(),
            SiteReference = model.SiteReference?.Trim(),
            SourceType = model.SourceType?.Trim(),
            ConfigJson = model.Config.HasValue ? JsonSerializer.Serialize(model.Config.Value, ReconJson.Defaults) : null,
            Status = ProjectStatus.Draft,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await dbContext.Projects.AddAsync(project, ct);
        await dbContext.SaveChangesAsync(ct);
        return project;
    }

    public async Task<PagedResult<Project>> ListProjectsAsync(ProjectQuery query, CancellationToken ct)
    {
        var projects = dbContext.Projects.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            projects = projects.Where(x => x.Status == status);
        }

        if (query.CreatedAfter is { } createdAfter)
        {
            projects = projects.Where(x => x.CreatedAtUtc >= createdAfter);
        }

        if (query.CreatedBefore is { } createdBefore)
        {
            projects = projects.Where(x => x.CreatedAtUtc <= createdBefore);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            projects = projects.Where(x =>
                x.Name.ToLower().Contains(term) ||
                (x.Description != null && x.Description.ToLower().Contains(term)) ||
                (x.ExternalReference != null && x.ExternalReference.ToLower().Contains(term)));
        }

        var allItems = await projects.ToListAsync(ct);
        var totalCount = allItems.Count;
        var items = allItems
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return new PagedResult<Project>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<Project> GetProjectAsync(Guid projectId, CancellationToken ct)
        => await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");

    public async Task<(Project Project, PipelineRun? LatestRun, int TotalImages, int ValidImages, int ArtifactCount)> GetProjectSummaryAsync(Guid projectId, CancellationToken ct)
    {
        var project = await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");
        var latestRun = (await dbContext.PipelineRuns.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        var totalImages = await dbContext.ProjectImages.CountAsync(x => x.ProjectId == projectId, ct);
        var validImages = await dbContext.ProjectImages.CountAsync(x => x.ProjectId == projectId && x.IsValidImage, ct);
        var artifactCount = await dbContext.Artifacts.CountAsync(x => x.ProjectId == projectId, ct);
        return (project, latestRun, totalImages, validImages, artifactCount);
    }

    public async Task RefreshProjectStatusAsync(Guid projectId, CancellationToken ct)
    {
        var project = await dbContext.Projects.FirstAsync(x => x.Id == projectId, ct);
        var validImageCount = await dbContext.ProjectImages.CountAsync(x => x.ProjectId == projectId && x.IsValidImage, ct);
        project.Status = statusService.DetermineReadyStatus(project, validImageCount, _options.MinimumValidImageCount);
        project.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken ct)
    {
        var project = await dbContext.Projects.FirstOrDefaultAsync(x => x.Id == projectId, ct)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");
        var hasActiveJobs = await dbContext.Jobs.AnyAsync(
            x => x.ProjectId == projectId &&
                 (x.Status == JobStatus.Queued || x.Status == JobStatus.Running || x.Status == JobStatus.RetryScheduled),
            ct);
        if (hasActiveJobs)
        {
            throw new ConflictException("The project has active jobs. Wait for them to finish before deleting the project.");
        }

        var runs = await dbContext.PipelineRuns.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var runIds = runs.Select(x => x.Id).ToArray();

        var importBatches = await dbContext.ImportBatches.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var importBatchIds = importBatches.Select(x => x.Id).ToArray();
        var importBatchItems = importBatchIds.Length == 0
            ? []
            : await dbContext.ImportBatchItems.Where(x => importBatchIds.Contains(x.ImportBatchId)).ToListAsync(ct);

        var jobs = await dbContext.Jobs.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var stageReports = runIds.Length == 0
            ? []
            : await dbContext.StageReports.Where(x => x.ProjectId == projectId || runIds.Contains(x.PipelineRunId)).ToListAsync(ct);
        var artifacts = await dbContext.Artifacts.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var images = await dbContext.ProjectImages.Where(x => x.ProjectId == projectId).ToListAsync(ct);

        var storageKeys = artifacts.Select(x => x.StorageKey)
            .Concat(images.Select(x => x.StorageKey))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var key in storageKeys)
        {
            await objectStorage.DeleteAsync(key, ct);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        dbContext.StageReports.RemoveRange(stageReports);
        dbContext.Jobs.RemoveRange(jobs);
        dbContext.Artifacts.RemoveRange(artifacts);
        dbContext.ProjectImages.RemoveRange(images);
        dbContext.ImportBatchItems.RemoveRange(importBatchItems);
        dbContext.ImportBatches.RemoveRange(importBatches);
        dbContext.PipelineRuns.RemoveRange(runs);
        dbContext.Projects.Remove(project);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        foreach (var runId in runIds)
        {
            var scratchPath = Path.Combine(_options.ScratchRootPath, runId.ToString("N"));
            if (Directory.Exists(scratchPath))
            {
                Directory.Delete(scratchPath, recursive: true);
            }
        }
    }
}

public sealed class ImageIntakeService(
    IReconDbContext dbContext,
    IObjectStorage objectStorage,
    IStorageKeyFactory storageKeyFactory,
    IJobQueue jobQueue,
    IClock clock)
{
    public async Task<IReadOnlyCollection<ProjectImage>> UploadAsync(Project project, IReadOnlyCollection<UploadImageModel> files, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var created = new List<ProjectImage>(files.Count);

        foreach (var file in files)
        {
            var imageId = Guid.NewGuid();
            var storageKey = storageKeyFactory.GetOriginalImageKey(project.Id, imageId, file.SafeFileName);
            await using var uploadStream = new MemoryStream(file.Bytes, writable: false);
            await objectStorage.SaveAsync(storageKey, uploadStream, file.ContentType, ct);

            var image = new ProjectImage
            {
                Id = imageId,
                ProjectId = project.Id,
                OriginalFileName = file.SafeFileName,
                StorageKey = storageKey,
                SourceType = file.SourceType,
                SourceUrl = file.SourceUrl,
                MimeType = file.ContentType,
                FileSizeBytes = file.Bytes.LongLength,
                ValidationStatus = "Pending",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await dbContext.ProjectImages.AddAsync(image, ct);
            await dbContext.Artifacts.AddAsync(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Type = ArtifactType.OriginalImage,
                Status = ArtifactStatus.Available,
                StorageKey = storageKey,
                FileName = file.SafeFileName,
                MimeType = file.ContentType,
                FileSizeBytes = file.Bytes.LongLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }, ct);
            await jobQueue.EnqueueAsync(CreateJob(project.Id, null, JobType.ValidateUploadedImage, new ValidateUploadedImagePayload(image.Id), now), ct);
            created.Add(image);
        }

        project.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(ct);
        return created;
    }

    public static Job CreateJob(Guid projectId, Guid? runId, JobType type, object payload, DateTimeOffset now, int priority = 100, int maxAttempts = 3)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            PipelineRunId = runId,
            Type = type,
            Status = JobStatus.Queued,
            Priority = priority,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            InputJson = JobPayloads.Serialize(payload),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
}

public sealed class ImportService(
    IReconDbContext dbContext,
    IJobQueue jobQueue,
    IClock clock)
{
    public async Task<ImportBatch> CreateBatchAsync(Project project, IReadOnlyCollection<Uri> urls, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var batch = new ImportBatch
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = ImportBatchStatus.Pending,
            RequestedCount = urls.Count,
            RequestJson = JsonSerializer.Serialize(new { urls }, ReconJson.Defaults),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await dbContext.ImportBatches.AddAsync(batch, ct);

        foreach (var uri in urls)
        {
            var item = new ImportBatchItem
            {
                Id = Guid.NewGuid(),
                ImportBatchId = batch.Id,
                SourceUrl = uri.ToString(),
                Status = ImportItemStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await dbContext.ImportBatchItems.AddAsync(item, ct);
            await jobQueue.EnqueueAsync(
                ImageIntakeService.CreateJob(project.Id, null, JobType.ImportImageFromUrl, new ImportImageFromUrlPayload(batch.Id, item.Id, project.Id, item.SourceUrl), now),
                ct);
        }

        batch.Status = ImportBatchStatus.Running;
        project.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<(ImportBatch Batch, IReadOnlyCollection<ImportBatchItem> Items)> GetBatchAsync(Guid projectId, Guid batchId, CancellationToken ct)
    {
        var batch = await dbContext.ImportBatches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == batchId, ct)
            ?? throw new NotFoundException($"Import batch '{batchId}' was not found.");
        var items = (await dbContext.ImportBatchItems.AsNoTracking()
            .Where(x => x.ImportBatchId == batchId)
            .ToListAsync(ct))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        return (batch, items);
    }

    public async Task UpdateBatchStateAsync(Guid batchId, CancellationToken ct)
    {
        var batch = await dbContext.ImportBatches.FirstAsync(x => x.Id == batchId, ct);
        var items = await dbContext.ImportBatchItems.Where(x => x.ImportBatchId == batchId).ToListAsync(ct);

        batch.SucceededCount = items.Count(x => x.Status == ImportItemStatus.Succeeded);
        batch.FailedCount = items.Count(x => x.Status is ImportItemStatus.Failed or ImportItemStatus.Rejected);
        batch.Status = batch.FailedCount switch
        {
            0 when batch.SucceededCount == batch.RequestedCount => ImportBatchStatus.Completed,
            > 0 when batch.SucceededCount > 0 => ImportBatchStatus.PartiallyCompleted,
            > 0 when batch.SucceededCount == 0 && items.All(x => x.Status is ImportItemStatus.Failed or ImportItemStatus.Rejected) => ImportBatchStatus.Failed,
            _ => ImportBatchStatus.Running
        };
        batch.ResultJson = JsonSerializer.Serialize(new { batch.RequestedCount, batch.SucceededCount, batch.FailedCount }, ReconJson.Defaults);
        batch.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }
}

public sealed class RunService(
    IReconDbContext dbContext,
    IJobQueue jobQueue,
    IClock clock,
    ProjectStatusService projectStatusService,
    IOptions<ReconOptions> options)
{
    private readonly ReconOptions _options = options.Value;

    public async Task<PipelineRun> StartRunAsync(Project project, RunStartModel model, CancellationToken ct)
    {
        var trackedProject = await dbContext.Projects.FirstOrDefaultAsync(x => x.Id == project.Id, ct)
            ?? throw new NotFoundException($"Project '{project.Id}' was not found.");
        var validImageCount = await dbContext.ProjectImages.CountAsync(x => x.ProjectId == project.Id && x.IsValidImage, ct);
        if (validImageCount < _options.MinimumValidImageCount)
        {
            throw new ConflictException($"At least {_options.MinimumValidImageCount} valid images are required.");
        }

        var activeRunExists = await dbContext.PipelineRuns.AnyAsync(
            x => x.ProjectId == project.Id && (x.Status == PipelineRunStatus.Queued || x.Status == PipelineRunStatus.Running),
            ct);
        if (activeRunExists)
        {
            throw new ConflictException("The project already has an active run.");
        }

        var now = clock.UtcNow;
        var stages = model.Stages.Count == 0
            ? [PipelineStage.Inspect, PipelineStage.Sparse, PipelineStage.Dense, PipelineStage.Export, PipelineStage.Publish]
            : model.Stages.Distinct().ToArray();

        var run = new PipelineRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = PipelineRunStatus.Queued,
            PipelineVersion = _options.PipelineVersion,
            RequestedStagesJson = JsonSerializer.Serialize(stages, ReconJson.Defaults),
            ConfigSnapshotJson = project.ConfigJson,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await dbContext.PipelineRuns.AddAsync(run, ct);
        projectStatusService.MarkRunStarted(trackedProject);
        trackedProject.UpdatedAtUtc = now;
        await jobQueue.EnqueueAsync(
            ImageIntakeService.CreateJob(project.Id, run.Id, JobType.StartPipelineRun, new StartPipelineRunPayload(run.Id, stages, model.ForceRebuild), now),
            ct);
        await dbContext.SaveChangesAsync(ct);
        return run;
    }

    public async Task<IReadOnlyCollection<PipelineRun>> ListRunsAsync(Guid projectId, CancellationToken ct)
        => (await dbContext.PipelineRuns.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToList();

    public async Task<RunDetails> GetRunDetailsAsync(Guid projectId, Guid runId, CancellationToken ct)
    {
        var run = await dbContext.PipelineRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == runId, ct)
            ?? throw new NotFoundException($"Run '{runId}' was not found.");
        var jobs = (await dbContext.Jobs.AsNoTracking().Where(x => x.ProjectId == projectId && x.PipelineRunId == runId).ToListAsync(ct))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        var reports = (await dbContext.StageReports.AsNoTracking().Where(x => x.ProjectId == projectId && x.PipelineRunId == runId).ToListAsync(ct))
            .OrderBy(x => x.StartedAtUtc)
            .ToList();
        var artifacts = (await dbContext.Artifacts.AsNoTracking().Where(x => x.ProjectId == projectId && x.PipelineRunId == runId).ToListAsync(ct))
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        return new RunDetails(run, jobs, reports, artifacts);
    }
}

public sealed class ArtifactService(IReconDbContext dbContext, IObjectStorage objectStorage)
{
    public async Task<IReadOnlyCollection<Artifact>> ListArtifactsAsync(Guid projectId, ArtifactQuery query, CancellationToken ct)
    {
        var artifacts = dbContext.Artifacts.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (query.Type is { } type)
        {
            artifacts = artifacts.Where(x => x.Type == type);
        }

        if (query.RunId is { } runId)
        {
            artifacts = artifacts.Where(x => x.PipelineRunId == runId);
        }

        if (query.Status is { } status)
        {
            artifacts = artifacts.Where(x => x.Status == status);
        }

        return await artifacts.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
    }

    public async Task<(Artifact Artifact, StoredObject Content)> GetArtifactContentAsync(Guid projectId, Guid artifactId, CancellationToken ct)
    {
        var artifact = await dbContext.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == artifactId, ct)
            ?? throw new NotFoundException($"Artifact '{artifactId}' was not found.");
        var content = await objectStorage.OpenReadAsync(artifact.StorageKey, ct)
            ?? throw new NotFoundException($"Artifact '{artifactId}' content is unavailable.");
        return (artifact, content);
    }

    public async Task<(Artifact Artifact, string EntryPath, byte[] Content)> GetScenePackageEntryContentAsync(
        Guid projectId,
        Guid artifactId,
        string entryPath,
        CancellationToken ct)
    {
        var normalizedEntryPath = NormalizeSceneEntryPath(entryPath);
        var (artifact, storedObject) = await GetArtifactContentAsync(projectId, artifactId, ct);
        if (artifact.Type != ArtifactType.OctreePackage)
        {
            throw new NotFoundException($"Artifact '{artifactId}' is not an octree scene package.");
        }

        await using var storedStream = storedObject.Stream;
        Stream archiveStream;
        if (storedStream.CanSeek)
        {
            storedStream.Position = 0;
            archiveStream = storedStream;
        }
        else
        {
            var buffered = new MemoryStream();
            await storedStream.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            archiveStream = buffered;
        }

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var zipEntryPath = $"scene/{normalizedEntryPath}";
        var entry = archive.Entries.FirstOrDefault(x => string.Equals(
            x.FullName.Replace('\\', '/'),
            zipEntryPath,
            StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new NotFoundException($"Scene entry '{normalizedEntryPath}' was not found in artifact '{artifactId}'.");
        }

        await using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        await entryStream.CopyToAsync(memory, ct);
        return (artifact, entry.FullName, memory.ToArray());
    }

    private static string NormalizeSceneEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            throw new NotFoundException("A scene entry path is required.");
        }

        var segments = entryPath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new NotFoundException("The requested scene entry path is invalid.");
        }

        return string.Join('/', segments);
    }
}

public sealed class ProjectImageService(
    IReconDbContext dbContext,
    IObjectStorage objectStorage,
    IStorageKeyFactory storageKeyFactory)
{
    public async Task<(ProjectImage Image, StoredObject Content)> GetImageContentAsync(
        Guid projectId,
        Guid imageId,
        string? variant,
        CancellationToken ct)
    {
        var image = await dbContext.ProjectImages.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == imageId, ct)
            ?? throw new NotFoundException($"Project image '{imageId}' was not found.");

        var normalizedVariant = string.IsNullOrWhiteSpace(variant) ? "original" : variant.Trim().ToLowerInvariant();
        return normalizedVariant switch
        {
            "original" => (image, await OpenRequiredAsync(image.StorageKey, $"Project image '{imageId}' content is unavailable.", ct)),
            "thumbnail" => await GetThumbnailOrOriginalAsync(image, ct),
            _ => throw new NotFoundException($"Image variant '{variant}' is not supported.")
        };
    }

    private async Task<(ProjectImage Image, StoredObject Content)> GetThumbnailOrOriginalAsync(ProjectImage image, CancellationToken ct)
    {
        var thumbnailKey = storageKeyFactory.GetThumbnailKey(image.ProjectId, image.Id);
        var thumbnail = await objectStorage.OpenReadAsync(thumbnailKey, ct);
        if (thumbnail is not null)
        {
            return (image, thumbnail);
        }

        return (image, await OpenRequiredAsync(image.StorageKey, $"Project image '{image.Id}' content is unavailable.", ct));
    }

    private async Task<StoredObject> OpenRequiredAsync(string storageKey, string message, CancellationToken ct)
        => await objectStorage.OpenReadAsync(storageKey, ct)
            ?? throw new NotFoundException(message);
}

public sealed class JobService(IReconDbContext dbContext)
{
    public async Task<IReadOnlyCollection<Job>> ListJobsAsync(JobQuery query, CancellationToken ct)
    {
        var jobs = dbContext.Jobs.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            jobs = jobs.Where(x => x.Status == status);
        }

        if (query.ProjectId is { } projectId)
        {
            jobs = jobs.Where(x => x.ProjectId == projectId);
        }

        if (query.RunId is { } runId)
        {
            jobs = jobs.Where(x => x.PipelineRunId == runId);
        }

        return (await jobs.ToListAsync(ct))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(query.Take, 1, 200))
            .ToList();
    }

    public async Task<Job> GetJobAsync(Guid jobId, CancellationToken ct)
        => await dbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId, ct)
            ?? throw new NotFoundException($"Job '{jobId}' was not found.");
}

public sealed class JobExecutionCoordinator(
    IJobQueue jobQueue,
    IReconDbContext dbContext,
    IClock clock,
    ProjectStatusService projectStatusService,
    IImageInspector imageInspector,
    IObjectStorage objectStorage,
    IStorageKeyFactory storageKeyFactory,
    IProjectPipelineService pipelineService,
    IUrlImporter urlImporter,
    IUrlImportSecurityValidator urlSecurityValidator,
    ImportService importService,
    ProjectService projectService,
    IOptions<ReconOptions> options,
    ILogger<JobExecutionCoordinator> logger)
{
    private readonly ReconOptions _options = options.Value;

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var job = await jobQueue.DequeueNextAsync(ct);
        if (job is null)
        {
            return false;
        }

        await ExecuteClaimedJobAsync(job, ct);
        return true;
    }

    public async Task<bool> ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null)
        {
            throw new NotFoundException($"Job '{jobId}' was not found.");
        }

        if (job.Status is not (JobStatus.Queued or JobStatus.RetryScheduled))
        {
            throw new ConflictException($"Job '{jobId}' is not queued.");
        }

        job.Status = JobStatus.Running;
        job.AttemptCount += 1;
        job.StartedAtUtc ??= clock.UtcNow;
        job.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        await ExecuteClaimedJobAsync(job, ct);
        return true;
    }

    private async Task ExecuteClaimedJobAsync(Job job, CancellationToken ct)
    {
        try
        {
            await HandleAsync(job, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            await jobQueue.MarkFailedAsync(job.Id, JsonSerializer.Serialize(new { error = ex.Message }, ReconJson.Defaults), shouldRetry: false, ct);
            await TryMarkRunAndProjectFailedAsync(job, ex.Message, ct);
        }
    }

    private async Task HandleAsync(Job job, CancellationToken ct)
    {
        switch (job.Type)
        {
            case JobType.ValidateUploadedImage:
                await HandleValidateUploadedImageAsync(job, ct);
                break;
            case JobType.ImportImageFromUrl:
                await HandleImportImageFromUrlAsync(job, ct);
                break;
            case JobType.StartPipelineRun:
                await HandleStartPipelineRunAsync(job, ct);
                break;
            case JobType.InspectProject:
                await HandlePipelineStageAsync(job, PipelineStage.Inspect, ct);
                break;
            case JobType.RunSparseReconstruction:
                await HandlePipelineStageAsync(job, PipelineStage.Sparse, ct);
                break;
            case JobType.RunDenseReconstruction:
                await HandlePipelineStageAsync(job, PipelineStage.Dense, ct);
                break;
            case JobType.ExportArtifacts:
                await HandlePipelineStageAsync(job, PipelineStage.Export, ct);
                break;
            case JobType.PublishArtifacts:
                await HandlePipelineStageAsync(job, PipelineStage.Publish, ct);
                break;
            case JobType.GenerateProjectSummary:
                await HandleGenerateSummaryAsync(job, ct);
                break;
            default:
                throw new NotSupportedException($"Job type '{job.Type}' is not supported.");
        }
    }

    private async Task HandleValidateUploadedImageAsync(Job job, CancellationToken ct)
    {
        var payload = JobPayloads.Deserialize<ValidateUploadedImagePayload>(job.InputJson);
        var image = await dbContext.ProjectImages.FirstOrDefaultAsync(x => x.Id == payload.ProjectImageId, ct)
            ?? throw new NotFoundException($"Project image '{payload.ProjectImageId}' was not found.");
        var stored = await objectStorage.OpenReadAsync(image.StorageKey, ct)
            ?? throw new NotFoundException($"Image '{image.Id}' content is unavailable.");

        await using var sourceStream = stored.Stream;
        await using var memory = new MemoryStream();
        await sourceStream.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();

        try
        {
            var inspection = await imageInspector.InspectAsync(bytes, ct);
            image.MimeType = inspection.MimeType;
            image.FileSizeBytes = inspection.FileSizeBytes;
            image.Width = inspection.Width;
            image.Height = inspection.Height;
            image.Sha256 = inspection.Sha256;
            image.IsValidImage = true;
            image.ValidationStatus = "Validated";
            image.ValidationError = null;
            image.ExifJson = inspection.ExifJson;
            image.UpdatedAtUtc = clock.UtcNow;

            var thumbKey = storageKeyFactory.GetThumbnailKey(image.ProjectId, image.Id);
            await using (var thumbStream = new MemoryStream(inspection.ThumbnailBytes, writable: false))
            {
                await objectStorage.SaveAsync(thumbKey, thumbStream, inspection.ThumbnailContentType, ct);
            }

            await dbContext.Artifacts.AddAsync(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = image.ProjectId,
                Type = ArtifactType.Thumbnail,
                Status = ArtifactStatus.Available,
                StorageKey = thumbKey,
                FileName = "thumb.jpg",
                MimeType = inspection.ThumbnailContentType,
                FileSizeBytes = inspection.ThumbnailBytes.LongLength,
                ChecksumSha256 = Convert.ToHexString(SHA256.HashData(inspection.ThumbnailBytes)).ToLowerInvariant(),
                CreatedAtUtc = clock.UtcNow,
                UpdatedAtUtc = clock.UtcNow
            }, ct);

            await dbContext.SaveChangesAsync(ct);
            await projectService.RefreshProjectStatusAsync(image.ProjectId, ct);
            await jobQueue.MarkSucceededAsync(job.Id, JsonSerializer.Serialize(new { imageId = image.Id }, ReconJson.Defaults), ct);
        }
        catch (Exception ex)
        {
            image.IsValidImage = false;
            image.ValidationStatus = "Failed";
            image.ValidationError = ex.Message;
            image.UpdatedAtUtc = clock.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            await projectService.RefreshProjectStatusAsync(image.ProjectId, ct);
            await jobQueue.MarkFailedAsync(job.Id, JsonSerializer.Serialize(new { error = ex.Message }, ReconJson.Defaults), shouldRetry: false, ct);
        }
    }

    private async Task HandleImportImageFromUrlAsync(Job job, CancellationToken ct)
    {
        var payload = JobPayloads.Deserialize<ImportImageFromUrlPayload>(job.InputJson);
        var item = await dbContext.ImportBatchItems.FirstAsync(x => x.Id == payload.ImportBatchItemId, ct);
        item.Status = ImportItemStatus.Downloading;
        item.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        try
        {
            var uri = new Uri(payload.Url);
            await urlSecurityValidator.ValidateAsync(uri, ct);
            var download = await urlImporter.DownloadAsync(uri, ct);

            var imageId = Guid.NewGuid();
            var storageKey = storageKeyFactory.GetOriginalImageKey(payload.ProjectId, imageId, download.FileName);
            await using (var uploadStream = new MemoryStream(download.Bytes, writable: false))
            {
                await objectStorage.SaveAsync(storageKey, uploadStream, download.ContentType, ct);
            }

            var now = clock.UtcNow;
            var image = new ProjectImage
            {
                Id = imageId,
                ProjectId = payload.ProjectId,
                OriginalFileName = download.FileName,
                StorageKey = storageKey,
                SourceType = "url_import",
                SourceUrl = uri.ToString(),
                MimeType = download.ContentType,
                FileSizeBytes = download.Bytes.LongLength,
                ValidationStatus = "Pending",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await dbContext.ProjectImages.AddAsync(image, ct);
            await dbContext.Artifacts.AddAsync(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = payload.ProjectId,
                Type = ArtifactType.OriginalImage,
                Status = ArtifactStatus.Available,
                StorageKey = storageKey,
                FileName = download.FileName,
                MimeType = download.ContentType,
                FileSizeBytes = download.Bytes.LongLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }, ct);

            item.Status = ImportItemStatus.Succeeded;
            item.ProjectImageId = image.Id;
            item.ErrorMessage = null;
            item.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(ct);

            await jobQueue.EnqueueAsync(
                ImageIntakeService.CreateJob(payload.ProjectId, null, JobType.ValidateUploadedImage, new ValidateUploadedImagePayload(image.Id), now),
                ct);
            await importService.UpdateBatchStateAsync(payload.ImportBatchId, ct);
            await jobQueue.MarkSucceededAsync(job.Id, JsonSerializer.Serialize(new { imageId = image.Id }, ReconJson.Defaults), ct);
        }
        catch (Exception ex)
        {
            item.Status = ex is SecurityException ? ImportItemStatus.Rejected : ImportItemStatus.Failed;
            item.ErrorMessage = ex.Message;
            item.UpdatedAtUtc = clock.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            await importService.UpdateBatchStateAsync(payload.ImportBatchId, ct);
            await jobQueue.MarkFailedAsync(job.Id, JsonSerializer.Serialize(new { error = ex.Message }, ReconJson.Defaults), shouldRetry: false, ct);
        }
    }

    private async Task HandleStartPipelineRunAsync(Job job, CancellationToken ct)
    {
        var payload = JobPayloads.Deserialize<StartPipelineRunPayload>(job.InputJson);
        var run = await dbContext.PipelineRuns.FirstAsync(x => x.Id == payload.RunId, ct);
        run.Status = PipelineRunStatus.Running;
        run.StartedAtUtc = clock.UtcNow;
        run.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        var firstStage = payload.Stages.OrderBy(StageSort).FirstOrDefault();
        await jobQueue.EnqueueAsync(
            ImageIntakeService.CreateJob(job.ProjectId, run.Id, MapStageToJobType(firstStage), new PipelineStagePayload(run.Id, payload.Stages), clock.UtcNow),
            ct);
        await jobQueue.MarkSucceededAsync(job.Id, JsonSerializer.Serialize(new { runId = run.Id }, ReconJson.Defaults), ct);
    }

    private async Task HandlePipelineStageAsync(Job job, PipelineStage stage, CancellationToken ct)
    {
        var payload = JobPayloads.Deserialize<PipelineStagePayload>(job.InputJson);
        var run = await dbContext.PipelineRuns.FirstAsync(x => x.Id == payload.RunId, ct);
        var project = await dbContext.Projects.FirstAsync(x => x.Id == run.ProjectId, ct);
        var images = await dbContext.ProjectImages.Where(x => x.ProjectId == project.Id && x.IsValidImage).ToListAsync(ct);
        var startedAt = clock.UtcNow;

        await jobQueue.ReportProgressAsync(job.Id, 10m, $"Running {stage}", ct);
        var context = new ProjectPipelineContext(project, run, images, Path.Combine(_options.ScratchRootPath, run.Id.ToString("N")));
        var result = stage switch
        {
            PipelineStage.Inspect => await pipelineService.RunInspectAsync(context, ct),
            PipelineStage.Sparse => await pipelineService.RunSparseAsync(context, ct),
            PipelineStage.Dense => await pipelineService.RunDenseAsync(context, ct),
            PipelineStage.Export => await pipelineService.RunExportAsync(context, ct),
            PipelineStage.Publish => await pipelineService.RunPublishAsync(context, ct),
            _ => throw new NotSupportedException($"Stage '{stage}' is not supported.")
        };

        var reportKey = storageKeyFactory.GetReportKey(project.Id, run.Id, stage);
        var reportBytes = Encoding.UTF8.GetBytes(result.ReportJson);
        await using (var reportStream = new MemoryStream(reportBytes, writable: false))
        {
            await objectStorage.SaveAsync(reportKey, reportStream, "application/json", ct);
        }

        await dbContext.Artifacts.AddAsync(new Artifact
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            Type = stage switch
            {
                PipelineStage.Inspect => ArtifactType.InspectReport,
                PipelineStage.Sparse => ArtifactType.SparseReport,
                PipelineStage.Dense => ArtifactType.DenseReport,
                PipelineStage.Export => ArtifactType.ExportReport,
                PipelineStage.Publish => ArtifactType.PublishReport,
                _ => ArtifactType.LogFile
            },
            Status = ArtifactStatus.Available,
            StorageKey = reportKey,
            FileName = Path.GetFileName(reportKey),
            MimeType = "application/json",
            FileSizeBytes = reportBytes.LongLength,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow
        }, ct);

        foreach (var generatedArtifact in result.Artifacts)
        {
            var key = generatedArtifact.ArtifactType switch
            {
                ArtifactType.SparseModel => storageKeyFactory.GetSparseOutputKey(project.Id, run.Id, generatedArtifact.FileName),
                ArtifactType.OctreePackage or ArtifactType.PotreePackage or ArtifactType.ExportPackage => storageKeyFactory.GetExportOutputKey(project.Id, run.Id, generatedArtifact.FileName),
                _ => storageKeyFactory.GetDenseOutputKey(project.Id, run.Id, generatedArtifact.FileName)
            };

            await using var artifactStream = new MemoryStream(generatedArtifact.Bytes, writable: false);
            await objectStorage.SaveAsync(key, artifactStream, generatedArtifact.ContentType, ct);
            await dbContext.Artifacts.AddAsync(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                PipelineRunId = run.Id,
                Type = generatedArtifact.ArtifactType,
                Status = ArtifactStatus.Available,
                StorageKey = key,
                FileName = generatedArtifact.FileName,
                MimeType = generatedArtifact.ContentType,
                FileSizeBytes = generatedArtifact.Bytes.LongLength,
                MetadataJson = generatedArtifact.MetadataJson,
                CreatedAtUtc = clock.UtcNow,
                UpdatedAtUtc = clock.UtcNow
            }, ct);
        }

        var finishedAt = clock.UtcNow;
        await dbContext.StageReports.AddAsync(new StageReport
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            Stage = stage,
            Success = result.Success,
            StartedAtUtc = startedAt,
            FinishedAtUtc = finishedAt,
            DurationSeconds = (finishedAt - startedAt).TotalSeconds,
            JsonPayload = result.ReportJson
        }, ct);

        run.UpdatedAtUtc = finishedAt;
        await dbContext.SaveChangesAsync(ct);
        await jobQueue.MarkSucceededAsync(job.Id, JsonSerializer.Serialize(new { stage }, ReconJson.Defaults), ct);

        var nextStageJob = GetNextQueuedStageJobType(stage, payload.Stages);
        if (nextStageJob is { } nextJobType)
        {
            await jobQueue.EnqueueAsync(ImageIntakeService.CreateJob(project.Id, run.Id, nextJobType, new PipelineStagePayload(run.Id, payload.Stages), clock.UtcNow), ct);
            await dbContext.SaveChangesAsync(ct);
            return;
        }

        await jobQueue.EnqueueAsync(ImageIntakeService.CreateJob(project.Id, run.Id, JobType.GenerateProjectSummary, new PipelineStagePayload(run.Id, payload.Stages), clock.UtcNow), ct);
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task HandleGenerateSummaryAsync(Job job, CancellationToken ct)
    {
        var payload = JobPayloads.Deserialize<PipelineStagePayload>(job.InputJson);
        var run = await dbContext.PipelineRuns.FirstAsync(x => x.Id == payload.RunId, ct);
        var project = await dbContext.Projects.FirstAsync(x => x.Id == run.ProjectId, ct);
        var artifacts = await dbContext.Artifacts.Where(x => x.ProjectId == project.Id && x.PipelineRunId == run.Id).ToListAsync(ct);
        var reports = await dbContext.StageReports.Where(x => x.ProjectId == project.Id && x.PipelineRunId == run.Id).ToListAsync(ct);

        var summary = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            runId = run.Id,
            completedStages = reports.Select(x => x.Stage),
            artifactCount = artifacts.Count,
            generatedAtUtc = clock.UtcNow
        }, ReconJson.Defaults);
        var key = storageKeyFactory.GetSummaryKey(project.Id);
        var bytes = Encoding.UTF8.GetBytes(summary);
        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            await objectStorage.SaveAsync(key, stream, "application/json", ct);
        }

        await dbContext.Artifacts.AddAsync(new Artifact
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            Type = ArtifactType.SummaryJson,
            Status = ArtifactStatus.Available,
            StorageKey = key,
            FileName = "summary.json",
            MimeType = "application/json",
            FileSizeBytes = bytes.LongLength,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow
        }, ct);

        run.Status = PipelineRunStatus.Succeeded;
        run.FinishedAtUtc = clock.UtcNow;
        run.UpdatedAtUtc = clock.UtcNow;
        projectStatusService.MarkRunSucceeded(project);
        project.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        await jobQueue.MarkSucceededAsync(job.Id, JsonSerializer.Serialize(new { summary = true }, ReconJson.Defaults), ct);
    }

    private async Task TryMarkRunAndProjectFailedAsync(Job job, string message, CancellationToken ct)
    {
        if (job.PipelineRunId is null)
        {
            return;
        }

        var run = await dbContext.PipelineRuns.FirstOrDefaultAsync(x => x.Id == job.PipelineRunId.Value, ct);
        var project = await dbContext.Projects.FirstOrDefaultAsync(x => x.Id == job.ProjectId, ct);
        if (run is null || project is null)
        {
            return;
        }

        run.Status = PipelineRunStatus.Failed;
        run.FinishedAtUtc = clock.UtcNow;
        run.UpdatedAtUtc = clock.UtcNow;
        projectStatusService.MarkRunFailed(project);
        project.UpdatedAtUtc = clock.UtcNow;

        var logKey = storageKeyFactory.GetLogKey(project.Id, run.Id, job.Id);
        var logBytes = Encoding.UTF8.GetBytes(message);
        await using (var stream = new MemoryStream(logBytes, writable: false))
        {
            await objectStorage.SaveAsync(logKey, stream, "text/plain", ct);
        }

        await dbContext.Artifacts.AddAsync(new Artifact
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            Type = ArtifactType.LogFile,
            Status = ArtifactStatus.Available,
            StorageKey = logKey,
            FileName = $"{job.Id}.log",
            MimeType = "text/plain",
            FileSizeBytes = logBytes.LongLength,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow
        }, ct);
        await dbContext.SaveChangesAsync(ct);
    }

    private static JobType MapStageToJobType(PipelineStage stage) => stage switch
    {
        PipelineStage.Inspect => JobType.InspectProject,
        PipelineStage.Sparse => JobType.RunSparseReconstruction,
        PipelineStage.Dense => JobType.RunDenseReconstruction,
        PipelineStage.Export => JobType.ExportArtifacts,
        PipelineStage.Publish => JobType.PublishArtifacts,
        _ => throw new NotSupportedException($"Stage '{stage}' is not supported.")
    };

    private static JobType? GetNextQueuedStageJobType(PipelineStage currentStage, IReadOnlyCollection<PipelineStage> stages)
    {
        var ordered = stages.OrderBy(StageSort).ToArray();
        var index = Array.IndexOf(ordered, currentStage);
        if (index >= 0 && index < ordered.Length - 1)
        {
            return MapStageToJobType(ordered[index + 1]);
        }

        return null;
    }

    private static int StageSort(PipelineStage stage) => stage switch
    {
        PipelineStage.Inspect => 0,
        PipelineStage.Sparse => 1,
        PipelineStage.Dense => 2,
        PipelineStage.Export => 3,
        PipelineStage.Publish => 4,
        _ => 99
    };
}
