using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.Simulator.Models;

[SuppressMessage("Performance", "CA1812", Justification = "Instances are created by System.Text.Json.")]
internal sealed record SimulatorReply(
    int V,
    long? Id,
    bool? Ok,
    string? Error,
    string? Event,
    SimulatorState? State)
{
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
