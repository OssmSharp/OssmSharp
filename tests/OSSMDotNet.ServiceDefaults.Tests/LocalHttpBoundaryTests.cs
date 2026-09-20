using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OSSMDotNet.ServiceDefaults.Tests;

public sealed class LocalHttpBoundaryTests
{
    private const int Port = 5182;
    private const string StyleHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public async Task ExactLoopbackHostAndOriginReachTheApplicationWithCanonicalSecurityHeaders()
    {
        using var services = CreateServices();
        var context = CreateRequest(HttpMethods.Get, $"http://127.0.0.1:{Port}");

        var reached = await InvokeAsync(services, context, [StyleHash]);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("no-referrer", Assert.Single(context.Response.Headers["Referrer-Policy"]));
        Assert.Equal("nosniff", Assert.Single(context.Response.Headers["X-Content-Type-Options"]));
        Assert.Equal("DENY", Assert.Single(context.Response.Headers["X-Frame-Options"]));
        Assert.Equal("no-store", Assert.Single(context.Response.Headers.CacheControl));
        Assert.Equal(
            $"default-src 'self'; script-src 'self'; style-src 'self' 'sha256-{StyleHash}'; connect-src 'self' ws://127.0.0.1:{Port}; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
            Assert.Single(context.Response.Headers["Content-Security-Policy"]));
    }

    [Theory]
    [InlineData("remote", "GET", null)]
    [InlineData("foreign-host", "GET", null)]
    [InlineData("missing-origin", "POST", null)]
    [InlineData("foreign-origin", "POST", "https://example.invalid")]
    public async Task RemoteOrUntrustedRequestsAreRejectedBeforeTheApplication(
        string caseName, string method, string? origin)
    {
        using var services = CreateServices();
        var context = CreateRequest(method, origin);
        if (caseName == "remote")
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.25");
        else if (caseName == "foreign-host")
            context.Request.Host = new HostString("localhost", Port);

        var reached = await InvokeAsync(services, context);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void NonCanonicalStyleHashesAreRejectedAtBoundaryRegistration(string styleHash)
    {
        using var services = CreateServices();
        var app = new ApplicationBuilder(services);

        var exception = Assert.Throws<ArgumentException>(() => app.UseLocalBoundary(Port, [styleHash]));

        Assert.Equal("styleHashes", exception.ParamName);
        Assert.Contains("canonical SHA-256", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateServices() =>
        new ServiceCollection()
            .AddSingleton<DiagnosticListener>(static _ => new("ServiceDefaults.Tests"))
            .AddMetrics()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

    private static DefaultHttpContext CreateRequest(string method, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Host = new HostString("127.0.0.1", Port);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        return context;
    }

    private static async Task<bool> InvokeAsync(
        IServiceProvider services, DefaultHttpContext context, IReadOnlyList<string>? styleHashes = null)
    {
        var reached = false;
        var app = new ApplicationBuilder(services);
        app.UseLocalBoundary(Port, styleHashes);
        app.Run(current =>
        {
            reached = true;
            current.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        await app.Build()(context);
        return reached;
    }
}
