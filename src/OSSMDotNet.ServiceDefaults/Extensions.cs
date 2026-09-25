using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Json;

namespace OSSMDotNet.ServiceDefaults;

/// <summary>
/// Configures shared Aspire service defaults and development health endpoints.
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
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        // Do not add a generic resilience handler: control operations must not be retried.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    /// <summary>
    /// Configures shared OpenTelemetry logging, metrics, tracing, and exporters.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureFileLogging();

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName,
                        "Microsoft.AspNetCore.SignalR.Client",
                        "Microsoft.AspNetCore.SignalR.Server")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health-check requests from traces.
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

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

    private static void ConfigureFileLogging<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        if (!builder.Configuration.GetValue("Logging:File:Enabled", true))
            return;

        var directory = builder.Configuration["Logging:File:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSSMDotNet", "logs");

        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Logging:File:Directory must be an absolute path.");

        Directory.CreateDirectory(directory);

        var fileLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.File(new JsonFormatter(renderMessage: true),
                Path.Combine(directory, $"{builder.Environment.ApplicationName}-.json"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7,
                retainedFileTimeLimit: TimeSpan.FromDays(7),
                shared: true)
            .CreateLogger();

        builder.Logging.AddSerilog(fileLogger, dispose: true);
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
