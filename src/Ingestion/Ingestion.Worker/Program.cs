using Ingestion.Worker;

DotNetEnv.Env.Load("../../../.env"); // adjust relative path to repo root .env
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();