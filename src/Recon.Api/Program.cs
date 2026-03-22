using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recon.Api;
using Recon.Api.Swagger;
using Recon.Core;
using Recon.Domain;
using Recon.Infrastructure;
using Serilog;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
AddSharedReconConfig(builder.Configuration, builder.Environment.ContentRootPath, builder.Environment.EnvironmentName);

var uploadRequestBodyLimit = builder.Configuration.GetSection("Recon").GetValue<long?>("MaxUploadRequestBodySizeBytes") ?? 500L * 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = uploadRequestBodyLimit;
});

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.Configure<ReconOptions>(builder.Configuration.GetSection("Recon"));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadRequestBodyLimit;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SchemaFilter<EnumDescriptionSchemaFilter>();
    options.OperationFilter<ReconExamplesOperationFilter>();
});
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddValidatorsFromAssemblyContaining<CreateProjectRequestValidator>();
builder.Services.AddReconInfrastructure(builder.Configuration);
builder.Services.AddScoped<ProjectStatusService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ImageIntakeService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<RunService>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<JobExecutionCoordinator>();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var isPayloadTooLarge = exception is not null && IsPayloadTooLarge(exception);
        var (status, title, extensions) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not Found", (IDictionary<string, object?>?)null),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict", null),
            RequestValidationException validationException => (StatusCodes.Status400BadRequest, "Validation Failed", new Dictionary<string, object?> { ["errors"] = validationException.Errors }),
            _ when isPayloadTooLarge => (StatusCodes.Status413PayloadTooLarge, "Payload Too Large", null),
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error", null)
        };

        context.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception switch
            {
                _ when isPayloadTooLarge => $"The upload request exceeded the configured body limit of {uploadRequestBodyLimit} bytes.",
                null when status == 500 => "An unexpected error occurred.",
                _ when status == 500 => "An unexpected error occurred.",
                _ => exception?.Message ?? "An unexpected error occurred."
            },
            Instance = context.Request.Path
        };

        if (extensions is not null)
        {
            foreach (var pair in extensions)
            {
                details.Extensions[pair.Key] = pair.Value;
            }
        }

        await Results.Problem(detail: details.Detail, statusCode: details.Status, title: details.Title, extensions: details.Extensions).ExecuteAsync(context);
    });
});

app.UseSerilogRequestLogging();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ReconDbContext>();
    await DatabaseStartup.InitializeAsync(db);
}

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Docker"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/ops", () => Results.Redirect("/ops/jobs.html"));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .WithTags("Health")
    .WithSummary("Liveness probe")
    .WithDescription("Returns a lightweight response that indicates the API process is running.");
app.MapGet("/health/ready", async (ReconDbContext db, CancellationToken ct) =>
{
    var ready = await db.Database.CanConnectAsync(ct);
    return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}).WithTags("Health")
  .WithSummary("Readiness probe")
  .WithDescription("Checks whether the API can reach the configured database and is ready to serve requests.");

var api = app.MapGroup("/api/v1");

api.MapPost("/projects", async (
    CreateProjectRequest request,
    IValidator<CreateProjectRequest> validator,
    ProjectService projectService,
    CancellationToken ct) =>
{
    await validator.ValidateAndThrowRequestAsync(request, ct);
    var project = await projectService.CreateProjectAsync(
        new CreateProjectModel(request.Name, request.Description, request.ExternalReference, request.OwnerReference, request.SiteReference, request.SourceType, request.Config),
        ct);

    return Results.Created($"/api/v1/projects/{project.Id}", ToProjectResponse(project, null, 0, 0, 0));
}).WithTags("Projects")
  .WithSummary("Create project")
  .WithDescription("Creates a new reconstruction project and stores project-level metadata without starting any processing.");

