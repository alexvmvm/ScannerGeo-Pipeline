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
using System.Text;
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
            var validPlyBytes = CreateValidAsciiPlyBytes();
            await storage.SaveAsync(denseKey, new MemoryStream(validPlyBytes, writable: false), "application/octet-stream", CancellationToken.None);

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
                FileSizeBytes = validPlyBytes.LongLength,
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

    [Fact]
    public async Task ColmapPipeline_DenseStage_RetriesPatchMatchStereoWithReducedImageSize_OnCudaIllegalMemoryAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ReconDbContext(new DbContextOptionsBuilder<ReconDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var root = Path.Combine(Path.GetTempPath(), "recon-dense-retry-tests", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(root, "storage");
        var scratchRoot = Path.Combine(root, "scratch");
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(scratchRoot);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Italy - Powerline",
                Status = ProjectStatus.ReadyForProcessing,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var run = new PipelineRun
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Status = PipelineRunStatus.Running,
                PipelineVersion = "colmap-octree",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            db.Projects.Add(project);
            db.PipelineRuns.Add(run);

            var storage = new FileSystemObjectStorage(Options.Create(new ReconOptions { StorageRootPath = storageRoot }));
            var image = new ProjectImage
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = "frame-01.jpg",
                StorageKey = "projects/test/images/frame-01.jpg",
                SourceType = "upload",
                MimeType = "image/jpeg",
                FileSizeBytes = 3,
                IsValidImage = true,
                ValidationStatus = "Validated",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await storage.SaveAsync(image.StorageKey, new MemoryStream([1, 2, 3]), image.MimeType, CancellationToken.None);

            var sparseKey = "projects/test/runs/test/sparse/sparse-model.zip";
            var sparseZipBytes = CreateSparseModelZip();
            await storage.SaveAsync(sparseKey, new MemoryStream(sparseZipBytes, writable: false), "application/zip", CancellationToken.None);

            db.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                PipelineRunId = run.Id,
                Type = ArtifactType.SparseModel,
                Status = ArtifactStatus.Available,
                StorageKey = sparseKey,
                FileName = "sparse-model.zip",
                MimeType = "application/zip",
                FileSizeBytes = sparseZipBytes.LongLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();

            var runner = new DenseRetryProcessRunner();
            var pipeline = new ColmapProjectPipelineService(
                storage,
                db,
                runner,
                Options.Create(new ReconOptions
                {
                    ScratchRootPath = scratchRoot,
                    ColmapBinaryPath = "colmap",
                    ColmapDenseRetryOnCudaFailure = true,
                    ColmapDenseRetryMaxImageSize = 4096
                }),
                NullLogger<ColmapProjectPipelineService>.Instance);

            var result = await pipeline.RunDenseAsync(
                new ProjectPipelineContext(project, run, [image], Path.Combine(scratchRoot, run.Id.ToString("N"))),
                CancellationToken.None);

            result.Artifacts.Should().HaveCount(2);
            result.Artifacts.Should().Contain(x => x.ArtifactType == ArtifactType.DensePointCloud);
            result.Artifacts.Should().Contain(x => x.ArtifactType == ArtifactType.DenseVisibilityPackage);
            result.ReportJson.Should().Contain("PatchMatchStereo.max_image_size");

            runner.PatchMatchCalls.Should().HaveCount(2);
            runner.PatchMatchCalls[0].Should().NotContain("4096");
            runner.PatchMatchCalls[0][Array.IndexOf(runner.PatchMatchCalls[0], "--PatchMatchStereo.geom_consistency") + 1].Should().Be("1");
            runner.PatchMatchCalls[1].Should().Contain("--PatchMatchStereo.max_image_size");
            runner.PatchMatchCalls[1].Should().Contain("4096");
            runner.StereoFusionArguments.Should().Contain("--StereoFusion.max_image_size");
            runner.StereoFusionArguments.Should().Contain("4096");
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
    public async Task ColmapPipeline_PublishFailsEarly_WhenDensePointCloudHasNoFinitePositions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ReconDbContext(new DbContextOptionsBuilder<ReconDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var root = Path.Combine(Path.GetTempPath(), "recon-publish-validation-tests", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(root, "storage");
        var scratchRoot = Path.Combine(root, "scratch");
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(scratchRoot);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Italy - Powerline",
                Status = ProjectStatus.ReadyForProcessing,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var run = new PipelineRun
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Status = PipelineRunStatus.Running,
                PipelineVersion = "colmap-octree",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            db.Projects.Add(project);
            db.PipelineRuns.Add(run);

            var storage = new FileSystemObjectStorage(Options.Create(new ReconOptions { StorageRootPath = storageRoot }));
            const string denseKey = "projects/test/runs/test/dense/fused.ply";
            var invalidPlyBytes = Encoding.ASCII.GetBytes("""
ply
format ascii 1.0
element vertex 2
property float x
property float y
property float z
end_header
NaN 0 0
1 Infinity 2
""");
            await storage.SaveAsync(denseKey, new MemoryStream(invalidPlyBytes, writable: false), "application/octet-stream", CancellationToken.None);

            db.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                PipelineRunId = run.Id,
                Type = ArtifactType.DensePointCloud,
                Status = ArtifactStatus.Available,
                StorageKey = denseKey,
                FileName = "fused.ply",
                MimeType = "application/octet-stream",
                FileSizeBytes = invalidPlyBytes.LongLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();

            var pipeline = new ColmapProjectPipelineService(
                storage,
                db,
                new RecordingProcessRunner(),
                Options.Create(new ReconOptions
                {
                    ScratchRootPath = scratchRoot,
                    ColmapBinaryPath = "colmap",
                    OctreeCliProjectPath = Path.Combine("external", "ScannerGeo-Octree", "src", "OctreeBuild.Cli", "OctreeBuild.Cli.csproj")
                }),
                NullLogger<ColmapProjectPipelineService>.Instance);

            var action = () => pipeline.RunPublishAsync(
                new ProjectPipelineContext(project, run, [], Path.Combine(scratchRoot, run.Id.ToString("N"))),
                CancellationToken.None);

            await action.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*Dense point cloud artifact is unusable for octree publish.*finite x/y/z vertex positions*");
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

    private static byte[] CreateSparseModelZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("0/cameras.txt");
            archive.CreateEntry("0/images.txt");
            archive.CreateEntry("0/points3D.txt");
        }

        return memory.ToArray();
    }

    private static byte[] CreateValidAsciiPlyBytes()
        => Encoding.ASCII.GetBytes("""
ply
format ascii 1.0
element vertex 1
property float x
property float y
property float z
end_header
1 2 3
""");

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

    private sealed class DenseRetryProcessRunner : IProcessRunner
    {
        public List<string[]> PatchMatchCalls { get; } = [];
        public IReadOnlyCollection<string> StereoFusionArguments { get; private set; } = [];

        public async Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
        {
            var args = arguments.ToArray();
            switch (args[0])
            {
                case "image_undistorter":
                    return new ProcessExecutionResult(fileName, arguments, 0, string.Empty, string.Empty, TimeSpan.Zero);
                case "model_converter":
                {
                    var outputPath = args[Array.IndexOf(args, "--output_path") + 1];
                    Directory.CreateDirectory(outputPath);
                    await File.WriteAllTextAsync(Path.Combine(outputPath, "cameras.txt"), "1 PINHOLE 1000 1000 1000 1000 500 500\n", ct);
                    await File.WriteAllTextAsync(Path.Combine(outputPath, "images.txt"), "1 1 0 0 0 0 0 0 1 frame-01.jpg\n\n", ct);
                    return new ProcessExecutionResult(fileName, arguments, 0, string.Empty, string.Empty, TimeSpan.Zero);
                }
                case "patch_match_stereo":
                {
                    PatchMatchCalls.Add(args);
                    if (PatchMatchCalls.Count == 1)
                    {
                        var stereoRoot = Path.Combine(args[Array.IndexOf(args, "--workspace_path") + 1], "stereo");
                        Directory.CreateDirectory(Path.Combine(stereoRoot, "depth_maps"));
                        Directory.CreateDirectory(Path.Combine(stereoRoot, "normal_maps"));
                        Directory.CreateDirectory(Path.Combine(stereoRoot, "consistency_graphs"));
                        await File.WriteAllTextAsync(Path.Combine(stereoRoot, "depth_maps", "partial.bin"), "partial", ct);
                        return new ProcessExecutionResult(
                            fileName,
                            arguments,
                            1,
                            string.Empty,
                            "CUDA error at /usr/local/src/colmap/src/colmap/mvs/gpu_mat.h:374 - an illegal memory access was encountered\nThis error is likely caused by the graphics card timeout detection mechanism of your operating system.",
                            TimeSpan.Zero);
                    }

                    var depthMapsRoot = Path.Combine(args[Array.IndexOf(args, "--workspace_path") + 1], "stereo", "depth_maps");
                    Directory.CreateDirectory(depthMapsRoot);
                    await using (var file = File.Create(Path.Combine(depthMapsRoot, "frame-01.jpg.geometric.bin")))
                    {
                        var header = Encoding.ASCII.GetBytes("1000&1000&1&");
                        await file.WriteAsync(header, ct);
                        var depthBytes = BitConverter.GetBytes(10f);
                        for (var index = 0; index < 1000 * 1000; index++)
                        {
                            await file.WriteAsync(depthBytes, ct);
                        }
                    }

                    return new ProcessExecutionResult(fileName, arguments, 0, string.Empty, string.Empty, TimeSpan.Zero);
                }
                case "stereo_fusion":
                {
                    StereoFusionArguments = args;
                    var outputPath = args[Array.IndexOf(args, "--output_path") + 1];
                    await File.WriteAllBytesAsync(outputPath, CreateValidAsciiPlyBytes(), ct);
                    return new ProcessExecutionResult(fileName, arguments, 0, string.Empty, string.Empty, TimeSpan.Zero);
                }
                default:
                    throw new InvalidOperationException($"Unexpected command '{args[0]}'.");
            }
        }
    }
}
