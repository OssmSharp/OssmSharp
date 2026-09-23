using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient.Tests;

/// <summary>
/// Checks operator command arguments, acknowledgements, and local subscription guards.
/// </summary>
public sealed class OperatorControlClientTests
{
    [Fact]
    public async Task EachOperatorCommandUsesItsCurrentHubMethodAndWireArgumentContract()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateOperatorClient();
        var expected = new AuthorityPrecondition("service-1", 17, new("session-1", "connection-1", "pending"));
        var approval = new ApproveGuestRequest("connection-1");
        var limits = new ControlLimits(24, 87);
        var unlock = new UnlockControlRequest(19);
        var simulatorFault = new SimulatorFaultRequest("bridge-timeout");

        await client.SubscribeAsync(EmptyCallbacks, 7, cancellationSource.Token);
        var cycle = await client.CycleAsync(expected, cancellationSource.Token);
        var approve = await client.ApproveAsync(approval, expected, cancellationSource.Token);
        var setLimits = await client.SetLimitsAsync(limits, expected, cancellationSource.Token);
        var recover = await client.RecoverAsync(expected, cancellationSource.Token);
        var arm = await client.ArmAsync(expected, cancellationSource.Token);
        var allowControl = await client.AllowControlAsync(expected, cancellationSource.Token);
        var stop = await client.StopAsync(cancellationSource.Token);
        var end = await client.EndAsync(expected, cancellationSource.Token);
        var unlockResult = await client.UnlockAsync(unlock, expected, cancellationSource.Token);
        var fault = await client.InjectSimulatorFaultAsync(simulatorFault, cancellationSource.Token);

        var invocations = fixture.Invocations;
        Assert.Equal(
            ["SubscribeOperator", "Cycle", "Approve", "SetLimits", "Recover", "Arm", "AllowControl", "Stop", "End", "Unlock", "SimulatorFault"],
            invocations.Select(invocation => invocation.Method));

        var subscription = invocations[0];
        Assert.Equal(7L, Assert.IsType<long>(subscription.Arguments[1]));
        AssertOperatorPrecondition(invocations[1].Arguments, expected);
        Assert.Equal("connection-1", Assert.IsType<ApproveGuestRequest>(invocations[2].Arguments[0]).ConnectionId);
        AssertOperatorPrecondition(invocations[2].Arguments.Skip(1).ToArray(), expected);
        var sentLimits = Assert.IsType<ControlLimits>(invocations[3].Arguments[0]);
        Assert.Equal((24, 87), (sentLimits.SpeedCap, sentLimits.MaximumDepth));
        AssertOperatorPrecondition(invocations[3].Arguments.Skip(1).ToArray(), expected);
        AssertOperatorPrecondition(invocations[4].Arguments, expected);
        AssertOperatorPrecondition(invocations[5].Arguments, expected);
        AssertOperatorPrecondition(invocations[6].Arguments, expected);
        Assert.Single(invocations[7].Arguments);
        AssertOperatorPrecondition(invocations[8].Arguments, expected);
        Assert.Equal(19L, Assert.IsType<UnlockControlRequest>(invocations[9].Arguments[0]).LockRevision);
        AssertOperatorPrecondition(invocations[9].Arguments.Skip(1).ToArray(), expected);
        Assert.Equal("bridge-timeout", Assert.IsType<SimulatorFaultRequest>(invocations[10].Arguments[0]).Kind);
        Assert.Single(invocations[10].Arguments.Skip(1));

        AssertAcknowledgement(cycle, invocations[1], "accepted-Cycle");
        AssertAcknowledgement(approve, invocations[2], "accepted-Approve");
        AssertAcknowledgement(setLimits, invocations[3], "accepted-SetLimits");
        AssertAcknowledgement(recover, invocations[4], "accepted-Recover");
        AssertAcknowledgement(arm, invocations[5], "accepted-Arm");
        AssertAcknowledgement(allowControl, invocations[6], "accepted-AllowControl");
        AssertAcknowledgement(stop, invocations[7], "accepted-Stop");
        AssertAcknowledgement(end, invocations[8], "accepted-End");
        AssertAcknowledgement(unlockResult, invocations[9], "accepted-Unlock");
        AssertAcknowledgement(fault, invocations[10], "accepted-SimulatorFault");
    }

    [Fact]
    public async Task InvalidSubscriptionArgumentsAreRejectedLocallyWithoutOpeningOrUsingTheHub()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateOperatorClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SubscribeAsync(null!, 0, cancellationSource.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.SubscribeAsync(EmptyCallbacks, -1, cancellationSource.Token));

        Assert.Equal(0, fixture.ConnectionCount);
        Assert.Empty(fixture.Invocations);
    }

    private static OperatorControlCallbacks EmptyCallbacks { get; } = new(
        static _ => Task.CompletedTask,
        static _ => Task.CompletedTask,
        static _ => Task.CompletedTask);

    private static void AssertOperatorPrecondition(IReadOnlyList<object?> arguments, AuthorityPrecondition expected)
    {
        var precondition = Assert.IsType<AuthorityPrecondition>(arguments[0]);
        Assert.Equal(expected.ServiceInstanceId, precondition.ServiceInstanceId);
        Assert.Equal(expected.AuthorityRevision, precondition.AuthorityRevision);
        Assert.Equal(expected.GuestAdmission.SessionId, precondition.GuestAdmission.SessionId);
        Assert.Equal(expected.GuestAdmission.ConnectionId, precondition.GuestAdmission.ConnectionId);
        Assert.Equal(expected.GuestAdmission.SessionMode, precondition.GuestAdmission.SessionMode);
        var correlation = Assert.IsType<string>(arguments[1]);
        Assert.Matches("^[0-9a-f]{32}$", correlation);
    }

    private static void AssertAcknowledgement(
        ControlAcknowledgement acknowledgement, HubInvocation invocation, string expectedCode)
    {
        Assert.Equal(expectedCode, acknowledgement.Code);
        var correlation = Assert.IsType<string>(invocation.Arguments[invocation.Arguments.Count - 1]);
        Assert.Equal(correlation, acknowledgement.Correlation);
        Assert.Matches("^[0-9a-f]{32}$", acknowledgement.Correlation);
    }
}
