using Ingestion.Worker.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ingestion.Worker.OpenSky;

/// <summary>
/// Registers everything needed to call OpenSky. One method used by both
/// Program.cs and the tests, so the tests exercise the real handler chain.
/// </summary>
public static class OpenSkyServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSky(this IServiceCollection services)
    {
        // TryAdd: a test that registered its own (faster) settings or clock keeps them.
        services.TryAddSingleton(OpenSkyResilienceSettings.Default);
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<OpenSkyTokenProvider>();
        services.AddSingleton<OpenSkyClient>();
        services.AddTransient<OpenSkyAuthHandler>();

        services.AddHttpClient(OpenSkyClientNames.Auth, (sp, client) =>
        {
            client.BaseAddress = sp.GetRequiredService<IOptions<OpenSkyOptions>>().Value.AuthBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        var api = services.AddHttpClient(OpenSkyClientNames.Api, (sp, client) =>
        {
            client.BaseAddress = sp.GetRequiredService<IOptions<OpenSkyOptions>>().Value.ApiBaseUrl;
            // The resilience pipeline owns timeouts (10 s per attempt). This outer
            // limit only has to be longer than all attempts plus their backoff.
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        // Handlers run in the order added, outermost first: resilience wraps auth,
        // so every retry passes through the auth handler and gets a valid token.
        api.AddResilienceHandler(OpenSkyResilience.PipelineName, (pipeline, context) =>
            OpenSkyResilience.Configure(pipeline, context.ServiceProvider.GetRequiredService<OpenSkyResilienceSettings>()));
        api.AddHttpMessageHandler<OpenSkyAuthHandler>();

        return services;
    }
}
