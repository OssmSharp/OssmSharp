using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.Simulator.Models;

[SuppressMessage("Performance", "CA1812", Justification = "Instances are created by System.Text.Json.")]
internal sealed record SimulatorReply(
    int V,
    long? Id,
    bool? Ok,
    string? Error,
    string? Event,
    SimulatorState? State);
