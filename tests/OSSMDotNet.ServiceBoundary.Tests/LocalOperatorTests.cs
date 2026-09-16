using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OSSMDotNet.ControlCore;
using OSSMDotNet.Cli;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ServiceBoundary.Tests;

public sealed class LocalOperatorTests
{
    [Fact]
    public async Task OperatorEndConsumesInvitationRetriesAndPublishesAReplacement()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var attemptId = Guid.NewGuid();

        using var first = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replay = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var end = await fixture.Client.PostAsync("/api/v1/admin/end", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, end.StatusCode);
        using var afterEnd = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, afterEnd.StatusCode);
        Assert.Contains("attempt-consumed", await afterEnd.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var replacement = await fixture.Client.GetAsync("/api/v1/admin/invitation", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        Assert.NotEqual(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await replacement.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var cycle = await fixture.Client.PostAsync("/api/v1/admin/invitation/cycle", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cycle.StatusCode);
        using var consumed = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, consumed.StatusCode);
        Assert.Contains("attempt-consumed", await consumed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public async Task CliNeedsNoCredentialInput(string operation)
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();

        var result = await AdminCommand.Create()
            .Parse([operation, "--admin-origin", fixture.Client.BaseAddress!.GetLeftPart(UriPartial.Authority)])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.invalid")]
    public async Task MissingOrForeignOriginCannotMutateControl(string? origin)
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;
        fixture.Client.DefaultRequestHeaders.Remove("Origin");
        if (origin is not null) fixture.Client.DefaultRequestHeaders.Add("Origin", origin);

        using var rejected = await fixture.Client.PostAsJsonAsync("/api/v1/admin/limits",
            new { speedCap = 20, maximumDepth = 60 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(before.Limits, engine.Snapshot.Limits);
        Assert.Equal(before.PolicyVersion, engine.Snapshot.PolicyVersion);
    }

    [Fact]
    public async Task ForeignHostIsRejectedEvenWithTheCorrectOrigin()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        fixture.Client.DefaultRequestHeaders.Host = "127.0.0.1:1";

        using var response = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonLoopbackRemoteAddressIsRejectedBeforeTheEndpointRuns()
    {
        var application = WebApplication.CreateBuilder();
        application.Services.AddProblemDetails();
        await using var host = application.Build();
        var builder = new ApplicationBuilder(host.Services);
        var reachedEndpoint = false;
        builder.UseLocalBoundary(5181);
        builder.Run(context =>
        {
            reachedEndpoint = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = host.Services };
        context.Request.Host = new HostString("127.0.0.1", 5181);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");

        await builder.Build()(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(reachedEndpoint);
    }

    [Theory]
    [InlineData("invitation/cycle")]
    [InlineData("approve")]
    [InlineData("limits")]
    [InlineData("recover")]
    [InlineData("arm")]
    [InlineData("stop")]
    [InlineData("end")]
    [InlineData("unlock")]
    [InlineData("simulator/disconnect")]
    public async Task AdministrativeMutationsRejectGetAndAdvertiseOnlyPost(string operation)
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;

        using var response = await fixture.Client.GetAsync($"/api/v1/admin/{operation}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("POST", Assert.Single(response.Content.Headers.Allow));
        Assert.Equal(before, engine.Snapshot);
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5184")]
    [InlineData("urls", "http://[::]:5184")]
    [InlineData("urls", "http://192.0.2.1:5184")]
    [InlineData("http_ports", "5184")]
    public async Task HostingUrlOverridesCannotReplaceTheOwnedLoopbackListener(string key, string value)
    {
        await using var fixture = new AdminEndpointFixture(configuration: new Dictionary<string, string?> { [key] = value });
        await fixture.StartAsync();

        Assert.Equal(fixture.Client.BaseAddress!.GetLeftPart(UriPartial.Authority), Assert.Single(fixture.Addresses));
        using var status = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }
}
