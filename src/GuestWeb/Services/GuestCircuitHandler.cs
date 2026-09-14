using Microsoft.AspNetCore.Components.Server.Circuits;

namespace OSSMDotNet.Web.GuestWeb.Services;

public sealed class GuestCircuitHandler(GuestSessionClient client) : CircuitHandler
{
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) =>
        client.DisposeAsync().AsTask();

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        client.DisposeAsync().AsTask();
}
