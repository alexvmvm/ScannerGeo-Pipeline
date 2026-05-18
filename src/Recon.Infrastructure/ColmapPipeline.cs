using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recon.Core;
using Recon.Domain;

namespace Recon.Infrastructure;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        // COLMAP's GPU feature extractor still initializes Qt. Force a headless
        // platform plugin so worker containers do not require an X display.
        if (!startInfo.Environment.ContainsKey("QT_QPA_PLATFORM"))
        {
            startInfo.Environment["QT_QPA_PLATFORM"] = "offscreen";
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var startedAt = Stopwatch.StartNew();
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        startedAt.Stop();

        return new ProcessExecutionResult(fileName, arguments, process.ExitCode, stdout, stderr, startedAt.Elapsed);
    }
}

public sealed class ColmapProjectPipelineService(
    IObjectStorage objectStorage,
    IReconDbContext dbContext,
    IProcessRunner processRunner,
    IOptions<ReconOptions> options,
    ILogger<ColmapProjectPipelineService> logger) : IProjectPipelineService
{
    private readonly ReconOptions _options = options.Value;

    public async Task<PipelineExecutionResult> RunInspectAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        await DownloadProjectImagesAsync(context.Images, workspace.ImagesDirectory, ct);
        var command = await RunColmapRequiredAsync([ "--help" ], workspace.WorkRoot, ct);
        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Inspect, workspace, [command], new { imageCount = context.Images.Count }), []);
    }

    public async Task<PipelineExecutionResult> RunSparseAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        await DownloadProjectImagesAsync(context.Images, workspace.ImagesDirectory, ct);

        var commands = new List<ProcessExecutionResult>();
        var databasePath = Path.Combine(workspace.WorkRoot, "database.db");
        commands.Add(await RunColmapRequiredAsync(
        [
            "feature_extractor",
            "--database_path", databasePath,
            "--image_path", workspace.ImagesDirectory,
            "--FeatureExtraction.use_gpu", _options.ColmapUseGpu ? "1" : "0"
        ], workspace.WorkRoot, ct));

        var matcherCommand = ResolveMatcherCommand(context.Project.ConfigJson);
        commands.Add(await RunColmapRequiredAsync(
        [
            matcherCommand,
            "--database_path", databasePath,
            "--FeatureMatching.use_gpu", _options.ColmapUseGpu ? "1" : "0"
        ], workspace.WorkRoot, ct));

        commands.Add(await RunColmapRequiredAsync(
        [
            "mapper",
            "--database_path", databasePath,
            "--image_path", workspace.ImagesDirectory,
            "--output_path", workspace.SparseRoot
        ], workspace.WorkRoot, ct));

        var modelDirectory = GetFirstModelDirectory(workspace.SparseRoot);
        var zipBytes = ZipDirectory(modelDirectory);
        var artifact = new PipelineArtifact("sparse-model.zip", "application/zip", zipBytes, ArtifactType.SparseModel, JsonSerializer.Serialize(new { format = "colmap-sparse" }, ReconJson.Defaults));

        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Sparse, workspace, commands, new { modelDirectory }), [artifact]);
    }

    public async Task<PipelineExecutionResult> RunDenseAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        await DownloadProjectImagesAsync(context.Images, workspace.ImagesDirectory, ct);
        var sparseArtifact = await GetArtifactContentAsync(context.Project.Id, context.Run.Id, ArtifactType.SparseModel, ct);
        var sparseZip = sparseArtifact.Bytes;
        var sparseModelDirectory = ExtractSparseModel(workspace, sparseZip);

        var commands = new List<ProcessExecutionResult>();
        commands.Add(await RunColmapRequiredAsync(
        [
            "image_undistorter",
            "--image_path", workspace.ImagesDirectory,
            "--input_path", sparseModelDirectory,
            "--output_path", workspace.DenseRoot,
            "--output_type", "COLMAP"
        ], workspace.WorkRoot, ct));

        var patchMatchResult = await RunDensePatchMatchStereoAsync(workspace, ct);
        commands.AddRange(patchMatchResult.Commands);

        var denseSparseTextRoot = Path.Combine(workspace.WorkRoot, "dense_sparse_txt");
        Directory.CreateDirectory(denseSparseTextRoot);
        commands.Add(await RunColmapRequiredAsync(
        [
            "model_converter",
            "--input_path", Path.Combine(workspace.DenseRoot, "sparse"),
            "--output_path", denseSparseTextRoot,
            "--output_type", "TXT"
        ], workspace.WorkRoot, ct));

        var fusedPath = Path.Combine(workspace.DenseRoot, "fused.ply");
        commands.Add(await RunColmapRequiredAsync(
        [
            "stereo_fusion",
            "--workspace_path", workspace.DenseRoot,
            "--workspace_format", "COLMAP",
            "--input_type", "geometric",
            .. BuildStereoFusionOptions(patchMatchResult.MaxImageSize),
            "--output_path", fusedPath
        ], workspace.WorkRoot, ct));

        var bytes = await File.ReadAllBytesAsync(fusedPath, ct);
        EnsurePlyContainsFiniteVertexPositions(bytes, "Dense reconstruction produced an unusable fused point cloud.");
        var artifacts = new List<PipelineArtifact>
        {
            new("fused.ply", "application/octet-stream", bytes, ArtifactType.DensePointCloud, JsonSerializer.Serialize(new { format = "ply" }, ReconJson.Defaults))
        };

        var visibilityPackageBytes = await TryCreateDenseVisibilityPackageAsync(workspace.DenseRoot, denseSparseTextRoot, ct);
        if (visibilityPackageBytes is not null)
        {
            artifacts.Add(new PipelineArtifact(
                "dense-visibility-package.zip",
                "application/zip",
                visibilityPackageBytes,
                ArtifactType.DenseVisibilityPackage,
                JsonSerializer.Serialize(new
                {
                    format = "colmap-dense-visibility",
                    sparseText = true,
                    depthMaps = "stereo/depth_maps"
                }, ReconJson.Defaults)));
        }

        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Dense, workspace, commands, new
        {
            fusedPath,
            sparseArtifactRunId = sparseArtifact.Artifact.PipelineRunId,
            denseVisibilityPackageCreated = visibilityPackageBytes is not null
        }), artifacts);
    }

    public async Task<PipelineExecutionResult> RunExportAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        var exportRoot = Path.Combine(workspace.WorkRoot, "export");
        Directory.CreateDirectory(exportRoot);
        var sparseArtifact = await GetArtifactContentAsync(context.Project.Id, context.Run.Id, ArtifactType.SparseModel, ct);
        var sparseModelDirectory = ExtractSparseModel(workspace, sparseArtifact.Bytes);

        var commands = new List<ProcessExecutionResult>
        {
            await RunColmapRequiredAsync(
            [
                "model_converter",
                "--input_path", sparseModelDirectory,
                "--output_path", exportRoot,
                "--output_type", "TXT"
            ], workspace.WorkRoot, ct)
        };

        var denseArtifact = await TryGetArtifactContentAsync(context.Project.Id, context.Run.Id, ArtifactType.DensePointCloud, ct);
        if (denseArtifact is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(exportRoot, "fused.ply"), denseArtifact.Bytes, ct);
        }

        var bundleBytes = ZipDirectory(exportRoot);
        var artifact = new PipelineArtifact(
            "export-package.zip",
            "application/zip",
            bundleBytes,
            ArtifactType.ExportPackage,
            JsonSerializer.Serialize(new
            {
                format = "colmap-text-export",
                sparseArtifactRunId = sparseArtifact.Artifact.PipelineRunId,
                denseArtifactRunId = denseArtifact?.Artifact.PipelineRunId
            }, ReconJson.Defaults));

        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Export, workspace, commands, new
        {
            exportRoot,
            sparseArtifactRunId = sparseArtifact.Artifact.PipelineRunId,
            denseArtifactRunId = denseArtifact?.Artifact.PipelineRunId
        }), [artifact]);
    }

    public async Task<PipelineExecutionResult> RunPublishAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        var publishRoot = Path.Combine(workspace.WorkRoot, "publish");
        Directory.CreateDirectory(publishRoot);
        var sceneRoot = Path.Combine(publishRoot, "scene");
        var fusedPath = Path.Combine(publishRoot, "fused.ply");
        var automationResponsePath = Path.Combine(publishRoot, "octree-build-response.json");
        var sceneId = $"run-{context.Run.Id:N}";

        var denseArtifact = await GetArtifactContentAsync(context.Project.Id, context.Run.Id, ArtifactType.DensePointCloud, ct);
        EnsurePlyContainsFiniteVertexPositions(denseArtifact.Bytes, "Dense point cloud artifact is unusable for octree publish.");
        await File.WriteAllBytesAsync(fusedPath, denseArtifact.Bytes, ct);

        var command = BuildOctreeBuildCommand(fusedPath, sceneRoot, automationResponsePath, sceneId);
        var processResult = await RunRequiredProcessAsync(command.FileName, command.Arguments, workspace.WorkRoot, "octree-build", ct);
        var automationResponse = ReadSuccessfulOctreeAutomationResponse(automationResponsePath);
        var bundleBytes = ZipDirectory(sceneRoot);
        var artifact = new PipelineArtifact(
            "octree-scene-package.zip",
            "application/zip",
            bundleBytes,
            ArtifactType.OctreePackage,
            JsonSerializer.Serialize(new
            {
                format = "scannergeo-octree-scene",
                sceneId,
                denseArtifactRunId = denseArtifact.Artifact.PipelineRunId,
                sqliteIncluded = automationResponse.TryGetProperty("artifacts", out var artifacts)
                    && artifacts.TryGetProperty("sqlitePath", out var sqlitePath)
                    && sqlitePath.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(sqlitePath.GetString())
            }, ReconJson.Defaults));

        return new PipelineExecutionResult(
            true,
            SerializeReport(PipelineStage.Publish, workspace, [processResult], new
            {
                fusedPath,
                publishRoot,
                sceneRoot,
                denseArtifactRunId = denseArtifact.Artifact.PipelineRunId,
                octree = automationResponse
            }),
            [artifact]);
    }

    private async Task DownloadProjectImagesAsync(IReadOnlyCollection<ProjectImage> images, string imagesDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(imagesDirectory);
        foreach (var image in images)
        {
            var stored = await objectStorage.OpenReadAsync(image.StorageKey, ct)
                ?? throw new FileNotFoundException($"Input image '{image.StorageKey}' was not found in object storage.");
            await using var stream = stored.Stream;
            await using var file = File.Create(Path.Combine(imagesDirectory, $"{image.Id:N}_{image.OriginalFileName}"));
            await stream.CopyToAsync(file, ct);
        }
    }

    private async Task<ResolvedArtifactContent> GetArtifactContentAsync(Guid projectId, Guid runId, ArtifactType type, CancellationToken ct)
        => await TryGetArtifactContentAsync(projectId, runId, type, ct)
            ?? throw new FileNotFoundException($"Artifact '{type}' for run '{runId}' was not found for project '{projectId}'.");

    private async Task<ResolvedArtifactContent?> TryGetArtifactContentAsync(Guid projectId, Guid runId, ArtifactType type, CancellationToken ct)
    {
        var artifact = (await dbContext.Artifacts.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.Type == type && x.Status == ArtifactStatus.Available)
            .ToListAsync(ct))
            .OrderByDescending(x => x.PipelineRunId == runId)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        if (artifact is null)
        {
            return null;
        }

        var stored = await objectStorage.OpenReadAsync(artifact.StorageKey, ct);
        if (stored is null)
        {
            return null;
        }

        await using var stream = stored.Stream;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return new ResolvedArtifactContent(artifact, memory.ToArray());
    }

    private string ExtractSparseModel(ColmapWorkspace workspace, byte[] zipBytes)
    {
        var zipPath = Path.Combine(workspace.WorkRoot, "sparse-model.zip");
        File.WriteAllBytes(zipPath, zipBytes);
        var extractRoot = Path.Combine(workspace.WorkRoot, "sparse_extract");
        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractRoot);
        return Directory.GetDirectories(extractRoot).FirstOrDefault() ?? extractRoot;
    }

    private async Task<ProcessExecutionResult> RunColmapRequiredAsync(IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
    {
        return await RunRequiredProcessAsync(_options.ColmapBinaryPath, arguments, workingDirectory, "COLMAP", ct);
    }

    private async Task<DensePatchMatchResult> RunDensePatchMatchStereoAsync(ColmapWorkspace workspace, CancellationToken ct)
    {
        var attemptedSizes = new HashSet<int>();
        var commands = new List<ProcessExecutionResult>();
        var maxImageSizes = new List<int?>();
        var configuredMaxImageSize = NormalizeMaxImageSize(_options.ColmapDenseMaxImageSize);
        maxImageSizes.Add(configuredMaxImageSize);

        var retryMaxImageSize = NormalizeMaxImageSize(_options.ColmapDenseRetryMaxImageSize);
        if (_options.ColmapDenseRetryOnCudaFailure
            && retryMaxImageSize.HasValue
            && retryMaxImageSize != configuredMaxImageSize
            && (!configuredMaxImageSize.HasValue || retryMaxImageSize.Value < configuredMaxImageSize.Value))
        {
            maxImageSizes.Add(retryMaxImageSize);
        }

        for (var attemptIndex = 0; attemptIndex < maxImageSizes.Count; attemptIndex++)
        {
            var maxImageSize = maxImageSizes[attemptIndex];
            var arguments = BuildPatchMatchStereoArguments(workspace.DenseRoot, maxImageSize);
            var result = await RunColmapAsync(arguments, workspace.WorkRoot, ct);
            commands.Add(result);
            if (result.ExitCode == 0)
            {
                return new DensePatchMatchResult(commands, maxImageSize);
            }

            var canRetry = attemptIndex < maxImageSizes.Count - 1
                && IsDenseCudaFailure(result.StandardError);
            if (!canRetry)
            {
                throw new InvalidOperationException($"COLMAP command failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            var nextMaxImageSize = maxImageSizes[attemptIndex + 1];
            if (nextMaxImageSize.HasValue && !attemptedSizes.Add(nextMaxImageSize.Value))
            {
                throw new InvalidOperationException($"COLMAP command failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            ResetDenseStereoOutputs(workspace.DenseRoot);
            logger.LogWarning(
                "COLMAP dense stereo failed with a CUDA memory error. Retrying with PatchMatchStereo.max_image_size={MaxImageSize}.",
                nextMaxImageSize);
        }

        throw new InvalidOperationException("COLMAP dense stereo failed without producing a retryable result.");
    }

    private async Task<ProcessExecutionResult> RunRequiredProcessAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, string toolName, CancellationToken ct)
    {
        var result = await RunProcessAsync(fileName, arguments, workingDirectory, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} command failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private async Task<ProcessExecutionResult> RunColmapAsync(IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
        => await RunProcessAsync(_options.ColmapBinaryPath, arguments, workingDirectory, ct);

    private async Task<ProcessExecutionResult> RunProcessAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
    {
        logger.LogInformation("Running process {Command} {Arguments}", fileName, string.Join(' ', arguments));
        return await processRunner.RunAsync(fileName, arguments, workingDirectory, ct);
    }

    private static string ResolveMatcherCommand(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return "exhaustive_matcher";
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.TryGetProperty("matcherType", out var matcher))
            {
                return matcher.GetString()?.ToLowerInvariant() switch
                {
                    "sequential" => "sequential_matcher",
                    _ => "exhaustive_matcher"
                };
            }
        }
        catch
        {
            // Ignore config parse failures here and fall back to the default matcher.
        }

        return "exhaustive_matcher";
    }

    private static string GetFirstModelDirectory(string sparseRoot)
    {
        var directory = Directory.GetDirectories(sparseRoot).OrderBy(x => x).FirstOrDefault();
        return directory ?? throw new DirectoryNotFoundException("COLMAP did not produce a sparse model directory.");
    }

    private static IReadOnlyCollection<string> BuildPatchMatchStereoArguments(string denseRoot, int? maxImageSize)
    {
        var arguments = new List<string>
        {
            "patch_match_stereo",
            "--workspace_path", denseRoot,
            "--workspace_format", "COLMAP",
            "--PatchMatchStereo.geom_consistency", "1"
        };

        if (maxImageSize.HasValue)
        {
            arguments.Add("--PatchMatchStereo.max_image_size");
            arguments.Add(maxImageSize.Value.ToString());
        }

        return arguments;
    }

    private static IReadOnlyCollection<string> BuildStereoFusionOptions(int? maxImageSize)
    {
        if (!maxImageSize.HasValue)
        {
            return [];
        }

        return
        [
            "--StereoFusion.max_image_size",
            maxImageSize.Value.ToString()
        ];
    }

    private static int? NormalizeMaxImageSize(int value) => value > 0 ? value : null;

    private static bool IsDenseCudaFailure(string standardError)
        => standardError.Contains("CUDA error", StringComparison.OrdinalIgnoreCase)
            && (standardError.Contains("illegal memory access", StringComparison.OrdinalIgnoreCase)
                || standardError.Contains("graphics card timeout detection mechanism", StringComparison.OrdinalIgnoreCase));

    private static void ResetDenseStereoOutputs(string denseRoot)
    {
        var stereoRoot = Path.Combine(denseRoot, "stereo");
        foreach (var directoryName in new[] { "depth_maps", "normal_maps", "consistency_graphs" })
        {
            var directoryPath = Path.Combine(stereoRoot, directoryName);
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static void EnsurePlyContainsFiniteVertexPositions(byte[] bytes, string failurePrefix)
    {
        var header = ParsePlyHeader(bytes);
        var hasFinitePoint = header.Format switch
        {
            "ascii" => AsciiPlyContainsFiniteVertexPositions(bytes.AsSpan(header.BodyOffset), header),
            "binary_little_endian" => BinaryPlyContainsFiniteVertexPositions(bytes.AsSpan(header.BodyOffset), header),
            _ => throw new InvalidDataException($"{failurePrefix} Unsupported PLY format '{header.Format}'.")
        };

        if (!hasFinitePoint)
        {
            throw new InvalidDataException($"{failurePrefix} PLY did not contain any finite x/y/z vertex positions.");
        }
    }

    private static bool AsciiPlyContainsFiniteVertexPositions(ReadOnlySpan<byte> body, PlyHeader header)
    {
        var text = Encoding.ASCII.GetString(body);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < header.VertexCount)
        {
            throw new InvalidDataException("ASCII PLY body ended before all vertex rows were read.");
        }

        for (var i = 0; i < header.VertexCount; i++)
        {
            var tokens = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < header.VertexProperties.Count)
            {
                throw new InvalidDataException($"ASCII PLY vertex row {i} has too few columns.");
            }

            var x = ParseAsciiValue(tokens[header.XPropertyIndex], header.VertexProperties[header.XPropertyIndex].Type);
            var y = ParseAsciiValue(tokens[header.YPropertyIndex], header.VertexProperties[header.YPropertyIndex].Type);
            var z = ParseAsciiValue(tokens[header.ZPropertyIndex], header.VertexProperties[header.ZPropertyIndex].Type);
            if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BinaryPlyContainsFiniteVertexPositions(ReadOnlySpan<byte> body, PlyHeader header)
    {
        using var stream = new MemoryStream(body.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        for (var i = 0; i < header.VertexCount; i++)
        {
            double x = 0;
            double y = 0;
            double z = 0;
            for (var propertyIndex = 0; propertyIndex < header.VertexProperties.Count; propertyIndex++)
            {
                var value = ReadBinaryValue(reader, header.VertexProperties[propertyIndex].Type);
                if (propertyIndex == header.XPropertyIndex)
                {
                    x = value;
                }
                else if (propertyIndex == header.YPropertyIndex)
                {
                    y = value;
                }
                else if (propertyIndex == header.ZPropertyIndex)
                {
                    z = value;
                }
            }

            if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
            {
                return true;
            }
        }

        return false;
    }

    private static PlyHeader ParsePlyHeader(ReadOnlySpan<byte> buffer)
    {
        var marker = Encoding.ASCII.GetBytes("end_header");
        var markerIndex = buffer.IndexOf(marker);
        if (markerIndex < 0)
        {
            throw new InvalidDataException("PLY header is missing end_header.");
        }

        var bodyOffset = markerIndex + marker.Length;
        if (bodyOffset < buffer.Length && buffer[bodyOffset] == (byte)'\r')
        {
            bodyOffset++;
        }

        if (bodyOffset < buffer.Length && buffer[bodyOffset] == (byte)'\n')
        {
            bodyOffset++;
        }

        var headerText = Encoding.ASCII.GetString(buffer[..markerIndex]);
        var lines = headerText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.TrimEnd('\r'))
            .ToArray();

        if (lines.Length == 0 || !string.Equals(lines[0], "ply", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Input is not a valid PLY file.");
        }

        var format = string.Empty;
        var vertexCount = -1;
        var vertexProperties = new List<PlyProperty>();
        var currentElement = string.Empty;

        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "comment":
                    continue;
                case "format":
                    if (parts.Length < 2)
                    {
                        throw new InvalidDataException("PLY format line is malformed.");
                    }

                    format = parts[1];
                    if (!string.Equals(format, "ascii", StringComparison.Ordinal) &&
                        !string.Equals(format, "binary_little_endian", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Unsupported PLY format '{format}'.");
                    }

                    break;
                case "element":
                    if (parts.Length < 3)
                    {
                        throw new InvalidDataException("PLY element line is malformed.");
                    }

                    currentElement = parts[1];
                    if (string.Equals(currentElement, "vertex", StringComparison.Ordinal))
                    {
                        vertexCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    }

                    break;
                case "property":
                    if (!string.Equals(currentElement, "vertex", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (parts.Length >= 2 && string.Equals(parts[1], "list", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("PLY vertex list properties are not supported.");
                    }

                    if (parts.Length < 3 || !IsSupportedPlyNumericType(parts[1]))
                    {
                        throw new InvalidDataException("PLY vertex property definition is malformed or unsupported.");
                    }

                    vertexProperties.Add(new PlyProperty(parts[2], parts[1]));
                    break;
            }
        }

        if (vertexCount < 0)
        {
            throw new InvalidDataException("PLY header did not define a vertex element.");
        }

        var xPropertyIndex = FindVertexPropertyIndex(vertexProperties, "x");
        var yPropertyIndex = FindVertexPropertyIndex(vertexProperties, "y");
        var zPropertyIndex = FindVertexPropertyIndex(vertexProperties, "z");

        return new PlyHeader(format, vertexCount, bodyOffset, vertexProperties, xPropertyIndex, yPropertyIndex, zPropertyIndex);
    }

    private static int FindVertexPropertyIndex(IReadOnlyList<PlyProperty> properties, string name)
    {
        var index = properties
            .Select((property, i) => new { property, i })
            .FirstOrDefault(x => string.Equals(x.property.Name, name, StringComparison.Ordinal))?.i ?? -1;
        if (index < 0)
        {
            throw new InvalidDataException($"PLY vertex element is missing required '{name}' property.");
        }

        return index;
    }

    private static bool IsSupportedPlyNumericType(string type)
        => type is "char" or "uchar" or "int8" or "uint8" or "short" or "ushort" or "int16" or "uint16"
            or "int" or "uint" or "int32" or "uint32" or "float" or "float32" or "double" or "float64";

    private static double ParseAsciiValue(string token, string type)
    {
        try
        {
            return type switch
            {
                "float" or "float32" or "double" or "float64" => double.Parse(token, CultureInfo.InvariantCulture),
                _ => long.Parse(token, CultureInfo.InvariantCulture)
            };
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"PLY numeric value '{token}' could not be parsed as {type}.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"PLY numeric value '{token}' overflowed {type}.", ex);
        }
    }

    private static double ReadBinaryValue(BinaryReader reader, string type)
        => type switch
        {
            "char" or "int8" => reader.ReadSByte(),
            "uchar" or "uint8" => reader.ReadByte(),
            "short" or "int16" => reader.ReadInt16(),
            "ushort" or "uint16" => reader.ReadUInt16(),
            "int" or "int32" => reader.ReadInt32(),
            "uint" or "uint32" => reader.ReadUInt32(),
            "float" or "float32" => reader.ReadSingle(),
            "double" or "float64" => reader.ReadDouble(),
            _ => throw new InvalidDataException($"Unsupported PLY numeric type '{type}'.")
        };

    private static byte[] ZipDirectory(string sourceDirectory)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            ZipFile.CreateFromDirectory(sourceDirectory, tempFile, CompressionLevel.SmallestSize, includeBaseDirectory: true);
            return File.ReadAllBytes(tempFile);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static async Task<byte[]?> TryCreateDenseVisibilityPackageAsync(string denseRoot, string denseSparseTextRoot, CancellationToken ct)
    {
        var camerasPath = Path.Combine(denseSparseTextRoot, "cameras.txt");
        var imagesPath = Path.Combine(denseSparseTextRoot, "images.txt");
        var depthMapsRoot = Path.Combine(denseRoot, "stereo", "depth_maps");
        if (!File.Exists(camerasPath) || !File.Exists(imagesPath) || !Directory.Exists(depthMapsRoot))
        {
            return null;
        }

        var depthMapFiles = Directory.GetFiles(depthMapsRoot, "*.bin", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (depthMapFiles.Length == 0)
        {
            return null;
        }

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddFileToArchiveAsync(archive, camerasPath, "sparse/cameras.txt", ct);
            await AddFileToArchiveAsync(archive, imagesPath, "sparse/images.txt", ct);

            foreach (var depthMapFile in depthMapFiles)
            {
                await AddFileToArchiveAsync(
                    archive,
                    depthMapFile,
                    $"stereo/depth_maps/{Path.GetFileName(depthMapFile)}",
                    ct);
            }
        }

        return memory.ToArray();
    }

    private static async Task AddFileToArchiveAsync(ZipArchive archive, string sourcePath, string entryPath, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Fastest);
        await using var sourceStream = File.OpenRead(sourcePath);
        await using var entryStream = entry.Open();
        await sourceStream.CopyToAsync(entryStream, ct);
    }

    private (string FileName, IReadOnlyCollection<string> Arguments) BuildOctreeBuildCommand(string inputPath, string outputPath, string resultJsonPath, string sceneId)
    {
        var buildArguments = new List<string>
        {
            "build",
            "--input", inputPath,
            "--output", outputPath,
            "--overwrite",
            "--write-sqlite",
            "--result-json", resultJsonPath,
            "--scene-id", sceneId
        };

        if (!string.IsNullOrWhiteSpace(_options.OctreeCliPath))
        {
            var cliPath = Path.GetFullPath(_options.OctreeCliPath);
            if (cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return ("dotnet", [cliPath, .. buildArguments]);
            }

            return (cliPath, buildArguments);
        }

        if (!string.IsNullOrWhiteSpace(_options.OctreeCliProjectPath))
        {
            return ("dotnet", ["run", "--project", Path.GetFullPath(_options.OctreeCliProjectPath), "--", .. buildArguments]);
        }

        throw new InvalidOperationException("Octree export requires either Recon:OctreeCliPath or Recon:OctreeCliProjectPath to be configured.");
    }

    private static JsonElement ReadSuccessfulOctreeAutomationResponse(string responsePath)
    {
        if (!File.Exists(responsePath))
        {
            throw new FileNotFoundException($"Octree build did not produce the expected automation response file '{responsePath}'.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(responsePath));
        var root = document.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            throw new InvalidOperationException($"Octree build failed: {message ?? "Unknown error."}");
        }

        return root;
    }

    private static string SerializeReport(PipelineStage stage, ColmapWorkspace workspace, IReadOnlyCollection<ProcessExecutionResult> commands, object extra)
        => JsonSerializer.Serialize(new
        {
            stage,
            workspace = workspace.WorkRoot,
            commands = commands.Select(x => new
            {
                fileName = x.FileName,
                arguments = x.Arguments,
                x.ExitCode,
                x.Duration,
                standardOutput = x.StandardOutput,
                standardError = x.StandardError
            }),
            extra
        }, ReconJson.Defaults);

    private static ColmapWorkspace PrepareWorkspace(string root)
    {
        var imagesDirectory = Path.Combine(root, "input", "images");
        var sparseRoot = Path.Combine(root, "sparse");
        var denseRoot = Path.Combine(root, "dense");
        Directory.CreateDirectory(imagesDirectory);
        Directory.CreateDirectory(sparseRoot);
        Directory.CreateDirectory(denseRoot);
        return new ColmapWorkspace(root, imagesDirectory, sparseRoot, denseRoot);
    }

    private sealed record ColmapWorkspace(string WorkRoot, string ImagesDirectory, string SparseRoot, string DenseRoot);
    private sealed record ResolvedArtifactContent(Artifact Artifact, byte[] Bytes);
    private sealed record DensePatchMatchResult(IReadOnlyCollection<ProcessExecutionResult> Commands, int? MaxImageSize);
    private sealed record PlyHeader(string Format, int VertexCount, int BodyOffset, IReadOnlyList<PlyProperty> VertexProperties, int XPropertyIndex, int YPropertyIndex, int ZPropertyIndex);
    private sealed record PlyProperty(string Name, string Type);
}
