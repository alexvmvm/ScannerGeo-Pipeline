using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Recon.Core;
using Recon.Infrastructure;

namespace Recon.Api.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    public string StorageRootPath { get; } = Path.Combine(Path.GetTempPath(), "recon-api-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ReconDbContext>>();
            services.RemoveAll<ReconDbContext>();
            services.RemoveAll<IReconDbContext>();

            services.AddDbContext<ReconDbContext>(options => options.UseSqlite(_connection));
            services.AddScoped<IReconDbContext>(sp => sp.GetRequiredService<ReconDbContext>());
            services.PostConfigure<ReconOptions>(options =>
            {
                options.ObjectStorageProvider = "FileSystem";
                options.StorageRootPath = StorageRootPath;
                options.MinimumValidImageCount = 3;
            });
        });
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(StorageRootPath);
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(StorageRootPath))
        {
            Directory.Delete(StorageRootPath, recursive: true);
        }
    }
}
