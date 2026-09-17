using OSSMDotNet.ControlClient;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient.Tests;

public sealed class GuestControlClientTests
{
    [Fact]
    public async Task JoinSubscribesThenRedeemsTheExactRequestWithoutExposingItsProof()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        var request = new GuestJoinRequest("join-1", "session-1", "private-proof");

        var receipt = await client.JoinAsync(EmptyCallbacks, request, cancellationSource.Token);

        Assert.Equal("joined", receipt.Code);
        Assert.Equal("join-1", receipt.Correlation);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
        var subscription = fixture.Invocations[0];
        Assert.Equal("join-1", Assert.IsType<string>(subscription.Arguments[0]));
        var join = Assert.IsType<GuestJoinRequest>(fixture.Invocations[1].Arguments[0]);
        Assert.Equal(("join-1", "session-1", "private-proof"), (join.Correlation, join.SessionId, join.InvitationProof));
        Assert.DoesNotContain("private-proof", $"{receipt.Code}:{receipt.Correlation}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinedGuestCommandsUseCurrentMethodsAndLeaveRestoresTheLocalJoinGuard()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        var input = new GuestInputRequest(
            "input-1",
            new GuestInput(5, "epoch-1", 8, "grant-1", 9, true, new(1, 2, 3, 4, 5)));

        await client.JoinAsync(EmptyCallbacks, new("join-1", "session-1", "proof-1"), cancellationSource.Token);
        var submit = await client.SubmitAsync(input, cancellationSource.Token);
        var stop = await client.StopAsync(cancellationSource.Token);
        var leave = await client.LeaveAsync(cancellationSource.Token);
        var postLeave = await Assert.ThrowsAsync<ControlClientException>(() =>
            client.SubmitAsync(input, cancellationSource.Token));

        Assert.Equal(["SubscribeGuest", "Join", "Submit", "GuestStop", "Leave"],
            fixture.Invocations.Select(invocation => invocation.Method));
        var sentInput = Assert.IsType<GuestInputRequest>(fixture.Invocations[2].Arguments[0]);
        Assert.Equal("input-1", sentInput.Correlation);
        Assert.Equal((5, "epoch-1", 8L, "grant-1", 9L, true),
            (sentInput.Input.V, sentInput.Input.Epoch, sentInput.Input.PolicyVersion, sentInput.Input.GrantId, sentInput.Input.Seq, sentInput.Input.Run));
        Assert.Equal((1, 2, 3, 4, 5),
            (sentInput.Input.Desired.Pattern, sentInput.Input.Desired.Speed, sentInput.Input.Desired.Stroke,
                sentInput.Input.Desired.Depth, sentInput.Input.Desired.Sensation));
        AssertAcknowledgement(submit, fixture.Invocations[2], "accepted-Submit", "input-1");
        AssertAcknowledgement(stop, fixture.Invocations[3], "accepted-GuestStop", null);
        AssertAcknowledgement(leave, fixture.Invocations[4], "accepted-Leave", null);
        Assert.Equal("guest-not-joined", postLeave.Code);
        Assert.Equal(5, fixture.Invocations.Count);
    }

    [Fact]
    public async Task CommandsBeforeJoinFailLocallyWithoutAnyHubInvocation()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        var input = new GuestInputRequest("input-1", new(1, "epoch", 1, "grant", 1, false, new(1, 2, 3, 4, 5)));

        var submit = await Assert.ThrowsAsync<ControlClientException>(() => client.SubmitAsync(input, cancellationSource.Token));
        var stop = await Assert.ThrowsAsync<ControlClientException>(() => client.StopAsync(cancellationSource.Token));
        var leave = await Assert.ThrowsAsync<ControlClientException>(() => client.LeaveAsync(cancellationSource.Token));

        Assert.Equal("guest-not-joined", submit.Code);
        Assert.Equal("guest-not-joined", stop.Code);
        Assert.Equal("guest-not-joined", leave.Code);
        Assert.Equal(0, fixture.ConnectionCount);
        Assert.Empty(fixture.Invocations);
    }

    [Fact]
    public async Task GuestCallbacksAreTypedSerializedAndObservedInHubSendOrder()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        var callbacksCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();

        await client.JoinAsync(new(
            status =>
            {
                events.Add($"state:{status.Mode}:{status.PolicyVersion}:{status.Desired.Depth}");
                return Task.CompletedTask;
            },
            resync =>
            {
                events.Add($"resync:{resync.Code}");
                callbacksCompleted.TrySetResult();
                return Task.CompletedTask;
            }),
            new("join-1", "session-1", "proof-1"),
            cancellationSource.Token);
        await fixture.SendGuestCallbacksAsync(cancellationSource.Token);
        await callbacksCompleted.Task.WaitAsync(cancellationSource.Token);

        Assert.Equal(["state:paused:1:20", "resync:fixture-resync"], events);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task NullJoinArgumentsFailBeforeOpeningTheHub()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.JoinAsync(null!, new("join", "session", "proof"), cancellationSource.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.JoinAsync(EmptyCallbacks, null!, cancellationSource.Token));

        Assert.Equal(0, fixture.ConnectionCount);
        Assert.Empty(fixture.Invocations);
    }

    [Fact]
    public async Task OnlyOneGuestCallbackRegistrationAndJoinAttemptAreAllowedForEachClient()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        var request = new GuestJoinRequest("join-1", "session-1", "proof-1");

        await client.JoinAsync(EmptyCallbacks, request, cancellationSource.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.JoinAsync(EmptyCallbacks, request, cancellationSource.Token));

        Assert.Equal("Guest callbacks may only be registered once before connecting.", exception.Message);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
        Assert.Equal(1, fixture.ConnectionCount);
    }

    private static GuestControlCallbacks EmptyCallbacks { get; } = new(
        static _ => Task.CompletedTask,
        static _ => Task.CompletedTask);

    private static void AssertAcknowledgement(
        ControlAcknowledgement acknowledgement, HubInvocation invocation, string expectedCode, string? fixedCorrelation)
    {
        Assert.Equal(expectedCode, acknowledgement.Code);
        var correlation = fixedCorrelation ?? Assert.IsType<string>(invocation.Arguments[invocation.Arguments.Count - 1]);
        Assert.Equal(correlation, acknowledgement.Correlation);
        if (fixedCorrelation is null)
            Assert.Matches("^[0-9a-f]{32}$", acknowledgement.Correlation);
    }
}
