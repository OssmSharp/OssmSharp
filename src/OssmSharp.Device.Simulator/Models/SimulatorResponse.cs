namespace OssmSharp.Device.Simulator.Models;

/// <summary>
/// Represents a correlated response from the native simulator.
/// </summary>
/// <param name="V">The protocol version.</param>
/// <param name="Id">The request correlation identifier.</param>
/// <param name="Ok">Whether the request succeeded.</param>
/// <param name="Error">The rejection code, or <see langword="null"/> when none was reported.</param>
/// <param name="State">The reported simulator state, or <see langword="null"/> when none was returned.</param>
internal sealed record SimulatorResponse(
    int V,
    long Id,
    bool Ok,
    string? Error,
    SimulatorState? State = null);
