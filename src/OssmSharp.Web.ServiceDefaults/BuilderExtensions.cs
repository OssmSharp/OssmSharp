using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace OssmSharp.Web.ServiceDefaults;

/// <summary>
/// Configures web hosting defaults and development health endpoints.
/// </summary>
public static class BuilderExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Registers shared telemetry and health checks.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var telemetry = builder.Services.AddOpenTelemetry()
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

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            telemetry.UseOtlpExporter();

        builder.AddDefaultHealthChecks();

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

    /// <summary>
    /// Rejects non-loopback requests and disallowed origins, and sets response security headers.
    /// </summary>
    /// <param name="app">The local web application's middleware pipeline.</param>
    /// <param name="port">The port expected in the loopback host and origin.</param>
    /// <param name="styleHashes">Canonical Base64 SHA-256 hashes of inline styles permitted by the content security policy.</param>
    /// <exception cref="ArgumentException">A style hash is not a canonical Base64 SHA-256 digest.</exception>
    public static void UseLocalBoundary(this IApplicationBuilder app, int port, IReadOnlyList<string>? styleHashes = null)
    {
        var styles = new StringBuilder("'self'");
        Span<byte> digest = stackalloc byte[32];
        foreach (var hash in styleHashes ?? [])
        {
            if (hash is not { Length: 44 } || !Convert.TryFromBase64String(hash, digest, out var written) ||
                written != digest.Length || Convert.ToBase64String(digest) != hash)
                throw new ArgumentException("Only canonical SHA-256 style hashes are accepted.", nameof(styleHashes));
            styles.Append(" 'sha256-").Append(hash).Append('\'');
        }
        app.UseExceptionHandler();
        var host = $"127.0.0.1:{port}";
        var origin = $"http://{host}";
        var policy = $"default-src 'self'; script-src 'self'; style-src {styles}; connect-src 'self' ws://{host}; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
        app.Use(async (context, next) =>
        {
            if (!string.Equals(context.Request.Host.Value, host, StringComparison.OrdinalIgnoreCase) ||
                context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var suppliedOrigin = context.Request.Headers.Origin.ToString();
            var needsOrigin = context.WebSockets.IsWebSocketRequest ||
                context.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
            if ((needsOrigin || suppliedOrigin.Length > 0) && !suppliedOrigin.Equals(origin, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] = policy;
            await next(context);
        });
    }

}
