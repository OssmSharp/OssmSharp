namespace OssmSharp.Web.GuestWeb.Services;

/// <summary>
/// Closes the scoped guest transport when its Blazor circuit disconnects or closes.
/// </summary>
/// <param name="client">The guest connection owned by this circuit.</param>
public sealed class GuestCircuitHandler(IGuestApiClient client) : CircuitHandler
{
    /// <inheritdoc />
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) =>
        client.DisposeAsync().AsTask();

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        client.DisposeAsync().AsTask();
}
