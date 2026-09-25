using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OSSMDotNet.ApiService.Models;
using OSSMDotNet.Models;

namespace OSSMDotNet.ApiService.SignalR;

/// <summary>
/// Provides versioned role-scoped SignalR operations over the transport-neutral control application.
/// </summary>
public sealed class ControlHub(
    IControlOperations operations,
    GuestControlOperations guests,
    SimulatorFaultOperations simulatorFaults,
    ControlHubConnectionRegistry connections,
    ControlPushDispatcher dispatcher,
    IAuthorizationService authorization,
    ILogger<ControlHub> logger) : Hub
{
    /// <summary>
    /// Subscribes an authorized operator connection to activity and live state updates.
    /// </summary>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <param name="afterSequence">The last activity sequence received by the operator.</param>
    /// <returns>An acknowledgement indicating whether the subscription was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public async Task<ControlAcknowledgement> SubscribeOperator(string correlation, long afterSequence)
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation", nameof(SubscribeOperator));
        if (afterSequence < 0)
            return RejectedAcknowledgement(validCorrelation, "invalid-activity-cursor", nameof(SubscribeOperator));
        connections.SubscribeOperator(Context.ConnectionId, afterSequence, -1);
        dispatcher.Trigger();
        return new("subscribed", validCorrelation);
    }

    /// <summary>
    /// Stages an authorized guest connection to receive scoped updates after joining.
    /// </summary>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the subscription was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> SubscribeGuest(string correlation)
    {
        await RequireGuestAsync("subscribe");
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation", nameof(SubscribeGuest));
        connections.StageGuest(Context.ConnectionId);
        return new("subscribed", validCorrelation);
    }

    /// <summary>
    /// Joins a staged guest connection to the requested invitation.
    /// </summary>
    /// <param name="request">The invitation and guest admission request.</param>
    /// <returns>A receipt indicating whether the guest joined.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<GuestJoinReceipt> Join(GuestJoinRequest? request)
    {
        await RequireGuestAsync("join");
        if (request is null)
            return RejectedJoinReceipt(null, "invalid-request");
        try
        {
            if (!connections.IsGuestStaged(Context.ConnectionId))
                throw new ControlRejectedException("guest-subscription-required");
            if (connections.TryGetGuest(Context.ConnectionId, out _))
                throw new ControlRejectedException("guest-already-joined");
            var logicalConnectionId = Guid.NewGuid().ToString("N");
            var joined = guests.Join(logicalConnectionId, request);
            if (!connections.TryBindGuest(Context.ConnectionId,
                new(logicalConnectionId, joined.SessionId, joined.GuestToken)))
            {
                guests.TransportLost(logicalConnectionId);
                throw new ControlRejectedException("guest-bind-failed");
            }
            connections.SubscribeGuest(Context.ConnectionId, -1);
            dispatcher.Trigger();
            ApiLog.HubLifecycleAccepted(logger, nameof(Join));
            return new("joined", request.Correlation);
        }
        catch (ControlRejectedException exception) { return RejectedJoinReceipt(request.Correlation, exception.Code, exception.Code is "guest-subscription-required" or "guest-already-joined" or "guest-bind-failed"); }
    }

    /// <summary>
    /// Submits motion input from the joined guest connection.
    /// </summary>
    /// <param name="request">The guest input to authorize and apply.</param>
    /// <returns>An acknowledgement indicating whether the input was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> Submit(GuestInputRequest? request)
    {
        await RequireGuestAsync("input");
        if (request is null)
        {
            try
            {
                if (connections.TryGetGuest(Context.ConnectionId, out var guest) && guest is not null)
                    guests.RecordRejectedSubmit(guest.SessionId, guest.Proof, guest.LogicalConnectionId);
            }
            catch (ControlRejectedException exception) { return RejectedAcknowledgement(null, exception.Code, nameof(Submit), false); }
            return RejectedAcknowledgement(null, "invalid-request", nameof(Submit));
        }
        return await GuestCommandAsync(request.Correlation, () =>
        {
            var guest = RequireGuest();
            return guests.Submit(guest.SessionId, guest.Proof, guest.LogicalConnectionId, request);
        });
    }

    /// <summary>
    /// Stops motion for the joined guest session.
    /// </summary>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the stop was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> GuestStop(string correlation) =>
        await GuestCommandAsync(correlation, () =>
        {
            var guest = RequireGuest();
            return guests.Stop(guest.SessionId, guest.Proof, guest.LogicalConnectionId, correlation);
        });

    /// <summary>
    /// Leaves the joined guest session and removes its connection binding.
    /// </summary>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the leave was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> Leave(string correlation) =>
        await GuestCommandAsync(correlation, () =>
        {
            var guest = RequireGuest();
            var result = guests.Leave(guest.SessionId, guest.Proof, guest.LogicalConnectionId, correlation);
            connections.Remove(Context.ConnectionId);
            ApiLog.HubLifecycleAccepted(logger, nameof(Leave));
            return result;
        });

    /// <summary>
    /// Stops device motion for an authorized operator.
    /// </summary>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the stop was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public async Task<ControlAcknowledgement> Stop(string correlation)
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation", nameof(Stop));
        operations.Stop();
        dispatcher.Trigger();
        ApiLog.HubLifecycleAccepted(logger, nameof(Stop));
        return new("stopped", validCorrelation);
    }

    /// <summary>
    /// Revokes guest authority and cycles the invitation.
    /// </summary>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the cycle was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Cycle(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.CycleInvitation(expected));

    /// <summary>
    /// Approves a pending guest under the expected authority revision.
    /// </summary>
    /// <param name="request">The pending guest approval request.</param>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether approval was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Approve(ApproveGuestRequest? request, AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Approve(request, expected));

    /// <summary>
    /// Applies compatible session limits under the expected authority revision.
    /// </summary>
    /// <param name="limits">The limits to apply atomically.</param>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the limits were accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> SetLimits(ControlLimits limits, AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.ApplySessionLimits(limits, expected));

    /// <summary>
    /// Recovers a stopped session into its paused state.
    /// </summary>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether recovery was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Recover(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Recover(expected));

    /// <summary>
    /// Arms the approved session when its safety checks succeed.
    /// </summary>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether arming was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Arm(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Arm(expected));

    /// <summary>
    /// Allows the approved guest to control the armed session.
    /// </summary>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether control was allowed.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> AllowControl(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.AllowControl(expected));

    /// <summary>
    /// Ends the session and replaces its invitation.
    /// </summary>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the session ended.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public async Task<ControlAcknowledgement> End(AuthorityPrecondition expected, string correlation)
    {
        var acknowledgement = await AdminCommandAsync(correlation, () => operations.End(expected));
        if (acknowledgement.Accepted)
            ApiLog.HubLifecycleAccepted(logger, nameof(End));
        return acknowledgement;
    }

    /// <summary>
    /// Unlocks the stopped controller into its paused state.
    /// </summary>
    /// <param name="request">The operator unlock request.</param>
    /// <param name="expected">The authority revision required for this change.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether unlock was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Unlock(UnlockControlRequest? request, AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Unlock(request, expected));

    /// <summary>
    /// Injects a simulator fault for an authorized operator.
    /// </summary>
    /// <param name="request">The simulator fault to inject.</param>
    /// <param name="correlation">The request identifier returned with the acknowledgement.</param>
    /// <returns>An acknowledgement indicating whether the fault request was accepted.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> SimulatorFault(SimulatorFaultRequest? request, string correlation)
    {
        if (request is null)
            return AdminCommandAsync(correlation, RejectInvalidRequest);
        return AdminCommandAsync(correlation, () => simulatorFaults.InjectAsync(request));
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.Remove(Context.ConnectionId) is { } guest)
            guests.TransportLost(guest.LogicalConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private HubGuestSession RequireGuest() =>
        connections.TryGetGuest(Context.ConnectionId, out var guest) && guest is not null
            ? guest
            : throw new ControlRejectedException("guest-not-joined");

    private async Task<ControlAcknowledgement> GuestCommandAsync(string? correlation, Func<ControlAcknowledgement> command,
        [CallerMemberName] string operation = "")
    {
        try
        {
            var acknowledgement = command();
            dispatcher.Trigger();
            return acknowledgement;
        }
        catch (ControlRejectedException exception) { return RejectedAcknowledgement(correlation, exception.Code, operation, exception.Code == "guest-not-joined"); }
    }

    private async Task<ControlAcknowledgement> AdminCommandAsync(string correlation, Action command,
        [CallerMemberName] string operation = "")
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation", operation);
        try { command(); }
        catch (ControlRejectedException exception) { return RejectedAcknowledgement(validCorrelation, exception.Code, operation, false); }
        dispatcher.Trigger();
        return new("accepted", validCorrelation);
    }

    private async Task<ControlAcknowledgement> AdminCommandAsync(string correlation, Func<Task> command,
        [CallerMemberName] string operation = "")
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation", operation);
        try { await command(); }
        catch (ControlRejectedException exception) { return RejectedAcknowledgement(validCorrelation, exception.Code, operation, false); }
        dispatcher.Trigger();
        return new("accepted", validCorrelation);
    }

    private async Task RequireAdminAsync()
    {
        if (!(await authorization.AuthorizeAsync(Context.User!, null, ControlServicePolicies.AdminWeb)).Succeeded)
            throw new HubException("operation-not-authorized");
    }

    private async Task RequireGuestAsync(string operation)
    {
        if (!(await authorization.AuthorizeAsync(Context.User!, operation, ControlServicePolicies.GuestWeb)).Succeeded)
            throw new HubException("operation-not-authorized");
    }

    private static bool TryValidateCorrelation(string? correlation, out string validCorrelation)
    {
        validCorrelation = correlation ?? string.Empty;
        return ControlProtocol.ValidText(validCorrelation, 64);
    }

    private ControlAcknowledgement RejectedAcknowledgement(string? correlation, string code, string operation, bool log = true)
    {
        if (log)
            ApiLog.HubInvocationRejected(logger, operation, code);
        return new(code, correlation is { } validCorrelation && ControlProtocol.ValidText(validCorrelation, 64) ? validCorrelation : string.Empty) { Accepted = false };
    }

    private GuestJoinReceipt RejectedJoinReceipt(string? correlation, string code, bool log = true)
    {
        if (log)
            ApiLog.HubInvocationRejected(logger, nameof(Join), code);
        return new(code, correlation is { } validCorrelation && ControlProtocol.ValidText(validCorrelation, 64) ? validCorrelation : string.Empty) { Accepted = false };
    }

    private static void RejectInvalidRequest() => throw new ControlRejectedException("invalid-request");
}
