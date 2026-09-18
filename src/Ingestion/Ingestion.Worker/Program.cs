using Ingestion.Worker;
using Ingestion.Worker.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// Local development only. In Docker and on EC2 the environment is populated
// from env_file / SSM, and there is no .env to find.
string? dotEnvPath = null;
if (builder.Environment.IsDevelopment())
{
    dotEnvPath = DotEnvLoader.Load();
}

builder.Configuration.AddAeroEnvironmentVariables(builder.Environment.IsDevelopment());

builder.Services
    .AddOptions<OpenSkyOptions>()
    .Bind(builder.Configuration.GetSection(OpenSkyOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient(OpenSkyClientNames.Api, client =>
{
    client.BaseAddress = new Uri("https://opensky-network.org/api/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient(OpenSkyClientNames.Auth, client =>
{
    client.BaseAddress = new Uri("https://auth.opensky-network.org/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
if (builder.Environment.IsDevelopment())
{
    if (dotEnvPath is null)
    {
        startupLogger.LogWarning(
            "No .env file found above {BaseDirectory} or {WorkingDirectory}; relying on the "
            + "ambient environment. Copy .env.example to .env at the repo root.",
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory());
    }
    else
    {
        startupLogger.LogInformation("Loaded environment from {DotEnvPath}", dotEnvPath);
    }
}

host.Run();
