using System.Diagnostics;
using System.IO.Compression;
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

        commands.Add(await RunColmapRequiredAsync(
        [
            "patch_match_stereo",
            "--workspace_path", workspace.DenseRoot,
            "--workspace_format", "COLMAP",
            "--PatchMatchStereo.geom_consistency", "true"
        ], workspace.WorkRoot, ct));

        var fusedPath = Path.Combine(workspace.DenseRoot, "fused.ply");
        commands.Add(await RunColmapRequiredAsync(
        [
            "stereo_fusion",
            "--workspace_path", workspace.DenseRoot,
            "--workspace_format", "COLMAP",
            "--input_type", "geometric",
            "--output_path", fusedPath
        ], workspace.WorkRoot, ct));

        var bytes = await File.ReadAllBytesAsync(fusedPath, ct);
        var artifact = new PipelineArtifact("fused.ply", "application/octet-stream", bytes, ArtifactType.DensePointCloud, JsonSerializer.Serialize(new { format = "ply" }, ReconJson.Defaults));
        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Dense, workspace, commands, new { fusedPath, sparseArtifactRunId = sparseArtifact.Artifact.PipelineRunId }), [artifact]);
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

    private async Task<ProcessExecutionResult> RunRequiredProcessAsync(string fileName, IReadOnlyCollection<string> arguments, string workingDirectory, string toolName, CancellationToken ct)
    {
        logger.LogInformation("Running process {Command} {Arguments}", fileName, string.Join(' ', arguments));
        var result = await processRunner.RunAsync(fileName, arguments, workingDirectory, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} command failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
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
}