api.MapGet("/projects", async (
    ProjectStatus? status,
    DateTimeOffset? createdAfter,
    DateTimeOffset? createdBefore,
    string? search,
    int? page,
    int? pageSize,
    ProjectService projectService,
    CancellationToken ct) =>
{
    var result = await projectService.ListProjectsAsync(
        new ProjectQuery(status, createdAfter, createdBefore, search, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 200)),
        ct);

    return Results.Ok(new PagedResponse<ProjectListItemResponse>(
        result.Items.Select(x => new ProjectListItemResponse(x.Id, x.Name, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray(),
        result.Page,
        result.PageSize,
        result.TotalCount));
}).WithTags("Projects")
  .WithSummary("List projects")
  .WithDescription("Returns paged projects with optional filtering by status, date range, and search text.");

api.MapGet("/projects/{projectId:guid}", async (Guid projectId, ProjectService projectService, CancellationToken ct) =>
{
    var summary = await projectService.GetProjectSummaryAsync(projectId, ct);
    return Results.Ok(ToProjectResponse(summary.Project, summary.LatestRun, summary.TotalImages, summary.ValidImages, summary.ArtifactCount));
}).WithTags("Projects")
  .WithSummary("Get project")
  .WithDescription("Returns project metadata together with the latest run summary, image counts, and artifact count.");

api.MapDelete("/projects/{projectId:guid}", async (Guid projectId, ProjectService projectService, CancellationToken ct) =>
{
    await projectService.DeleteProjectAsync(projectId, ct);
    return Results.NoContent();
}).WithTags("Projects")
  .WithSummary("Delete project")
  .WithDescription("Deletes the project together with its runs, jobs, images, artifacts, import state, and stored object content.");

api.MapPost("/projects/{projectId:guid}/images", async (
    Guid projectId,
    HttpRequest request,
    ProjectService projectService,
    ImageIntakeService intakeService,
    IOptions<ReconOptions> options,
    CancellationToken ct) =>
{
    var project = await projectService.GetProjectAsync(projectId, ct);
    if (!request.HasFormContentType)
    {
        throw new RequestValidationException("Multipart form-data is required.", new Dictionary<string, string[]> { ["files"] = ["Multipart form-data is required."] });
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(ct);
    }
    catch (Exception ex) when (IsPayloadTooLarge(ex))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Payload Too Large",
            detail: $"The upload request exceeded the configured body limit of {uploadRequestBodyLimit} bytes.");
    }

    var files = form.Files;
    if (files.Count == 0)
    {
        throw new RequestValidationException("At least one file is required.", new Dictionary<string, string[]> { ["files"] = ["At least one file is required."] });
    }

    if (files.Count > options.Value.MaxUploadFileCount)
    {
        throw new RequestValidationException("Too many files.", new Dictionary<string, string[]> { ["files"] = [$"A maximum of {options.Value.MaxUploadFileCount} files is allowed."] });
    }

    var uploads = new List<UploadImageModel>(files.Count);
    foreach (var file in files)
    {
        if (file.Length == 0)
        {
            throw new RequestValidationException("Empty files are not allowed.", new Dictionary<string, string[]> { ["files"] = [$"File '{file.FileName}' is empty."] });
        }

        if (file.Length > options.Value.MaxUploadFileSizeBytes)
        {
            throw new RequestValidationException("File too large.", new Dictionary<string, string[]> { ["files"] = [$"File '{file.FileName}' exceeds the size limit."] });
        }

        var extension = Path.GetExtension(file.FileName);
        if (!options.Value.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new RequestValidationException("File type not allowed.", new Dictionary<string, string[]> { ["files"] = [$"File '{file.FileName}' has an unsupported extension."] });
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        uploads.Add(new UploadImageModel(Path.GetFileName(file.FileName), string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, memory.ToArray(), "upload", null));
    }

    var images = await intakeService.UploadAsync(project, uploads, ct);
    return Results.Accepted($"/api/v1/projects/{projectId}/images", new UploadImagesResponse(images.Select(ToImageResponse).ToArray()));
}).WithTags("Images")
  .WithSummary("Upload images")
  .WithDescription("Accepts multipart image uploads, validates basic file constraints, stores the originals, and enqueues background image validation.")
  .DisableAntiforgery();

api.MapGet("/projects/{projectId:guid}/images", async (
    Guid projectId,
    string? validationStatus,
    string? sourceType,
    IReconDbContext dbContext,
    CancellationToken ct) =>
{
    var query = dbContext.ProjectImages.AsNoTracking().Where(x => x.ProjectId == projectId);
    if (!string.IsNullOrWhiteSpace(validationStatus))
    {
        query = query.Where(x => x.ValidationStatus == validationStatus);
    }

    if (!string.IsNullOrWhiteSpace(sourceType))
    {
        query = query.Where(x => x.SourceType == sourceType);
    }

    var items = (await query.ToListAsync(ct)).OrderBy(x => x.CreatedAtUtc).ToArray();
    return Results.Ok(items.Select(ToImageResponse).ToArray());
}).WithTags("Images")
  .WithSummary("List project images")
  .WithDescription("Returns images for a project with optional filtering by validation status and source type.");

