namespace OssmSharp.Device.Simulator.Models;

/// <summary>
/// Carries the motion pattern and percentage settings sent to the simulator.
/// </summary>
/// <param name="Pattern">The selected motion pattern.</param>
/// <param name="Speed">The speed percentage.</param>
/// <param name="Stroke">The stroke percentage.</param>
/// <param name="Depth">The depth percentage.</param>
/// <param name="Sensation">The sensation percentage.</param>
internal sealed record SimulatorDesired(
    int Pattern,
    int Speed,
    int Stroke,
    int Depth,
    int Sensation);
