using OssmSharp.Runtime;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Api.Tests.Control.Models;

internal sealed record PendingSettingsRequest(SettingsRequest Request, long Epoch, long Sequence, TaskCompletionSource<SetResult> DeviceResult, CancellationToken WorkToken)
{
    public Task<SettingsResponse>? Reply { get; set; }
    public bool Settled { get; set; }
}
