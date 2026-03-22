using Microsoft.Extensions.Options;
using Recon.Core;

namespace Recon.Infrastructure;

public sealed class ColmapRuntimeValidator(IOptions<ReconOptions> options, IProcessRunner processRunner)
{
    private readonly ReconOptions _options = options.Value;

    public async Task ValidateAsync(CancellationToken ct)
    {
        if (!string.Equals(_options.PipelineProvider, "Colmap", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProcessExecutionResult result;
        try
        {
            var workingDirectory = Path.Combine(_options.ScratchRootPath, "startup-validation");
            result = await processRunner.RunAsync(_options.ColmapBinaryPath, ["--help"], workingDirectory, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"COLMAP mode is enabled, but '{_options.ColmapBinaryPath}' could not be started. Install COLMAP or set Recon:PipelineProvider to 'Simulated'.",
                ex);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"COLMAP mode is enabled, but '{_options.ColmapBinaryPath} --help' failed with exit code {result.ExitCode}. {result.StandardError}".Trim());
        }
    }
}
