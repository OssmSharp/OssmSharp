using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OSSMDotNet.DeviceBridge;
using OSSMDotNet.Models;
using OSSMDotNet.AdminService;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ServiceDefaults;

namespace OSSMDotNet.ServiceBoundary.Tests;

public sealed class AdminEndpointTests
{
    [Fact]
    public async Task LiveLogReadsNeedNoCredentialsAndDoNotExpire()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var operations = fixture.Services.GetRequiredService<AdminOperations>();
        operations.Stop();
        var entry = Assert.Single(operations.ReadLog(0));
        Assert.Equal("Stop requested.", entry.Message);
        Assert.Empty(operations.ReadLog(entry.Sequence));
        fixture.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(entry, Assert.Single(operations.ReadLog(0)));
    }

    [Fact]
    public async Task StatusAndStopNeedNoCredentialsEvenAfterThirtyMinutes()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();

        using var status = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        fixture.Time.Advance(TimeSpan.FromMinutes(31));
        using var later = await fixture.Client.GetAsync("/api/v1/admin/status", TestContext.Current.CancellationToken);
        using var stop = await fixture.Client.PostAsync("/api/v1/admin/stop", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        Assert.Null(engine.Snapshot.Grant);
        Assert.False(engine.Snapshot.DeviceEnabled);
    }

    [Fact]
    public async Task ApiAndBlazorShareTheSingletonControlRegistrationAndInvitationIntent()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        Assert.Same(fixture.Services.GetRequiredService<SimulatorProcessBridge>(),
            fixture.Services.GetRequiredService<IDeviceBridge>());
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;
        var attemptId = Guid.NewGuid();
        var operations = fixture.Services.GetRequiredService<AdminOperations>();

        using var first = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        using var replay = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(await first.Content.ReadFromJsonAsync<Invitation>(TestContext.Current.CancellationToken),
            operations.Generate(attemptId));
        var options = fixture.Services.GetRequiredService<IOptions<AdminOptions>>().Value;
        Assert.Equal(operations.Generate(attemptId),
            engine.CreateInvitation(attemptId, new Uri(options.GuestOrigin), options.PipeName));
        Assert.Equal(before.Mode, engine.Snapshot.Mode);
        Assert.Equal(before.Generation, engine.Snapshot.Generation);

        using var nextRead = await fixture.Client.PostAsJsonAsync("/api/v1/admin/invitation", new { attemptId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await nextRead.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownPayloadFieldsAndForeignOriginsCannotMutateControl()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
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
