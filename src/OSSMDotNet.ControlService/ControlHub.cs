using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Provides versioned role-scoped SignalR operations over the transport-neutral control application.</summary>
public sealed class ControlHub(
    IControlOperations operations,
    GuestControlOperations guests,
    ControlHubConnectionRegistry connections,
    ControlPushDispatcher dispatcher,
    IAuthorizationService authorization) : Hub
{
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public async Task<ControlAcknowledgement> SubscribeOperator(string correlation, long afterSequence)
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation");
        if (afterSequence < 0)
            return RejectedAcknowledgement(validCorrelation, "invalid-activity-cursor");
        connections.SubscribeOperator(Context.ConnectionId, afterSequence, -1);
        dispatcher.Trigger();
        return new("subscribed", validCorrelation);
    }

    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> SubscribeGuest(string correlation)
    {
        await RequireGuestAsync("subscribe");
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation");
        connections.StageGuest(Context.ConnectionId);
        return new("subscribed", validCorrelation);
    }

    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<GuestJoinReceipt> Join(GuestJoinRequest? request)
    {
        await RequireGuestAsync("join");
        if (request is null)
            return RejectedJoinReceipt(null, "invalid-request");
        try
        {
            var receipt = guests.Join(Context.ConnectionId, request);
            connections.SubscribeGuest(Context.ConnectionId, -1);
            dispatcher.Trigger();
            return receipt;
        }
        catch (ControlRejectedException exception) { return RejectedJoinReceipt(request.Correlation, exception.Code); }
    }

    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> Submit(GuestInputRequest? request)
    {
        await RequireGuestAsync("input");
        if (request is null)
            return RejectedAcknowledgement(null, "invalid-request");
        return await GuestCommandAsync(request.Correlation, () => guests.Submit(Context.ConnectionId, request));
    }

    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> GuestStop(string correlation) =>
        await GuestCommandAsync(correlation, () => guests.Stop(Context.ConnectionId, correlation));

    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public async Task<ControlAcknowledgement> Leave(string correlation) =>
        await GuestCommandAsync(correlation, () => guests.Leave(Context.ConnectionId, correlation));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public async Task<ControlAcknowledgement> Stop(string correlation)
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation");
        operations.Stop();
        dispatcher.Trigger();
        return new("stopped", validCorrelation);
    }

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Cycle(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.CycleInvitation(expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Approve(ApproveGuestRequest? request, AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Approve(request, expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> SetLimits(ControlLimits limits, AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.ApplySessionLimits(limits, expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Recover(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Recover(expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Arm(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Arm(expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> AllowControl(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.AllowControl(expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> End(AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.End(expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> Unlock(UnlockControlRequest? request, AuthorityPrecondition expected, string correlation) =>
        AdminCommandAsync(correlation, () => operations.Unlock(request, expected));

    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlAcknowledgement> SimulatorFault(SimulatorFaultRequest? request, string correlation) =>
        AdminCommandAsync(correlation, () => operations.InjectSimulatorFaultAsync(request));

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        guests.TransportLost(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task<ControlAcknowledgement> GuestCommandAsync(string? correlation, Func<ControlAcknowledgement> command)
    {
        try
        {
            var acknowledgement = command();
            dispatcher.Trigger();
            return acknowledgement;
        }
        catch (ControlRejectedException exception) { return RejectedAcknowledgement(correlation, exception.Code); }
    }

    private async Task<ControlAcknowledgement> AdminCommandAsync(string correlation, Action command)
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation");
        try { command(); }
        catch (ControlRejectedException exception) { return RejectedAcknowledgement(validCorrelation, exception.Code); }
        dispatcher.Trigger();
        return new("accepted", validCorrelation);
    }

    private async Task<ControlAcknowledgement> AdminCommandAsync(string correlation, Func<Task> command)
    {
        await RequireAdminAsync();
        if (!TryValidateCorrelation(correlation, out var validCorrelation))
            return RejectedAcknowledgement(correlation, "invalid-correlation");
        try { await command(); }
        catch (ControlRejectedException exception) { return RejectedAcknowledgement(validCorrelation, exception.Code); }
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

    private static ControlAcknowledgement RejectedAcknowledgement(string? correlation, string code) =>
        new(code, correlation is { } validCorrelation && ControlProtocol.ValidText(validCorrelation, 64) ? validCorrelation : string.Empty) { Accepted = false };

    private static GuestJoinReceipt RejectedJoinReceipt(string? correlation, string code) =>
        new(code, correlation is { } validCorrelation && ControlProtocol.ValidText(validCorrelation, 64) ? validCorrelation : string.Empty) { Accepted = false };
}
