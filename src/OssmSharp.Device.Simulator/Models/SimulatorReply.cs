using System.Diagnostics.CodeAnalysis;

namespace OssmSharp.Device.Simulator.Models;

/// <summary>
/// Represents a simulator command response or unsolicited state event.
/// </summary>
/// <param name="V">The protocol version.</param>
/// <param name="Id">The request correlation identifier, if this is a response.</param>
/// <param name="Ok">Whether the command succeeded, if this is a response.</param>
/// <param name="Error">The reported failure code, if any.</param>
/// <param name="Event">The unsolicited event name, if this is an event.</param>
/// <param name="State">The embedded simulator state, if present.</param>
[SuppressMessage("Performance", "CA1812", Justification = "Instances are created by System.Text.Json.")]
internal sealed record SimulatorReply(
    int V,
    long? Id,
    bool? Ok,
    string? Error,
    string? Event,
    SimulatorState? State)
{
    /// <summary>
    /// Rejects malformed protocol envelopes and invalid embedded state.
    /// </summary>
    /// <exception cref="InvalidDataException">The response or its state violates the simulator protocol.</exception>
    internal void Validate()
    {
        if (V != 1)
        {
            throw new InvalidDataException("The simulator returned an invalid response envelope.");
        }

        if (Event is not null)
        {
            if (Event != "state" || Id is not null || Ok is not null || Error is not null || State is null)
            {
                throw new InvalidDataException("The simulator returned an invalid response envelope.");
            }
        }
        else if (Id is not { } id || id < 0 || Ok is not { } ok ||
                 (ok && (id == 0 || Error is not null || State is null)) ||
                 (!ok && Error is not (
                     "invalid_request" or
                     "invalid_desired" or
                     "unsupported_version" or
                     "frame_too_large" or
                     "busy" or
                     "disconnected")))
        {
            throw new InvalidDataException("The simulator returned an invalid response envelope.");
        }

        State?.Validate();
    }
}