api.MapPost("/projects/{projectId:guid}/imports", async (
    Guid projectId,
    CreateImportBatchRequest request,
    IValidator<CreateImportBatchRequest> validator,
    ProjectService projectService,
    ImportService importService,
    CancellationToken ct) =>
{
    await validator.ValidateAndThrowRequestAsync(request, ct);
    var project = await projectService.GetProjectAsync(projectId, ct);
    var batch = await importService.CreateBatchAsync(project, request.Urls.Select(static x => new Uri(x)).ToArray(), ct);
    var details = await importService.GetBatchAsync(projectId, batch.Id, ct);
    return Results.Accepted($"/api/v1/projects/{projectId}/imports/{batch.Id}", ToImportBatchResponse(details.Batch, details.Items));
}).WithTags("Imports")
  .WithSummary("Create import batch")
  .WithDescription("Creates a URL import batch, records batch items, and enqueues background download and validation jobs.");

api.MapGet("/projects/{projectId:guid}/imports/{importBatchId:guid}", async (
    Guid projectId,
    Guid importBatchId,
    ImportService importService,
    CancellationToken ct) =>
{
    var details = await importService.GetBatchAsync(projectId, importBatchId, ct);
    return Results.Ok(ToImportBatchResponse(details.Batch, details.Items));
}).WithTags("Imports")
  .WithSummary("Get import batch")
  .WithDescription("Returns batch-level status plus per-item import results, including failures and linked images.");

api.MapPost("/projects/{projectId:guid}/runs", async (
    Guid projectId,
    CreatePipelineRunRequest request,
    IValidator<CreatePipelineRunRequest> validator,
    ProjectService projectService,
    RunService runService,
    CancellationToken ct) =>
{
    await validator.ValidateAndThrowRequestAsync(request, ct);
    var project = await projectService.GetProjectAsync(projectId, ct);
    var run = await runService.StartRunAsync(project, new RunStartModel(request.Stages ?? [], request.ForceRebuild), ct);
    return Results.Accepted($"/api/v1/projects/{projectId}/runs/{run.Id}", ToRunResponse(run));
}).WithTags("Runs")
  .WithSummary("Start pipeline run")
  .WithDescription("Validates the project has enough valid images, creates a pipeline run, and enqueues the first background job.");

api.MapGet("/projects/{projectId:guid}/runs", async (Guid projectId, RunService runService, CancellationToken ct) =>
{
    var runs = await runService.ListRunsAsync(projectId, ct);
    return Results.Ok(runs.Select(ToRunResponse).ToArray());
}).WithTags("Runs")
  .WithSummary("List project runs")
  .WithDescription("Returns all recorded pipeline runs for a project ordered by most recent first.");

api.MapGet("/projects/{projectId:guid}/runs/{runId:guid}", async (Guid projectId, Guid runId, RunService runService, CancellationToken ct) =>
{
    var details = await runService.GetRunDetailsAsync(projectId, runId, ct);
    return Results.Ok(new PipelineRunDetailsResponse(
        ToRunResponse(details.Run),
        details.Reports.Select(x => new StageReportResponse(x.Stage, x.Success, x.StartedAtUtc, x.FinishedAtUtc, x.DurationSeconds)).ToArray(),
        details.Jobs.Select(ToJobResponse).ToArray(),
        details.Artifacts.Select(ToArtifactResponse).ToArray()));
}).WithTags("Runs")
  .WithSummary("Get project run")
  .WithDescription("Returns run metadata, stage reports, jobs, and artifacts associated with a specific pipeline run.");

