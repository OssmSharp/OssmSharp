using Microsoft.AspNetCore.Components.Server.Circuits;

namespace OSSMDotNet.GuestWeb.Services;

/// <summary>Closes the scoped guest transport when its Blazor circuit disconnects or closes.</summary>
public sealed class GuestCircuitHandler(GuestSessionClient client) : CircuitHandler
{
    /// <summary>Disposes the guest transport when the circuit connection drops.</summary>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) =>
        client.DisposeAsync().AsTask();

    /// <summary>Disposes the guest transport when the circuit closes.</summary>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        client.DisposeAsync().AsTask();
}
