using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OSSMDotNet.Web.HostRuntime;

public static class LocalHttpBoundary
{
    public static void UseLocalBoundary(this WebApplication app, int port)
    {
        app.UseExceptionHandler();
        var host = $"127.0.0.1:{port}";
        var origin = $"http://{host}";
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
            context.Response.Headers["Content-Security-Policy"] =
                $"default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws://{host}; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
            await next(context);
        });
    }
}
