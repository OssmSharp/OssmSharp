namespace OssmSharp.Device.Simulator.Models;

/// <summary>
/// Carries a versioned simulator operation and its optional motion or scalar argument.
/// </summary>
/// <param name="V">The protocol version.</param>
/// <param name="Id">The request correlation identifier.</param>
/// <param name="Op">The simulator operation to perform.</param>
/// <param name="CommandId">The device command identifier, if applicable.</param>
/// <param name="Desired">The requested motion settings, if applicable.</param>
/// <param name="Value">The scalar operation argument, if applicable.</param>
internal sealed record SimulatorRequest(
    int V,
    long Id,
    string Op,
    long? CommandId,
    SimulatorDesired? Desired,
    int? Value);
