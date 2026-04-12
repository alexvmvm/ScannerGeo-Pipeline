using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recon.Core;
using Recon.Domain;
using Recon.Infrastructure;

namespace Recon.Core.Tests;

public sealed class CoreServicesTests
{
    [Fact]
    public void ProjectStatusService_MarksProjectReadyWhenEnoughValidImagesExist()
    {
        var service = new ProjectStatusService();
        var status = service.DetermineReadyStatus(new Project { Status = ProjectStatus.Draft }, 3, 3);
        status.Should().Be(ProjectStatus.ReadyForProcessing);
    }

    [Fact]
    public async Task RunService_RejectsRunWhenTooFewValidImagesExist()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        fixture.DbContext.ProjectImages.Add(new ProjectImage
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "one.jpg",
            StorageKey = "one",
            SourceType = "upload",
            MimeType = "image/jpeg",
            FileSizeBytes = 10,
            IsValidImage = true,
            ValidationStatus = "Validated",
            CreatedAtUtc = fixture.Clock.UtcNow,
            UpdatedAtUtc = fixture.Clock.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var service = new RunService(fixture.DbContext, fixture.Queue, fixture.Clock, new ProjectStatusService(), Options.Create(new ReconOptions { MinimumValidImageCount = 3 }));
        var action = async () => await service.StartRunAsync(project, new RunStartModel([], false), CancellationToken.None);
        await action.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task ImageIntakeService_CreatesOriginalImageArtifactAndValidationJob()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        var service = new ImageIntakeService(fixture.DbContext, fixture.Storage, new StorageKeyFactory(), fixture.Queue, fixture.Clock);

        await service.UploadAsync(project, [new UploadImageModel("scan.png", "image/png", Encoding.UTF8.GetBytes("png"), "upload", null)], CancellationToken.None);

        (await fixture.DbContext.Artifacts.CountAsync()).Should().Be(1);
        (await fixture.DbContext.Jobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ImportService_UpdateBatchState_SetsPartiallyCompleted()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        var importService = new ImportService(fixture.DbContext, fixture.Queue, fixture.Clock);
        var batch = await importService.CreateBatchAsync(project, [new Uri("https://example.com/1.jpg"), new Uri("https://example.com/2.jpg")], CancellationToken.None);
        var items = await fixture.DbContext.ImportBatchItems.Where(x => x.ImportBatchId == batch.Id).ToListAsync();

        items[0].Status = ImportItemStatus.Succeeded;
        items[1].Status = ImportItemStatus.Failed;
        await fixture.DbContext.SaveChangesAsync();

        await importService.UpdateBatchStateAsync(batch.Id, CancellationToken.None);

        var updated = await fixture.DbContext.ImportBatches.SingleAsync(x => x.Id == batch.Id);
        updated.Status.Should().Be(ImportBatchStatus.PartiallyCompleted);
        updated.SucceededCount.Should().Be(1);
        updated.FailedCount.Should().Be(1);
    }

    [Fact]
    public async Task RunService_RejectsWhenActiveRunAlreadyExists()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddValidImagesAsync(project.Id, 3);
        fixture.DbContext.PipelineRuns.Add(new PipelineRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = PipelineRunStatus.Running,
            PipelineVersion = "test",
            CreatedAtUtc = fixture.Clock.UtcNow,
            UpdatedAtUtc = fixture.Clock.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var service = new RunService(fixture.DbContext, fixture.Queue, fixture.Clock, new ProjectStatusService(), Options.Create(new ReconOptions { MinimumValidImageCount = 3 }));
        var action = async () => await service.StartRunAsync(project, new RunStartModel([PipelineStage.Inspect], false), CancellationToken.None);
        await action.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task RunService_StartRun_UpdatesProjectStatusToProcessing()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddValidImagesAsync(project.Id, 3);

        var service = new RunService(
            fixture.DbContext,
            fixture.Queue,
            fixture.Clock,
            new ProjectStatusService(),
            Options.Create(new ReconOptions { MinimumValidImageCount = 3 }));

        await service.StartRunAsync(project, new RunStartModel([PipelineStage.Inspect], false), CancellationToken.None);

        var refreshedProject = await fixture.DbContext.Projects.SingleAsync(x => x.Id == project.Id);
        refreshedProject.Status.Should().Be(ProjectStatus.Processing);
    }

    [Fact]
    public async Task JobExecutionCoordinator_StartPipelineRun_QueuesFirstStage()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        var run = new PipelineRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = PipelineRunStatus.Queued,
            PipelineVersion = "test",
            RequestedStagesJson = "[\"Inspect\",\"Sparse\"]",
            CreatedAtUtc = fixture.Clock.UtcNow,
            UpdatedAtUtc = fixture.Clock.UtcNow
        };
        fixture.DbContext.PipelineRuns.Add(run);
        fixture.DbContext.Jobs.Add(ImageIntakeService.CreateJob(project.Id, run.Id, JobType.StartPipelineRun, new StartPipelineRunPayload(run.Id, [PipelineStage.Inspect, PipelineStage.Sparse], false), fixture.Clock.UtcNow));
        await fixture.DbContext.SaveChangesAsync();

