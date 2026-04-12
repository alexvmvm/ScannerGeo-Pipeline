using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recon.Core;
using Recon.Domain;
using Recon.Infrastructure;
using System.IO.Compression;
using System.Text.Json;

namespace Recon.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task Queue_ClaimsHighestPriorityQueuedJob()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ReconDbContext(new DbContextOptionsBuilder<ReconDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Type = JobType.ValidateUploadedImage,
            Status = JobStatus.Queued,
            Priority = 10,
            InputJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Type = JobType.StartPipelineRun,
            Status = JobStatus.Queued,
            Priority = 100,
            InputJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var queue = new PostgresJobQueue(db, new StaticClock());
        var claimed = await queue.DequeueNextAsync(CancellationToken.None);

        claimed!.Priority.Should().Be(100);
        claimed.Status.Should().Be(JobStatus.Running);
    }

    [Fact]
    public void StorageKeyFactory_GeneratesDeterministicPaths()
    {
        var factory = new StorageKeyFactory();
        var projectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var imageId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var key = factory.GetOriginalImageKey(projectId, imageId, "scan 01.jpg");
        key.Should().Be("projects/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/images/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/original/scan_01.jpg");
        factory.GetExportOutputKey(projectId, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "scene package.zip")
            .Should().Be("projects/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/runs/cccccccc-cccc-cccc-cccc-cccccccccccc/export/scene_package.zip");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.10.1")]
    public void UrlValidator_BlocksPrivateAndLoopbackRanges(string ipAddress)
    {
        var validator = new UrlImportSecurityValidator();
        validator.IsBlocked(System.Net.IPAddress.Parse(ipAddress)).Should().BeTrue();
    }

    [Fact]
    public async Task ImageInspector_ExtractsImageMetadata()
    {
        var inspector = new ImageSharpInspector();
        var result = await inspector.InspectAsync(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg=="), CancellationToken.None);

        result.Width.Should().Be(1);
        result.Height.Should().Be(1);
        result.MimeType.Should().Be("image/png");
        result.ThumbnailBytes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FileSystemObjectStorage_RoundTripsSavedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "recon-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new FileSystemObjectStorage(Options.Create(new ReconOptions { StorageRootPath = root }));
            await storage.SaveAsync("projects/a/file.txt", new MemoryStream("hello"u8.ToArray()), "text/plain", CancellationToken.None);

            var stored = await storage.OpenReadAsync("projects/a/file.txt", CancellationToken.None);
            stored.Should().NotBeNull();
            using var reader = new StreamReader(stored!.Stream);
            (await reader.ReadToEndAsync()).Should().Be("hello");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DependencyInjection_UsesFileSystemStorage_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReconDb"] = "Data Source=:memory:",
            ["Recon:ObjectStorageProvider"] = "FileSystem",
            ["Recon:StorageRootPath"] = Path.Combine(Path.GetTempPath(), "di-storage")
        }).Build();

        var services = new ServiceCollection();
        services.AddReconInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IObjectStorage>().Should().BeOfType<FileSystemObjectStorage>();
    }

    [Fact]
    public void DependencyInjection_UsesMinioStorage_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReconDb"] = "Host=localhost;Port=5432;Database=recon;Username=postgres;Password=postgres",
            ["Recon:ObjectStorageProvider"] = "Minio",
            ["Recon:ObjectStorageEndpoint"] = "http://localhost:9000",
            ["Recon:ObjectStorageBucket"] = "recon",
            ["Recon:ObjectStorageAccessKey"] = "minioadmin",
            ["Recon:ObjectStorageSecretKey"] = "minioadmin"
        }).Build();

        var services = new ServiceCollection();
        services.AddReconInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IObjectStorage>().Should().BeOfType<MinioObjectStorage>();
    }

    [Fact]
    public void DependencyInjection_UsesColmapPipeline_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReconDb"] = "Host=localhost;Port=5432;Database=recon;Username=postgres;Password=postgres",
            ["Recon:ObjectStorageProvider"] = "FileSystem",
            ["Recon:StorageRootPath"] = Path.Combine(Path.GetTempPath(), "pipeline-storage"),
            ["Recon:PipelineProvider"] = "Colmap",
            ["Recon:ColmapBinaryPath"] = "colmap"
        }).Build();

        var services = new ServiceCollection();
        services.AddReconInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IProjectPipelineService>().Should().BeOfType<ColmapProjectPipelineService>();
    }

    [Fact]
    public void DependencyInjection_UsesSimulatedPipeline_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReconDb"] = "Host=localhost;Port=5432;Database=recon;Username=postgres;Password=postgres",
            ["Recon:ObjectStorageProvider"] = "FileSystem",
            ["Recon:StorageRootPath"] = Path.Combine(Path.GetTempPath(), "pipeline-storage"),
            ["Recon:PipelineProvider"] = "Simulated"
        }).Build();

        var services = new ServiceCollection();
        services.AddReconInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IProjectPipelineService>().Should().BeOfType<SimulatedProjectPipelineService>();
    }

    [Fact]
    public async Task ColmapRuntimeValidator_SkipsCheck_WhenPipelineIsSimulated()
    {
        var runner = new RecordingProcessRunner();
        var validator = new ColmapRuntimeValidator(
            Options.Create(new ReconOptions
            {
                PipelineProvider = "Simulated",
                ColmapBinaryPath = "colmap",
                ScratchRootPath = Path.GetTempPath()
            }),
            runner);

        await validator.ValidateAsync(CancellationToken.None);

        runner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ColmapRuntimeValidator_ThrowsHelpfulError_WhenColmapCannotStart()
    {
        var validator = new ColmapRuntimeValidator(
            Options.Create(new ReconOptions
            {
                PipelineProvider = "Colmap",
                ColmapBinaryPath = "missing-colmap",
                ScratchRootPath = Path.GetTempPath()
            }),
            new ThrowingProcessRunner(new FileNotFoundException("not found")));

        var action = () => validator.ValidateAsync(CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*COLMAP mode is enabled*missing-colmap*Simulated*");
    }

    [Fact]
    public async Task ColmapRuntimeValidator_RunsHelpCheck_WhenPipelineIsColmap()
    {
        var runner = new RecordingProcessRunner();
        var validator = new ColmapRuntimeValidator(
            Options.Create(new ReconOptions
            {
                PipelineProvider = "Colmap",
                ColmapBinaryPath = "colmap",
                ScratchRootPath = Path.GetTempPath()
            }),
            runner);

        await validator.ValidateAsync(CancellationToken.None);

        runner.CallCount.Should().Be(1);
        runner.FileName.Should().Be("colmap");
        runner.Arguments.Should().Equal("--help");
    }

    [Fact]
    public async Task ColmapPipeline_PublishBuildsOctreeScenePackage_FromLatestDenseArtifact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ReconDbContext(new DbContextOptionsBuilder<ReconDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var root = Path.Combine(Path.GetTempPath(), "recon-octree-export-tests", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(root, "storage");
        var scratchRoot = Path.Combine(root, "scratch");
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(scratchRoot);

        try
        {
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Octree export",
                Status = ProjectStatus.ReadyForProcessing,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            var previousRun = new PipelineRun
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Status = PipelineRunStatus.Succeeded,
                PipelineVersion = "colmap-octree",
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                FinishedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-9)
            };
            var run = new PipelineRun
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Status = PipelineRunStatus.Running,
                PipelineVersion = "colmap-octree",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            db.Projects.Add(project);
            db.PipelineRuns.Add(previousRun);
            db.PipelineRuns.Add(run);

            var storage = new FileSystemObjectStorage(Options.Create(new ReconOptions { StorageRootPath = storageRoot }));
            const string denseKey = "projects/test/runs/test/dense/fused.ply";
            await storage.SaveAsync(denseKey, new MemoryStream("ply\nend_header\n"u8.ToArray()), "application/octet-stream", CancellationToken.None);

            db.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                PipelineRunId = previousRun.Id,
                Type = ArtifactType.DensePointCloud,
                Status = ArtifactStatus.Available,
                StorageKey = denseKey,
                FileName = "fused.ply",
                MimeType = "application/octet-stream",
                FileSizeBytes = 15,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var runner = new OctreeExportProcessRunner();
            var pipeline = new ColmapProjectPipelineService(
                storage,
                db,
                runner,
                Options.Create(new ReconOptions
                {
                    ScratchRootPath = scratchRoot,
                    ColmapBinaryPath = "colmap",
                    OctreeCliProjectPath = Path.Combine("external", "ScannerGeo-Octree", "src", "OctreeBuild.Cli", "OctreeBuild.Cli.csproj")
                }),
                NullLogger<ColmapProjectPipelineService>.Instance);

            var result = await pipeline.RunPublishAsync(new ProjectPipelineContext(project, run, [], Path.Combine(scratchRoot, run.Id.ToString("N"))), CancellationToken.None);

            result.Artifacts.Should().ContainSingle();
            var artifact = result.Artifacts.Single();
            artifact.ArtifactType.Should().Be(ArtifactType.OctreePackage);
            artifact.FileName.Should().Be("octree-scene-package.zip");

            using var archive = new ZipArchive(new MemoryStream(artifact.Bytes), ZipArchiveMode.Read);
            archive.Entries.Select(x => x.FullName).Should().Contain("scene/manifest.json");
            archive.Entries.Select(x => x.FullName).Should().Contain("scene/nodes/r.bin");

            runner.FileName.Should().Be("dotnet");
            runner.Arguments.Should().Contain("run");
            runner.Arguments.Should().Contain("--project");
            runner.Arguments.Should().Contain(Path.GetFullPath(Path.Combine("external", "ScannerGeo-Octree", "src", "OctreeBuild.Cli", "OctreeBuild.Cli.csproj")));
            result.ReportJson.Should().Contain(previousRun.Id.ToString());
            result.ReportJson.Should().Contain("run-");
            artifact.MetadataJson.Should().Contain("scannergeo-octree-scene");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StaticClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyCollection<string> Arguments { get; private set; } = [];

        public Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
        {
            CallCount++;
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(new ProcessExecutionResult(fileName, arguments, 0, string.Empty, string.Empty, TimeSpan.Zero));
        }
    }

    private sealed class ThrowingProcessRunner(Exception exception) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
            => throw exception;
    }

    private sealed class OctreeExportProcessRunner : IProcessRunner
    {
        public string? FileName { get; private set; }
        public IReadOnlyCollection<string> Arguments { get; private set; } = [];

        public async Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
        {
            FileName = fileName;
            Arguments = arguments.ToArray();

            var args = arguments.ToArray();
            var outputPath = args[Array.IndexOf(args, "--output") + 1];
            var resultJsonPath = args[Array.IndexOf(args, "--result-json") + 1];
            var sceneId = args[Array.IndexOf(args, "--scene-id") + 1];
            var nodesPath = Path.Combine(outputPath, "nodes");
            var reportsPath = Path.Combine(outputPath, "reports");

            Directory.CreateDirectory(nodesPath);
            Directory.CreateDirectory(reportsPath);
            await File.WriteAllTextAsync(Path.Combine(outputPath, "manifest.json"), "{\"sceneId\":\"test-scene\",\"rootNodeId\":\"r\",\"nodes\":[]}", ct);
            await File.WriteAllBytesAsync(Path.Combine(nodesPath, "r.bin"), [1, 2, 3], ct);
            await File.WriteAllTextAsync(Path.Combine(outputPath, "scene.db"), string.Empty, ct);
            await File.WriteAllTextAsync(Path.Combine(reportsPath, "build-report.json"), "{\"nodeCount\":1}", ct);

            var response = JsonSerializer.Serialize(new
            {
                status = "success",
                exitCode = 0,
                report = new
                {
                    sceneId
                },
                artifacts = new
                {
                    outputDirectoryPath = outputPath,
                    nodesDirectoryPath = nodesPath,
                    manifestPath = Path.Combine(outputPath, "manifest.json"),
                    sqlitePath = Path.Combine(outputPath, "scene.db"),
                    buildReportPath = Path.Combine(reportsPath, "build-report.json")
                }
            });
            await File.WriteAllTextAsync(resultJsonPath, response, ct);

            return new ProcessExecutionResult(fileName, arguments, 0, string.Empty, string.Empty, TimeSpan.Zero);
        }
    }
}
