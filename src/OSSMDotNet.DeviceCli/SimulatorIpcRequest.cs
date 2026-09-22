namespace OSSMDotNet.DeviceCli;

/// <summary>
/// Represents one private simulator-host operation and its bounded motion arguments.
/// </summary>
internal sealed record SimulatorIpcRequest(
    string Operation,
    string? SimulatorPath = null,
    int Speed = 0,
    int Stroke = 0,
    int Depth = 0,
    int DurationMilliseconds = 0);
