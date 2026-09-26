namespace OssmSharp.Cli.Simulator.Ipc;

/// <summary>
/// Represents one private simulator-host operation and its bounded motion arguments.
/// </summary>
/// <param name="Operation">The operation to perform in the host.</param>
/// <param name="SimulatorPath">The optional simulator executable path.</param>
/// <param name="Speed">The requested speed.</param>
/// <param name="Stroke">The requested stroke length.</param>
/// <param name="Depth">The requested depth.</param>
/// <param name="DurationMilliseconds">The requested run duration in milliseconds.</param>
internal sealed record SimulatorIpcRequest(
    string Operation,
    string? SimulatorPath = null,
    int Speed = 0,
    int Stroke = 0,
    int Depth = 0,
    int DurationMilliseconds = 0);
