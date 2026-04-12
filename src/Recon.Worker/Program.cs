using Recon.Core;
using Recon.Infrastructure;
using Recon.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
AddSharedReconConfig(builder.Configuration, builder.Environment.ContentRootPath, builder.Environment.EnvironmentName);

builder.Services.Configure<ReconOptions>(builder.Configuration.GetSection("Recon"));
builder.Services.AddReconInfrastructure(builder.Configuration);
builder.Services.AddScoped<ProjectStatusService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ImageIntakeService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<RunService>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<JobExecutionCoordinator>();
builder.Services.AddHostedService<Worker>();

builder.Services.AddSerilog((services, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var host = builder.Build();
await host.RunAsync();

static void AddSharedReconConfig(ConfigurationManager configuration, string contentRootPath, string environmentName)
{
    var solutionRoot = Path.GetFullPath(Path.Combine(contentRootPath, "..", ".."));
    var defaultOctreeProjectPath = Path.Combine(solutionRoot, "external", "ScannerGeo-Octree", "src", "OctreeBuild.Cli", "OctreeBuild.Cli.csproj");
    configuration.AddJsonFile("reconsettings.json", optional: true, reloadOnChange: true);
    configuration.AddJsonFile($"reconsettings.{environmentName}.json", optional: true, reloadOnChange: true);
    configuration.AddJsonFile(Path.Combine(solutionRoot, "reconsettings.json"), optional: true, reloadOnChange: true);
    configuration.AddJsonFile(Path.Combine(solutionRoot, $"reconsettings.{environmentName}.json"), optional: true, reloadOnChange: true);
    if (File.Exists(defaultOctreeProjectPath))
    {
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recon:OctreeCliProjectPath"] = defaultOctreeProjectPath
        });
    }

    configuration.AddEnvironmentVariables();
}
