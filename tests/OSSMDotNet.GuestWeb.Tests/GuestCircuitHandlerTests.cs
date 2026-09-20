using OSSMDotNet.GuestWeb.Services;

namespace OSSMDotNet.GuestWeb.Tests;

public sealed class GuestCircuitHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionLifecycleDisposesTheJoinedClientAndBlocksLaterGuestAuthority(bool connectionDown)
    {
        using var cancellationSource = CreateTimeout();
        await using var fixture = await PageControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        var stateCallbacks = 0;
        await client.JoinAsync(
            new(
                _ =>
                {
                    Interlocked.Increment(ref stateCallbacks);
                    return Task.CompletedTask;
                },
                static _ => Task.CompletedTask),
            new GuestJoinRequest("join-1", "session-1", "proof-1"),
            cancellationSource.Token);
        var handler = new GuestCircuitHandler(client);

        if (connectionDown)
            await handler.OnConnectionDownAsync(null!, cancellationSource.Token);
        else
            await handler.OnCircuitClosedAsync(null!, cancellationSource.Token);

        await fixture.WaitForDisconnectionAsync(cancellationSource.Token);
        await fixture.SendGuestStatusAsync(PageControlHubFixture.ReadyGuestStatus(1), cancellationSource.Token);
        var exception = await Assert.ThrowsAsync<ControlClientException>(() =>
            client.SubmitAsync(new GuestInputRequest("input-1",
                new GuestInput(2, "epoch-1", 7, "grant-1", 1, true, new(0, 80, 60, 60, 50))),
                cancellationSource.Token));

        Assert.Equal("guest-not-joined", exception.Code);
        Assert.Equal(0, stateCallbacks);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
        Assert.Equal(1, fixture.DisconnectionCount);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
