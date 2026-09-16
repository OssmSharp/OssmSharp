using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ServiceBoundary.Tests;

public sealed class ContentSecurityPolicyTests
{
    [Fact]
    public async Task ExplicitStyleHashesDoNotPermitArbitraryInlineStylesOrScripts()
    {
        var digest = Convert.ToBase64String(SHA256.HashData("known widget style"u8));
        await using var fixture = new AdminEndpointFixture([digest]);
        await fixture.StartAsync();
        using var response = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains($"style-src 'self' 'sha256-{digest}'", policy);
        Assert.Contains("script-src 'self';", policy);
        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
    }

    [Theory]
    [InlineData("'unsafe-inline'")]
    [InlineData("a\r\nb")]
    [InlineData("not-a-digest")]
    public async Task MalformedStyleHashesCannotInjectPolicyDirectives(string hash)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLocalBlazor();
        await using var app = builder.Build();
        Assert.Throws<ArgumentException>(() => app.UseLocalBoundary(5181, [hash]));
    }
}
