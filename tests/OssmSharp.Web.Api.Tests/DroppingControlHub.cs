using Microsoft.AspNetCore.SignalR;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Api.Tests;

public sealed class DroppingControlHub(TaskCompletionSource received) : Hub
{
    public Task<ControlResponse> EndGuest(string correlation) => DropAfterReceiving();

    public Task<ControlResponse> RefreshInvitation(string correlation) => DropAfterReceiving();

    private Task<ControlResponse> DropAfterReceiving()
    {
        received.TrySetResult();
        Context.Abort();
        return new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
            .Task.WaitAsync(Context.ConnectionAborted);
    }
}
