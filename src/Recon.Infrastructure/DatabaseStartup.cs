using Microsoft.EntityFrameworkCore;

namespace Recon.Infrastructure;

public static class DatabaseStartup
{
    private const long MigrationLockId = 60340122017431411;

    public static async Task InitializeAsync(ReconDbContext dbContext, CancellationToken ct = default)
    {
        if (dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await InitializePostgresAsync(dbContext, ct);
            return;
        }

        await dbContext.Database.EnsureCreatedAsync(ct);
    }

    private static async Task InitializePostgresAsync(ReconDbContext dbContext, CancellationToken ct)
    {
        await dbContext.Database.OpenConnectionAsync(ct);
        var lockHeld = false;

        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var lockCommand = connection.CreateCommand();
            lockCommand.CommandText = $"SELECT pg_advisory_lock({MigrationLockId})";
            await lockCommand.ExecuteScalarAsync(ct);
            lockHeld = true;

            await dbContext.Database.MigrateAsync(ct);
        }
        finally
        {
            try
            {
                if (lockHeld)
                {
                    var connection = dbContext.Database.GetDbConnection();
                    await using var unlockCommand = connection.CreateCommand();
                    unlockCommand.CommandText = $"SELECT pg_advisory_unlock({MigrationLockId})";
                    await unlockCommand.ExecuteScalarAsync(ct);
                }
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }
}
