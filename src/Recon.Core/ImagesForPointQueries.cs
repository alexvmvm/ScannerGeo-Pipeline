using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Recon.Domain;

namespace Recon.Core;

public sealed record ImagesForPointQuery(double X, double Y, double Z, int MaxResults, Guid? RunId, bool IncludeImageUrls);
public sealed record ImagesForPointQueryResult(Guid ProjectId, Guid RunId, double X, double Y, double Z, int MatchCount, IReadOnlyCollection<ImagePointMatch> Matches);
public sealed record ImagePointMatch(Guid ImageId, string FileName, int Width, int Height, double U, double V, double Score, double CameraDistance, double DistanceToImageCenterPixels, string? ImageUrl, string? ThumbnailUrl);
public sealed record ProjectionImageRecord(Guid ImageId, string FileName, int Width, int Height, string CameraModel, double Fx, double Fy, double Cx, double Cy, double K1, double K2, double[] RotationMatrix3x3, double[] Translation3, double[] CameraCenter3, string? ImageUrl, string? ThumbnailUrl);
public readonly record struct ProjectedPoint(double U, double V, double CameraDistance, double DistanceToImageCenterPixels);

public sealed class PointProjectionService
{
    public bool TryProject(ProjectionImageRecord image, double x, double y, double z, out ProjectedPoint result)
    {
        // COLMAP stores world-to-camera poses in images.txt: P_camera = R * P_world + t.
        var cameraX = (image.RotationMatrix3x3[0] * x) + (image.RotationMatrix3x3[1] * y) + (image.RotationMatrix3x3[2] * z) + image.Translation3[0];
        var cameraY = (image.RotationMatrix3x3[3] * x) + (image.RotationMatrix3x3[4] * y) + (image.RotationMatrix3x3[5] * z) + image.Translation3[1];
        var cameraZ = (image.RotationMatrix3x3[6] * x) + (image.RotationMatrix3x3[7] * y) + (image.RotationMatrix3x3[8] * z) + image.Translation3[2];
        if (cameraZ <= 0d)
        {
            result = default;
            return false;
        }

        var normalizedX = cameraX / cameraZ;
        var normalizedY = cameraY / cameraZ;
        var r2 = (normalizedX * normalizedX) + (normalizedY * normalizedY);
        var (distortedX, distortedY) = image.CameraModel switch
        {
            "SIMPLE_PINHOLE" or "PINHOLE" => (normalizedX, normalizedY),
            "SIMPLE_RADIAL" => ApplyRadialDistortion(normalizedX, normalizedY, 1d + (image.K1 * r2)),
            "RADIAL" => ApplyRadialDistortion(normalizedX, normalizedY, 1d + (image.K1 * r2) + (image.K2 * r2 * r2)),
            _ => (double.NaN, double.NaN)
        };
        if (!double.IsFinite(distortedX) || !double.IsFinite(distortedY))
        {
            result = default;
            return false;
        }

        var u = (image.Fx * distortedX) + image.Cx;
        var v = (image.Fy * distortedY) + image.Cy;
        if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0d || v < 0d || u >= image.Width || v >= image.Height)
        {
            result = default;
            return false;
        }

        var dx = x - image.CameraCenter3[0];
        var dy = y - image.CameraCenter3[1];
        var dz = z - image.CameraCenter3[2];
        result = new ProjectedPoint(
            u,
            v,
            Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)),
            Math.Sqrt(Math.Pow(u - (image.Width / 2d), 2d) + Math.Pow(v - (image.Height / 2d), 2d)));
        return true;
    }

    private static (double X, double Y) ApplyRadialDistortion(double x, double y, double factor) => (x * factor, y * factor);
}

