namespace OSSMDotNet.GuestWeb.Tests;

public sealed class PageControlHub(PageControlHubFixture fixture) : Hub
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
        fixture.RecordAsync("SubscribeGuest", [correlation]);

    public Task<GuestJoinReceipt> Join(GuestJoinRequest request) => fixture.JoinAsync(request);

    public Task<ControlAcknowledgement> Submit(GuestInputRequest request) =>
        fixture.RecordAsync("Submit", [request]);

    public Task<ControlAcknowledgement> GuestStop(string correlation) =>
        fixture.RecordAsync("GuestStop", [correlation]);

    public Task<ControlAcknowledgement> Leave(string correlation) =>
        fixture.RecordAsync("Leave", [correlation]);

    public Task<ControlAcknowledgement> Cycle(AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("Cycle", [expected, correlation]);

    public Task<ControlAcknowledgement> Approve(ApproveGuestRequest request, AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("Approve", [request, expected, correlation]);

    public Task<ControlAcknowledgement> SetLimits(ControlLimits limits, AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("SetLimits", [limits, expected, correlation]);

    public Task<ControlAcknowledgement> Recover(AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("Recover", [expected, correlation]);

    public Task<ControlAcknowledgement> Arm(AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("Arm", [expected, correlation]);

    public Task<ControlAcknowledgement> AllowControl(AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("AllowControl", [expected, correlation]);

    public Task<ControlAcknowledgement> End(AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("End", [expected, correlation]);

    public Task<ControlAcknowledgement> Unlock(UnlockControlRequest request, AuthorityPrecondition expected, string correlation) =>
        fixture.RecordAsync("Unlock", [request, expected, correlation]);
}
