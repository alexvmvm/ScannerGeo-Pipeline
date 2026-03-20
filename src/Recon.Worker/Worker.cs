using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recon.Infrastructure;
using Recon.Core;

namespace Recon.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, IOptions<ReconOptions> options) : BackgroundService
{
    private readonly TimeSpan _idleDelay = TimeSpan.FromSeconds(Math.Max(1, options.Value.WorkerIdlePollSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startupScope = scopeFactory.CreateScope())
        {
            var startupDbContext = startupScope.ServiceProvider.GetRequiredService<ReconDbContext>();
            if (startupDbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                await startupDbContext.Database.MigrateAsync(stoppingToken);
            }
            else
            {
                await startupDbContext.Database.EnsureCreatedAsync(stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<JobExecutionCoordinator>();
            var handled = await coordinator.ProcessNextAsync(stoppingToken);
            if (!handled)
            {
                await Task.Delay(_idleDelay, stoppingToken);
            }
        }
    }
}
