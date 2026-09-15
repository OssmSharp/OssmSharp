using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class InvitationEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorEndRotatesInvitationButGuestLeaveRetainsIt(bool operatorEnd)
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var invitation = (await fixture.Client.GetFromJsonAsync<Invitation>(
            "/api/v1/admin/invitation", TestContext.Current.CancellationToken))!;
        var secret = new Uri(invitation.Url).Fragment["#invite=".Length..];
        var guest = engine.Join(invitation.SessionId, secret, "guest");

        if (operatorEnd)
        {
            using var response = await fixture.Client.PostAsync(
                "/api/v1/admin/end", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        else engine.Leave(guest.SessionId, guest.GuestToken, guest.ConnectionId);

        var next = (await fixture.Client.GetFromJsonAsync<Invitation>(
            "/api/v1/admin/invitation", TestContext.Current.CancellationToken))!;
        Assert.Equal("Paused", engine.Snapshot.Mode);
        Assert.Equal("Ended", engine.Snapshot.SessionMode);
        Assert.True(engine.Snapshot.OperationPending);
        Assert.Null(engine.Snapshot.Grant);
        Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(() =>
            engine.GuestSnapshot(guest.SessionId, guest.GuestToken, guest.ConnectionId)).Code);
        if (operatorEnd)
        {
            Assert.NotEqual(invitation.Url, next.Url);
            Assert.Equal("invitation-invalid", Assert.Throws<ControlRejectedException>(() =>
                engine.Join(invitation.SessionId, secret, "old-link")).Code);
        }
        else Assert.Equal(invitation, next);
        engine.Join(next.SessionId, new Uri(next.Url).Fragment["#invite=".Length..], "next");
        Assert.Equal("PendingApproval", engine.Snapshot.SessionMode);
        Assert.Null(engine.Snapshot.Grant);
    }

    [Fact]
    public async Task InvitationIsAutomaticallyAvailableAndCyclingRevokesTheGuestWithoutArming()
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;
        using var first = await fixture.Client.GetAsync("/api/v1/admin/invitation", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var invitation = (await first.Content.ReadFromJsonAsync<Invitation>(TestContext.Current.CancellationToken))!;
        Assert.Equal(before, engine.Snapshot);
        var guest = engine.Join(invitation.SessionId, new Uri(invitation.Url).Fragment["#invite=".Length..], "guest");
        using var cycle = await fixture.Client.PostAsync("/api/v1/admin/invitation/cycle", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cycle.StatusCode);
        var replacement = (await cycle.Content.ReadFromJsonAsync<Invitation>(TestContext.Current.CancellationToken))!;
        Assert.NotEqual(invitation.Url, replacement.Url);
        Assert.Equal("Paused", engine.Snapshot.Mode);
        Assert.Null(engine.Snapshot.Grant);
        Assert.True(engine.Snapshot.OperationPending);
        Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(() =>
            engine.GuestSnapshot(guest.SessionId, guest.GuestToken, guest.ConnectionId)).Code);
        Assert.Throws<ControlRejectedException>(() =>
            engine.Join(invitation.SessionId, new Uri(invitation.Url).Fragment["#invite=".Length..], "old-link"));
        Assert.Equal(replacement,
            await fixture.Client.GetFromJsonAsync<Invitation>("/api/v1/admin/invitation", TestContext.Current.CancellationToken));
        engine.Join(replacement.SessionId, new Uri(replacement.Url).Fragment["#invite=".Length..], "next");
        Assert.Equal("PendingApproval", engine.Snapshot.SessionMode);
        Assert.Null(engine.Snapshot.Grant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.invalid")]
    public async Task CyclingRequiresLocalOrigin(string? origin)
    {
        await using var fixture = new AdminEndpointFixture();
        await fixture.StartAsync();
        var engine = fixture.Services.GetRequiredService<PrototypeEngine>();
        var before = engine.Snapshot;
        fixture.Client.DefaultRequestHeaders.Remove("Origin");
        if (origin is not null) fixture.Client.DefaultRequestHeaders.Add("Origin", origin);

        using var response = await fixture.Client.PostAsync("/api/v1/admin/invitation/cycle", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, engine.Snapshot);
    }
}
