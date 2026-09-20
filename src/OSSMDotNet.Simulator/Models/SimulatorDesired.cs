namespace OSSMDotNet.Simulator.Models;

internal sealed record SimulatorDesired(
    int Pattern,
    int Speed,
    int Stroke,
    int Depth,
    int Sensation);
