using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace OSSMDotNet.ServiceDefaults;

/// <summary>Applies loopback, origin, and content-security-policy protections to a local web host.</summary>
public static class LocalHttpBoundary
{
    /// <summary>Configures the local HTTP boundary for the specified loopback port and allowed style hashes.</summary>
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
