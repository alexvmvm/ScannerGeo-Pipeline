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
        var command = await RunRequiredAsync([ "--help" ], workspace.WorkRoot, ct);
        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Inspect, workspace, [command], new { imageCount = context.Images.Count }), []);
    }

    public async Task<PipelineExecutionResult> RunSparseAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        await DownloadProjectImagesAsync(context.Images, workspace.ImagesDirectory, ct);

        var commands = new List<ProcessExecutionResult>();
        var databasePath = Path.Combine(workspace.WorkRoot, "database.db");
        commands.Add(await RunRequiredAsync(
        [
            "feature_extractor",
            "--database_path", databasePath,
            "--image_path", workspace.ImagesDirectory,
            "--SiftExtraction.use_gpu", _options.ColmapUseGpu ? "1" : "0"
        ], workspace.WorkRoot, ct));

        var matcherCommand = ResolveMatcherCommand(context.Project.ConfigJson);
        commands.Add(await RunRequiredAsync(
        [
            matcherCommand,
            "--database_path", databasePath,
            "--SiftMatching.use_gpu", _options.ColmapUseGpu ? "1" : "0"
        ], workspace.WorkRoot, ct));

        commands.Add(await RunRequiredAsync(
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
        var sparseZip = await GetArtifactBytesAsync(context.Run.Id, ArtifactType.SparseModel, ct);
        var sparseModelDirectory = ExtractSparseModel(workspace, sparseZip);

        var commands = new List<ProcessExecutionResult>();
        commands.Add(await RunRequiredAsync(
        [
            "image_undistorter",
            "--image_path", workspace.ImagesDirectory,
            "--input_path", sparseModelDirectory,
            "--output_path", workspace.DenseRoot,
            "--output_type", "COLMAP"
        ], workspace.WorkRoot, ct));

        commands.Add(await RunRequiredAsync(
        [
            "patch_match_stereo",
            "--workspace_path", workspace.DenseRoot,
            "--workspace_format", "COLMAP",
            "--PatchMatchStereo.geom_consistency", "true"
        ], workspace.WorkRoot, ct));

        var fusedPath = Path.Combine(workspace.DenseRoot, "fused.ply");
        commands.Add(await RunRequiredAsync(
        [
            "stereo_fusion",
            "--workspace_path", workspace.DenseRoot,
            "--workspace_format", "COLMAP",
            "--input_type", "geometric",
            "--output_path", fusedPath
        ], workspace.WorkRoot, ct));

        var bytes = await File.ReadAllBytesAsync(fusedPath, ct);
        var artifact = new PipelineArtifact("fused.ply", "application/octet-stream", bytes, ArtifactType.DensePointCloud, JsonSerializer.Serialize(new { format = "ply" }, ReconJson.Defaults));
        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Dense, workspace, commands, new { fusedPath }), [artifact]);
    }

    public async Task<PipelineExecutionResult> RunExportAsync(ProjectPipelineContext context, CancellationToken ct)
    {
        var workspace = PrepareWorkspace(context.WorkingDirectory);
        var sparseZip = await GetArtifactBytesAsync(context.Run.Id, ArtifactType.SparseModel, ct);
        var sparseModelDirectory = ExtractSparseModel(workspace, sparseZip);
        var exportRoot = Path.Combine(workspace.WorkRoot, "export");
        Directory.CreateDirectory(exportRoot);

        var commands = new List<ProcessExecutionResult>
        {
            await RunRequiredAsync(
            [
                "model_converter",
                "--input_path", sparseModelDirectory,
                "--output_path", exportRoot,
                "--output_type", "TXT"
            ], workspace.WorkRoot, ct)
        };

        var denseBytes = await TryGetArtifactBytesAsync(context.Run.Id, ArtifactType.DensePointCloud, ct);
        if (denseBytes is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(exportRoot, "fused.ply"), denseBytes, ct);
        }

        var bundleBytes = ZipDirectory(exportRoot);
        var artifact = new PipelineArtifact("export-package.zip", "application/zip", bundleBytes, ArtifactType.OctreePackage, JsonSerializer.Serialize(new { format = "colmap-text-export" }, ReconJson.Defaults));
        return new PipelineExecutionResult(true, SerializeReport(PipelineStage.Export, workspace, commands, new { exportRoot }), [artifact]);
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

    private async Task<byte[]> GetArtifactBytesAsync(Guid runId, ArtifactType type, CancellationToken ct)
        => await TryGetArtifactBytesAsync(runId, type, ct)
            ?? throw new FileNotFoundException($"Artifact '{type}' for run '{runId}' was not found.");

    private async Task<byte[]?> TryGetArtifactBytesAsync(Guid runId, ArtifactType type, CancellationToken ct)
    {
        var artifact = (await dbContext.Artifacts.AsNoTracking()
            .Where(x => x.PipelineRunId == runId && x.Type == type && x.Status == ArtifactStatus.Available)
            .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAtUtc)
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
        return memory.ToArray();
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

    private async Task<ProcessExecutionResult> RunRequiredAsync(IReadOnlyCollection<string> arguments, string workingDirectory, CancellationToken ct)
    {
        logger.LogInformation("Running COLMAP command {Command} {Arguments}", _options.ColmapBinaryPath, string.Join(' ', arguments));
        var result = await processRunner.RunAsync(_options.ColmapBinaryPath, arguments, workingDirectory, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"COLMAP command failed with exit code {result.ExitCode}: {result.StandardError}");
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
}
