using System.Diagnostics.CodeAnalysis;

namespace OSSMDotNet.Simulator.Models;

[SuppressMessage("Performance", "CA1812", Justification = "Instances are created by System.Text.Json.")]
internal sealed record SimulatorState(
    long SampleSequence,
    long ChangeRevision,
    long? CommandId,
    SimulatorDesired Desired,
    bool Enabled,
    bool Moving,
    double Position,
    double Velocity,
    bool Connected,
    string ProfileId);
