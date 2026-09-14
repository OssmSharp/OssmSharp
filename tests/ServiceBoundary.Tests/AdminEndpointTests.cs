using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OSSMDotNet.DeviceControl;
using OSSMDotNet.Simulation;
using OSSMDotNet.Web.AdminService;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class AdminEndpointTests
{
    [Fact]
    public async Task LiveLogReadsRequireTheOperatorCapability()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var operations = fixture.Services.GetRequiredService<AdminOperations>();
        Assert.Throws<ControlRejectedException>(() => operations.ReadLog(fixture.ForwardingToken, 0));
        operations.Stop(fixture.Token);
        var entry = Assert.Single(operations.ReadLog(fixture.Token, 0));
        Assert.Equal("Stop requested.", entry.Message);
        Assert.Empty(operations.ReadLog(fixture.Token, entry.Sequence));
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Throws<ControlRejectedException>(() => operations.ReadLog(fixture.Token, 0));
    }

    [Fact]
    public async Task AdminRequiresOperatorProofAndNeverAcceptsTheForwardingCredential()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        using var anonymous = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        fixture.Authorize(fixture.ForwardingToken);
        using var guestService = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, guestService.StatusCode);

        fixture.Authorize();
        using var authenticated = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        using var expired = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task ApiAndBlazorShareTheSingletonControlRegistrationAndInvitationIntent()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        fixture.Authorize();
        Assert.Same(fixture.Services.GetRequiredService<SimulatorProcessBridge>(),
            fixture.Services.GetRequiredService<IDeviceBridge>());
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;
        var attemptId = Guid.NewGuid();

        using var first = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        using var replay = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before.Mode, engine.Snapshot.Mode);
        Assert.Equal(before.Generation, engine.Snapshot.Generation);

        using var conflicting = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
    }

    [Fact]
    public async Task UnknownPayloadFieldsAndForeignOriginsCannotMutateControl()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        fixture.Authorize();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;
        fixture.Client.DefaultRequestHeaders.Add("X-Test-Secret", "do-not-echo-this-marker");

        using var malformed = await fixture.Client.PostAsJsonAsync("/api/v1/admin/limits",
            new { speedCap = 20, maximumDepth = 60, arm = true }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain("do-not-echo-this-marker", await malformed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        fixture.Client.DefaultRequestHeaders.Remove("Origin");
        fixture.Client.DefaultRequestHeaders.Add("Origin", "https://example.invalid");
        using var foreign = await fixture.Client.PostAsJsonAsync("/api/v1/admin/limits",
            new { speedCap = 20, maximumDepth = 60 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(before.PolicyVersion, engine.Snapshot.PolicyVersion);
        Assert.Equal(before.Limits, engine.Snapshot.Limits);
    }
}
