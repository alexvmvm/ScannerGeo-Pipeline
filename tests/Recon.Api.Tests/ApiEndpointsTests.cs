using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Recon.Domain;
using Recon.Core;
using Recon.Infrastructure;

namespace Recon.Api.Tests;

public sealed class ApiEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task CreateProject_ReturnsCreatedProject()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "Test Project",
            description = "Initial scan"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var payload = await response.Content.ReadFromJsonAsync<ProjectPayload>(_jsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("Test Project", payload.Name);
        Assert.Equal("Draft", payload.Status);
    }

    [Fact]
    public async Task ListAndGetProject_ReturnProjectData()
    {
        var client = factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Listed Project" });
        var project = await create.Content.ReadFromJsonAsync<ProjectPayload>(_jsonOptions);

        var list = await client.GetFromJsonAsync<PagedPayload<ProjectListItemPayload>>("/api/v1/projects", _jsonOptions);
        var fetched = await client.GetFromJsonAsync<ProjectPayload>($"/api/v1/projects/{project!.Id}", _jsonOptions);

        Assert.Contains(list!.Items, x => x.Id == project.Id);
        Assert.Equal(project.Id, fetched!.Id);
    }

    [Fact]
    public async Task UploadImages_AcceptsMultipartFiles()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Upload Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        multipart.Add(content, "files", "tiny.png");

        var response = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payload = await response.Content.ReadFromJsonAsync<UploadPayload>(_jsonOptions);
        Assert.Single(payload!.Images);
        Assert.Equal("tiny.png", payload.Images.First().OriginalFileName);
    }

    [Fact]
    public async Task ProjectImageContent_ReturnsStoredImageBytes()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Image Content Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        multipart.Add(content, "files", "tiny.png");

        var uploadResponse = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payload = await uploadResponse.Content.ReadFromJsonAsync<UploadPayload>(_jsonOptions);

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/images/{payload!.Images.First().Id}/content");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(TinyPngBytes(), bytes);
    }

    [Fact]
    public async Task UploadImages_RejectsUnsupportedExtension()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Upload Reject Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        multipart.Add(content, "files", "notes.txt");

        var response = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateImportBatch_ReturnsAcceptedBatch()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Import Project");

        var response = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/imports", new
        {
            urls = new[] { "https://example.com/photo1.jpg", "https://example.com/photo2.jpg" }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payload = await response.Content.ReadFromJsonAsync<ImportBatchPayload>(_jsonOptions);
        Assert.Equal(2, payload!.RequestedCount);
        Assert.Equal("Running", payload.Status);
    }

    [Fact]
    public async Task StartRun_ReturnsAcceptedWhenProjectHasValidImages()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Run Project");
        await SeedValidImagesAsync(project.Id, 3);

        var response = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/runs", new
        {
            stages = new[] { "Inspect", "Sparse", "Dense", "Export" },
            forceRebuild = false
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payload = await response.Content.ReadFromJsonAsync<PipelineRunPayload>(_jsonOptions);
        Assert.Equal("Queued", payload!.Status);
    }

    [Fact]
    public async Task StartRun_ReturnsConflictWhenTooFewValidImagesExist()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Too Few Images");

        var response = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/runs", new
        {
            stages = new[] { "Inspect" },
            forceRebuild = false
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task InvalidProjectRequest_ReturnsProblemDetails()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(_jsonOptions);
        Assert.Equal("Validation Failed", problem!.Title);
    }

    [Fact]
    public async Task GetJob_ReturnsQueuedValidationJob()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Job Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        multipart.Add(content, "files", "tiny.png");

        var uploadResponse = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconDbContext>();
        var job = db.Jobs.Single(x => x.ProjectId == project.Id && x.Type == JobType.ValidateUploadedImage);

        var response = await client.GetAsync($"/api/v1/jobs/{job.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JobPayload>(_jsonOptions);
        Assert.Equal("ValidateUploadedImage", payload!.Type);
        Assert.Equal("Queued", payload.Status);
    }

    [Fact]
    public async Task OpsPage_IsServed()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/ops/jobs.html");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Operations", html);
        Assert.Contains("Projects", html);
        Assert.Contains("Photos", html);
    }

    [Fact]
    public async Task ProjectPhotosPage_IsServed()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/ops/project-photos.html");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Project Photos", html);
    }

    [Fact]
    public async Task OctreeViewerPage_IsServed()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/octree-viewer/index.html");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Octree Viewer", html);
    }

    [Fact]
    public async Task OctreeSceneEntryEndpoint_ReturnsManifestFromStoredPackage()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Octree Viewer Project");
        var artifactId = await SeedOctreeArtifactAsync(project.Id);

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/artifacts/{artifactId}/scene/manifest.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"sceneId\":\"test-scene\"", body);
        Assert.Contains("\"rootNodeId\":\"r\"", body);
    }

    [Fact]
    public async Task OpsJobs_ReturnsRichJobDetails()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Ops List Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        multipart.Add(content, "files", "tiny.png");
        var uploadResponse = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var jobs = await client.GetFromJsonAsync<JobDetailsPayload[]>($"/api/v1/ops/jobs?projectId={project.Id}", _jsonOptions);

        Assert.NotNull(jobs);
        var job = Assert.Single(jobs!);
        Assert.Equal("ValidateUploadedImage", job.Type);
        Assert.False(string.IsNullOrWhiteSpace(job.InputJson));
        Assert.Equal(0, job.AttemptCount);
    }

    [Fact]
    public async Task ProcessSelectedJob_ExecutesQueuedJobAndReturnsOutput()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Ops Process Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        multipart.Add(content, "files", "tiny.png");
        var uploadResponse = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var jobs = await client.GetFromJsonAsync<JobDetailsPayload[]>($"/api/v1/ops/jobs?projectId={project.Id}", _jsonOptions);
        var job = Assert.Single(jobs!);

        var processResponse = await client.PostAsync($"/api/v1/ops/jobs/{job.Id}/process", null);
        processResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await processResponse.Content.ReadFromJsonAsync<ProcessSelectedJobPayload>(_jsonOptions);

        Assert.NotNull(payload);
        Assert.True(payload!.Handled);
        Assert.Equal("Succeeded", payload.Job.Status);
        Assert.Contains("imageId", payload.Job.OutputJson ?? string.Empty);
    }

    [Fact]
    public async Task ProcessSelectedJob_ReturnsConflictWhenJobIsNotQueued()
    {
        var client = factory.CreateClient();
        var project = await CreateProjectAsync(client, "Ops Conflict Project");

        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPngBytes());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        multipart.Add(content, "files", "tiny.png");
        var uploadResponse = await client.PostAsync($"/api/v1/projects/{project.Id}/images", multipart);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var jobs = await client.GetFromJsonAsync<JobDetailsPayload[]>($"/api/v1/ops/jobs?projectId={project.Id}", _jsonOptions);
        var job = Assert.Single(jobs!);

        var firstProcessResponse = await client.PostAsync($"/api/v1/ops/jobs/{job.Id}/process", null);
        firstProcessResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secondProcessResponse = await client.PostAsync($"/api/v1/ops/jobs/{job.Id}/process", null);
        secondProcessResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await secondProcessResponse.Content.ReadFromJsonAsync<ValidationProblemPayload>(_jsonOptions);
        Assert.NotNull(problem);
        Assert.Equal("Conflict", problem!.Title);
    }

    private async Task<ProjectPayload> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectPayload>(_jsonOptions))!;
    }

    private async Task SeedValidImagesAsync(Guid projectId, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconDbContext>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            db.ProjectImages.Add(new ProjectImage
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = $"image-{i}.jpg",
                StorageKey = $"projects/{projectId}/images/{Guid.NewGuid()}/original/image-{i}.jpg",
                SourceType = "upload",
                MimeType = "image/jpeg",
                FileSizeBytes = 100,
                Width = 100,
                Height = 100,
                Sha256 = Guid.NewGuid().ToString("N"),
                IsValidImage = true,
                ValidationStatus = "Validated",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedOctreeArtifactAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconDbContext>();
        var objectStorage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var artifactId = Guid.NewGuid();
        var storageKey = $"projects/{projectId}/runs/test/export/octree-scene-package.zip";
        var now = DateTimeOffset.UtcNow;

        await using (var zipStream = new MemoryStream(CreateOctreeScenePackageBytes()))
        {
            await objectStorage.SaveAsync(storageKey, zipStream, "application/zip", CancellationToken.None);
        }

        db.Artifacts.Add(new Artifact
        {
            Id = artifactId,
            ProjectId = projectId,
            Type = ArtifactType.OctreePackage,
            Status = ArtifactStatus.Available,
            StorageKey = storageKey,
            FileName = "octree-scene-package.zip",
            MimeType = "application/zip",
            FileSizeBytes = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        await db.SaveChangesAsync();
        return artifactId;
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==");

    private static byte[] CreateOctreeScenePackageBytes()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("scene/manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.Write("""
{"version":1,"sceneId":"test-scene","rootNodeId":"r","bounds":{"min":[0,0,0],"max":[1,1,1]},"nodes":[{"nodeId":"r","parentNodeId":null,"depth":0,"file":"nodes/r.bin","pointCount":1,"childMask":0,"isLeaf":true,"version":1,"geometricError":1,"bounds":{"min":[0,0,0],"max":[1,1,1]}}]}
""");
            }

            var nodeEntry = archive.CreateEntry("scene/nodes/r.bin");
            using var nodeStream = nodeEntry.Open();
            nodeStream.Write(CreateOctreeNodeBytes());
        }

        return memory.ToArray();
    }

    private static byte[] CreateOctreeNodeBytes()
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(new[] { (byte)'O', (byte)'C', (byte)'T', (byte)'N' });
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((uint)1);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(1f);
        writer.Write(1f);
        writer.Write(new byte[12]);
        writer.Write((ushort)32768);
        writer.Write((ushort)32768);
        writer.Write((ushort)32768);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Flush();
        return memory.ToArray();
    }

    private sealed record ProjectPayload(Guid Id, string Name, string Status);
    private sealed record ProjectListItemPayload(Guid Id, string Name);
    private sealed record PagedPayload<T>(IReadOnlyCollection<T> Items);
    private sealed record UploadPayload(IReadOnlyCollection<ProjectImagePayload> Images);
    private sealed record ProjectImagePayload(Guid Id, string OriginalFileName);
    private sealed record ImportBatchPayload(Guid Id, string Status, int RequestedCount);
    private sealed record PipelineRunPayload(Guid Id, string Status);
    private sealed record JobPayload(Guid Id, string Type, string Status);
    private sealed record JobDetailsPayload(Guid Id, string Type, string Status, int AttemptCount, string InputJson, string? OutputJson);
    private sealed record ProcessSelectedJobPayload(bool Handled, JobDetailsPayload Job);
    private sealed record ValidationProblemPayload(string Title)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; init; } = [];

        public IDictionary<string, JsonElement> Errors
            => Extensions.TryGetValue("errors", out var value)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value.GetRawText())!
                : new Dictionary<string, JsonElement>();
    }
}

internal static class ApiTestAssertions
{
    public static void ShouldBe(this HttpStatusCode actual, HttpStatusCode expected) => Assert.Equal(expected, actual);

    public static void ShouldBe(this string? actual, string expected) => Assert.Equal(expected, actual);
}