public sealed class ImagesForPointQueryService(
    IReconDbContext dbContext,
    IObjectStorage objectStorage,
    PointProjectionService pointProjectionService,
    ILogger<ImagesForPointQueryService> logger)
{
    private const int VisibilityCandidateMultiplier = 4;
    private const int MinimumVisibilityCandidateLimit = 32;
    private const int VisibilityNeighborhoodRadiusPixels = 1;
    private const double DepthAbsoluteTolerance = 0.05d;
    private const double DepthRelativeTolerance = 0.01d;

    public async Task<ImagesForPointQueryResult> QueryAsync(Guid projectId, ImagesForPointQuery request, CancellationToken ct)
    {
        var projectExists = await dbContext.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct);
        if (!projectExists)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        var (run, images) = await ResolveRunAndImagesAsync(projectId, request, ct);
        var denseVisibility = await TryLoadDenseVisibilityPackageAsync(projectId, run.Id, ct);
        logger.LogInformation(
            "Images-for-point query project={ProjectId} run={RunId} point=({X}, {Y}, {Z}) candidates={CandidateCount} denseVisibility={DenseVisibility}",
            projectId,
            run.Id,
            request.X,
            request.Y,
            request.Z,
            images.Count,
            denseVisibility is not null);

        var geometricCandidates = images
            .Select(image => TryProject(image, request))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(x => x.Match.Score)
            .ThenBy(x => x.Match.DistanceToImageCenterPixels)
            .ThenBy(x => x.Match.CameraDistance)
            .Take(GetVisibilityCandidateLimit(request.MaxResults))
            .ToArray();

        IReadOnlyCollection<ImagePointMatch> matches;
        if (denseVisibility is null)
        {
            matches = geometricCandidates
                .Select(x => x.Match)
                .Take(request.MaxResults)
                .ToArray();
        }
        else
        {
            using var archive = new ZipArchive(new MemoryStream(denseVisibility.PackageBytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
            var depthMapCache = new Dictionary<string, DenseDepthMap?>(StringComparer.OrdinalIgnoreCase);

            matches = geometricCandidates
                .Select(candidate => ApplyDenseVisibilityFilter(candidate, request, denseVisibility, archive, depthMapCache))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .OrderByDescending(x => x.Visibility)
                .ThenByDescending(x => x.Match.Score)
                .ThenBy(x => x.Match.DistanceToImageCenterPixels)
                .ThenBy(x => x.Match.CameraDistance)
                .Take(request.MaxResults)
                .Select(x => x.Match)
                .ToArray();
        }

        logger.LogInformation("Images-for-point query finished project={ProjectId} run={RunId} accepted={AcceptedCount}", projectId, run.Id, matches.Count);
        return new ImagesForPointQueryResult(projectId, run.Id, request.X, request.Y, request.Z, matches.Count, matches);
    }

    private GeometricCandidate? TryProject(ProjectionImageRecord image, ImagesForPointQuery request)
    {
        if (!pointProjectionService.TryProject(image, request.X, request.Y, request.Z, out var projected))
        {
            return null;
        }

        var imageDiagonal = Math.Sqrt((image.Width * image.Width) + (image.Height * image.Height));
        var normalizedCenterDistance = imageDiagonal <= 0d ? 1d : Math.Clamp(projected.DistanceToImageCenterPixels / imageDiagonal, 0d, 1d);
        var centerScore = 1d - normalizedCenterDistance;
        var distanceScore = 1d / (1d + projected.CameraDistance);
        var score = Math.Clamp((0.7d * centerScore) + (0.3d * distanceScore), 0d, 1d);

        var match = new ImagePointMatch(
            image.ImageId,
            image.FileName,
            image.Width,
            image.Height,
            projected.U,
            projected.V,
            Math.Round(score, 6),
            Math.Round(projected.CameraDistance, 6),
            Math.Round(projected.DistanceToImageCenterPixels, 6),
            image.ImageUrl,
            image.ThumbnailUrl);

        return new GeometricCandidate(image, match);
    }

    private DenseFilteredCandidate? ApplyDenseVisibilityFilter(
        GeometricCandidate candidate,
        ImagesForPointQuery request,
        DenseVisibilityPackageData denseVisibility,
        ZipArchive archive,
        IDictionary<string, DenseDepthMap?> depthMapCache)
    {
        if (!denseVisibility.ImagesById.TryGetValue(candidate.Image.ImageId, out var denseImage))
        {
            return new DenseFilteredCandidate(candidate.Match, DepthVisibilityState.Unknown);
        }

        var visibility = EvaluateDepthVisibility(denseImage, request, archive, depthMapCache);
        return visibility switch
        {
            DepthVisibilityState.Occluded => null,
            DepthVisibilityState.Visible => new DenseFilteredCandidate(candidate.Match with
            {
                Score = Math.Round(Math.Min(candidate.Match.Score + 0.05d, 1d), 6)
            }, DepthVisibilityState.Visible),
            _ => new DenseFilteredCandidate(candidate.Match, DepthVisibilityState.Unknown)
        };
    }

    private DepthVisibilityState EvaluateDepthVisibility(
        DenseVisibilityImageRecord denseImage,
        ImagesForPointQuery request,
        ZipArchive archive,
        IDictionary<string, DenseDepthMap?> depthMapCache)
    {
        if (!TryProjectForDepthTest(denseImage.Image, request.X, request.Y, request.Z, out var u, out var v, out var cameraDepth))
        {
            return DepthVisibilityState.Unknown;
        }

        if (!depthMapCache.TryGetValue(denseImage.DepthMapEntryPath, out var depthMap))
        {
            depthMap = TryLoadDepthMap(archive, denseImage.DepthMapEntryPath);
            depthMapCache[denseImage.DepthMapEntryPath] = depthMap;
        }

        if (depthMap is null || depthMap.Channels != 1)
        {
            return DepthVisibilityState.Unknown;
        }

        var centerX = (int)Math.Round(u, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        var tolerance = Math.Max(DepthAbsoluteTolerance, cameraDepth * DepthRelativeTolerance);
        var minDepth = double.PositiveInfinity;
        var hadValidDepth = false;

        for (var dy = -VisibilityNeighborhoodRadiusPixels; dy <= VisibilityNeighborhoodRadiusPixels; dy++)
        {
            for (var dx = -VisibilityNeighborhoodRadiusPixels; dx <= VisibilityNeighborhoodRadiusPixels; dx++)
            {
                var sampleX = centerX + dx;
                var sampleY = centerY + dy;
                if (sampleX < 0 || sampleY < 0 || sampleX >= depthMap.Width || sampleY >= depthMap.Height)
                {
                    continue;
                }

                var sampleDepth = depthMap.ReadDepth(sampleX, sampleY);
                if (!double.IsFinite(sampleDepth) || sampleDepth <= 0d)
                {
                    continue;
                }

                hadValidDepth = true;
                minDepth = Math.Min(minDepth, sampleDepth);
                if (Math.Abs(sampleDepth - cameraDepth) <= tolerance)
                {
                    return DepthVisibilityState.Visible;
                }
            }
        }

        if (!hadValidDepth)
        {
            return DepthVisibilityState.Unknown;
        }

        return minDepth + tolerance < cameraDepth
            ? DepthVisibilityState.Occluded
            : DepthVisibilityState.Unknown;
    }

    private async Task<(PipelineRun Run, IReadOnlyCollection<ProjectionImageRecord> Images)> ResolveRunAndImagesAsync(Guid projectId, ImagesForPointQuery request, CancellationToken ct)
    {
        if (request.RunId is { } explicitRunId)
        {
            var run = await dbContext.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == explicitRunId, ct)
                ?? throw new NotFoundException($"Run '{explicitRunId}' was not found.");
            var explicitImages = await TryLoadProjectionImagesAsync(projectId, run, request.IncludeImageUrls, ct);
            if (explicitImages is null)
            {
                throw new ConflictException($"Run '{explicitRunId}' does not have usable projection data.");
            }

            return (run, explicitImages);
        }

        var runs = (await dbContext.PipelineRuns.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.Status == PipelineRunStatus.Succeeded)
            .ToListAsync(ct))
            .OrderByDescending(x => x.FinishedAtUtc ?? x.CreatedAtUtc)
            .ToList();
        foreach (var run in runs)
        {
            var images = await TryLoadProjectionImagesAsync(projectId, run, request.IncludeImageUrls, ct);
            if (images is not null)
            {
                return (run, images);
            }
        }

        throw new ConflictException("The project has no completed reconstruction run with usable camera export data.");
    }

    private async Task<IReadOnlyCollection<ProjectionImageRecord>?> TryLoadProjectionImagesAsync(Guid projectId, PipelineRun run, bool includeImageUrls, CancellationToken ct)
    {
        if (run.Status != PipelineRunStatus.Succeeded)
        {
            return null;
        }

        var artifact = (await dbContext.Artifacts.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.PipelineRunId == run.Id && x.Status == ArtifactStatus.Available)
            .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault(x => ArtifactTypeCompatibility.Matches(x, ArtifactType.ExportPackage));
        if (artifact is null || IsSimulatedArtifact(artifact.MetadataJson))
        {
            return null;
        }

        var stored = await objectStorage.OpenReadAsync(artifact.StorageKey, ct);
        if (stored is null)
        {
            return null;
        }

        var records = await LoadProjectionImagesAsync(projectId, stored, includeImageUrls, ct);
        return records.Count == 0 ? null : records;
    }

    private async Task<IReadOnlyCollection<ProjectionImageRecord>> LoadProjectionImagesAsync(Guid projectId, StoredObject stored, bool includeImageUrls, CancellationToken ct)
    {
        await using var artifactStream = stored.Stream;
        Stream zipStream;
        if (artifactStream.CanSeek)
        {
            artifactStream.Position = 0;
            zipStream = artifactStream;
        }
        else
        {
            var buffered = new MemoryStream();
            await artifactStream.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            zipStream = buffered;
        }

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
        return await LoadProjectionImagesFromArchiveAsync(projectId, archive, includeImageUrls, ct);
    }

    private async Task<DenseVisibilityPackageData?> TryLoadDenseVisibilityPackageAsync(Guid projectId, Guid runId, CancellationToken ct)
    {
        var artifact = (await dbContext.Artifacts.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.PipelineRunId == runId && x.Status == ArtifactStatus.Available)
            .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault(x => ArtifactTypeCompatibility.Matches(x, ArtifactType.DenseVisibilityPackage));
        if (artifact is null)
        {
            return null;
        }

        var stored = await objectStorage.OpenReadAsync(artifact.StorageKey, ct);
        if (stored is null)
        {
            return null;
        }

        await using var storedStream = stored.Stream;
        using var packageMemory = new MemoryStream();
        await storedStream.CopyToAsync(packageMemory, ct);
        var bytes = packageMemory.ToArray();

        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        var projectImages = await dbContext.ProjectImages.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .ToDictionaryAsync(x => x.Id, ct);
        var projectionImages = await LoadProjectionImagesFromArchiveAsync(projectId, archive, includeImageUrls: false, ct);
        if (projectionImages.Count == 0)
        {
            return null;
        }

        var depthEntryByImageId = BuildDepthEntryIndex(archive);
        var denseImagesById = projectionImages
            .Where(image => depthEntryByImageId.ContainsKey(image.ImageId))
            .ToDictionary(
                image => image.ImageId,
                image => new DenseVisibilityImageRecord(
                    image,
                    depthEntryByImageId[image.ImageId]),
                comparer: EqualityComparer<Guid>.Default);

        return denseImagesById.Count == 0
            ? null
            : new DenseVisibilityPackageData(bytes, denseImagesById);
    }

    private async Task<IReadOnlyCollection<ProjectionImageRecord>> LoadProjectionImagesFromArchiveAsync(Guid projectId, ZipArchive archive, bool includeImageUrls, CancellationToken ct)
    {
        var camerasEntry = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith("/cameras.txt", StringComparison.OrdinalIgnoreCase) || string.Equals(x.FullName, "cameras.txt", StringComparison.OrdinalIgnoreCase));
        var imagesEntry = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith("/images.txt", StringComparison.OrdinalIgnoreCase) || string.Equals(x.FullName, "images.txt", StringComparison.OrdinalIgnoreCase));
        if (camerasEntry is null || imagesEntry is null)
        {
            return [];
        }

        var projectImages = await dbContext.ProjectImages.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
        Dictionary<int, ColmapCamera> cameras;
        await using (var camerasStream = camerasEntry.Open())
        using (var camerasReader = new StreamReader(camerasStream))
        {
            cameras = ParseCameras(await camerasReader.ReadToEndAsync(ct));
        }

        await using var imagesStream = imagesEntry.Open();
        using var imagesReader = new StreamReader(imagesStream);
        return ParseProjectionImages(await imagesReader.ReadToEndAsync(ct), cameras, projectImages, projectId, includeImageUrls);
    }

    private static Dictionary<Guid, string> BuildDepthEntryIndex(ZipArchive archive)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.Contains("/depth_maps/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = entry.FullName.Replace('\\', '/');
            var imageName = TryGetDepthMapImageName(normalized);
            if (imageName is null || !TryParseProjectImageId(imageName, out var imageId))
            {
                continue;
            }

            if (result.TryGetValue(imageId, out var existingEntry))
            {
                if (ShouldReplaceDepthEntry(existingEntry, normalized))
                {
                    result[imageId] = normalized;
                }

                continue;
            }

            result[imageId] = normalized;
        }

        return result;
    }

    private static DenseDepthMap? TryLoadDepthMap(ZipArchive archive, string entryPath)
    {
        var entry = archive.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return DenseDepthMap.TryParse(memory.ToArray());
    }

    private static Dictionary<int, ColmapCamera> ParseCameras(string text)
    {
        var result = new Dictionary<int, ColmapCamera>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith('#'))
            {
                continue;
            }

            var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            if (!TryMapCameraModel(parts[1].ToUpperInvariant(), parts.Skip(4).Select(ParseInvariantDouble).ToArray(), out var intrinsics))
            {
                continue;
            }

            var cameraId = int.Parse(parts[0], CultureInfo.InvariantCulture);
            result[cameraId] = new ColmapCamera(cameraId, parts[1].ToUpperInvariant(), int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture), intrinsics.Fx, intrinsics.Fy, intrinsics.Cx, intrinsics.Cy, intrinsics.K1, intrinsics.K2);
        }

        return result;
    }

    private static IReadOnlyCollection<ProjectionImageRecord> ParseProjectionImages(string text, IReadOnlyDictionary<int, ColmapCamera> cameras, IReadOnlyDictionary<Guid, ProjectImage> projectImages, Guid projectId, bool includeImageUrls)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.None).Select(x => x.Trim()).ToArray();
        var result = new List<ProjectionImageRecord>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 10 || !cameras.TryGetValue(int.Parse(parts[8], CultureInfo.InvariantCulture), out var camera) || !TryParseProjectImageId(parts[9], out var imageId))
            {
                continue;
            }

            var rotation = QuaternionToRotationMatrix(ParseInvariantDouble(parts[1]), ParseInvariantDouble(parts[2]), ParseInvariantDouble(parts[3]), ParseInvariantDouble(parts[4]));
            var translation = new[] { ParseInvariantDouble(parts[5]), ParseInvariantDouble(parts[6]), ParseInvariantDouble(parts[7]) };
            var cameraCenter = ComputeCameraCenter(rotation, translation);
            projectImages.TryGetValue(imageId, out var projectImage);

            result.Add(new ProjectionImageRecord(imageId, projectImage?.OriginalFileName ?? parts[9], camera.Width, camera.Height, camera.Model, camera.Fx, camera.Fy, camera.Cx, camera.Cy, camera.K1, camera.K2, rotation, translation, cameraCenter, includeImageUrls ? BuildImageUrl(projectId, imageId) : null, includeImageUrls ? BuildThumbnailUrl(projectId, imageId) : null));
            index++;
        }

        return result;
    }

    private static bool TryMapCameraModel(string model, double[] parameters, out (double Fx, double Fy, double Cx, double Cy, double K1, double K2) intrinsics)
    {
        intrinsics = default;
        switch (model)
        {
            case "SIMPLE_PINHOLE" when parameters.Length >= 3: intrinsics = (parameters[0], parameters[0], parameters[1], parameters[2], 0d, 0d); return true;
            case "PINHOLE" when parameters.Length >= 4: intrinsics = (parameters[0], parameters[1], parameters[2], parameters[3], 0d, 0d); return true;
            case "SIMPLE_RADIAL" when parameters.Length >= 4: intrinsics = (parameters[0], parameters[0], parameters[1], parameters[2], parameters[3], 0d); return true;
            case "RADIAL" when parameters.Length >= 5: intrinsics = (parameters[0], parameters[0], parameters[1], parameters[2], parameters[3], parameters[4]); return true;
            default: return false;
        }
    }

    private static double[] QuaternionToRotationMatrix(double qw, double qx, double qy, double qz)
    {
        var norm = Math.Sqrt((qw * qw) + (qx * qx) + (qy * qy) + (qz * qz));
        if (norm <= 0d)
        {
            return [1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d];
        }

        qw /= norm; qx /= norm; qy /= norm; qz /= norm;
        return
        [
            1d - (2d * ((qy * qy) + (qz * qz))), 2d * ((qx * qy) - (qz * qw)), 2d * ((qx * qz) + (qy * qw)),
            2d * ((qx * qy) + (qz * qw)), 1d - (2d * ((qx * qx) + (qz * qz))), 2d * ((qy * qz) - (qx * qw)),
            2d * ((qx * qz) - (qy * qw)), 2d * ((qy * qz) + (qx * qw)), 1d - (2d * ((qx * qx) + (qy * qy)))
        ];
    }

    private static double[] ComputeCameraCenter(double[] rotation, double[] translation)
        => [-((rotation[0] * translation[0]) + (rotation[3] * translation[1]) + (rotation[6] * translation[2])), -((rotation[1] * translation[0]) + (rotation[4] * translation[1]) + (rotation[7] * translation[2])), -((rotation[2] * translation[0]) + (rotation[5] * translation[1]) + (rotation[8] * translation[2]))];

    private static bool TryProjectImageId(string exportedName, out Guid imageId)
        => TryParseProjectImageId(exportedName, out imageId);

    private static bool TryParseProjectImageId(string exportedName, out Guid imageId)
    {
        imageId = Guid.Empty;
        var separator = exportedName.IndexOf('_');
        return separator > 0 && Guid.TryParseExact(exportedName[..separator], "N", out imageId);
    }

    private static bool IsSimulatedArtifact(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("simulated", out var simulated) && simulated.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetDepthMapImageName(string entryPath)
    {
        var fileName = Path.GetFileName(entryPath);
        if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var withoutBin = fileName[..^4];
        var separator = withoutBin.LastIndexOf('.');
        return separator <= 0 ? null : withoutBin[..separator];
    }

    private static bool ShouldReplaceDepthEntry(string existingEntry, string candidateEntry)
    {
        var existingIsGeometric = existingEntry.EndsWith(".geometric.bin", StringComparison.OrdinalIgnoreCase);
        var candidateIsGeometric = candidateEntry.EndsWith(".geometric.bin", StringComparison.OrdinalIgnoreCase);
        return candidateIsGeometric && !existingIsGeometric;
    }

    private static bool TryProjectForDepthTest(ProjectionImageRecord image, double x, double y, double z, out double u, out double v, out double cameraDepth)
    {
        var cameraX = (image.RotationMatrix3x3[0] * x) + (image.RotationMatrix3x3[1] * y) + (image.RotationMatrix3x3[2] * z) + image.Translation3[0];
        var cameraY = (image.RotationMatrix3x3[3] * x) + (image.RotationMatrix3x3[4] * y) + (image.RotationMatrix3x3[5] * z) + image.Translation3[1];
        cameraDepth = (image.RotationMatrix3x3[6] * x) + (image.RotationMatrix3x3[7] * y) + (image.RotationMatrix3x3[8] * z) + image.Translation3[2];
        if (cameraDepth <= 0d)
        {
            u = 0d;
            v = 0d;
            return false;
        }

        var normalizedX = cameraX / cameraDepth;
        var normalizedY = cameraY / cameraDepth;
        var r2 = (normalizedX * normalizedX) + (normalizedY * normalizedY);
        var (distortedX, distortedY) = image.CameraModel switch
        {
            "SIMPLE_PINHOLE" or "PINHOLE" => (normalizedX, normalizedY),
            "SIMPLE_RADIAL" => (normalizedX * (1d + (image.K1 * r2)), normalizedY * (1d + (image.K1 * r2))),
            "RADIAL" => (normalizedX * (1d + (image.K1 * r2) + (image.K2 * r2 * r2)), normalizedY * (1d + (image.K1 * r2) + (image.K2 * r2 * r2))),
            _ => (double.NaN, double.NaN)
        };

        u = (image.Fx * distortedX) + image.Cx;
        v = (image.Fy * distortedY) + image.Cy;
        return double.IsFinite(u) && double.IsFinite(v) && u >= 0d && v >= 0d && u < image.Width && v < image.Height;
    }

    private static int GetVisibilityCandidateLimit(int maxResults) => Math.Max(maxResults * VisibilityCandidateMultiplier, MinimumVisibilityCandidateLimit);

    private static string BuildImageUrl(Guid projectId, Guid imageId) => $"/api/v1/projects/{projectId}/images/{imageId}/content";
    private static string BuildThumbnailUrl(Guid projectId, Guid imageId) => $"/api/v1/projects/{projectId}/images/{imageId}/content?variant=thumbnail";
    private static double ParseInvariantDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private sealed record ColmapCamera(int CameraId, string Model, int Width, int Height, double Fx, double Fy, double Cx, double Cy, double K1, double K2);
    private sealed record GeometricCandidate(ProjectionImageRecord Image, ImagePointMatch Match);
    private sealed record DenseFilteredCandidate(ImagePointMatch Match, DepthVisibilityState Visibility);
    private sealed record DenseVisibilityImageRecord(ProjectionImageRecord Image, string DepthMapEntryPath);
    private sealed record DenseVisibilityPackageData(byte[] PackageBytes, IReadOnlyDictionary<Guid, DenseVisibilityImageRecord> ImagesById);

    private enum DepthVisibilityState
    {
        Unknown = 0,
        Visible = 1,
        Occluded = -1
    }

    private sealed class DenseDepthMap(int width, int height, int channels, byte[] bytes, int dataOffset)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Channels { get; } = channels;
        private byte[] Bytes { get; } = bytes;
        private int DataOffset { get; } = dataOffset;

        public float ReadDepth(int x, int y)
        {
            var elementOffset = DataOffset + (((y * Width) + x) * sizeof(float) * Channels);
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(elementOffset, sizeof(float))));
        }

        public static DenseDepthMap? TryParse(byte[] bytes)
        {
            var firstSeparator = Array.IndexOf(bytes, (byte)'&');
            if (firstSeparator <= 0)
            {
                return null;
            }

            var secondSeparator = Array.IndexOf(bytes, (byte)'&', firstSeparator + 1);
            if (secondSeparator <= firstSeparator + 1)
            {
                return null;
            }

            var thirdSeparator = Array.IndexOf(bytes, (byte)'&', secondSeparator + 1);
            if (thirdSeparator <= secondSeparator + 1)
            {
                return null;
            }

            if (!int.TryParse(System.Text.Encoding.ASCII.GetString(bytes, 0, firstSeparator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(System.Text.Encoding.ASCII.GetString(bytes, firstSeparator + 1, secondSeparator - firstSeparator - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
                !int.TryParse(System.Text.Encoding.ASCII.GetString(bytes, secondSeparator + 1, thirdSeparator - secondSeparator - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channels))
            {
                return null;
            }

            if (width <= 0 || height <= 0 || channels <= 0)
            {
                return null;
            }

            var dataOffset = thirdSeparator + 1;
            var requiredBytes = (long)width * height * channels * sizeof(float);
            if (bytes.LongLength < dataOffset + requiredBytes)
            {
                return null;
            }

            return new DenseDepthMap(width, height, channels, bytes, dataOffset);
        }
    }
}