        var coordinator = fixture.CreateCoordinator();
        await coordinator.ProcessNextAsync(CancellationToken.None);

        var jobs = (await fixture.DbContext.Jobs.ToListAsync()).OrderBy(x => x.CreatedAtUtc).ToList();
        jobs.Should().Contain(x => x.Type == JobType.InspectProject && x.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task JobExecutionCoordinator_ValidationJob_UpdatesImageAndThumbnailArtifact()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        var imageId = Guid.NewGuid();
        var key = $"projects/{project.Id}/images/{imageId}/original/tiny.png";
        await fixture.Storage.SaveAsync(key, new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==")), "image/png", CancellationToken.None);
        fixture.DbContext.ProjectImages.Add(new ProjectImage
        {
            Id = imageId,
            ProjectId = project.Id,
            OriginalFileName = "tiny.png",
            StorageKey = key,
            SourceType = "upload",
            MimeType = "image/png",
            FileSizeBytes = 68,
            ValidationStatus = "Pending",
            CreatedAtUtc = fixture.Clock.UtcNow,
            UpdatedAtUtc = fixture.Clock.UtcNow
        });
        fixture.DbContext.Jobs.Add(ImageIntakeService.CreateJob(project.Id, null, JobType.ValidateUploadedImage, new ValidateUploadedImagePayload(imageId), fixture.Clock.UtcNow));
        await fixture.DbContext.SaveChangesAsync();

        var coordinator = fixture.CreateCoordinator();
        await coordinator.ProcessNextAsync(CancellationToken.None);

        var image = await fixture.DbContext.ProjectImages.SingleAsync(x => x.Id == imageId);
        image.IsValidImage.Should().BeTrue();
        (await fixture.DbContext.Artifacts.CountAsync(x => x.Type == ArtifactType.Thumbnail)).Should().Be(1);
    }

    [Fact]
    public async Task JobExecutionCoordinator_CompletedStage_PersistsNextStageJob()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        project.Status = ProjectStatus.Processing;
        await fixture.AddValidImagesAsync(project.Id, 3);

        var run = new PipelineRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = PipelineRunStatus.Running,
            PipelineVersion = "test",
            CreatedAtUtc = fixture.Clock.UtcNow,
            UpdatedAtUtc = fixture.Clock.UtcNow,
            StartedAtUtc = fixture.Clock.UtcNow
        };

        fixture.DbContext.PipelineRuns.Add(run);
        fixture.DbContext.Jobs.Add(ImageIntakeService.CreateJob(
            project.Id,
            run.Id,
            JobType.InspectProject,
            new PipelineStagePayload(run.Id, [PipelineStage.Inspect, PipelineStage.Sparse]),
            fixture.Clock.UtcNow));
        await fixture.DbContext.SaveChangesAsync();

        var coordinator = fixture.CreateCoordinator();
        await coordinator.ProcessNextAsync(CancellationToken.None);

        var sparseJob = await fixture.DbContext.Jobs.SingleOrDefaultAsync(x =>
            x.PipelineRunId == run.Id &&
            x.Type == JobType.RunSparseReconstruction);

