using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OssmSharp.Defaults;

namespace OssmSharp.Web.ServiceDefaults;

/// <summary>
/// Configures web hosting defaults and development health endpoints.
/// </summary>
public static class ServiceDefaultsExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Registers shared telemetry, health checks, and service discovery without automatic HTTP retries.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.AddHostDefaults();

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation())
            .WithTracing(tracing =>
            {
                tracing.AddSource("Microsoft.AspNetCore.SignalR.Client", "Microsoft.AspNetCore.SignalR.Server")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health-check requests from traces.
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    );
            });

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        // Do not add a generic resilience handler: control operations must not be retried.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    /// <summary>
    /// Registers readiness and liveness health checks.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Report the app live when it can respond to health checks.
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps readiness and liveness endpoints only in development.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The configured web application.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Exposing health endpoints outside development can reveal service state.
        // Review https://aka.ms/dotnet/aspire/healthchecks before enabling them.
        if (app.Environment.IsDevelopment())
        {
            // Readiness requires every registered health check to pass.
            app.MapHealthChecks(HealthEndpointPath);

            // Liveness requires health checks tagged "live" to pass.
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
