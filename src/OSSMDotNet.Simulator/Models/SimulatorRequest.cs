namespace OSSMDotNet.Simulator.Models;

internal sealed record SimulatorRequest(
    int V,
    long Id,
    string Op,
    long? CommandId,
    SimulatorDesired? Desired,
    int? Value);