        sparseJob.Should().NotBeNull();
        sparseJob!.Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task JobExecutionCoordinator_GenerateSummary_CompletesRunAndProject()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var project = await fixture.CreateProjectAsync();
        project.Status = ProjectStatus.Processing;
        var run = new PipelineRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = PipelineRunStatus.Running,
            PipelineVersion = "test",
            CreatedAtUtc = fixture.Clock.UtcNow,
            UpdatedAtUtc = fixture.Clock.UtcNow
        };
        fixture.DbContext.PipelineRuns.Add(run);
        fixture.DbContext.Jobs.Add(ImageIntakeService.CreateJob(project.Id, run.Id, JobType.GenerateProjectSummary, new PipelineStagePayload(run.Id, [PipelineStage.Inspect]), fixture.Clock.UtcNow));
        await fixture.DbContext.SaveChangesAsync();

        var coordinator = fixture.CreateCoordinator();
        await coordinator.ProcessNextAsync(CancellationToken.None);

        var refreshedRun = await fixture.DbContext.PipelineRuns.SingleAsync(x => x.Id == run.Id);
        var refreshedProject = await fixture.DbContext.Projects.SingleAsync(x => x.Id == project.Id);
        refreshedRun.Status.Should().Be(PipelineRunStatus.Succeeded);
        refreshedProject.Status.Should().Be(ProjectStatus.Succeeded);
        (await fixture.DbContext.Artifacts.CountAsync(x => x.Type == ArtifactType.SummaryJson)).Should().Be(1);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ReconDbContext DbContext { get; }
        public FakeClock Clock { get; } = new();
        public FileSystemObjectStorage Storage { get; }
        public PostgresJobQueue Queue { get; }
        public string StorageRoot { get; }

        private TestFixture(SqliteConnection connection, ReconDbContext dbContext, FileSystemObjectStorage storage, string storageRoot)
        {
            _connection = connection;
            DbContext = dbContext;
            Storage = storage;
            StorageRoot = storageRoot;
            Queue = new PostgresJobQueue(dbContext, Clock);
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ReconDbContext>().UseSqlite(connection).Options;
            var dbContext = new ReconDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();
            var storageRoot = Path.Combine(Path.GetTempPath(), "recon-core-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storageRoot);
            var storage = new FileSystemObjectStorage(Options.Create(new ReconOptions { StorageRootPath = storageRoot }));
            return new TestFixture(connection, dbContext, storage, storageRoot);
        }

        public async Task<Project> CreateProjectAsync()
        {
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Project",
                Status = ProjectStatus.Draft,
                CreatedAtUtc = Clock.UtcNow,
                UpdatedAtUtc = Clock.UtcNow
            };
            DbContext.Projects.Add(project);
            await DbContext.SaveChangesAsync();
            return project;
        }

        public async Task AddValidImagesAsync(Guid projectId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                DbContext.ProjectImages.Add(new ProjectImage
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    OriginalFileName = $"image-{i}.jpg",
                    StorageKey = $"key-{i}",
                    SourceType = "upload",
                    MimeType = "image/jpeg",
                    FileSizeBytes = 100,
                    Width = 100,
                    Height = 100,
                    Sha256 = Guid.NewGuid().ToString("N"),
                    IsValidImage = true,
                    ValidationStatus = "Validated",
                    CreatedAtUtc = Clock.UtcNow,
                    UpdatedAtUtc = Clock.UtcNow
                });
            }

            await DbContext.SaveChangesAsync();
        }

        public JobExecutionCoordinator CreateCoordinator()
            => new(
                Queue,
                DbContext,
                Clock,
                new ProjectStatusService(),
                new ImageSharpInspector(),
                Storage,
                new StorageKeyFactory(),
                new SimulatedProjectPipelineService(),
                new StubUrlImporter(),
                new UrlImportSecurityValidator(),
                new ImportService(DbContext, Queue, Clock),
                new ProjectService(DbContext, Clock, new ProjectStatusService(), Options.Create(new ReconOptions()), Storage),
                Options.Create(new ReconOptions { ScratchRootPath = StorageRoot }),
                NullLogger<JobExecutionCoordinator>.Instance);

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class StubUrlImporter : IUrlImporter
    {
        public Task<UrlImportResult> DownloadAsync(Uri uri, CancellationToken ct) => throw new NotSupportedException();
    }
}
