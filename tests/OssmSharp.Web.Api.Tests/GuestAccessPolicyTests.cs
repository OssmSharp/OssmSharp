using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Web.ServiceDefaults;
using OssmSharp.Web.ServiceDefaults.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class GuestAccessPolicyTests
{
    private const string LocalOrigin = "http://127.0.0.1:5001";

    [Theory]
    [InlineData("", true)]
    [InlineData("https://guest.example", true)]
    [InlineData("https://guest.example:8443/", true)]
    [InlineData("http://guest.example", false)]
    [InlineData("https://guest.example/join", false)]
    [InlineData("https://guest.example/?invite=secret", false)]
    [InlineData("https://guest.example/#invite=secret", false)]
    [InlineData("https://user:password@guest.example", false)]
    [InlineData("https://*.example", false)]
    [InlineData(" ", false)]
    public void HostnameMustBeAnExactHttpsOrigin(string origin, bool valid)
    {
        var options = new GuestOptions { GuestPort = 5001, Hostname = origin };
        Assert.Equal(valid, Validator.TryValidateObject(options, new(options), [], validateAllProperties: true));
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Extra:Url", "http://0.0.0.0:5002")]
    [InlineData("preferHostingUrls", "true")]
    public void GuestListenerOverridesFailValidation(string key, string value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:Guest:GuestPort"] = "5001",
            [key] = value,
        });
        builder.Services.AddGuestConfiguration();
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GuestOptions>>().Value);
    }

    [Theory]
    [InlineData("guest.example", null, "https", "GET", null, 200)]
    [InlineData("guest.example", "https://guest.example", "https", "POST", null, 200)]
    [InlineData("guest.example", null, "https", "POST", null, 403)]
    [InlineData("guest.example", "https://evil.example", "https", "POST", null, 403)]
    [InlineData("guest.example", "null", "https", "POST", null, 403)]
    [InlineData("guest.example", "https://guest.example, https://evil.example", "https", "POST", null, 403)]
    [InlineData("guest.example", null, null, "GET", null, 403)]
    [InlineData("guest.example", null, "http", "GET", null, 403)]
    [InlineData("guest.example:444", null, "https", "GET", null, 403)]
    [InlineData("evil.example", "https://guest.example", "https", "GET", null, 403)]
    [InlineData("guest.example", null, "https", "GET", "198.51.100.10", 403)]
    [InlineData("local", null, null, "GET", null, 200)]
    [InlineData("local", "local", null, "POST", null, 200)]
    [InlineData("local", "https://guest.example", "https", "POST", null, 403)]
    [InlineData("local", null, null, "POST", null, 403)]
    [InlineData("local", "https://evil.example", null, "GET", null, 403)]
    [Trait("Category", "Integration")]
    public async Task AccessPolicyMatchesHostOriginProtocolAndPeer(
        string host, string? origin, string? protocol, string method, string? peer, int expectedStatus)
    {
        await using var app = await StartAccessPolicyAsync("https://guest.example");
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), app.Urls.Single() + "/test");
        request.Headers.Host = host == "local" ? new Uri(LocalOrigin).Authority : host;
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin == "local" ? LocalOrigin : origin);
        if (protocol is not null)
            request.Headers.Add("X-Forwarded-Proto", protocol);
        request.Headers.Add("X-Forwarded-Host", "guest.example");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        if (peer is not null)
            request.Headers.Add("X-Test-Peer", peer);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedStatus == 200)
        {
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Contains(
                host == "local" ? $"ws://{request.Headers.Host}" : "wss://guest.example",
                response.Headers.GetValues("Content-Security-Policy").Single(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LocalOnlyModeRejectsPublicHostEvenWithForwardedHeaders()
    {
        await using var app = await StartAccessPolicyAsync(string.Empty);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + "/test");
        request.Headers.Host = "guest.example";
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<WebApplication> StartAccessPolicyAsync(string publicOrigin)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["AllowedHosts"] = "*";
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Test-Peer"].FirstOrDefault() is { } peer)
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            await next(context);
        });
        app.UseWebSockets();
        app.UseGuestAccessPolicy(new() { GuestPort = 5001, Hostname = publicOrigin });
        app.MapMethods("/test", ["GET", "POST"], () => Results.Ok());
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