api.MapGet("/projects/{projectId:guid}/artifacts", async (
    Guid projectId,
    ArtifactType? type,
    Guid? runId,
    ArtifactStatus? status,
    ArtifactService artifactService,
    CancellationToken ct) =>
{
    var artifacts = await artifactService.ListArtifactsAsync(projectId, new ArtifactQuery(type, runId, status), ct);
    return Results.Ok(artifacts.Select(ToArtifactResponse).ToArray());
}).WithTags("Artifacts")
  .WithSummary("List project artifacts")
  .WithDescription("Returns stored artifacts for a project with optional filtering by artifact type, run, and status.");

api.MapGet("/projects/{projectId:guid}/artifacts/{artifactId:guid}", async (
    Guid projectId,
    Guid artifactId,
    ArtifactService artifactService,
    CancellationToken ct) =>
{
    var result = await artifactService.GetArtifactContentAsync(projectId, artifactId, ct);
    return Results.File(result.Content.Stream, result.Artifact.MimeType, result.Artifact.FileName);
}).WithTags("Artifacts")
  .WithSummary("Download artifact")
  .WithDescription("Streams the requested artifact content from object storage using the recorded artifact metadata.");

api.MapGet("/jobs/{jobId:guid}", async (Guid jobId, JobService jobService, CancellationToken ct) =>
{
    var job = await jobService.GetJobAsync(jobId, ct);
    return Results.Ok(ToJobResponse(job));
}).WithTags("Jobs")
  .WithSummary("Get job")
  .WithDescription("Returns the current status, progress, and timestamps for a background job.");

var ops = api.MapGroup("/ops").WithTags("Ops");

ops.MapGet("/jobs", async (
    JobStatus? status,
    Guid? projectId,
    Guid? runId,
    int? take,
    JobService jobService,
    CancellationToken ct) =>
{
    var jobs = await jobService.ListJobsAsync(new JobQuery(status, projectId, runId, take ?? 25), ct);
    return Results.Ok(jobs.Select(ToJobDetailsResponse).ToArray());
}).WithSummary("List recent jobs")
  .WithDescription("Returns recent jobs with their inputs, outputs, errors, attempts, and timestamps for simple operations/debugging.");

ops.MapGet("/jobs/{jobId:guid}", async (Guid jobId, JobService jobService, CancellationToken ct) =>
{
    var job = await jobService.GetJobAsync(jobId, ct);
    return Results.Ok(ToJobDetailsResponse(job));
}).WithSummary("Get job details")
  .WithDescription("Returns the full stored job record, including serialized input, output, and error payloads.");

ops.MapPost("/jobs/{jobId:guid}/process", async (
    Guid jobId,
    JobExecutionCoordinator coordinator,
    JobService jobService,
    CancellationToken ct) =>
{
    var currentJob = await jobService.GetJobAsync(jobId, ct);
    if (currentJob.Status is not (JobStatus.Queued or JobStatus.RetryScheduled))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: $"Job '{jobId}' is not queued.",
            extensions: new Dictionary<string, object?> { ["job"] = ToJobDetailsResponse(currentJob) });
    }

    try
    {
        await coordinator.ProcessJobAsync(jobId, ct);
    }
    catch (ConflictException ex)
    {
        currentJob = await jobService.GetJobAsync(jobId, ct);
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: ex.Message,
            extensions: new Dictionary<string, object?> { ["job"] = ToJobDetailsResponse(currentJob) });
    }

    var job = await jobService.GetJobAsync(jobId, ct);
    return Results.Ok(new ProcessSelectedJobResponse(true, ToJobDetailsResponse(job)));
}).WithSummary("Process selected queued job")
  .WithDescription("Executes one specific queued job immediately inside the API process. Returns the updated stored job record.");

ops.MapPost("/jobs/process-next", async (
    IReconDbContext dbContext,
    JobExecutionCoordinator coordinator,
    JobService jobService,
    CancellationToken ct) =>
{
    var nextCandidateId = (await dbContext.Jobs.AsNoTracking()
        .Where(x => x.Status == JobStatus.Queued || x.Status == JobStatus.RetryScheduled)
        .ToListAsync(ct))
        .OrderByDescending(x => x.Priority)
        .ThenBy(x => x.CreatedAtUtc)
        .Select(x => (Guid?)x.Id)
        .FirstOrDefault();

    var handled = await coordinator.ProcessNextAsync(ct);
    if (!handled)
    {
        return Results.Ok(new ProcessNextJobResponse(false, null, null));
    }

    JobDetailsResponse? job = null;
    if (nextCandidateId is { } jobId)
    {
        job = ToJobDetailsResponse(await jobService.GetJobAsync(jobId, ct));
    }

    return Results.Ok(new ProcessNextJobResponse(true, nextCandidateId, job));
}).WithSummary("Process next queued job")
  .WithDescription("Claims and executes the next queued background job immediately inside the API process. Intended for local operations and debugging.");

