using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

public sealed class ControlHubContractTests
{
    public static IEnumerable<object?[]> OperatorCommands =>
    [
        ["stop"],
        ["cycle"],
        ["approve"],
        ["set-limits"],
        ["recover"],
        ["arm"],
        ["allow-control"],
        ["end"],
        ["unlock"],
        ["simulator-fault"]
    ];

    public static IEnumerable<object?[]> GuestDeniedOperatorCommands =>
        OperatorCommands.Select(static row => new object?[] { row[0]! });

    public static IEnumerable<object?[]> GuestInputRejections =>
    [
        ["correlation", "invalid-correlation"],
        ["version", "invalid-input"],
        ["grant", "grant-invalid"],
        ["policy", "policy-invalid"],
        ["desired-values", "desired-invalid"]
    ];

    public static IEnumerable<object?[]> AnonymousOperationGroups =>
    [
        ["operator-subscription-and-log"],
        ["operator-commands"],
        ["guest-subscription-and-stage"],
        ["guest-join"],
        ["guest-input-stop-and-leave"]
    ];

    [Fact]
    public async Task OperatorAndGuestSubscriptionsDeliverOnlyTheirScopedCurrentCallbacksAndF1AdmissionPublication()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var operatorClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await using var guestClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);

        var operatorSubscription = await operatorClient.InvokeAsync<ControlAcknowledgement>(
            "SubscribeOperator", "operator-subscribe", 0);
        await fixture.DispatchAsync(cancellationToken);
        var initial = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        var guestSubscription = await guestClient.InvokeAsync<ControlAcknowledgement>(
            "SubscribeGuest", "guest-subscribe");
        var receipt = await guestClient.InvokeAsync<GuestJoinReceipt>(
            "Join",
            new GuestJoinRequest(
                "guest-join",
                initial.Invitation.SessionId,
                LocalControlServiceHubFixture.InvitationProof(initial.Invitation)));

        var joined = fixture.Authority.Read();
        await fixture.DispatchAsync(cancellationToken);
        var operatorAfterAdmission = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        var guestAfterAdmission = await guestClient.Callbacks.NextGuestStateAsync(cancellationToken);
        var receiptJson = JsonSerializer.Serialize(receipt);
        var guestJson = JsonSerializer.Serialize(guestAfterAdmission);

        Assert.Equal(("subscribed", "operator-subscribe"), (operatorSubscription.Code, operatorSubscription.Correlation));
        Assert.Equal(("subscribed", "guest-subscribe"), (guestSubscription.Code, guestSubscription.Correlation));
        Assert.Equal(("joined", "guest-join"), (receipt.Code, receipt.Correlation));
        Assert.Equal(initial.Authority.Precondition.AuthorityRevision,
            operatorAfterAdmission.Authority.Precondition.AuthorityRevision);
        Assert.True(operatorAfterAdmission.Authority.SnapshotRevision > initial.Authority.SnapshotRevision);
        Assert.Equal(joined.SnapshotRevision, operatorAfterAdmission.Authority.SnapshotRevision);
        Assert.Equal("PendingApproval", operatorAfterAdmission.Authority.Snapshot.SessionMode);
        Assert.Equal("PendingApproval", guestAfterAdmission.SessionMode);
        Assert.Null(guestAfterAdmission.ConnectionId);
        Assert.DoesNotContain("guestToken", receiptJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invitationProof", receiptJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof", guestJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["operatorState", "operatorState"], operatorClient.Callbacks.Events);
        Assert.Equal(["guestState"], guestClient.Callbacks.Events);
    }

    [Theory]
    [MemberData(nameof(OperatorCommands))]
    public async Task EveryOperatorCommandAcknowledgesAndPublishesANewerOrderedOperatorSnapshot(string operation)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await client.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "subscribe", 0);
        await fixture.DispatchAsync(cancellationToken);
        var initial = await client.Callbacks.NextOperatorStateAsync(cancellationToken);
        var expected = PrepareOperatorCommand(fixture, operation);
        var before = fixture.Authority.Read();

        var acknowledgement = await InvokeOperatorCommandAsync(client, operation, expected);
        if (operation == "simulator-fault")
            fixture.Authority.Tick();
        var after = fixture.Authority.Read();
        await fixture.DispatchAsync(cancellationToken);
        var callback = await client.Callbacks.NextOperatorStateAsync(cancellationToken);

        Assert.Equal(operation == "stop" ? "stopped" : "accepted", acknowledgement.Code);
        Assert.Equal("command", acknowledgement.Correlation);
        Assert.Equal(
            before.Precondition.AuthorityRevision + (operation == "simulator-fault" ? 0 : 1),
            after.Precondition.AuthorityRevision);
        Assert.Equal(before.SnapshotRevision + 1, after.SnapshotRevision);
        Assert.Equal(after.SnapshotRevision, callback.Authority.SnapshotRevision);
        Assert.True(callback.Authority.SnapshotRevision > initial.Authority.SnapshotRevision);
        Assert.Equal(["operatorState", "operatorState"], client.Callbacks.Events);
    }

    [Theory]
    [MemberData(nameof(GuestDeniedOperatorCommands))]
    public async Task GuestRoleCannotInvokeAnyOperatorCommandAndLeavesAuthorityAndBridgeUnchanged(string operation)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var before = fixture.Authority.Read();
        fixture.Bridge.ClearCalls();

        var error = await AssertHubRejectionAsync(() => InvokeOperatorCommandAsync(client, operation, before.Precondition));

        Assert.Equal($"Failed to invoke '{HubMethodName(operation)}' because user is unauthorized", error.Message);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
        Assert.Empty(client.Callbacks.Events);
    }

    [Theory]
    [InlineData("SubscribeGuest")]
    [InlineData("Join")]
    [InlineData("Submit")]
    [InlineData("GuestStop")]
    [InlineData("Leave")]
    public async Task AdminRoleCannotInvokeAnyGuestOperationAndLeavesAdmissionUnchanged(string operation)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        var before = fixture.Authority.Read();
        fixture.Bridge.ClearCalls();

        var error = await AssertHubRejectionAsync(() => InvokeGuestOperationAsync(client, operation));

        Assert.Equal($"Failed to invoke '{operation}' because user is unauthorized", error.Message);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.False(fixture.Registry.IsGuestStaged(client.ConnectionId));
        Assert.Empty(fixture.Bridge.Calls);
        Assert.Empty(client.Callbacks.Events);
    }

    [Theory]
    [MemberData(nameof(AnonymousOperationGroups))]
    public async Task AnonymousNoKeyClientIsRejectedAtTheCurrentHubBoundaryForEveryOperationGroup(string operationGroup)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = fixture.CreateAnonymousConnection();
        var before = fixture.Authority.Read();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.StartAsync(cancellationToken));

        Assert.True(error.StatusCode == HttpStatusCode.Unauthorized,
            $"Anonymous {operationGroup} connections must be rejected.");
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
        await client.DisposeAsync();
    }

    [Theory]
    [InlineData("operator-correlation", "SubscribeOperator", "invalid-correlation")]
    [InlineData("operator-cursor", "SubscribeOperator", "invalid-activity-cursor")]
    [InlineData("guest-correlation", "SubscribeGuest", "invalid-correlation")]
    public async Task SubscriptionValidationReturnsCurrentRejectionCodeWithoutStateOrBridgeWork(
        string caseName, string operation, string rejectionCode)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        var role = operation == "SubscribeOperator"
            ? LocalControlServiceHubFixture.HubRole.Admin
            : LocalControlServiceHubFixture.HubRole.Guest;
        await using var client = await fixture.ConnectAsync(role, cancellationToken);
        var before = fixture.Authority.Read();

        var rejection = operation switch
        {
            "SubscribeOperator" when caseName == "operator-correlation" =>
                await client.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", " ", 0),
            "SubscribeOperator" =>
                await client.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "cursor", -1),
            _ => await client.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", " ")
        };

        AssertRejected(rejection, rejectionCode);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task ExpectedCommandRejectionsReturnStableAcknowledgementsInsteadOfFrameworkHubErrors()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        var before = fixture.Authority.Read();

        var acknowledgement = await client.InvokeAsync<ControlAcknowledgement>(
            "SubscribeOperator", " ", 0);

        Assert.False(acknowledgement.Accepted);
        Assert.Equal(("invalid-correlation", ""), (acknowledgement.Code, acknowledgement.Correlation));
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task NullGuestRequestPayloadsReturnStableRejectedResponses()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var before = fixture.Authority.Read();

        var join = await client.InvokeAsync<GuestJoinReceipt>("Join", new object?[] { null });
        await client.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        var submit = await client.InvokeAsync<ControlAcknowledgement>("Submit", new object?[] { null });

        AssertRejected(join, "invalid-request");
        AssertRejected(submit, "invalid-request");
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task NullOperatorRequestPayloadsReturnStableRejectedAcknowledgements()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        var before = fixture.Authority.Read();

        var approve = await client.InvokeAsync<ControlAcknowledgement>(
            "Approve", new object?[] { null, before.Precondition, "approve" });
        var unlock = await client.InvokeAsync<ControlAcknowledgement>(
            "Unlock", new object?[] { null, before.Precondition, "unlock" });
        var fault = await client.InvokeAsync<ControlAcknowledgement>(
            "SimulatorFault", new object?[] { null, "fault" });

        AssertRejected(approve, "invalid-request");
        AssertRejected(unlock, "invalid-request");
        AssertRejected(fault, "invalid-request");
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Theory]
    [InlineData("no-stage", "guest-subscription-required")]
    [InlineData("malformed-correlation", "invalid-correlation")]
    [InlineData("bad-proof", "invitation-invalid")]
    public async Task JoinBoundaryCasesExposeOnlyTheirCurrentHubRejectionCode(
        string caseName, string rejectionCode)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var invitation = fixture.ReadInvitation();
        if (caseName != "no-stage")
            await client.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        var before = fixture.Authority.Read();
        var request = caseName switch
        {
            "malformed-correlation" => new GuestJoinRequest(" ", invitation.SessionId,
                LocalControlServiceHubFixture.InvitationProof(invitation)),
            "bad-proof" => new GuestJoinRequest("join", invitation.SessionId, "bad-proof"),
            _ => new GuestJoinRequest("join", invitation.SessionId,
                LocalControlServiceHubFixture.InvitationProof(invitation))
        };

        var rejection = await client.InvokeAsync<GuestJoinReceipt>("Join", request);

        AssertRejected(rejection, rejectionCode);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task DuplicateAndSecondGuestJoinRequestsCannotCreateAnotherBoundAdmission()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var first = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        await using var second = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var invitation = fixture.ReadInvitation();
        var request = new GuestJoinRequest("join", invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(invitation));
        await first.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "first-stage");
        await second.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "second-stage");
        await first.InvokeAsync<GuestJoinReceipt>("Join", request);
        var before = fixture.Authority.Read();

        var duplicate = await first.InvokeAsync<GuestJoinReceipt>("Join", request);
        var secondGuest = await second.InvokeAsync<GuestJoinReceipt>("Join", request);

        AssertRejected(duplicate, "guest-already-joined");
        AssertRejected(secondGuest, "session-conflict");
        Assert.Equal(before, fixture.Authority.Read());
        Assert.False(fixture.Registry.TryGetGuest(second.ConnectionId, out _));
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Theory]
    [MemberData(nameof(GuestInputRejections))]
    public async Task JoinedGuestInputRejectionsUseExactCodesWithoutAdvancingAuthorityOrPublication(
        string caseName, string rejectionCode)
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var client = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        fixture.Authority.Recover(fixture.Authority.Read().Precondition);
        var invitation = fixture.ReadInvitation();
        await client.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        await client.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest("join", invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(invitation)));
        var pending = fixture.Authority.Read();
        fixture.Authority.Approve(pending.Precondition, pending.Snapshot.ConnectionId!);
        fixture.Authority.Arm(fixture.Authority.Read().Precondition);
        var status = fixture.Guests.Status(client.ConnectionId);
        var before = fixture.Authority.Read();
        fixture.Bridge.ClearCalls();

        var rejection = await client.InvokeAsync<ControlAcknowledgement>(
            "Submit",
            new GuestInputRequest(
                caseName == "correlation" ? " " : "input",
                CreateInput(status, caseName)));

        AssertRejected(rejection, rejectionCode);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task JoinedGuestInputStopAndLeaveAcknowledgeWithScopedOrderedCallbacks()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        fixture.Authority.Recover(fixture.Authority.Read().Precondition);
        await using var operatorClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await using var guestClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        await operatorClient.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "operator", 0);
        await fixture.DispatchAsync(cancellationToken);
        var initial = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        await guestClient.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        await guestClient.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "join",
            initial.Invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(initial.Invitation)));
        await fixture.DispatchAsync(cancellationToken);
        await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        await guestClient.Callbacks.NextGuestStateAsync(cancellationToken);
        var pending = fixture.Authority.Read();
        fixture.Authority.Approve(pending.Precondition, pending.Snapshot.ConnectionId!);
        fixture.Authority.Arm(fixture.Authority.Read().Precondition);
        await fixture.DispatchAsync(cancellationToken);
        await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        var armedGuest = await guestClient.Callbacks.NextGuestStateAsync(cancellationToken);

        var input = await guestClient.InvokeAsync<ControlAcknowledgement>(
            "Submit",
            new GuestInputRequest("input", CreateInput(armedGuest, "valid")));
        await fixture.DispatchAsync(cancellationToken);
        var operatorAfterInput = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        var guestAfterInput = await guestClient.Callbacks.NextGuestStateAsync(cancellationToken);
        var stop = await guestClient.InvokeAsync<ControlAcknowledgement>("GuestStop", "stop");
        await fixture.DispatchAsync(cancellationToken);
        var operatorAfterStop = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        var guestAfterStop = await guestClient.Callbacks.NextGuestStateAsync(cancellationToken);
        var leave = await guestClient.InvokeAsync<ControlAcknowledgement>("Leave", "leave");
        await fixture.DispatchAsync(cancellationToken);
        var operatorAfterLeave = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);

        Assert.Equal(("accepted", "input"), (input.Code, input.Correlation));
        Assert.Equal(("stopped", "stop"), (stop.Code, stop.Correlation));
        Assert.Equal(("ended", "leave"), (leave.Code, leave.Correlation));
        Assert.Equal(1, operatorAfterInput.Authority.Snapshot.AckSeq);
        Assert.Equal(1, guestAfterInput.AckSeq);
        Assert.Equal("guest-stop", operatorAfterStop.Authority.Snapshot.Reason);
        Assert.Equal("guest-stop", guestAfterStop.Reason);
        Assert.Equal("Ended", operatorAfterLeave.Authority.Snapshot.SessionMode);
        Assert.Null(guestAfterInput.ConnectionId);
        Assert.DoesNotContain("guestState", operatorClient.Callbacks.Events);
        Assert.DoesNotContain("operatorState", guestClient.Callbacks.Events);
        Assert.Equal(0, guestClient.Callbacks.GuestStateCount);
    }

    [Fact]
    public async Task ExpiredAndRevokedGuestAdmissionsRejectCommandsWithTheirCurrentCodesAndNoExtraRevision()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var guest = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var invitation = fixture.ReadInvitation();
        await guest.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        await guest.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "join",
            invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(invitation)));
        fixture.Time.Advance(TimeSpan.FromSeconds(61));
        var beforeExpiry = fixture.Authority.Read();

        var expired = await guest.InvokeAsync<ControlAcknowledgement>("GuestStop", "expired");
        var afterExpiry = fixture.Authority.Read();

        AssertRejected(expired, "session-expired");
        Assert.Equal(beforeExpiry.Precondition.AuthorityRevision, afterExpiry.Precondition.AuthorityRevision);
        Assert.Equal(beforeExpiry.SnapshotRevision, afterExpiry.SnapshotRevision);
        Assert.Equal("Ended", afterExpiry.Snapshot.SessionMode);

        await using var revokedFixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var operatorClient = await revokedFixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await using var revokedGuest = await revokedFixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var revocableInvitation = revokedFixture.ReadInvitation();
        await revokedGuest.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        await revokedGuest.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "join",
            revocableInvitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(revocableInvitation)));
        var expected = revokedFixture.Authority.Read().Precondition;
        await operatorClient.InvokeAsync<ControlAcknowledgement>("Cycle", expected, "cycle");
        var beforeRevokedCommand = revokedFixture.Authority.Read();

        var revoked = await revokedGuest.InvokeAsync<ControlAcknowledgement>("GuestStop", "revoked");

        AssertRejected(revoked, "guest-unauthorized");
        Assert.Equal(beforeRevokedCommand, revokedFixture.Authority.Read());
        Assert.Empty(revokedFixture.Bridge.Calls);
    }

    [Fact]
    public async Task TransportLossRevokesOnlyTheDisconnectedGuestAndLeavesAnotherStagedConnectionUsable()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        var first = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        await using var second = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        var invitation = fixture.ReadInvitation();
        await first.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "first-stage");
        await second.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "second-stage");
        await first.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "first-join",
            invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(invitation)));
        var firstConnectionId = first.ConnectionId;
        var beforeLoss = fixture.Authority.Read();

        await first.DisposeAsync();
        await fixture.WaitForDisconnectAsync(firstConnectionId, cancellationToken);
        var afterLoss = fixture.Authority.Read();
        await fixture.DispatchAsync(cancellationToken);
        var secondReceipt = await second.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "second-join",
            invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(invitation)));

        Assert.Equal("Ended", afterLoss.Snapshot.SessionMode);
        Assert.Equal(beforeLoss.Precondition.AuthorityRevision, afterLoss.Precondition.AuthorityRevision);
        Assert.Equal(beforeLoss.SnapshotRevision + 1, afterLoss.SnapshotRevision);
        Assert.True(fixture.Registry.IsGuestStaged(second.ConnectionId));
        Assert.False(second.Callbacks.TryTakeGuestState(out _));
        Assert.Equal(("joined", "second-join"), (secondReceipt.Code, secondReceipt.Correlation));
        Assert.True(fixture.Registry.TryGetGuest(second.ConnectionId, out _));
    }

    [Fact]
    public async Task TerminalAndCapacityResyncCallbacksArriveOncePerPublicationRevisionOverLocalSignalR()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var operatorClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await using var guestClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, cancellationToken);
        await operatorClient.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "operator", 0);
        await fixture.DispatchAsync(cancellationToken);
        await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        var invitation = fixture.ReadInvitation();
        await guestClient.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "stage");
        await guestClient.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "join",
            invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(invitation)));
        await fixture.DispatchAsync(cancellationToken);
        await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        await guestClient.Callbacks.NextGuestStateAsync(cancellationToken);
        await guestClient.InvokeAsync<ControlAcknowledgement>("Leave", "leave");
        await fixture.DispatchAsync(cancellationToken);
        var firstTerminal = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        await fixture.DispatchAsync(cancellationToken);
        Assert.False(operatorClient.Callbacks.TryTakeOperatorState(out _));

        fixture.Authority.Tick();
        await fixture.DispatchAsync(cancellationToken);
        var newerTerminal = await operatorClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        Assert.Equal("Ended", firstTerminal.Authority.Snapshot.SessionMode);
        Assert.Equal("Ended", newerTerminal.Authority.Snapshot.SessionMode);
        Assert.True(newerTerminal.Authority.SnapshotRevision > firstTerminal.Authority.SnapshotRevision);

        await using var resyncFixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var resyncClient = await resyncFixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await resyncClient.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "resync", 0);
        await resyncFixture.DispatchAsync(cancellationToken);
        var initial = await resyncClient.Callbacks.NextOperatorStateAsync(cancellationToken);
        Assert.Equal(QueueState.Queued, resyncFixture.Registry.QueueOperatorState(
            resyncClient.ConnectionId,
            TerminalOperatorState(initial.Authority.SnapshotRevision + 1)));
        Assert.Equal(QueueState.Saturated, resyncFixture.Registry.QueueOperatorState(
            resyncClient.ConnectionId,
            TerminalOperatorState(initial.Authority.SnapshotRevision + 2)));
        resyncFixture.Registry.RequireResync(resyncClient.ConnectionId);
        var beforeResync = resyncFixture.Authority.Read();

        await resyncFixture.DispatchAsync(cancellationToken);
        var resync = await resyncClient.Callbacks.NextResyncAsync(cancellationToken);

        Assert.Equal("delivery-resync-required", resync.Code);
        Assert.Empty(resyncFixture.Registry.OperatorConnections());
        Assert.Equal(beforeResync, resyncFixture.Authority.Read());
        Assert.Equal(["operatorState", "resyncRequired"], resyncClient.Callbacks.Events);
    }

    [Fact]
    public async Task OperatorLogCallbacksContinueWithoutDuplicatesAndExpiredCursorsReportGap()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        await using var first = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await first.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "first", 0);
        for (var index = 1; index <= 100; index++)
            fixture.AppendOperatorActivity($"{index:D3}:{new string('x', 390)}");
        await fixture.DispatchAsync(cancellationToken);
        await first.Callbacks.NextOperatorStateAsync(cancellationToken);

        var firstSequences = await ReadAllLogPagesAsync(fixture, first.Callbacks, cancellationToken);
        for (var index = 101; index <= 300; index++)
            fixture.AppendOperatorActivity($"{index:D3}:{new string('y', 390)}");
        await using var expired = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, cancellationToken);
        await expired.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "expired", 0);
        await fixture.DispatchAsync(cancellationToken);
        await expired.Callbacks.NextOperatorStateAsync(cancellationToken);
        var expiredBatches = await ReadAllLogBatchesAsync(fixture, expired.Callbacks, cancellationToken);
        var expiredSequences = expiredBatches.SelectMany(batch => batch.Entries).Select(entry => entry.Sequence).ToArray();

        Assert.Equal(Enumerable.Range(1, 100).Select(static value => (long)value), firstSequences);
        Assert.Equal(firstSequences.Length, firstSequences.Distinct().Count());
        Assert.True(expiredBatches[0].Gap);
        Assert.Equal(101, expiredSequences[0]);
        Assert.Equal(300, expiredSequences[^1]);
        Assert.Equal(200, expiredSequences.Length);
        Assert.Equal(expiredSequences.Length, expiredSequences.Distinct().Count());
        Assert.Equal(["operatorState"], first.Callbacks.Events.Take(1));
        Assert.Contains("operatorLog", expired.Callbacks.Events);
    }

    [Fact]
    public async Task DispatcherCancellationCompletesAsCanceledWithoutMutatingAuthority()
    {
        using var cancellationSource = CreateTimeout();
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(cancellationToken);
        fixture.Registry.SubscribeOperator("cancelled", 0, -1);
        var dispatcher = new ControlPushDispatcher(
            fixture.Operations,
            fixture.Guests,
            fixture.Registry,
            fixture.Activity,
            new CanceledPushSink(),
            NullLogger<ControlPushDispatcher>.Instance);
        var before = fixture.Authority.Read();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchOnceAsync(cancelled.Token));

        Assert.Equal(before, fixture.Authority.Read());
        Assert.Contains("cancelled", fixture.Registry.OperatorConnections());
        Assert.Empty(fixture.Bridge.Calls);
        dispatcher.Dispose();
        _ = cancellationToken;
    }

    private static AuthorityPrecondition PrepareOperatorCommand(LocalControlServiceHubFixture fixture, string operation)
    {
        switch (operation)
        {
            case "approve":
            case "allow-control":
                fixture.CreatePendingGuest();
                break;
            case "arm":
                fixture.PrepareApprovedGuest();
                break;
            case "unlock":
                fixture.LockAndSettle();
                break;
        }
        return fixture.Authority.Read().Precondition;
    }

    private static Task<ControlAcknowledgement> InvokeOperatorCommandAsync(
        LocalControlServiceHubFixture.HubClient client,
        string operation,
        AuthorityPrecondition expected) =>
        operation switch
        {
            "stop" => client.InvokeAsync<ControlAcknowledgement>("Stop", "command"),
            "cycle" => client.InvokeAsync<ControlAcknowledgement>("Cycle", expected, "command"),
            "approve" => client.InvokeAsync<ControlAcknowledgement>("Approve",
                new ApproveGuestRequest(expected.GuestAdmission.ConnectionId!), expected, "command"),
            "set-limits" => client.InvokeAsync<ControlAcknowledgement>("SetLimits",
                new ControlLimits(99, 99), expected, "command"),
            "recover" => client.InvokeAsync<ControlAcknowledgement>("Recover", expected, "command"),
            "arm" => client.InvokeAsync<ControlAcknowledgement>("Arm", expected, "command"),
            "allow-control" => client.InvokeAsync<ControlAcknowledgement>("AllowControl", expected, "command"),
            "end" => client.InvokeAsync<ControlAcknowledgement>("End", expected, "command"),
            "unlock" => client.InvokeAsync<ControlAcknowledgement>("Unlock",
                new UnlockControlRequest(expected.GuestAdmission.SessionId is null ? 1 : 0), expected, "command"),
            "simulator-fault" => client.InvokeAsync<ControlAcknowledgement>("SimulatorFault",
                new SimulatorFaultRequest("external-depth"), "command"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static Task InvokeGuestOperationAsync(LocalControlServiceHubFixture.HubClient client, string operation) =>
        operation switch
        {
            "SubscribeGuest" => client.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "guest"),
            "Join" => client.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest("guest", "session", "proof")),
            "Submit" => client.InvokeAsync<ControlAcknowledgement>("Submit", new GuestInputRequest("guest",
                new GuestInput(2, "epoch", 1, "grant", 1, true, new ControlValues(0, 10, 10, 20, 50)))),
            "GuestStop" => client.InvokeAsync<ControlAcknowledgement>("GuestStop", "guest"),
            "Leave" => client.InvokeAsync<ControlAcknowledgement>("Leave", "guest"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static GuestInput CreateInput(GuestStatus status, string caseName)
    {
        var grant = Assert.IsType<GuestGrant>(status.Grant);
        return caseName switch
        {
            "version" => new(1, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 10, 10, 20, 50)),
            "grant" => new(2, grant.Epoch, grant.PolicyVersion, "wrong-grant", 1, true, new(0, 10, 10, 20, 50)),
            "policy" => new(2, grant.Epoch, grant.PolicyVersion + 1, grant.Id, 1, true, new(0, 10, 10, 20, 50)),
            "desired-values" => new(2, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 10, 30, 20, 50)),
            _ => new(2, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 10, 10, 20, 50))
        };
    }

    private static OperatorLiveControlStatus TerminalOperatorState(long revision) =>
        new(
            new(
                new ControlSnapshot(
                    "Paused",
                    "fixture",
                    1,
                    0,
                    true,
                    false,
                    DesiredState.Stopped,
                    null,
                    new ControlLimits(100, 100),
                    1,
                    "Ended",
                    null,
                    null,
                    0,
                    null),
                new AuthorityPrecondition("fixture", 0, new GuestAdmission(null, null, "Ended")),
                revision),
            new Invitation("fixture", "http://127.0.0.1:5182/join/fixture", null, true));

    private static async Task<long[]> ReadAllLogPagesAsync(
        LocalControlServiceHubFixture fixture,
        LocalControlServiceHubFixture.HubCallbackRecorder callbacks,
        CancellationToken cancellationToken) =>
        (await ReadAllLogBatchesAsync(fixture, callbacks, cancellationToken))
        .SelectMany(batch => batch.Entries)
        .Select(entry => entry.Sequence)
        .ToArray();

    private static async Task<List<ControlActivityBatch>> ReadAllLogBatchesAsync(
        LocalControlServiceHubFixture fixture,
        LocalControlServiceHubFixture.HubCallbackRecorder callbacks,
        CancellationToken cancellationToken)
    {
        var batches = new List<ControlActivityBatch>();
        while (true)
        {
            await fixture.DispatchAsync(cancellationToken);
            var batch = await callbacks.NextOperatorLogAsync(cancellationToken);
            batches.Add(batch);
            if (batch.ContinuationSequence is null)
                return batches;
        }
    }

    private static async Task<HubException> AssertHubRejectionAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<HubException>(action);
        return exception;
    }

    private static void AssertRejected(IControlResponse response, string expectedCode)
    {
        Assert.False(response.Accepted);
        Assert.Equal(expectedCode, response.Code);
    }

    private static string HubMethodName(string operation) =>
        operation switch
        {
            "set-limits" => "SetLimits",
            "allow-control" => "AllowControl",
            "simulator-fault" => "SimulatorFault",
            _ => char.ToUpperInvariant(operation[0]) + operation[1..]
        };

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }

    private sealed class CanceledPushSink : IControlHubPushSink
    {
        public Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken) =>
            Task.FromCanceled(cancellationToken);
    }
}
