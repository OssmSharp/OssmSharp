using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Simulation;

internal sealed record SimulatorRequest(int V, long Id, string Op, long? CommandId, DesiredState? Desired, int? Value);
