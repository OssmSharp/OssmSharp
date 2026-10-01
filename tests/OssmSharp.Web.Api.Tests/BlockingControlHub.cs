using Microsoft.AspNetCore.SignalR;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Api.Tests;

public sealed class BlockingControlHub(TaskCompletionSource entered, TaskCompletionSource<ControlResponse>? pendingStop = null) : Hub
{
    public async Task<ControlResponse> Connect(string correlation)
    {
        entered.TrySetResult();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await pending.Task.WaitAsync(Context.ConnectionAborted);
        return new(correlation, "connected", true);
    }

    public Task<ControlResponse> Stop(string correlation) => pendingStop is null
        ? Task.FromResult(new ControlResponse(correlation, "stopped", true))
        : pendingStop.Task.WaitAsync(Context.ConnectionAborted);

    public ControlResponse EndGuest(string correlation) => new(correlation, "stopped", true);
}