app.Run();

static ProjectResponse ToProjectResponse(Project project, PipelineRun? latestRun, int totalImages, int validImages, int artifactCount)
    => new(
        project.Id,
        project.Name,
        project.Description,
        project.Status,
        project.ExternalReference,
        project.OwnerReference,
        project.SiteReference,
        project.SourceType,
        project.CreatedAtUtc,
        project.UpdatedAtUtc,
        latestRun is null ? null : new ProjectRunSummaryResponse(latestRun.Id, latestRun.Status, latestRun.CreatedAtUtc, latestRun.FinishedAtUtc),
        totalImages,
        validImages,
        artifactCount);

static ProjectImageResponse ToImageResponse(ProjectImage image)
    => new(image.Id, image.OriginalFileName, image.SourceType, image.SourceUrl, image.MimeType, image.FileSizeBytes, image.ValidationStatus, image.IsValidImage, image.Width, image.Height);

static ImportBatchResponse ToImportBatchResponse(ImportBatch batch, IReadOnlyCollection<ImportBatchItem> items)
    => new(batch.Id, batch.Status, batch.RequestedCount, batch.SucceededCount, batch.FailedCount, items.Select(x => new ImportBatchItemResponse(x.Id, x.SourceUrl, x.Status, x.ErrorMessage, x.ProjectImageId)).ToArray());

static PipelineRunResponse ToRunResponse(PipelineRun run)
    => new(run.Id, run.Status, run.PipelineVersion, run.CreatedAtUtc, run.StartedAtUtc, run.FinishedAtUtc);

static ArtifactResponse ToArtifactResponse(Artifact artifact)
    => new(artifact.Id, artifact.Type, artifact.Status, artifact.FileName, artifact.MimeType, artifact.FileSizeBytes, artifact.PipelineRunId, artifact.CreatedAtUtc);

static JobResponse ToJobResponse(Job job)
    => new(job.Id, job.Type, job.Status, job.ProjectId, job.PipelineRunId, job.ProgressPercent, job.ProgressMessage, job.CreatedAtUtc, job.StartedAtUtc, job.FinishedAtUtc);

static JobDetailsResponse ToJobDetailsResponse(Job job)
    => new(
        job.Id,
        job.Type,
        job.Status,
        job.ProjectId,
        job.PipelineRunId,
        job.AttemptCount,
        job.MaxAttempts,
        job.ProgressPercent,
        job.ProgressMessage,
        job.InputJson,
        job.OutputJson,
        job.ErrorJson,
        job.CreatedAtUtc,
        job.UpdatedAtUtc,
        job.StartedAtUtc,
        job.FinishedAtUtc);

static bool IsPayloadTooLarge(Exception exception)
    => exception switch
    {
        BadHttpRequestException badHttpRequestException when badHttpRequestException.StatusCode == StatusCodes.Status413PayloadTooLarge => true,
        InvalidDataException invalidDataException when invalidDataException.Message.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase) => true,
        _ when exception.InnerException is not null => IsPayloadTooLarge(exception.InnerException),
        _ => false
    };

static void AddSharedReconConfig(ConfigurationManager configuration, string contentRootPath, string environmentName)
{
    var solutionRoot = Path.GetFullPath(Path.Combine(contentRootPath, "..", ".."));
    configuration.AddJsonFile("reconsettings.json", optional: true, reloadOnChange: true);
    configuration.AddJsonFile($"reconsettings.{environmentName}.json", optional: true, reloadOnChange: true);
    configuration.AddJsonFile(Path.Combine(solutionRoot, "reconsettings.json"), optional: true, reloadOnChange: true);
    configuration.AddJsonFile(Path.Combine(solutionRoot, $"reconsettings.{environmentName}.json"), optional: true, reloadOnChange: true);
    configuration.AddEnvironmentVariables();
}

public partial class Program;
