using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Recon.Core;
using Recon.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Recon.Infrastructure;

public sealed class ImageSharpInspector : IImageInspector
{
    public async Task<ImageInspectionResult> InspectAsync(byte[] fileBytes, CancellationToken ct)
    {
        using var image = await Image.LoadAsync(new MemoryStream(fileBytes, writable: false), ct);
        await using var thumbnailStream = new MemoryStream();
        image.Clone(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(320, 320)
        })).SaveAsJpeg(thumbnailStream, new JpegEncoder { Quality = 75 });

        var sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        var exif = JsonSerializer.Serialize(new
        {
            width = image.Width,
            height = image.Height,
            format = image.Metadata.DecodedImageFormat?.Name
        }, ReconJson.Defaults);

        return new ImageInspectionResult(
            image.Metadata.DecodedImageFormat?.DefaultMimeType ?? "application/octet-stream",
            fileBytes.LongLength,
            image.Width,
            image.Height,
            sha256,
            exif,
            thumbnailStream.ToArray(),
            "image/jpeg");
    }
}

public sealed class HttpUrlImporter(
    IHttpClientFactory clientFactory,
    IUrlImportSecurityValidator securityValidator,
    IOptions<ReconOptions> options) : IUrlImporter
{
    private readonly ReconOptions _options = options.Value;

    public async Task<UrlImportResult> DownloadAsync(Uri uri, CancellationToken ct)
    {
        var client = clientFactory.CreateClient(nameof(HttpUrlImporter));
        client.Timeout = TimeSpan.FromSeconds(_options.ImportTimeoutSeconds);

        var current = uri;
        for (var redirectCount = 0; redirectCount <= _options.MaxRedirects; redirectCount++)
        {
            await securityValidator.ValidateAsync(current, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(response.StatusCode))
            {
                if (response.Headers.Location is null)
                {
                    throw new SecurityException("Redirect response did not contain a location header.");
                }

                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 0 and var length && length > _options.MaxImportDownloadBytes)
            {
                throw new SecurityException("The download exceeded the maximum allowed size.");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var memory = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    break;
                }

                await memory.WriteAsync(buffer.AsMemory(0, read), ct);
                if (memory.Length > _options.MaxImportDownloadBytes)
                {
                    throw new SecurityException("The download exceeded the maximum allowed size.");
                }
            }

            var fileName = Path.GetFileName(current.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"import-{Guid.NewGuid():N}.bin";
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = GuessContentType(fileName);
            }

            return new UrlImportResult(fileName, contentType!, memory.ToArray());
        }

        throw new SecurityException("The redirect limit was exceeded.");
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
        => (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private static string GuessContentType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
}

public sealed class SimulatedProjectPipelineService : IProjectPipelineService
{
    public Task<PipelineExecutionResult> RunInspectAsync(ProjectPipelineContext context, CancellationToken ct)
        => Task.FromResult(CreateResult(context, PipelineStage.Inspect, []));

    public Task<PipelineExecutionResult> RunSparseAsync(ProjectPipelineContext context, CancellationToken ct)
        => Task.FromResult(CreateResult(context, PipelineStage.Sparse,
        [
            new PipelineArtifact("model.bin", "application/octet-stream", CreateSimulatedSparsePlaceholderBytes(), ArtifactType.SparseModel, "{\"simulated\":true}")
        ]));

    public Task<PipelineExecutionResult> RunDenseAsync(ProjectPipelineContext context, CancellationToken ct)
        => Task.FromResult(CreateResult(context, PipelineStage.Dense,
        [
            new PipelineArtifact("fused.ply", "application/octet-stream", CreateSimulatedDensePlaceholderBytes(context), ArtifactType.DensePointCloud, "{\"simulated\":true,\"format\":\"ply\"}")
        ]));

    public Task<PipelineExecutionResult> RunExportAsync(ProjectPipelineContext context, CancellationToken ct)
        => Task.FromResult(CreateResult(context, PipelineStage.Export,
        [
            new PipelineArtifact("export-package.zip", "application/zip", CreateSimulatedExportPlaceholderBytes(), ArtifactType.ExportPackage, "{\"simulated\":true,\"format\":\"colmap-text-export\"}")
        ]));

    public Task<PipelineExecutionResult> RunPublishAsync(ProjectPipelineContext context, CancellationToken ct)
        => Task.FromResult(CreateResult(context, PipelineStage.Publish,
        [
            new PipelineArtifact("octree-scene-package.zip", "application/zip", CreateSimulatedExportPlaceholderBytes(), ArtifactType.OctreePackage, "{\"simulated\":true,\"format\":\"scannergeo-octree-scene\"}")
        ]));

    private static PipelineExecutionResult CreateResult(ProjectPipelineContext context, PipelineStage stage, IReadOnlyCollection<PipelineArtifact> artifacts)
    {
        var reportJson = JsonSerializer.Serialize(new
        {
            projectId = context.Project.Id,
            runId = context.Run.Id,
            stage,
            simulated = true,
            imageCount = context.Images.Count,
            generatedAtUtc = DateTimeOffset.UtcNow
        }, ReconJson.Defaults);

        return new PipelineExecutionResult(true, reportJson, artifacts);
    }

    private static byte[] CreateSimulatedSparsePlaceholderBytes()
        => Encoding.UTF8.GetBytes("SIMULATED sparse model placeholder. Configure Recon:PipelineProvider=Colmap for real reconstruction output.\n");

    private static byte[] CreateSimulatedDensePlaceholderBytes(ProjectPipelineContext context)
        => Encoding.UTF8.GetBytes(
            "ply\n" +
            "format ascii 1.0\n" +
            "comment SIMULATED placeholder output\n" +
            $"comment project {context.Project.Id}\n" +
            $"comment run {context.Run.Id}\n" +
            "element vertex 0\n" +
            "property float x\n" +
            "property float y\n" +
            "property float z\n" +
            "end_header\n");

    private static byte[] CreateSimulatedExportPlaceholderBytes()
        => Encoding.UTF8.GetBytes("SIMULATED export placeholder. Configure Recon:PipelineProvider=Colmap for real export output.\n");
}

public static class DependencyInjection
{
    public static IServiceCollection AddReconInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReconOptions>(configuration.GetSection("Recon"));

        services.AddDbContext<ReconDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("ReconDb")
                ?? "Host=localhost;Port=5432;Database=recon;Username=postgres;Password=postgres";
            options.UseNpgsql(connectionString);
        });

        services.AddScoped<IReconDbContext>(sp => sp.GetRequiredService<ReconDbContext>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICallerContext, AnonymousCallerContext>();
        services.AddSingleton<IStorageKeyFactory, StorageKeyFactory>();
        services.AddSingleton<IObjectStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ReconOptions>>();
            return string.Equals(options.Value.ObjectStorageProvider, "FileSystem", StringComparison.OrdinalIgnoreCase)
                ? new FileSystemObjectStorage(options)
                : new MinioObjectStorage(options);
        });
        services.AddSingleton<IUrlImportSecurityValidator, UrlImportSecurityValidator>();
        services.AddSingleton<IImageInspector, ImageSharpInspector>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ColmapRuntimeValidator>();
        services.AddScoped<IProjectPipelineService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ReconOptions>>();
            return string.Equals(options.Value.PipelineProvider, "Colmap", StringComparison.OrdinalIgnoreCase)
                ? new ColmapProjectPipelineService(
                    sp.GetRequiredService<IObjectStorage>(),
                    sp.GetRequiredService<IReconDbContext>(),
                    sp.GetRequiredService<IProcessRunner>(),
                    options,
                    sp.GetRequiredService<ILogger<ColmapProjectPipelineService>>())
                : new SimulatedProjectPipelineService();
        });
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddHttpClient(nameof(HttpUrlImporter), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Recon.Api/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddScoped<IUrlImporter, HttpUrlImporter>();

        return services;
    }
}
