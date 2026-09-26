using OssmSharp.Web.Application.Models;

namespace OSSMDotNet.ApiService.Hosting;

/// <summary>
/// Applies direct-service protections without trusting browser or reverse-proxy headers.
/// </summary>
public static class ApiBoundary
{
    /// <summary>
    /// Rejects forwarded headers and non-HTTPS traffic when TLS is configured, and sets no-store security headers.
    /// </summary>
    /// <param name="app">The request pipeline to protect.</param>
    /// <param name="options">The validated listener configuration.</param>
    /// <exception cref="ArgumentNullException">The pipeline or configuration is null.</exception>
    public static void UseApiBoundary(this IApplicationBuilder app, ControlServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (HasForwardedHeaders(context.Request.Headers) ||
                (options.HttpsCertificatePath.Length > 0 && !context.Request.IsHttps))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
    }

    private static bool HasForwardedHeaders(IHeaderDictionary headers) =>
        headers.ContainsKey("Forwarded") ||
        headers.ContainsKey("X-Forwarded-For") ||
        headers.ContainsKey("X-Forwarded-Host") ||
        headers.ContainsKey("X-Forwarded-Port") ||
        headers.ContainsKey("X-Forwarded-Proto") ||
        headers.ContainsKey("X-Forwarded-Prefix") ||
        headers.ContainsKey("X-Original-For") ||
        headers.ContainsKey("X-Original-Host") ||
        headers.ContainsKey("X-Original-Proto") ||
        headers.ContainsKey("X-Real-IP") ||
        headers.ContainsKey("X-Forwarded-Ssl");
}
