using Ingestion.Worker.Configuration;
using Ingestion.Worker.Health;
using Ingestion.Worker.OpenSky;
using Ingestion.Worker.Pipeline;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;

// A web host rather than a plain worker host, only so the worker can serve
// /health/live and /health/ready. It still has no other HTTP endpoints.
var builder = WebApplication.CreateBuilder(args);

// Local development only. In Docker and on EC2 the environment is populated
// from env_file / SSM, and there is no .env to find.
string? dotEnvPath = null;
if (builder.Environment.IsDevelopment())
{
    dotEnvPath = DotEnvLoader.Load();
}

builder.Configuration.AddAeroEnvironmentVariables(builder.Environment.IsDevelopment());

// Serilog: readable text locally, one JSON object per line everywhere else so
// log tools can filter on fields like RunId (PRD 8.1, P1.8).
builder.Services.AddSerilog(logging =>
{
    logging.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext();
    if (builder.Environment.IsDevelopment())
    {
        logging.WriteTo.Console();
    }
    else
    {
        logging.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

// --- Configuration, checked at startup so a missing value fails fast --------

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

builder.Services.AddSingleton(TimeProvider.System);

// --- OpenSky -----------------------------------------------------------------

// Token cache, auth handler, resilience pipeline and the client itself.
builder.Services.AddOpenSky();

// --- Database ----------------------------------------------------------------

// One shared connection pool for the writer and the health check.
builder.Services.AddSingleton(sp =>
    NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

// --- Pipeline: poller -> bounded buffer -> writer ----------------------------

builder.Services.AddSingleton<PipelineState>();
builder.Services.AddSingleton(sp => BatchBuffer.Create(
    sp.GetRequiredService<PipelineState>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(BatchBuffer))));

// Hosted services stop in REVERSE order of registration. The poller is
// registered last so it stops first and closes the buffer; the writer then
// flushes what is left before it stops (PRD 8.1 item 6).
builder.Services.AddSingleton<BatchWriter>();
builder.Services.AddHostedService<WriterService>();
builder.Services.AddHostedService<PollingService>();

builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

// --- Health ------------------------------------------------------------------

builder.Services.AddHealthChecks()
    .AddCheck<ReadinessHealthCheck>("pipeline", tags: ["ready"]);

var app = builder.Build();

// Live: the process answers HTTP. Runs no checks, so it never fails because
// of the database or OpenSky; restarting would not fix those.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

if (builder.Environment.IsDevelopment())
{
    if (dotEnvPath is null)
    {
        app.Logger.LogWarning(
            "No .env file found above {BaseDirectory} or {WorkingDirectory}; relying on the "
            + "ambient environment. Copy .env.example to .env at the repo root.",
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory());
    }
    else
    {
        app.Logger.LogInformation("Loaded environment from {DotEnvPath}", dotEnvPath);
    }
}

app.Run();
