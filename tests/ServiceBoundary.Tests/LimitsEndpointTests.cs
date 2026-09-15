using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OSSMDotNet.DeviceControl;
using OSSMDotNet.Web.AdminService;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class LimitsEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LimitsEndpointKeepsReadyOrRejectsConflictWithoutPausing(bool conflict)
    {
        var bridge = new EndpointBridge();
        await using var fixture = new AdminEndpointFixture(bridge: bridge);
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        bridge.Publish();
        engine.Recover();
        var operations = fixture.Services.GetRequiredService<AdminOperations>();
        var invitation = operations.ReadInvitation();
        var guest = engine.Join(invitation.SessionId, new Uri(invitation.Url).Fragment["#invite=".Length..], "guest");
        engine.Approve(guest.ConnectionId);
        engine.Arm();
        var grant = engine.Snapshot.Grant!;
        var desired = DesiredState.Stopped with { Speed = 20 };
        engine.Submit(guest.SessionId, guest.GuestToken, guest.ConnectionId,
            new(2, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, desired));
        engine.Tick();
        engine.Tick();
        var before = engine.Snapshot;
        var logBefore = operations.ReadLog(0).Count;

        using var response = await fixture.Client.PostAsJsonAsync("/api/v1/admin/limits",
            new ControlLimits(conflict ? 10 : 50, 80), TestContext.Current.CancellationToken);

        var after = engine.Snapshot;
        Assert.Equal("Ready", after.Mode);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.AckSeq, after.AckSeq);
        Assert.Equal(before.Desired, after.Desired);
        Assert.Equal(before.Grant!.Epoch, after.Grant!.Epoch);
        Assert.Equal(1, bridge.Writes);
        Assert.Equal(0, bridge.Stops);
        if (conflict)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("limits-conflict", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(before, after);
            Assert.Equal(logBefore, operations.ReadLog(0).Count);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(new ControlLimits(50, 80), after.Limits);
            Assert.Equal(before.PolicyVersion + 1, after.PolicyVersion);
            Assert.Equal("Session limits applied.", operations.ReadLog(0).Last().Message);
        }
    }
}
