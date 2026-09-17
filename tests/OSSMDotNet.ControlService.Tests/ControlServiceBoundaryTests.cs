using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OSSMDotNet.ControlService;
using OSSMDotNet.ControlService.Models;
using System.Diagnostics;

namespace OSSMDotNet.ControlService.Tests;

public sealed class ControlServiceBoundaryTests
{
    [Theory]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Port")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Forwarded-Prefix")]
    [InlineData("X-Original-For")]
    [InlineData("X-Original-Host")]
    [InlineData("X-Original-Proto")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Forwarded-Ssl")]
    public async Task EveryForwardedHeaderIsRejectedBeforeReachingTheService(string header)
    {
        using var services = CreateServices();
        var context = new DefaultHttpContext();
        context.Request.Headers[header] = "untrusted-forwarded-value";

        var reached = await InvokeAsync(services, context, new());

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("X-Frame-Options"));
    }

    [Fact]
    public async Task CertificateConfigurationRejectsDirectHttpButAllowsDirectHttps()
    {
        using var services = CreateServices();
        var options = new ControlServiceOptions { HttpsCertificatePath = @"C:\certificates\control-service.pfx" };
        var insecure = new DefaultHttpContext();

        var insecureReached = await InvokeAsync(services, insecure, options);

        Assert.False(insecureReached);
        Assert.Equal(StatusCodes.Status400BadRequest, insecure.Response.StatusCode);
        var secure = new DefaultHttpContext();
        secure.Request.IsHttps = true;

        var secureReached = await InvokeAsync(services, secure, options);

        Assert.True(secureReached);
        Assert.Equal(StatusCodes.Status204NoContent, secure.Response.StatusCode);
        Assert.Equal("no-store", Assert.Single(secure.Response.Headers.CacheControl));
        Assert.Equal("DENY", Assert.Single(secure.Response.Headers["X-Frame-Options"]));
    }

    [Fact]
    public async Task DirectHttpWithoutACertificateReachesTheServiceAndReceivesSecurityHeaders()
    {
        using var services = CreateServices();
        var context = new DefaultHttpContext();

        var reached = await InvokeAsync(services, context, new());

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("no-referrer", Assert.Single(context.Response.Headers["Referrer-Policy"]));
        Assert.Equal("nosniff", Assert.Single(context.Response.Headers["X-Content-Type-Options"]));
        Assert.Equal("DENY", Assert.Single(context.Response.Headers["X-Frame-Options"]));
    }

    private static ServiceProvider CreateServices() =>
        new ServiceCollection()
            .AddSingleton<DiagnosticListener>(static _ => new("ControlService.Tests"))
            .AddMetrics()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

    private static async Task<bool> InvokeAsync(
        IServiceProvider services, DefaultHttpContext context, ControlServiceOptions options)
    {
        var reached = false;
        var app = new ApplicationBuilder(services);
        app.UseControlServiceBoundary(options);
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
