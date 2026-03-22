using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recon.Core;
using Recon.Domain;
using Recon.Infrastructure;

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
}
