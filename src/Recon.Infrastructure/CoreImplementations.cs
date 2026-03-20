using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using System.Net;
using System.Security;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recon.Core;
using Recon.Domain;

namespace Recon.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class AnonymousCallerContext : ICallerContext
{
    public string CallerId => "anonymous";
}

public sealed class StorageKeyFactory : IStorageKeyFactory
{
    public string GetOriginalImageKey(Guid projectId, Guid imageId, string fileName)
        => $"projects/{projectId}/images/{imageId}/original/{Sanitize(fileName)}";

    public string GetThumbnailKey(Guid projectId, Guid imageId)
        => $"projects/{projectId}/images/{imageId}/thumbnail/thumb.jpg";

    public string GetReportKey(Guid projectId, Guid runId, PipelineStage stage)
        => $"projects/{projectId}/runs/{runId}/reports/{stage.ToString().ToLowerInvariant()}.json";

    public string GetDenseOutputKey(Guid projectId, Guid runId, string fileName)
        => $"projects/{projectId}/runs/{runId}/dense/{Sanitize(fileName)}";

    public string GetSparseOutputKey(Guid projectId, Guid runId, string fileName)
        => $"projects/{projectId}/runs/{runId}/sparse/{Sanitize(fileName)}";

    public string GetLogKey(Guid projectId, Guid runId, Guid jobId)
        => $"projects/{projectId}/runs/{runId}/logs/{jobId}.log";

    public string GetSummaryKey(Guid projectId)
        => $"projects/{projectId}/current/summary.json";

    private static string Sanitize(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Replace(' ', '_');
    }
}

public sealed class FileSystemObjectStorage(IOptions<ReconOptions> options) : IObjectStorage
{
    private readonly string _root = options.Value.StorageRootPath;

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        var path = GetPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public async Task<StoredObject?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = File.OpenRead(path);
        var contentType = GuessContentType(path);
        var length = stream.Length;
        await Task.CompletedTask;
        return new StoredObject(contentType, length, stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
        => Task.FromResult(File.Exists(GetPath(key)));

    private string GetPath(string key)
        => Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));

    private static string GuessContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".ply" => "application/octet-stream",
            ".txt" or ".log" => "text/plain",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
}

public sealed class MinioObjectStorage(IOptions<ReconOptions> options) : IObjectStorage
{
    private readonly ReconOptions _options = options.Value;
    private readonly IAmazonS3 _client = CreateClient(options.Value);
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private volatile bool _bucketReady;

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);

        Stream uploadStream = content;
        if (!content.CanSeek)
        {
            var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            uploadStream = buffered;
        }
        else if (content.Position != 0)
        {
            content.Position = 0;
        }

        var request = new PutObjectRequest
        {
            BucketName = _options.ObjectStorageBucket,
            Key = key,
            InputStream = uploadStream,
            ContentType = contentType,
            AutoCloseStream = false
        };

        await _client.PutObjectAsync(request, ct);

        if (!ReferenceEquals(uploadStream, content))
        {
            await uploadStream.DisposeAsync();
        }
    }

    public async Task<StoredObject?> OpenReadAsync(string key, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);

        try
        {
            using var response = await _client.GetObjectAsync(_options.ObjectStorageBucket, key, ct);
            await using var source = response.ResponseStream;
            var memory = new MemoryStream();
            await source.CopyToAsync(memory, ct);
            memory.Position = 0;
            return new StoredObject(response.Headers.ContentType ?? "application/octet-stream", memory.Length, memory);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);

        try
        {
            await _client.GetObjectMetadataAsync(_options.ObjectStorageBucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return false;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bucketLock.WaitAsync(ct);
        try
        {
            if (_bucketReady)
            {
                return;
            }

            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_client, _options.ObjectStorageBucket);
            if (!exists)
            {
                await _client.PutBucketAsync(new PutBucketRequest
                {
                    BucketName = _options.ObjectStorageBucket
                }, ct);
            }

            _bucketReady = true;
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    private static IAmazonS3 CreateClient(ReconOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.ObjectStorageEndpoint,
            ForcePathStyle = true,
            UseHttp = !options.ObjectStorageUseSsl
        };

        var credentials = new BasicAWSCredentials(
            options.ObjectStorageAccessKey ?? "minioadmin",
            options.ObjectStorageSecretKey ?? "minioadmin");

        return new AmazonS3Client(credentials, config);
    }
}

public sealed class PostgresJobQueue(ReconDbContext dbContext, IClock clock) : IJobQueue
{
    public async Task EnqueueAsync(Job job, CancellationToken ct)
    {
        await dbContext.Jobs.AddAsync(job, ct);
    }

    public async Task<Job?> DequeueNextAsync(CancellationToken ct)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;
        var candidates = await dbContext.Jobs
            .Where(x => x.Status == JobStatus.Queued || x.Status == JobStatus.RetryScheduled)
            .OrderByDescending(x => x.Priority)
            .Take(50)
            .ToListAsync(ct);

        var job = candidates
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.CreatedAtUtc)
            .FirstOrDefault();

        if (job is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        job.Status = JobStatus.Running;
        job.AttemptCount += 1;
        job.StartedAtUtc ??= now;
        job.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return job;
    }

    public async Task MarkRunningAsync(Guid jobId, CancellationToken ct)
    {
        var job = await dbContext.Jobs.FirstAsync(x => x.Id == jobId, ct);
        job.Status = JobStatus.Running;
        job.StartedAtUtc ??= clock.UtcNow;
        job.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task MarkSucceededAsync(Guid jobId, string? outputJson, CancellationToken ct)
    {
        var job = await dbContext.Jobs.FirstAsync(x => x.Id == jobId, ct);
        job.Status = JobStatus.Succeeded;
        job.OutputJson = outputJson;
        job.FinishedAtUtc = clock.UtcNow;
        job.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid jobId, string errorJson, bool shouldRetry, CancellationToken ct)
    {
        var job = await dbContext.Jobs.FirstAsync(x => x.Id == jobId, ct);
        job.Status = shouldRetry && job.AttemptCount < job.MaxAttempts ? JobStatus.RetryScheduled : JobStatus.Failed;
        job.ErrorJson = errorJson;
        job.FinishedAtUtc = job.Status == JobStatus.Failed ? clock.UtcNow : null;
        job.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task ReportProgressAsync(Guid jobId, decimal percent, string? message, CancellationToken ct)
    {
        var job = await dbContext.Jobs.FirstAsync(x => x.Id == jobId, ct);
        job.ProgressPercent = percent;
        job.ProgressMessage = message;
        job.UpdatedAtUtc = clock.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }
}

public sealed class UrlImportSecurityValidator : IUrlImportSecurityValidator
{
    public async Task ValidateAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new SecurityException("Only http and https URLs are allowed.");
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Localhost URLs are blocked.");
        }

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IsBlocked(ip))
            {
                throw new SecurityException("The target address is blocked.");
            }

            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Could not resolve host '{uri.Host}': {ex.Message}");
        }

        if (addresses.Length == 0 || addresses.Any(IsBlocked))
        {
            throw new SecurityException("The target address resolves to a blocked range.");
        }
    }

    public bool IsBlocked(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false
            };
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || address.ToString().StartsWith("fc", StringComparison.OrdinalIgnoreCase)
                || address.ToString().StartsWith("fd", StringComparison.OrdinalIgnoreCase)
                || address.Equals(IPAddress.IPv6Loopback);
        }

        return false;
    }
}
