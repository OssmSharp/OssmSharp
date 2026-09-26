using Microsoft.AspNetCore.SignalR;

namespace OssmSharp.Web.Client.Tests;

/// <summary>
/// Forwards client requests to the fixture so tests can observe and control hub responses.
/// </summary>
/// <param name="fixture">The fixture recording requests and producing responses.</param>
public sealed class LocalControlHub(LocalControlHubFixture fixture) : Hub
{
    public override Task OnConnectedAsync()
    {
        fixture.RegisterConnection(Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        fixture.RegisterDisconnection();
        return base.OnDisconnectedAsync(exception);
    }

    public Task<ControlAcknowledgement> SubscribeOperator(string correlation, long afterSequence) =>
        fixture.SubscribeOperatorAsync(Clients.Caller, correlation, afterSequence);

    public Task<ControlAcknowledgement> SubscribeGuest(string correlation) =>
        fixture.SubscribeGuestAsync(correlation);

    public Task<GuestJoinReceipt> Join(GuestJoinRequest request) => fixture.JoinAsync(request);

    public Task<ControlAcknowledgement> Cycle(AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("Cycle", expected, correlation);

    public Task<ControlAcknowledgement> Approve(ApproveGuestRequest request, AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("Approve", request, expected, correlation);

    public Task<ControlAcknowledgement> SetLimits(ControlLimits limits, AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("SetLimits", limits, expected, correlation);

    public Task<ControlAcknowledgement> Recover(AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("Recover", expected, correlation);

    public Task<ControlAcknowledgement> Arm(AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("Arm", expected, correlation);

    public Task<ControlAcknowledgement> AllowControl(AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("AllowControl", expected, correlation);

    public Task<ControlAcknowledgement> Stop(string correlation) =>
        fixture.AcknowledgeAsync("Stop", correlation);

    public Task<ControlAcknowledgement> End(AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("End", expected, correlation);

    public Task<ControlAcknowledgement> Unlock(UnlockControlRequest request, AuthorityPrecondition expected, string correlation) =>
        fixture.AcknowledgeAsync("Unlock", request, expected, correlation);

    public Task<ControlAcknowledgement> SimulatorFault(SimulatorFaultRequest request, string correlation) =>
        fixture.AcknowledgeAsync("SimulatorFault", request, correlation);

    public Task<ControlAcknowledgement> Submit(GuestInputRequest request) =>
        fixture.AcknowledgeAsync("Submit", request);

    public Task<ControlAcknowledgement> GuestStop(string correlation) =>
        fixture.AcknowledgeAsync("GuestStop", correlation);

    public Task<ControlAcknowledgement> Leave(string correlation) =>
        fixture.AcknowledgeAsync("Leave", correlation);
}
