using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Json;

namespace OssmSharp.Defaults;

/// <summary>
/// Configures host-neutral logging and telemetry.
/// </summary>
public static class HostDefaultsExtensions
{
    /// <summary>
    /// Registers file logging, OpenTelemetry logging, runtime and HTTP instrumentation, and optional OTLP export.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder AddHostDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
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
                metrics.AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddHttpClientInstrumentation();
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
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
}
