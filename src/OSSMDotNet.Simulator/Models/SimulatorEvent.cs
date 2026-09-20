namespace OSSMDotNet.Simulator.Models;

/// <summary>
/// Represents an unsolicited state event from the native simulator.
/// </summary>
/// <param name="V">The protocol version.</param>
/// <param name="Event">The event name.</param>
/// <param name="State">The reported simulator state.</param>
internal sealed record SimulatorEvent(
    int V,
    string Event,
    SimulatorState State);
